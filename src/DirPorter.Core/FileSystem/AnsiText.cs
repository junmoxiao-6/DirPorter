using System.Runtime.InteropServices;
using System.Text;

namespace DirPorter.Core.FileSystem;

/// <summary>
/// 系统 ANSI 代码页文本解码。
///
/// 为什么需要它（**本轮实测发现**）：robocopy 的 <c>/LOG:</c> 文件是**单字节 ANSI 编码**，
/// 不是 UTF-16 —— <c>/UNICODE</c> 只改变控制台输出，不改变日志文件。
/// 用 <c>Encoding.Unicode</c> 去读会把每个 ASCII 字符拼成 CJK 码点，
/// 导致**文件行、中文错误行全部解析不出来**（表现为"已复制 0 个文件"、进度恒为 0）。
///
/// 实现说明：.NET Core 默认不带 legacy code page，
/// 因此不引入 <c>System.Text.Encoding.CodePages</c> 依赖，直接用 <c>MultiByteToWideChar</c>
/// 以系统 ANSI 代码页（<c>CP_ACP = 0</c>）转换 —— 这正是 Windows CRT 在文件 I/O 中使用的编码。
/// </summary>
internal static class AnsiText
{
    private const uint CpAcp = 0;

    /// <summary>解码字节；自动识别 UTF-16LE / UTF-8 BOM，否则按系统 ANSI 代码页。</summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length == 0) return string.Empty;

        // BOM 优先（未来若有工具写 UTF-16/UTF-8 日志也能正确读）
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        return DecodeAnsi(bytes);
    }

    /// <summary>按系统 ANSI 代码页解码。</summary>
    public static string DecodeAnsi(byte[] bytes)
    {
        if (bytes.Length == 0) return string.Empty;

        var needed = MultiByteToWideChar(CpAcp, 0, bytes, bytes.Length, null, 0);
        if (needed <= 0) return string.Empty;

        var buffer = new char[needed];
        var written = MultiByteToWideChar(CpAcp, 0, bytes, bytes.Length, buffer, needed);
        return written <= 0 ? string.Empty : new string(buffer, 0, written);
    }

    /// <summary>按行读取（日志可能正在被写，故共享读 + 允许删除共享）。</summary>
    public static IEnumerable<string> ReadLines(string path)
    {
        byte[] bytes;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var memory = new MemoryStream())
        {
            stream.CopyTo(memory);
            bytes = memory.ToArray();
        }

        var text = Decode(bytes);
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;

            var end = i;
            if (end > start && text[end - 1] == '\r') end--;

            yield return text[start..end];
            start = i + 1;
        }

        if (start < text.Length) yield return text[start..];
    }

    /// <summary>
    /// ⚠️ 必须显式声明 <c>CharSet.Unicode</c>：DllImport 默认按 ANSI 编组，
    /// 会把 <c>char[]</c> 当成 1 字节的 ANSI 数组写入，结果是**一串 NUL**（本机实测踩到过）。
    /// 同时 <c>byte[]</c> 仍按字节数组传入，不受 CharSet 影响。
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int MultiByteToWideChar(
        uint codePage, uint dwFlags, byte[] lpMultiByteStr, int cbMultiByte, char[]? lpWideCharStr, int cchWideChar);
}
