namespace DirPorter.Core.FileSystem;

/// <summary>
/// 按句柄读取文件信息（硬链接数、文件索引、卷序列号）。
/// 放在 FileSystem 层，供 Links 与 TreeProbe 共用，避免重复实现。
/// 只读取属性，不写任何内容。
/// </summary>
internal static class FileInformation
{
    public static Kernel32.ByHandleFileInformation? Query(string extendedPath)
    {
        var handle = Kernel32.CreateFile(
            extendedPath,
            Kernel32.FileReadAttributes,
            Kernel32.FileShareRead | Kernel32.FileShareWrite | Kernel32.FileShareDelete,
            IntPtr.Zero,
            Kernel32.OpenExisting,
            Kernel32.FileFlagBackupSemantics,
            IntPtr.Zero);

        if (handle == Kernel32.InvalidHandleValue) return null;

        try
        {
            return Kernel32.GetFileInformationByHandle(handle, out var info) ? info : null;
        }
        finally
        {
            Kernel32.CloseHandle(handle);
        }
    }
}
