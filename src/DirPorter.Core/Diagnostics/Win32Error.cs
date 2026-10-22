using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DirPorter.Core.Diagnostics;

/// <summary>
/// Win32 错误码工具。需求硬要求：日志必须同时给出错误码与系统消息文本。
/// 系统文本使用 <see cref="Win32Exception"/> 读取（内部即 FormatMessageW），
/// 避免自行 P/Invoke FormatMessage 带来的编码与本地化问题。
/// </summary>
public static class Win32Error
{
    /// <summary>最近一次 Win32 错误码。仅在 SetLastError=true 的调用之后立即使用时有效。</summary>
    public static int Last => Marshal.GetLastWin32Error();

    /// <summary>把错误码格式化为 "Win32Code=0x00000005 (5) Access is denied."。</summary>
    public static string Describe(int code)
    {
        if (code == 0) return "Win32Code=0x00000000 (0) The operation completed successfully.";
        var text = SafeMessage(code);
        return $"Win32Code=0x{code:X8} ({code}) {text}";
    }

    /// <summary>取系统消息文本；无对应文本时返回 Unknown error。</summary>
    public static string SafeMessage(int code)
    {
        try
        {
            // Win32Exception 内部调用 FormatMessageW，并按系统 UI 语言返回文本。
            var msg = new Win32Exception(code).Message;
            return string.IsNullOrWhiteSpace(msg) ? $"Unknown error (0x{code:X8})" : msg.TrimEnd('\r', '\n', '.', ' ');
        }
        catch
        {
            return $"Unknown error (0x{code:X8})";
        }
    }

    /// <summary>构造带错误码的异常，消息格式统一。</summary>
    public static DirPorterNativeException Failure(string operation, int code)
        => new(code, $"{operation} 失败：{Describe(code)}");
}

/// <summary>
/// 原生调用失败异常。保留错误码供上层判断（如 1314 = 需要提权）。
/// </summary>
public sealed class DirPorterNativeException : Exception
{
    public int Win32Code { get; }
    public string Operation { get; }

    public DirPorterNativeException(int win32Code, string message) : base(message)
    {
        Win32Code = win32Code;
        Operation = string.Empty;
    }

    public DirPorterNativeException(string operation, int win32Code, string message) : base(message)
    {
        Win32Code = win32Code;
        Operation = operation;
    }
}
