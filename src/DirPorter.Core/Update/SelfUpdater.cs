using System.Diagnostics;
using System.Text;

namespace DirPorter.Core.Update;

/// <summary>
/// 自更新执行器（R12.12）。
///
/// 为什么不用批处理脚本（0.3.1 及以前的做法）：
/// <list type="bullet">
/// <item>cmd 窗口会被安全软件盯上，企业策略里常被拦；</item>
/// <item>脚本里没法做"大小校验 / 失败重试次数精确控制 / 日志"；</item>
/// <item>脚本只能"复制覆盖"，复制到一半失败会把 exe 弄坏，程序再也起不来。</item>
/// </list>
///
/// 现在的做法：主程序把**自己**复制到临时目录，带着参数启动这份副本，
/// 由它等主进程退出 → 校验 → **原子替换**（先写 .new，再 MoveFileEx 替换）→ 重启。
/// 单文件 exe 自包含，不需要任何额外文件或脚本。
///
/// 调用方式（内部命令，不在 --help 里宣传）：
/// <code>
/// dirporter apply-update --new &lt;新版本exe&gt; --target &lt;当前exe&gt; --pid &lt;主进程PID&gt; --relaunch
/// </code>
/// </summary>
public static class SelfUpdater
{
    public const string CommandName = "apply-update";

    private const int MaxWaitExitMs = 120_000;
    private const int MaxReplaceMs = 120_000;
    private const int RetryDelayMs = 300;

    public static bool IsInvocation(string[] args) =>
        args.Length > 0 && string.Equals(args[0], CommandName, StringComparison.OrdinalIgnoreCase);

    /// <summary>执行更新。返回 0 = 成功（含已重启），1 = 替换失败，2 = 参数错误。</summary>
    public static int Run(string[] args)
    {
        if (!TryParse(args, out var newExe, out var targetExe, out var pid, out var relaunch))
        {
            Log("参数错误：" + string.Join(' ', args));
            return 2;
        }

        Log($"开始新版={newExe} 目标={targetExe} 主进程={pid}");

        if (!File.Exists(newExe))
        {
            Log("找不到下载好的新版本文件，放弃。");
            return 1;
        }

        if (pid > 0) WaitExit(pid, MaxWaitExitMs);

        var replaced = ReplaceAtomically(newExe, targetExe, MaxReplaceMs);

        if (replaced)
        {
            Log("替换成功。");

            if (relaunch)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(targetExe) { UseShellExecute = true });
                    Log("已重启新版本。");
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
                {
                    Log("重启失败（请手动打开）：" + ex.Message);
                }
            }

            TryDelete(newExe);
        }
        else
        {
            Log("替换失败：目标目录可能不可写（需要管理员），或文件一直被占用。");
            TryDelete(targetExe + ".new");
        }

        return replaced ? 0 : 1;
    }

    /// <summary>
    /// 原子替换：先把新文件写到同目录的 <c>.new</c>，校验大小后再一次性改名覆盖。
    /// 这样任何一步失败都不会破坏现有 exe —— "更新失败变砖"是这类功能最不能接受的结果。
    /// </summary>
    private static bool ReplaceAtomically(string newExe, string targetExe, int timeoutMs)
    {
        var staged = targetExe + ".new";
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            try
            {
                File.Copy(newExe, staged, overwrite: true);

                var expected = new FileInfo(newExe).Length;
                var actual = new FileInfo(staged).Length;

                if (actual != expected)
                {
                    Log($"暂存文件大小不符（期望 {expected}，实际 {actual}），重试。");
                    TryDelete(staged);
                    Thread.Sleep(RetryDelayMs);
                    continue;
                }

                // MoveFileEx + REPLACE_EXISTING：同卷内是原子操作，目标要么是旧的、要么是新的。
                File.Move(staged, targetExe, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(RetryDelayMs);
            }
        }

        return false;
    }

    private static void WaitExit(int pid, int timeoutMs)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            Log("等待主进程退出…");

            if (!process.WaitForExit(timeoutMs))
            {
                Log("等待超时，仍然继续尝试替换。");
            }
        }
        catch (ArgumentException)
        {
            // 进程已经不在了 —— 正常情况。
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static bool TryParse(string[] args, out string newExe, out string targetExe, out int pid, out bool relaunch)
    {
        newExe = string.Empty;
        targetExe = string.Empty;
        pid = 0;
        relaunch = false;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--new" when i + 1 < args.Length:
                    newExe = args[++i];
                    break;
                case "--target" when i + 1 < args.Length:
                    targetExe = args[++i];
                    break;
                case "--pid" when i + 1 < args.Length:
                    int.TryParse(args[++i], out pid);
                    break;
                case "--relaunch":
                    relaunch = true;
                    break;
            }
        }

        return newExe.Length > 0 && targetExe.Length > 0;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>更新日志：写到数据根（申请权限失败就静默放弃，不影响更新本身）。</summary>
    private static void Log(string message)
    {
        try
        {
            var root = Environment.GetEnvironmentVariable("DIRPORTER_DATA_ROOT");
            if (string.IsNullOrWhiteSpace(root))
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DirPorter");

            var dir = Path.Combine(root, "logs");
            Directory.CreateDirectory(dir);

            var line = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                .Append("  ")
                .Append(message)
                .AppendLine()
                .ToString();

            File.AppendAllText(Path.Combine(dir, "update.log"), line, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
