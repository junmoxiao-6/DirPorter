using DirPorter.Core.FileSystem;
using DirPorter.Core.Privilege;

namespace DirPorter.Tests;

/// <summary>
/// 测试用临时工作目录。
/// ⚠️ 清理必须跳过重解析点：直接 Directory.Delete(root, true) 会顺着测试中创建的
/// Junction 进入目标目录删掉真实数据 —— 这正是被测代码要防的那个致命 bug，测试自身也不能犯。
/// </summary>
public sealed class TempWorkspace : IDisposable
{
    public string Root { get; }

    public TempWorkspace(string label = "dp")
    {
        Root = Path.Combine(Path.GetTempPath(), $"{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
    }

    public string PathOf(string relative) => Path.Combine(Root, relative);

    public string CreateDirectory(string relative)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(path);
        return path;
    }

    public string CreateFile(string relative, string content = "sentinel")
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public string ReadFile(string path) => File.ReadAllText(path);

    public void Dispose()
    {
        try
        {
            SafeDeleteTree(Root);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TempWorkspace] 清理失败（不影响测试结果）：{ex.Message}");
        }
    }

    /// <summary>
    /// 安全递归删除：遇重解析点只删本体（removing the link, never the target）。
    /// 全程使用 <c>\\?\</c> 扩展长度路径，保证清理超长路径用例时同样有效。
    /// </summary>
    public static void SafeDeleteTree(string directory)
    {
        var root = PathNormalizer.NormalizeAbsolute(directory);
        if (!Directory.Exists(root)) return;

        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            var attributes = File.GetAttributes(entry);
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            var isReparse = (attributes & FileAttributes.ReparsePoint) != 0;

            if (isDirectory && !isReparse)
            {
                SafeDeleteTree(entry);
                continue;
            }

            if (isDirectory)
            {
                // 目录型链接/联接：recursive:false ⇒ Win32 RemoveDirectory ⇒ 只删链接本体。
                Directory.Delete(entry, recursive: false);
            }
            else
            {
                File.Delete(entry);
            }
        }

        Directory.Delete(root, recursive: false);
    }
}

/// <summary>可注入的卷解析假实现，用于在没有第二个物理分区的情况下验证跨卷判定。</summary>
public sealed class FakeVolumeResolver : IVolumeResolver
{
    public bool Fail { get; set; }
    public DriveTypeKind DriveType { get; set; } = DriveTypeKind.Fixed;
    public string FileSystemName { get; set; } = "NTFS";
    public uint SerialForLink { get; set; } = 1111;
    public uint SerialForTarget { get; set; } = 1111;

    /// <summary>模拟目标卷可用空间。</summary>
    public long FreeBytes { get; set; } = long.MaxValue / 2;

    /// <summary>判定某个路径应归属哪一侧（默认为目标侧）。</summary>
    public Func<string, bool> IsLinkPath { get; set; } = static _ => false;

    public bool TryGetVolumeInfo(string path, out VolumeInfo info)
    {
        if (Fail)
        {
            info = null!;
            return false;
        }

        var serial = IsLinkPath(path) ? SerialForLink : SerialForTarget;
        info = new VolumeInfo(
            RootPath: @"C:\",
            DriveType: DriveType,
            FileSystemName: FileSystemName,
            SerialNumber: serial,
            FreeBytesAvailable: FreeBytes,
            TotalBytes: long.MaxValue / 2,
            SupportsReparsePoints: Win32VolumeResolver.FileSystemSupportsReparsePoints(FileSystemName),
            SupportsHardLinks: Win32VolumeResolver.FileSystemSupportsReparsePoints(FileSystemName));
        return true;
    }
}

/// <summary>先用真实实现，再按需覆盖特定路径（例如把链接落点伪装成另一个卷）。</summary>
public sealed class OverridingVolumeResolver : IVolumeResolver
{
    private readonly IVolumeResolver _inner;
    private readonly Func<string, VolumeInfo?> _override;

    public OverridingVolumeResolver(IVolumeResolver inner, Func<string, VolumeInfo?> @override)
    {
        _inner = inner;
        _override = @override;
    }

    public bool TryGetVolumeInfo(string path, out VolumeInfo info)
    {
        var overridden = _override(path);
        if (overridden is not null)
        {
            info = overridden;
            return true;
        }

        return _inner.TryGetVolumeInfo(path, out info);
    }
}

/// <summary>可注入的权限探测假实现。</summary>
public sealed class FakePrivilegeProbe : IPrivilegeProbe
{
    public bool IsElevated { get; set; }
    public bool HasSeCreateSymbolicLinkPrivilege { get; set; }
    public bool IsDeveloperModeEnabled { get; set; }
    public bool HasSeSecurityPrivilege { get; set; }
    public bool HasSeRestorePrivilege { get; set; }

    public bool CanCreateSymbolicLink => (IsElevated && HasSeCreateSymbolicLinkPrivilege) || IsDeveloperModeEnabled;

    public string Describe()
        => $"Elevated={IsElevated}, SeCreateSymbolicLinkPrivilege={HasSeCreateSymbolicLinkPrivilege}, "
           + $"SeSecurityPrivilege={HasSeSecurityPrivilege}, SeRestorePrivilege={HasSeRestorePrivilege}, "
           + $"DeveloperMode={IsDeveloperModeEnabled}";

    /// <summary>模拟有权限。</summary>
    public static FakePrivilegeProbe Elevated() => new() { IsElevated = true, HasSeCreateSymbolicLinkPrivilege = true };

    /// <summary>模拟完整管理员（含 SeSecurity / SeRestore）。</summary>
    public static FakePrivilegeProbe FullAdmin() => new()
    {
        IsElevated = true,
        HasSeCreateSymbolicLinkPrivilege = true,
        HasSeSecurityPrivilege = true,
        HasSeRestorePrivilege = true,
    };

    /// <summary>模拟无权限（普通用户 + 未开开发者模式），与本机实测环境一致。</summary>
    public static FakePrivilegeProbe Standard() => new();

    /// <summary>模拟仅开启开发者模式。</summary>
    public static FakePrivilegeProbe DeveloperMode() => new() { IsDeveloperModeEnabled = true };
}

/// <summary>
/// 共享数据根的快照/还原护栏（<c>using</c> 语义）。
///
/// 为什么必须用：真实迁移用例（R8 端到端、R9 同卷）会经引擎往**进程级共享数据根**写
/// <c>migrations.json</c> / <c>runs/</c> / 台账。若不还原，残留记录会让后面的
/// `verify` 类用例读到"源已消失"的孤儿记录而失败（实测踩过：R9 只清自己的临时目录、
/// 从不还原数据根，累积出 19KB 残留记录）。
///
/// 使用纪律（两条缺一不可）：
/// <list type="number">
/// <item>测试类必须挂 <c>[Collection(DataRootSequentialCollection.Name)]</c>（串行，防并行互踩）；</item>
/// <item>真实迁移用例必须在最外层 <c>using var guard = new DataRootGuard();</c> 包住。</item>
/// </list>
/// </summary>
public sealed class DataRootGuard : IDisposable
{
    private readonly Dictionary<string, byte[]> _snapshot;
    private readonly HashSet<string> _existing;

    public DataRootGuard()
    {
        _snapshot = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        _existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var root = TestEnvironment.DataRoot;

        if (!Directory.Exists(root)) return;

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);
            _existing.Add(relative);

            try
            {
                _snapshot[relative] = File.ReadAllBytes(file);
            }
            catch (IOException)
            {
                // 文件正被写句柄持有（短暂占用）：跳过内容快照。
                // 但保留在 _existing 里 —— 还原时**绝不删除**它（它是运行前就存在的文件）。
                RetryRead(file, relative);
            }
        }
    }

    /// <summary>短暂重试一次：写者通常几毫秒内释放；仍失败则放弃内容（但保留存在性记录）。</summary>
    private void RetryRead(string file, string relative)
    {
        try
        {
            Thread.Sleep(50);
            _snapshot[relative] = File.ReadAllBytes(file);
        }
        catch
        {
            // 内容没快照到就算了：还原时不删它、也不改它，保持现状是最安全的近似。
        }
    }

    /// <summary>把数据根还原到快照状态（多出来的文件删掉，改过的写回）。</summary>
    public void Dispose()
    {
        var root = TestEnvironment.DataRoot;

        try
        {
            if (!Directory.Exists(root)) return;

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file);

                // 只删除"快照之外"的文件：运行前就存在的（含当时被占用、没读进内容的）一律保留。
                if (_existing.Contains(relative)) continue;

                try { File.Delete(file); } catch { }
            }

            foreach (var (relative, bytes) in _snapshot)
            {
                var path = Path.Combine(root, relative);
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                try
                {
                    // 只在内容确实不同时才写回，避免无谓地刷新其它用例正在读的文件。
                    if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) continue;
                    File.WriteAllBytes(path, bytes);
                }
                catch { }
            }
        }
        catch
        {
            // 还原失败不该让用例本身失败（真正的问题是环境污染，已由串行集合兜住）。
        }
    }
}

/// <summary>真实环境能力探测（用于决定测试走"真实创建"还是"验证拒绝理由"分支）。</summary>
public static class NativeTestHelpers
{
    public static bool RealSymlinkCreationAvailable => Win32PrivilegeProbe.Current.CanCreateSymbolicLink;

    public static string DescribeRealPrivileges() => Win32PrivilegeProbe.Current.Describe();
}
