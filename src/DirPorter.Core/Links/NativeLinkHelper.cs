using DirPorter.Core.FileSystem;

namespace DirPorter.Core.Links;

/// <summary>
/// 链接相关的原生操作封装（**薄委托层**）。
///
/// 分层约定：真正的原语实现在 <see cref="ReparsePointOps"/>（FileSystem 层），
/// 本类只保留 Links 语义层需要的最小入口，避免同一段安全关键代码出现两份实现。
/// </summary>
internal static class NativeLinkHelper
{
    /// <summary>取文件属性；路径不存在返回 null。</summary>
    public static uint? TryGetAttributes(string extendedPath)
    {
        var attrs = Kernel32.GetFileAttributes(extendedPath);
        return attrs == Kernel32.InvalidFileAttributes ? null : attrs;
    }

    /// <summary>路径是否存在（对悬空链接同样返回 true，因为链接本体存在）。</summary>
    public static bool Exists(string extendedPath) => TryGetAttributes(extendedPath) is not null;

    public static bool IsDirectory(uint attributes) => (attributes & Kernel32.FileAttributeDirectory) != 0;

    public static bool IsReparsePoint(uint attributes) => (attributes & Kernel32.FileAttributeReparsePoint) != 0;

    /// <summary>以"不跟随重解析点"的方式打开句柄。</summary>
    public static IntPtr OpenReparsePointHandle(string extendedPath, uint desiredAccess)
        => ReparsePointOps.OpenLinkHandle(extendedPath, desiredAccess);

    /// <summary>读取重解析点数据；非重解析点或读取失败返回 null。</summary>
    public static byte[]? ReadReparseData(string extendedPath) => ReparsePointOps.ReadRaw(extendedPath);

    /// <summary>写入重解析点数据。</summary>
    public static void SetReparseData(IntPtr handle, byte[] reparseBuffer, string operation)
        => ReparsePointOps.SetRaw(handle, reparseBuffer, operation);

    /// <summary>仅剥离重解析点标签。</summary>
    public static void DeleteReparseData(IntPtr handle, uint tag, string operation)
        => ReparsePointOps.DeleteTag(handle, tag, operation);

    /// <summary>
    /// 只删除链接本体。⚠️ 安全红线实现位于 <see cref="ReparsePointOps.RemoveLinkBody"/>。
    /// 此步失败时的回滚动作：若标签已剥离而空壳未删掉，会残留空目录/空文件，由调用方记录并提示。
    /// </summary>
    public static void RemoveLinkBody(string extendedPath, bool isDirectory, uint reparseTag, string operation)
        => ReparsePointOps.RemoveLinkBody(extendedPath, isDirectory, reparseTag, operation);

    /// <summary>查询文件信息（硬链接数、文件索引、卷序列号）。</summary>
    public static Kernel32.ByHandleFileInformation? QueryFileInformation(string extendedPath)
        => FileInformation.Query(extendedPath);

    /// <summary>把缓冲区前若干字节转为十六进制预览，便于 inspect 输出与问题定位。</summary>
    public static string? HexPreview(byte[]? buffer, int maxBytes = 64)
    {
        if (buffer is null || buffer.Length == 0) return null;
        var count = Math.Min(maxBytes, buffer.Length);
        return Convert.ToHexString(buffer.AsSpan(0, count)).Chunk(2).Select(static c => new string(c)).Aggregate((a, b) => a + " " + b);
    }
}
