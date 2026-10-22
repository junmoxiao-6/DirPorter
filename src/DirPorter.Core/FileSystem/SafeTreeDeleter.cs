using DirPorter.Core.Diagnostics;

namespace DirPorter.Core.FileSystem;

/// <summary>安全删除的结果。</summary>
public sealed record SafeDeleteResult(
    int DeletedFiles,
    int DeletedDirectories,
    int DeletedLinks,
    long DeletedBytes,
    IReadOnlyList<ProbeError> Failures)
{
    public bool Success => Failures.Count == 0;
    public bool AnyFailure => Failures.Count > 0;

    public string Describe()
        => $"删除文件 {DeletedFiles} 个（{SpaceRequirement.Format(DeletedBytes)}）、目录 {DeletedDirectories} 个、链接本体 {DeletedLinks} 个"
           + (AnyFailure ? $"，{Failures.Count} 项删除失败" : string.Empty) + "。";
}

/// <summary>
/// 安全递归删除器 —— **全项目唯一允许递归删除的地方**。
///
/// 铁律（由需求「最高优先级安全红线」推论而来，必须同样严格遵守）：
/// 遇到重解析点（Junction / 符号链接）时**只删链接本体，绝不穿越**。
/// 原因：迁移完成后 `.migbak` 目录内部可能仍含有嵌套链接（源目录本身含链接是合法场景）；
/// 一旦递归删除穿越进去，会把链接指向的**真实数据**全部删光 ——
/// 这与 Unlink 误删目标是同一类致命 bug，只是发生在另一条代码路径上。
///
/// 实现保证：
/// 1. 遍历时先读属性，命中 `FILE_ATTRIBUTE_REPARSE_POINT` 即走
///    <see cref="ReparsePointOps.RemoveLinkBody"/>，**不进入该目录**；
/// 2. 真实目录用 <c>RemoveDirectory</c>（对非空目录必然失败），不做任何"顺手删干净"的兜底；
/// 3. 单个对象的失败被记录而不是抛出：能删的尽量删、删不掉的如实报告。
/// </summary>
public static class SafeTreeDeleter
{
    /// <summary>删除整棵树（含根）。</summary>
    public static SafeDeleteResult DeleteTree(string rootPath, CancellationToken cancellationToken = default)
    {
        var failures = new List<ProbeError>();
        var counters = new Counters();
        var root = PathNormalizer.NormalizeAbsolute(rootPath);

        DeleteDirectoryRecursive(root, counters, failures, cancellationToken);

        return new SafeDeleteResult(counters.Files, counters.Directories, counters.Links, counters.Bytes, failures);
    }

    /// <summary>清空目录内容，但保留根目录本身。</summary>
    public static SafeDeleteResult ClearDirectoryContents(string rootPath, CancellationToken cancellationToken = default)
    {
        var failures = new List<ProbeError>();
        var counters = new Counters();
        var root = PathNormalizer.TrimTrailingSeparator(PathNormalizer.NormalizeAbsolute(rootPath));

        foreach (var child in Enumerate(root, failures))
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteEntry(PathNormalizer.Combine(root, child.Name), child.Attributes, child.Size, counters, failures, cancellationToken);
        }

        return new SafeDeleteResult(counters.Files, counters.Directories, counters.Links, counters.Bytes, failures);
    }

    private static void DeleteDirectoryRecursive(
        string extendedDirectory, Counters counters, List<ProbeError> failures, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var child in Enumerate(extendedDirectory, failures))
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteEntry(PathNormalizer.Combine(extendedDirectory, child.Name), child.Attributes, child.Size, counters, failures, cancellationToken);
        }

        if (Kernel32.RemoveDirectory(extendedDirectory))
        {
            counters.Directories++;
            return;
        }

        var code = Win32Error.Last;
        if (code is Kernel32.ErrorFileNotFound or Kernel32.ErrorPathNotFound) return;

        failures.Add(new ProbeError(
            PathNormalizer.StripExtendedPrefix(extendedDirectory),
            code == Kernel32.ErrorDirectoryNotEmpty
                ? "目录非空，未删除（内部有对象删除失败或被占用）。"
                : "目录删除失败。",
            code));
    }

    private static void DeleteEntry(
        string extendedPath, uint attributes, long size, Counters counters, List<ProbeError> failures, CancellationToken cancellationToken)
    {
        var isDirectory = (attributes & Kernel32.FileAttributeDirectory) != 0;
        var isReparsePoint = (attributes & Kernel32.FileAttributeReparsePoint) != 0;

        if (isReparsePoint)
        {
            // ⚠️ 只删链接本体，绝不穿越。
            try
            {
                var info = ReparsePointOps.Inspect(extendedPath);
                ReparsePointOps.RemoveLinkBody(extendedPath, isDirectory, info.Tag, "安全删除链接本体");
                counters.Links++;
            }
            catch (Exception ex)
            {
                failures.Add(new ProbeError(
                    PathNormalizer.StripExtendedPrefix(extendedPath), $"删除链接本体失败：{ex.Message}", 0));
            }

            return;
        }

        if (isDirectory)
        {
            DeleteDirectoryRecursive(extendedPath, counters, failures, cancellationToken);
            return;
        }

        if (Kernel32.DeleteFile(extendedPath))
        {
            counters.Files++;
            counters.Bytes += size;
            return;
        }

        var code = Win32Error.Last;
        if (code is Kernel32.ErrorFileNotFound or Kernel32.ErrorPathNotFound) return;

        // 只读属性是阻止删除的常见原因，清位后只重试一次（不做无界重试）。
        if (code == Kernel32.ErrorAccessDenied)
        {
            var cleared = attributes & ~Kernel32.FileAttributeReadOnly;
            if (Kernel32.SetFileAttributes(extendedPath, cleared) && Kernel32.DeleteFile(extendedPath))
            {
                counters.Files++;
                counters.Bytes += size;
                return;
            }

            code = Win32Error.Last;
        }

        failures.Add(new ProbeError(PathNormalizer.StripExtendedPrefix(extendedPath), "删除文件失败。", code));
    }

    private static List<FindEntry> Enumerate(string extendedDirectory, List<ProbeError> failures)
    {
        var result = new List<FindEntry>();
        var searchPath = PathNormalizer.EnsureTrailingSeparator(extendedDirectory) + "*";

        var handle = Kernel32.FindFirstFileEx(
            searchPath, Kernel32.FindExInfoBasic, out var data,
            Kernel32.FindExSearchNameMatch, IntPtr.Zero, Kernel32.FindFirstExLargeFetch);

        if (handle == Kernel32.InvalidHandleValue)
        {
            var code = Win32Error.Last;
            if (code is not (Kernel32.ErrorFileNotFound or Kernel32.ErrorPathNotFound))
            {
                failures.Add(new ProbeError(
                    PathNormalizer.StripExtendedPrefix(extendedDirectory), "无法枚举目录内容。", code));
            }

            return result;
        }

        try
        {
            while (true)
            {
                var name = Kernel32.GetFileName(data);
                if (name is not ("." or ".."))
                    result.Add(new FindEntry(name, data.dwFileAttributes, Kernel32.ToFileSize(data)));

                if (!Kernel32.FindNextFile(handle, out data)) break;
            }
        }
        finally
        {
            Kernel32.FindClose(handle);
        }

        return result;
    }

    private readonly record struct FindEntry(string Name, uint Attributes, long Size);

    private sealed class Counters
    {
        public int Files;
        public int Directories;
        public int Links;
        public long Bytes;
    }
}
