using System.Text;

namespace DirPorter.Core.Links;

/// <summary>
/// 重解析点数据缓冲区的构造与解析。
///
/// 结构（REPARSE_DATA_BUFFER，winnt.h）：
/// <code>
///   ULONG  ReparseTag;          // 偏移 0
///   USHORT ReparseDataLength;   // 偏移 4（仅 union 部分长度，不含头部 8 字节）
///   USHORT Reserved;            // 偏移 6
///   union {
///     struct {                  // MOUNT_POINT，偏移 8 起
///       USHORT SubstituteNameOffset;  // 偏移 8，相对 PathBuffer 起点
///       USHORT SubstituteNameLength;  // 偏移 10
///       USHORT PrintNameOffset;       // 偏移 12
///       USHORT PrintNameLength;       // 偏移 14
///       WCHAR  PathBuffer[1];         // 偏移 16
///     };
///     struct {                  // SYMLINK，偏移 8 起
///       USHORT SubstituteNameOffset;  // 8
///       USHORT SubstituteNameLength;  // 10
///       USHORT PrintNameOffset;       // 12
///       USHORT PrintNameLength;       // 14
///       ULONG  Flags;                 // 16（0x1 = SYMLINK_FLAG_RELATIVE）
///       WCHAR  PathBuffer[1];         // 20
///     };
///   };
/// </code>
/// 长度单位均为字节，且不含字符串结尾的 NUL。
/// </summary>
public static class ReparseData
{
    public const uint TagMountPoint = 0xA0000003;
    public const uint TagSymlink = 0xA000000C;

    /// <summary>MAXIMUM_REPARSE_DATA_BUFFER_SIZE。用于 GET 时的输出缓冲区大小。</summary>
    public const int MaximumReparseDataBufferSize = 16384;

    /// <summary>MOUNT_POINT 固定字段起点（8 字节头部 + 8 字节字段）。</summary>
    public const int MountPointPathBufferOffset = 16;

    /// <summary>SYMLINK 固定字段起点（8 字节头部 + 12 字节字段）。</summary>
    public const int SymlinkPathBufferOffset = 20;

    /// <summary>SYMLINK_FLAG_RELATIVE。</summary>
    public const uint SymlinkFlagRelative = 0x00000001;

    /// <summary>替换名前缀（NT 对象管理器路径）。</summary>
    public const string NtPrefix = @"\??\";

    /// <summary>
    /// 构造 MOUNT_POINT_REPARSE_DATA_BUFFER（Junction）。
    /// </summary>
    /// <param name="substituteName">
    /// 替换名，必须是 NT 绝对路径，形如 <c>\??\C:\Target</c>。
    /// 卷根目标形如 <c>\??\C:\</c>（保留尾反斜杠）。
    /// </param>
    /// <param name="printName">打印名，普通 Win32 路径，用于资源管理器与 dir 展示。</param>
    public static byte[] BuildMountPoint(string substituteName, string printName)
    {
        ArgumentException.ThrowIfNullOrEmpty(substituteName);
        ArgumentException.ThrowIfNullOrEmpty(printName);

        var subBytes = Encoding.Unicode.GetBytes(substituteName);
        var printBytes = Encoding.Unicode.GetBytes(printName);

        // PathBuffer 布局：substitute + NUL + print + NUL
        var pathBuffer = new byte[subBytes.Length + 2 + printBytes.Length + 2];
        Buffer.BlockCopy(subBytes, 0, pathBuffer, 0, subBytes.Length);
        Buffer.BlockCopy(printBytes, 0, pathBuffer, subBytes.Length + 2, printBytes.Length);

        // ReparseDataLength = 8（四个 USHORT）+ PathBuffer 长度
        var reparseDataLength = 8 + pathBuffer.Length;
        var buffer = new byte[8 + reparseDataLength];

        BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), TagMountPoint);
        BitConverter.TryWriteBytes(buffer.AsSpan(4, 2), (ushort)reparseDataLength);
        BitConverter.TryWriteBytes(buffer.AsSpan(6, 2), (ushort)0); // Reserved

        var p = 8;
        BitConverter.TryWriteBytes(buffer.AsSpan(p + 0, 2), (ushort)0);                        // SubstituteNameOffset
        BitConverter.TryWriteBytes(buffer.AsSpan(p + 2, 2), (ushort)subBytes.Length);          // SubstituteNameLength
        BitConverter.TryWriteBytes(buffer.AsSpan(p + 4, 2), (ushort)(subBytes.Length + 2));    // PrintNameOffset
        BitConverter.TryWriteBytes(buffer.AsSpan(p + 6, 2), (ushort)printBytes.Length);        // PrintNameLength

        Buffer.BlockCopy(pathBuffer, 0, buffer, MountPointPathBufferOffset, pathBuffer.Length);
        return buffer;
    }

    /// <summary>
    /// 解析重解析点缓冲区。支持 MOUNT_POINT 与 SYMLINK 两种标签。
    /// 其它标签返回 false（例如 OneDrive 的占位符重解析点）。
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> buffer, out ReparseParseResult result)
    {
        result = default;

        if (buffer.Length < 8) return false;

        var tag = BitConverter.ToUInt32(buffer[..4]);
        var reparseDataLength = BitConverter.ToUInt16(buffer.Slice(4, 2));
        if (buffer.Length < 8 + reparseDataLength) return false;

        var isMountPoint = tag == TagMountPoint;
        var isSymlink = tag == TagSymlink;
        if (!isMountPoint && !isSymlink) return false;

        var fixedOffset = isMountPoint ? 8 : 8;
        var pathBufferOffset = isMountPoint ? MountPointPathBufferOffset : SymlinkPathBufferOffset;
        if (buffer.Length < pathBufferOffset) return false;

        var subOffset = BitConverter.ToUInt16(buffer.Slice(fixedOffset + 0, 2));
        var subLength = BitConverter.ToUInt16(buffer.Slice(fixedOffset + 2, 2));
        var printOffset = BitConverter.ToUInt16(buffer.Slice(fixedOffset + 4, 2));
        var printLength = BitConverter.ToUInt16(buffer.Slice(fixedOffset + 6, 2));
        var flags = isSymlink ? BitConverter.ToUInt32(buffer.Slice(fixedOffset + 8, 4)) : 0u;

        var pathBufferStart = pathBufferOffset;
        var substitute = ReadString(buffer, pathBufferStart + subOffset, subLength);
        var print = ReadString(buffer, pathBufferStart + printOffset, printLength);

        result = new ReparseParseResult(tag, substitute, print, isSymlink && (flags & SymlinkFlagRelative) != 0);
        return true;
    }

    /// <summary>
    /// 由普通 Win32 绝对路径构造 Junction 的替换名（<c>\??\C:\X</c>）。
    /// </summary>
    public static string ToSubstituteName(string plainAbsolutePath, bool isUnc = false)
    {
        var p = plainAbsolutePath.Replace('/', '\\');
        if (isUnc)
        {
            // \\server\share → \??\UNC\server\share
            return NtPrefix + "UNC" + p[1..];
        }
        return NtPrefix + p;
    }

    /// <summary>把替换名还原为普通 Win32 路径（去掉 <c>\??\</c> / <c>\??\UNC\</c>）。</summary>
    public static string FromSubstituteName(string substituteName)
    {
        if (string.IsNullOrEmpty(substituteName)) return substituteName;
        if (substituteName.StartsWith(NtPrefix + "UNC", StringComparison.OrdinalIgnoreCase))
            return @"\\" + substituteName[(NtPrefix.Length + 3 + 1)..];
        if (substituteName.StartsWith(NtPrefix, StringComparison.Ordinal))
            return substituteName[NtPrefix.Length..];
        return substituteName;
    }

    private static string ReadString(ReadOnlySpan<byte> buffer, int offset, int byteLength)
    {
        if (byteLength <= 0) return string.Empty;
        if (offset < 0 || offset + byteLength > buffer.Length) return string.Empty;
        return Encoding.Unicode.GetString(buffer.Slice(offset, byteLength));
    }
}

/// <summary>重解析点解析结果。</summary>
public readonly record struct ReparseParseResult(uint Tag, string SubstituteName, string PrintName, bool IsRelative);
