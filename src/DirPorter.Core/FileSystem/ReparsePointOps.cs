using DirPorter.Core.Diagnostics;

namespace DirPorter.Core.FileSystem;

/// <summary>重解析点检视的最小结果。</summary>
public readonly record struct ReparseTagInfo(bool PathExists, bool IsReparsePoint, bool IsDirectory, uint Tag)
{
    public const uint TagMountPoint = 0xA0000003;
    public const uint TagSymlink = 0xA000000C;
}

/// <summary>
/// 重解析点底层原语（FileSystem 层）。
///
/// 分层约定：
/// - **本文件 = 原语**：句柄打开策略、GET/SET/DELETE 重解析点、安全删除链接本体；
/// - `Links/` = 语义：Junction / Symlink / HardLink 的构造、检视、能力判定。
///
/// 安全关键代码只允许存在一份实现：所有删除链接本体的路径（Unlink、回滚、`.migbak` 清理）
/// 都必须经过 <see cref="RemoveLinkBody"/>。
/// </summary>
public static class ReparsePointOps
{
    /// <summary>MAXIMUM_REPARSE_DATA_BUFFER_SIZE。</summary>
    public const int MaximumReparseDataBufferSize = 16384;

    /// <summary>
    /// 以"不跟随重解析点"的方式打开句柄。目录需要 FILE_FLAG_BACKUP_SEMANTICS。
    /// </summary>
    public static IntPtr OpenLinkHandle(string extendedPath, uint desiredAccess)
        => Kernel32.CreateFile(
            extendedPath,
            desiredAccess,
            Kernel32.FileShareRead | Kernel32.FileShareWrite | Kernel32.FileShareDelete,
            IntPtr.Zero,
            Kernel32.OpenExisting,
            Kernel32.FileFlagOpenReparsePoint | Kernel32.FileFlagBackupSemantics,
            IntPtr.Zero);

    /// <summary>读取重解析点原始数据；非重解析点或失败返回 null。</summary>
    public static byte[]? ReadRaw(string extendedPath)
    {
        var handle = OpenLinkHandle(extendedPath, Kernel32.FileReadAttributes);
        if (handle == Kernel32.InvalidHandleValue) return null;

        try
        {
            var buffer = new byte[MaximumReparseDataBufferSize];
            var outPtr = System.Runtime.InteropServices.Marshal.AllocHGlobal(buffer.Length);
            try
            {
                var ok = Kernel32.DeviceIoControl(
                    handle, Kernel32.FsctlGetReparsePoint,
                    IntPtr.Zero, 0, outPtr, (uint)buffer.Length, out var returned, IntPtr.Zero);
                if (!ok || returned == 0) return null;

                System.Runtime.InteropServices.Marshal.Copy(outPtr, buffer, 0, (int)returned);
                return buffer;
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(outPtr);
            }
        }
        finally
        {
            Kernel32.CloseHandle(handle);
        }
    }

    /// <summary>写入重解析点数据。句柄必须以 FILE_FLAG_OPEN_REPARSE_POINT 打开。</summary>
    public static void SetRaw(IntPtr handle, byte[] reparseBuffer, string operation)
    {
        var inPtr = System.Runtime.InteropServices.Marshal.AllocHGlobal(reparseBuffer.Length);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(reparseBuffer, 0, inPtr, reparseBuffer.Length);
            var ok = Kernel32.DeviceIoControl(
                handle, Kernel32.FsctlSetReparsePoint,
                inPtr, (uint)reparseBuffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero);
            if (!ok)
            {
                var code = Win32Error.Last;
                throw new DirPorterNativeException(operation, code, $"{operation} 失败：{Win32Error.Describe(code)}");
            }
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(inPtr);
        }
    }

    /// <summary>仅剥离重解析点标签（不删除对象本体）。</summary>
    public static void DeleteTag(IntPtr handle, uint tag, string operation)
    {
        var input = new byte[8];
        BitConverter.TryWriteBytes(input.AsSpan(0, 4), tag);
        BitConverter.TryWriteBytes(input.AsSpan(4, 2), (ushort)0);
        BitConverter.TryWriteBytes(input.AsSpan(6, 2), (ushort)0);

        var inPtr = System.Runtime.InteropServices.Marshal.AllocHGlobal(input.Length);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(input, 0, inPtr, input.Length);
            var ok = Kernel32.DeviceIoControl(
                handle, Kernel32.FsctlDeleteReparsePoint,
                inPtr, (uint)input.Length, IntPtr.Zero, 0, out _, IntPtr.Zero);
            if (!ok)
            {
                var code = Win32Error.Last;
                throw new DirPorterNativeException(operation, code, $"{operation} 失败：{Win32Error.Describe(code)}");
            }
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(inPtr);
        }
    }

    /// <summary>读取路径的重解析点标签信息。</summary>
    public static ReparseTagInfo Inspect(string extendedPath)
    {
        var attributes = Kernel32.GetFileAttributes(extendedPath);
        if (attributes == Kernel32.InvalidFileAttributes) return default;

        var isReparse = (attributes & Kernel32.FileAttributeReparsePoint) != 0;
        var isDirectory = (attributes & Kernel32.FileAttributeDirectory) != 0;
        var tag = 0u;

        if (isReparse)
        {
            var raw = ReadRaw(extendedPath);
            if (raw is { Length: >= 4 }) tag = BitConverter.ToUInt32(raw.AsSpan(0, 4));
        }

        return new ReparseTagInfo(true, isReparse, isDirectory, tag);
    }

    /// <summary>
    /// **只删除链接本体**（全项目仅此一份实现）。
    ///
    /// ⚠️ 最高优先级安全红线（需求「Unlink」小节）：
    /// 绝不递归删除。递归删除会顺着重解析点进入目标目录，把用户的真实数据全部删光。
    ///
    /// 物理保证：
    /// 1. 句柄以 <c>FILE_FLAG_OPEN_REPARSE_POINT</c> 打开 —— 拿到的是链接本体，不是它的目标；
    /// 2. 先 <c>FSCTL_DELETE_REPARSE_POINT</c> 剥离标签 —— 只改目录项元数据，不触碰任何数据；
    /// 3. 剥离后对象是**空的**普通目录/空文件，再 <c>RemoveDirectory</c> / <c>DeleteFile</c>：
    ///    <c>RemoveDirectory</c> 对非空目录必然返回 ERROR_DIR_NOT_EMPTY，"误删数据"在物理上不可能发生；
    /// 4. 剥离标签失败时回退到 <c>RemoveDirectory</c>/<c>DeleteFile</c>（同样不递归）。
    ///
    /// 注意：需求原文写的是"打开句柄后调用 RemoveDirectory"，但 RemoveDirectory 只接受路径、
    /// 不接受句柄，二者无法组合；此处采用等价且更严格的实现，意图完全一致。
    ///
    /// 此步失败时的回滚动作：若标签已剥离但空壳未删掉，会残留一个空目录/空文件，
    /// 由调用方记录并在报告中如实标注（绝不静默）。
    /// </summary>
    public static void RemoveLinkBody(string extendedPath, bool isDirectory, uint reparseTag, string operation)
    {
        // 实测：FSCTL_DELETE_REPARSE_POINT 仅用 DELETE 权限会返回 ERROR_ACCESS_DENIED(5)，必须带写权限。
        var handle = OpenLinkHandle(
            extendedPath,
            Kernel32.GenericWrite | Kernel32.Delete | Kernel32.FileReadAttributes | Kernel32.FileWriteAttributes);

        var tagStripped = false;
        var stripError = 0;

        if (handle == Kernel32.InvalidHandleValue)
        {
            stripError = Win32Error.Last;
        }
        else
        {
            try
            {
                DeleteTag(handle, reparseTag, operation);
                tagStripped = true;
            }
            catch (DirPorterNativeException ex)
            {
                stripError = ex.Win32Code;
            }
            finally
            {
                Kernel32.CloseHandle(handle);
            }
        }

        if (!tagStripped)
        {
            // 回退路径：RemoveDirectory 是"只删链接本体"的文档化安全操作，
            // 对非空真实目录必然失败，因此同样不可能递归。
            System.Diagnostics.Debug.WriteLine(
                $"[DirPorter] FSCTL_DELETE_REPARSE_POINT 失败（{Win32Error.Describe(stripError)}），回退为 RemoveDirectory/DeleteFile。");

            var fallbackOk = isDirectory ? Kernel32.RemoveDirectory(extendedPath) : Kernel32.DeleteFile(extendedPath);
            if (fallbackOk) return;

            var fallbackError = Win32Error.Last;
            if (fallbackError is Kernel32.ErrorFileNotFound or Kernel32.ErrorPathNotFound) return;

            throw new DirPorterNativeException(
                operation,
                fallbackError,
                $"{operation} 失败：剥离重解析点标签失败（{Win32Error.Describe(stripError)}），"
                + $"回退删除也失败（{Win32Error.Describe(fallbackError)}）。");
        }

        var removed = isDirectory ? Kernel32.RemoveDirectory(extendedPath) : Kernel32.DeleteFile(extendedPath);
        if (removed) return;

        var code = Win32Error.Last;
        if (code is Kernel32.ErrorFileNotFound or Kernel32.ErrorPathNotFound) return;

        throw new DirPorterNativeException(
            operation, code,
            $"{operation}：剥离标签后删除{(isDirectory ? "空目录" : "空文件")}壳失败。{Win32Error.Describe(code)}");
    }
}
