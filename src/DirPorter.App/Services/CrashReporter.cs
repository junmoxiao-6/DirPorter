using System.IO;
using System.Text;
using DirPorter.Core.Config;
using DirPorter.Core.Diagnostics;

namespace DirPorter.App.Services;

/// <summary>
/// 崩溃兜底（需求「通用」：异常全局兜底并写崩溃日志）。
///
/// 日志同时记录：时间、来源、版本、进程/线程、OS/CLR、数据根目录、完整异常链（含内部异常）、
/// 若是 <see cref="DirPorterNativeException"/> 还单独给出 Win32 错误码与系统消息文本（需求硬要求）。
///
/// 写日志本身绝不允许再抛（否则兜底反而变成二次崩溃源），因此整体吞异常。
/// </summary>
internal static class CrashReporter
{
    /// <summary>最近一次写入的崩溃日志路径（供 UI 提示「日志已保存到 …」）。</summary>
    public static string? LastCrashLogPath { get; private set; }

    public static string? WriteCrashLog(string source, Exception exception)
    {
        try
        {
            var directory = Path.Combine(DirPorterPaths.DataRoot, "crashes");
            Directory.CreateDirectory(directory);

            var path = Path.Combine(
                directory,
                $"crash-{DateTime.Now:yyyyMMdd-HHmmss}-p{Environment.ProcessId}.log");

            File.WriteAllText(path, BuildReport(source, exception), new UTF8Encoding(true));
            LastCrashLogPath = path;
            return path;
        }
        catch
        {
            return null;
        }
    }

    public static string BuildReport(string source, Exception exception)
    {
        var sb = new StringBuilder();

        sb.AppendLine("=== DirPorter 崩溃日志 ===");
        sb.AppendLine($"时间：{DateTimeOffset.Now:O}");
        sb.AppendLine($"来源：{source}");
        sb.AppendLine($"版本：{typeof(CrashReporter).Assembly.GetName().Version}");
        sb.AppendLine($"进程：{Environment.ProcessId}  线程：{Environment.CurrentManagedThreadId}");
        sb.AppendLine($"OS：{Environment.OSVersion}  64 位进程：{Environment.Is64BitProcess}");
        sb.AppendLine($"CLR：{Environment.Version}");
        sb.AppendLine($"数据根目录：{DirPorterPaths.DataRoot}"
                      + (DirPorterPaths.IsFallback ? $"（已降级：{DirPorterPaths.FallbackReason}）" : string.Empty));
        sb.AppendLine();

        sb.AppendLine("--- 异常 ---");
        AppendException(sb, exception, depth: 0);

        sb.AppendLine();
        sb.AppendLine("--- 恢复指引 ---");
        sb.AppendLine("· 若崩溃发生在迁移过程中，先执行 `dirporter recover` 查看是否有中间态需要修复。");
        sb.AppendLine("· 源目录数据的安全性由「停车目录 + 台账 + 回滚」保障，不会因为进程崩溃而丢失。");
        sb.AppendLine("· 本文件仅用于定位问题，可安全分享（不含密码/密钥）。");

        return sb.ToString();
    }

    private static void AppendException(StringBuilder sb, Exception? exception, int depth)
    {
        if (exception is null) return;

        var indent = new string(' ', depth * 2);
        sb.AppendLine($"{indent}{exception.GetType().FullName}: {exception.Message}");

        // 原生失败：补出 Win32 错误码 + 系统消息文本（需求：两者必须都有）。
        if (exception is DirPorterNativeException native)
        {
            sb.AppendLine($"{indent}  Win32Code=0x{native.Win32Code:X8} ({native.Win32Code})"
                          + $"  {Win32Error.SafeMessage(native.Win32Code)}");
            if (!string.IsNullOrEmpty(native.Operation))
                sb.AppendLine($"{indent}  操作：{native.Operation}");
        }

        if (!string.IsNullOrWhiteSpace(exception.StackTrace))
            sb.AppendLine($"{indent}{exception.StackTrace}");

        if (exception.InnerException is not null)
        {
            sb.AppendLine($"{indent}--- 内部异常 ---");
            AppendException(sb, exception.InnerException, depth + 1);
        }

        if (exception is AggregateException aggregate)
        {
            var index = 0;
            foreach (var inner in aggregate.InnerExceptions)
            {
                sb.AppendLine($"{indent}--- 聚合异常 #{++index} ---");
                AppendException(sb, inner, depth + 1);
            }
        }
    }
}
