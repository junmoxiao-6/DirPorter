using System.IO.Hashing;
using System.Text;

namespace DirPorter.Core.Ledger;

/// <summary>
/// 元数据指纹 —— 台账 <c>checksum</c> 字段的默认算法（用户决策：`--verify=metadata`）。
///
/// 定义（**必须写死，否则 restore 校验无法复现**）：
/// <code>
/// checksum = XxHash3( 按相对路径 OrdinalIgnoreCase 升序拼接的 "&lt;相对路径&gt;|&lt;长度&gt;|&lt;最后写入时间ticks&gt;\n" )
/// </code>
///
/// 为什么不是全文哈希：2GB / 5 万文件的场景下，逐文件读取内容需要数分钟到数十分钟，
/// 而"回滚前后是否等价"用元数据指纹即可判定。需要内容级保证时用 <c>--verify=content</c>
/// （代价：restore 校验从秒级变分钟级，Dry-run 中会显式标注）。
///
/// 内存：为了确定性必须排序，5 万条 ≈ 5MB 峰值（远低于 300MB 稳态上限）；
/// 条目按流式生成后排序，不保留文件句柄。
/// </summary>
public static class MetadataFingerprint
{
    public const string MethodName = "xxHash3";

    /// <summary>计算目录树的元数据指纹。返回 16 位小写十六进制字符串。</summary>
    public static string Compute(string rootPath, out long fileCount, out long totalBytes)
        => Compute(rootPath, CancellationToken.None, out fileCount, out totalBytes);

    public static string Compute(string rootPath, CancellationToken cancellationToken, out long fileCount, out long totalBytes)
    {
        var entries = CollectEntries(rootPath, cancellationToken, out fileCount, out totalBytes);
        var hasher = new XxHash3();

        foreach (var entry in entries.OrderBy(static e => e.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            // 排序用 OrdinalIgnoreCase（跨机器稳定），内容保留原始大小写（改名的差异要能被发现）。
            hasher.Append(Encoding.UTF8.GetBytes(
                string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"{entry.RelativePath}|{entry.Length}|{entry.LastWriteTicks}\n")));
        }

        return Convert.ToHexString(hasher.GetCurrentHash()).ToLowerInvariant();
    }

    /// <summary>计算内容哈希（<c>--verify=content</c> 档位）。慢，但可提供内容级保证。</summary>
    public static string ComputeContentHash(string rootPath, IProgress<long>? progress, CancellationToken cancellationToken, out long fileCount, out long totalBytes)
    {
        var entries = CollectEntries(rootPath, cancellationToken, out fileCount, out totalBytes);
        var hasher = new XxHash3();

        foreach (var entry in entries.OrderBy(static e => e.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativeBytes = Encoding.UTF8.GetBytes(entry.RelativePath);
            hasher.Append(relativeBytes);

            using var stream = new FileStream(entry.ExtendedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20);
            var buffer = new byte[1 << 20];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hasher.Append(buffer.AsSpan(0, read));
            }

            progress?.Report(1);
        }

        return Convert.ToHexString(hasher.GetCurrentHash()).ToLowerInvariant();
    }

    private static List<Entry> CollectEntries(string rootPath, CancellationToken cancellationToken, out long fileCount, out long totalBytes)
    {
        var root = FileSystem.PathNormalizer.NormalizeAbsolute(rootPath);
        var rootPlain = FileSystem.PathNormalizer.StripExtendedPrefix(root);
        var entries = new List<Entry>();

        fileCount = 0;
        totalBytes = 0;

        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var directory = stack.Pop();
            var searchPath = FileSystem.PathNormalizer.EnsureTrailingSeparator(directory) + "*";

            var handle = FileSystem.Kernel32.FindFirstFileEx(
                searchPath, FileSystem.Kernel32.FindExInfoBasic, out var data,
                FileSystem.Kernel32.FindExSearchNameMatch, IntPtr.Zero, FileSystem.Kernel32.FindFirstExLargeFetch);

            if (handle == FileSystem.Kernel32.InvalidHandleValue) continue;

            try
            {
                while (true)
                {
                    var name = FileSystem.Kernel32.GetFileName(data);

                    if (name is not ("." or ".."))
                    {
                        var childPath = FileSystem.PathNormalizer.Combine(directory, name);
                        var isDirectory = (data.dwFileAttributes & FileSystem.Kernel32.FileAttributeDirectory) != 0;
                        var isReparse = (data.dwFileAttributes & FileSystem.Kernel32.FileAttributeReparsePoint) != 0;

                        if (isDirectory)
                        {
                            // ⚠️ 不进入重解析点（与复制/删除保持同一语义，避免成环与重复计数）
                            if (!isReparse) stack.Push(childPath);
                        }
                        else if (!isReparse)
                        {
                            var length = FileSystem.Kernel32.ToFileSize(data);
                            var ticks = ToTicks(data.ftLastWriteTime.dwLowDateTime, data.ftLastWriteTime.dwHighDateTime);
                            var relative = Path.GetRelativePath(rootPlain, FileSystem.PathNormalizer.StripExtendedPrefix(childPath));

                            entries.Add(new Entry(childPath, relative, length, ticks));
                            fileCount++;
                            totalBytes += length;
                        }
                    }

                    if (!FileSystem.Kernel32.FindNextFile(handle, out data)) break;
                }
            }
            finally
            {
                FileSystem.Kernel32.FindClose(handle);
            }
        }

        return entries;
    }

    private static long ToTicks(uint low, uint high) => ((long)high << 32) | low;

    private readonly record struct Entry(string ExtendedPath, string RelativePath, long Length, long LastWriteTicks);
}
