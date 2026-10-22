using System.Diagnostics;

namespace DirPorter.App.Services;

/// <summary>
/// 系统还原点（需求：迁移前可选创建，失败不阻塞主流程但要提示）。
///
/// 说明：<c>Checkpoint-Computer</c> 在 Windows 上受两条限制 ——
/// 需要管理员权限，且系统还原被组策略/系统保护关闭时不可用。
/// 因此这里把"失败原因"如实返回给调用方，由 UI 提示，绝不假装成功。
/// 另外 Windows 默认有"24 小时内只能创建一个还原点"的节流（SystemRestorePointCreationFrequency），
/// 该限制也会体现为命令失败，属正常现象。
/// </summary>
internal static class SystemRestorePoint
{
    public readonly record struct Result(bool Created, string Message);

    public static async Task<Result> TryCreateAsync(string description, CancellationToken cancellationToken = default)
    {
        var script =
            "$ErrorActionPreference='Stop'; " +
            $"Checkpoint-Computer -Description '{Escape(description)}' -RestorePointType 'MODIFY_SETTINGS'";

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("powershell.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };

            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-NonInteractive");
            process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
            process.StartInfo.ArgumentList.Add("Bypass");
            process.StartInfo.ArgumentList.Add("-Command");
            process.StartInfo.ArgumentList.Add(script);

            process.Start();

            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode == 0)
                return new Result(true, description);

            var reason = string.IsNullOrWhiteSpace(stderr) ? $"退出码 {process.ExitCode}" : stderr.Trim();
            return new Result(false, reason);
        }
        catch (Exception ex)
        {
            return new Result(false, ex.Message);
        }
    }

    /// <summary>单引号转义，避免描述文本破坏 PowerShell 字符串。</summary>
    private static string Escape(string text) => text.Replace("'", "''", StringComparison.Ordinal);
}
