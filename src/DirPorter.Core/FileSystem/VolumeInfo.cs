using DirPorter.Core.Diagnostics;

namespace DirPorter.Core.FileSystem;

/// <summary>驱动器类型（与 Win32 GetDriveType 常量一一对应）。</summary>
public enum DriveTypeKind : uint
{
    Unknown = 0,
    NoRootDir = 1,
    Removable = 2,
    Fixed = 3,
    Remote = 4,
    CdRom = 5,
    RamDisk = 6,
}

/// <summary>卷信息快照。用于 PRECHECK 第 7/8 条与链接能力判定。</summary>
public sealed record VolumeInfo(
    string RootPath,
    DriveTypeKind DriveType,
    string FileSystemName,
    uint SerialNumber,
    long FreeBytesAvailable,
    long TotalBytes,
    bool SupportsReparsePoints,
    bool SupportsHardLinks)
{
    public bool IsFixed => DriveType == DriveTypeKind.Fixed;
}

/// <summary>
/// 卷解析抽象。抽象出来是为了让"跨卷"这类判定可在单元测试中注入假实现——
/// 注意：用 <c>subst</c> 造出来的盘符与原卷是同一个卷（卷序列号相同），
/// 因此 subst 无法用来验证硬链接的跨卷拒绝，必须能被注入替换。
/// </summary>
public interface IVolumeResolver
{
    bool TryGetVolumeInfo(string path, out VolumeInfo info);
}

/// <summary>基于 Win32 的卷解析实现。</summary>
public sealed class Win32VolumeResolver : IVolumeResolver
{
    public bool TryGetVolumeInfo(string path, out VolumeInfo info)
    {
        info = null!;
        var plain = PathNormalizer.TrimTrailingSeparator(PathNormalizer.StripExtendedPrefix(path));
        if (string.IsNullOrWhiteSpace(plain)) return false;

        var root = QueryVolumePathName(plain);
        if (root is null) return false;

        var driveType = (DriveTypeKind)Kernel32.GetDriveType(root);
        if (driveType is DriveTypeKind.NoRootDir or DriveTypeKind.Unknown) return false;

        if (!QueryVolumeInformation(root, out var fsName, out var serial)) return false;

        long free = 0, total = 0;
        if (Kernel32.GetDiskFreeSpaceEx(root, out var freeAvail, out var totalBytes, out var totalFree))
        {
            free = (long)freeAvail;
            total = (long)totalBytes;
        }
        else
        {
            free = total = 0;
        }

        var supportsReparse = FileSystemSupportsReparsePoints(fsName);

        info = new VolumeInfo(
            RootPath: root,
            DriveType: driveType,
            FileSystemName: fsName,
            SerialNumber: serial,
            FreeBytesAvailable: free,
            TotalBytes: total,
            SupportsReparsePoints: supportsReparse,
            SupportsHardLinks: supportsReparse);

        return true;
    }

    /// <summary>
    /// 文件系统是否支持重解析点（Junction / Symlink 的前提）。
    /// 需求第 8 条：NTFS 或 ReFS 才算合格目标，FAT32/exFAT 硬拒绝。
    /// </summary>
    public static bool FileSystemSupportsReparsePoints(string fileSystemName)
    {
        if (string.IsNullOrWhiteSpace(fileSystemName)) return false;
        return fileSystemName.Equals("NTFS", StringComparison.OrdinalIgnoreCase)
               // TODO(verify)-1: ReFS 对 IO_REPARSE_TAG_MOUNT_POINT 的支持面随系统版本变化，
            // 当前按"支持"处理，编码阶段用真实 ReFS 卷实测后再决定是否降级为"仅符号链接"。
               || fileSystemName.Equals("ReFS", StringComparison.OrdinalIgnoreCase);
    }

    private static unsafe string? QueryVolumePathName(string plainPath)
    {
        var buffer = new char[1024];
        fixed (char* p = buffer)
        {
            if (!Kernel32.GetVolumePathName(plainPath, p, (uint)buffer.Length))
            {
                return null;
            }
        }
        var len = Array.IndexOf(buffer, '\0');
        if (len < 0) len = buffer.Length;
        return len == 0 ? null : new string(buffer, 0, len);
    }

    private static unsafe bool QueryVolumeInformation(string root, out string fileSystemName, out uint serial)
    {
        fileSystemName = string.Empty;
        serial = 0;

        var volumeName = new char[261];
        var fsName = new char[261];
        fixed (char* pRoot = root)
        fixed (char* pVol = volumeName)
        fixed (char* pFs = fsName)
        {
            var ok = Kernel32.GetVolumeInformation(
                pRoot, pVol, (uint)volumeName.Length, out serial, out _, out _, pFs, (uint)fsName.Length);
            if (!ok) return false;
        }

        var len = Array.IndexOf(fsName, '\0');
        if (len < 0) len = fsName.Length;
        fileSystemName = len == 0 ? string.Empty : new string(fsName, 0, len);
        return true;
    }
}
