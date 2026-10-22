using System.Runtime.InteropServices;

namespace DirPorter.Core.FileSystem;

/// <summary>
/// kernel32 原生调用声明。全部使用 LibraryImport（.NET 8 源生 P/Invoke）。
/// 缓冲区一律使用指针，避免自动编组的隐藏拷贝与长度歧义。
/// </summary>
internal static partial class Kernel32
{
    public static readonly IntPtr InvalidHandleValue = new(-1);
    public const uint InvalidFileAttributes = 0xFFFFFFFF;

    // ---- dwDesiredAccess ----
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint Delete = 0x00010000;
    public const uint FileReadAttributes = 0x0080;
    public const uint FileWriteAttributes = 0x0100;
    public const uint FileListDirectory = 0x0001;
    public const uint FileAddFile = 0x0002;
    public const uint FileAddSubdirectory = 0x0004;
    public const uint FileDeleteChild = 0x0040;
    public const uint FileTraverse = 0x0020;
    public const uint ReadControl = 0x00020000;
    public const uint WriteDac = 0x00040000;

    // ---- dwShareMode ----
    public const uint FileShareRead = 0x00000001;
    public const uint FileShareWrite = 0x00000002;
    public const uint FileShareDelete = 0x00000004;

    // ---- dwCreationDisposition ----
    public const uint CreateNew = 1;
    public const uint CreateAlways = 2;
    public const uint OpenExisting = 3;
    public const uint OpenAlways = 4;

    // ---- dwFlagsAndAttributes ----
    public const uint FileFlagBackupSemantics = 0x02000000;
    public const uint FileFlagOpenReparsePoint = 0x00200000;
    public const uint FileFlagWriteThrough = 0x80000000;

    // ---- 文件属性 ----
    public const uint FileAttributeReadOnly = 0x00000001;
    public const uint FileAttributeHidden = 0x00000002;
    public const uint FileAttributeSystem = 0x00000004;
    public const uint FileAttributeDirectory = 0x00000010;
    public const uint FileAttributeArchive = 0x00000020;
    public const uint FileAttributeReparsePoint = 0x00000400;
    public const uint FileAttributeCompressed = 0x00000800;
    public const uint FileAttributeNotContentIndexed = 0x00002000;
    public const uint FileAttributeTemporary = 0x00000100;
    public const uint FileAttributeNormal = 0x00000080;

    /// <summary>可通过 SetFileAttributes 设置的属性位（压缩/加密/稀疏必须走各自的 API，不在其中）。</summary>
    public const uint SettableFileAttributes =
        FileAttributeReadOnly | FileAttributeHidden | FileAttributeSystem
        | FileAttributeArchive | FileAttributeNotContentIndexed | FileAttributeTemporary;
    public const uint FileAttributeEncrypted = 0x00004000;
    /// <summary>OneDrive 等云占位文件：数据未在本地，访问会触发回调下载。</summary>
    public const uint FileAttributeRecallOnDataAccess = 0x00400000;
    /// <summary>打开即触发回调下载的占位文件。</summary>
    public const uint FileAttributeRecallOnOpen = 0x00040000;
    public const uint FileAttributeSparseFile = 0x00000200;

    // ---- CreateSymbolicLink 标志 ----
    public const uint SymbolicLinkFlagDirectory = 0x00000001;
    public const uint SymbolicLinkFlagAllowUnprivilegedCreate = 0x00000002;

    // ---- 驱动器类型 ----
    public const uint DriveUnknown = 0;
    public const uint DriveNoRootDir = 1;
    public const uint DriveRemovable = 2;
    public const uint DriveFixed = 3;
    public const uint DriveRemote = 4;
    public const uint DriveCdRom = 5;
    public const uint DriveRamDisk = 6;

    // ---- MoveFileEx 标志 ----
    public const uint MoveFileReplaceExisting = 0x00000001;
    public const uint MoveFileCopyAllowed = 0x00000002;
    public const uint MoveFileWriteThrough = 0x00000008;

    // ---- 常用错误码（只在代码中按语义使用，禁止魔法数字）----
    public const int ErrorSuccess = 0;
    public const int ErrorFileNotFound = 2;
    public const int ErrorPathNotFound = 3;
    public const int ErrorAccessDenied = 5;
    public const int ErrorInvalidHandle = 6;
    public const int ErrorInvalidParameter = 87;
    public const int ErrorInsufficientBuffer = 122;
    public const int ErrorDirectoryNotEmpty = 145;
    public const int ErrorAlreadyExists = 183;

    /// <summary>ERROR_MORE_DATA：FSCTL_QUERY_ALLOCATED_RANGES 输出缓冲区不够，需带上次结果继续查询。</summary>
    public const int ErrorMoreData = 234;
    public const int ErrorFilenameExcedRange = 206;
    public const int ErrorPrivilegeNotHeld = 1314;

    /// <summary>AdjustTokenPrivileges 返回 TRUE 但并非所有特权都被分配（必须显式检查）。</summary>
    public const int ErrorNotAllAssigned = 1300;
    public const int ErrorNotAReparsePoint = 4390;

    /// <summary>
    /// CTL_CODE 宏的等价实现，用于自文档化 FSCTL 常量。
    /// CTL_CODE(DeviceType, Function, Method, Access) = (DeviceType&lt;&lt;16) | (Access&lt;&lt;14) | (Function&lt;&lt;2) | Method
    /// </summary>
    public static uint CtlCode(uint deviceType, uint function, uint method, uint access)
        => (deviceType << 16) | (access << 14) | (function << 2) | method;

    private const uint FileDeviceFileSystem = 0x00000009;
    private const uint MethodBuffered = 0;
    private const uint FileAnyAccess = 0;

    // 重解析点 IOCTL（winioctl.h：函数号 41 / 42 / 43，METHOD_BUFFERED，FILE_ANY_ACCESS）
    public static readonly uint FsctlSetReparsePoint = CtlCode(FileDeviceFileSystem, 41, MethodBuffered, FileAnyAccess);
    public static readonly uint FsctlGetReparsePoint = CtlCode(FileDeviceFileSystem, 42, MethodBuffered, FileAnyAccess);
    public static readonly uint FsctlDeleteReparsePoint = CtlCode(FileDeviceFileSystem, 43, MethodBuffered, FileAnyAccess);

    // ============================ 目录遍历 ============================

    /// <summary>FindExInfoBasic：不填充 cAlternateFileName，比 FindExInfoStandard 快约 30%。</summary>
    public const uint FindExInfoBasic = 1;

    /// <summary>FindExSearchNameMatch。注意：此模式不支持 8.3 短名匹配。</summary>
    public const uint FindExSearchNameMatch = 0;

    /// <summary>FIND_FIRST_EX_LARGE_FETCH：批量取回目录项，显著减少系统调用。</summary>
    public const uint FindFirstExLargeFetch = 0x00000002;

    /// <summary>FindStreamInfoStandard。</summary>
    public const uint FindStreamInfoStandard = 0;

    /// <summary>FindFirstStreamW 在"没有额外数据流"时返回 ERROR_HANDLE_EOF。</summary>
    public const int ErrorHandleEof = 38;

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr FindFirstFileEx(
        string lpFileName,
        uint fInfoLevelId,
        out Win32FindData lpFindFileData,
        uint fSearchOp,
        IntPtr lpSearchFilter,
        uint dwAdditionalFlags);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextFileW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FindNextFile(IntPtr hFindFile, out Win32FindData lpFindFileData);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FindClose(IntPtr hFindFile);

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstStreamW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr FindFirstStream(string lpFileName, uint infoLevel, out Win32FindStreamData lpFindStreamData, uint dwFlags);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextStreamW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FindNextStream(IntPtr hFindStream, out Win32FindStreamData lpFindStreamData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal unsafe struct Win32FindData
    {
        public uint dwFileAttributes;
        public FileTime ftCreationTime;
        public FileTime ftLastAccessTime;
        public FileTime ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        public fixed char cFileName[260];
        public fixed char cAlternateFileName[14];
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal unsafe struct Win32FindStreamData
    {
        public long StreamSize;
        public fixed char cStreamName[296]; // MAX_PATH + 36
    }

    /// <summary>从 WIN32_FIND_DATAW 中取出文件名（cFileName 为定长 NUL 结尾缓冲区）。</summary>
    internal static unsafe string GetFileName(in Win32FindData data)
    {
        // 定长缓冲区位于本地结构体副本上，本身已固定（栈上），不能再套 fixed。
        var local = data;
        return new string(local.cFileName);
    }

    /// <summary>从 WIN32_FIND_STREAM_DATA 中取出数据流名。</summary>
    internal static unsafe string GetStreamName(in Win32FindStreamData data)
    {
        var local = data;
        return new string(local.cStreamName);
    }

    public static long ToFileSize(in Win32FindData data) => ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow;

    // ============================ 文件与目录 ============================

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateDirectoryW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateDirectory(string lpPathName, IntPtr lpSecurityAttributes);

    [LibraryImport("kernel32.dll", EntryPoint = "RemoveDirectoryW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RemoveDirectory(string lpPathName);

    [LibraryImport("kernel32.dll", EntryPoint = "DeleteFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteFile(string lpFileName);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileAttributesW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint GetFileAttributes(string lpFileName);

    [LibraryImport("kernel32.dll", EntryPoint = "SetFileAttributesW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetFileAttributes(string lpFileName, uint dwFileAttributes);

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool MoveFileEx(string lpExistingFileName, string lpNewFileName, uint dwFlags);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint GetCurrentProcessId();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandle(IntPtr hFile, out ByHandleFileInformation lpFileInformation);

    // ============================ 链接创建 ============================

    [LibraryImport("kernel32.dll", EntryPoint = "CreateSymbolicLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateSymbolicLink(string lpSymlinkFileName, string lpTargetFileName, uint dwFlags);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    // ============================ 卷信息 ============================

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool GetVolumePathName(string lpszFileName, char* lpszVolumePathName, uint cchBufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool GetVolumeInformation(
        char* lpRootPathName,
        char* lpVolumeNameBuffer,
        uint nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        char* lpFileSystemNameBuffer,
        uint nFileSystemNameSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDriveTypeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint GetDriveType(string lpRootPathName);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetDiskFreeSpaceEx(string lpDirectoryName, out ulong lpFreeBytesAvailable, out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);

    [LibraryImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint GetCompressedFileSize(string lpFileName, out uint lpFileSizeHigh);

    // ============================ 复制引擎 B（自研）============================

    // CopyFileExW 的进度回调返回值
    public const uint ProgressContinue = 0;
    public const uint ProgressCancel = 1;
    public const uint ProgressStop = 2;
    public const uint ProgressQuiet = 3;

    // 回调原因
    public const uint CallbackChunkFinished = 0;
    public const uint CallbackStreamSwitch = 1;

    // CopyFileExW 标志位
    public const uint CopyFileFailIfExists = 0x00000001;
    public const uint CopyFileRestartable = 0x00000002;
    public const uint CopyFileAllowDecryptedDestination = 0x00000008;
    public const uint CopyFileRequestSecurityPrivileges = 0x00002000;
    public const uint CopyFileNoBuffering = 0x00001000;
    public const uint CopyFileNoOffload = 0x00040000;

    /// <summary>
    /// CopyFileExW 的进度回调。
    ///
    /// ⚠️ 签名要点：4 个 LARGE_INTEGER **按值**传递（x64/x86 都是 8 字节），
    /// 因此用 <see cref="long"/> 映射是正确的；这是该 API 最常见的 P/Invoke 陷阱来源。
    /// 返回 <see cref="ProgressStop"/> 会**保留已复制的部分文件**（非原子，调用方必须清理），
    /// 返回 <see cref="ProgressCancel"/> 会删除部分文件副本。我们统一用 Cancel 语义。
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate uint CopyProgressRoutine(
        long totalFileSize,
        long totalBytesTransferred,
        long streamSize,
        long streamBytesTransferred,
        uint dwStreamNumber,
        uint dwCallbackReason,
        IntPtr hSourceFile,
        IntPtr hDestinationFile,
        IntPtr lpData);

    /// <summary>
    /// 复制单个文件（数据）。**不复制安全描述符**（这是该 API 的文档化行为）——
    /// DACL/所有者由 <c>SecurityInfoOps</c> 单独搬运。
    /// </summary>
    [DllImport("kernel32.dll", EntryPoint = "CopyFileExW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CopyFileEx(
        string lpExistingFileName,
        string lpNewFileName,
        CopyProgressRoutine? lpProgressRoutine,
        IntPtr lpData,
        ref int pbCancel,
        uint dwCopyFlags);

    /// <summary>设置文件/目录的三个时间戳。传 <see cref="IntPtr.Zero"/> 表示"不改这一项"。</summary>
    [DllImport("kernel32.dll", EntryPoint = "SetFileTime", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetFileTime(IntPtr hFile, IntPtr lpCreationTime, IntPtr lpLastAccessTime, IntPtr lpLastWriteTime);

    [DllImport("kernel32.dll", EntryPoint = "LocalFree", SetLastError = true)]
    public static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>同步读。使用不安全指针以避免对大缓冲区的额外复制。</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "ReadFile", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool ReadFile(
        IntPtr hFile, byte* lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "WriteFile", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool WriteFile(
        IntPtr hFile, byte* lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

    /// <summary>把当前文件指针处设为文件末尾（用于预分配）。</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetEndOfFile(IntPtr hFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetFilePointerEx(IntPtr hFile, long liDistanceToMove, out long lpNewFilePointer, uint dwMoveMethod);

    /// <summary>FILE_BEGIN。</summary>
    public const uint FileBegin = 0;

    // ============================ 稀疏文件控制码 ============================

    /// <summary>FSCTL_SET_SPARSE：把文件标记/取消标记为稀疏。</summary>
    public const uint FsctlSetSparse = 0x000900C4;

    /// <summary>FSCTL_QUERY_ALLOCATED_RANGES：查询某区间内**实际占用磁盘**的范围。</summary>
    public const uint FsctlQueryAllocatedRanges = 0x000940CF;

    /// <summary>FSCTL_SET_ZERO_DATA：把某区间标记为零（稀疏文件里表现为"不占空间"）。</summary>
    public const uint FsctlSetZeroData = 0x000980C8;

    /// <summary>FSCTL_SET_SPARSE 的输入缓冲区。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct FileSetSparseBuffer
    {
        public byte SetSparse;
    }

    /// <summary>FILE_ALLOCATED_RANGE_BUFFER：既是查询输入，也是查询输出的元素。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct FileAllocatedRangeBuffer
    {
        public long FileOffset;
        public long Length;
    }

    /// <summary>FILE_ZERO_DATA_INFORMATION。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct FileZeroDataInformation
    {
        public long FileOffset;
        public long BeyondFinalZero;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileTime
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;

        public static FileTime FromTicks(long ticks) => new()
        {
            dwLowDateTime = (uint)(ticks & 0xFFFFFFFF),
            dwHighDateTime = (uint)((ticks >> 32) & 0xFFFFFFFF),
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ByHandleFileInformation
    {
        public uint dwFileAttributes;
        public FileTime ftCreationTime;
        public FileTime ftLastAccessTime;
        public FileTime ftLastWriteTime;
        public uint dwVolumeSerialNumber;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint nNumberOfLinks;
        public uint nFileIndexHigh;
        public uint nFileIndexLow;
    }
}
