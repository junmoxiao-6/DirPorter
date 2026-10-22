using System.ComponentModel;
using System.Diagnostics;
using DirPorter.Core.Privilege;

namespace DirPorter.App.Services;

/// <summary>一次提权启动的结果。</summary>
/// <param name="Started">是否成功启动了提权进程。</param>
/// <param name="Canceled">是否被用户在 UAC 上取消（ERROR_CANCELLED=1223）。这与"失败"必须区分：取消不是故障。</param>
/// <param name="ExitCode">提权进程的退出码（未等待时为 0，无意义）。</param>
/// <param name="Error">失败原因（成功与取消时为 null）。</param>
public sealed record ElevationLaunch(bool Started, bool Canceled, int ExitCode, string? Error);

/// <summary>按需提权（"以管理员身份重启"）。</summary>
public interface IElevationService
{
    /// <summary>当前进程是否已在提权状态。</summary>
    bool IsElevated { get; }

    /// <summary>当前进程的令牌提升类型。</summary>
    TokenElevationKind TokenKind { get; }

    /// <summary>是否值得向用户提供"以管理员身份重启"（已提权就没必要了）。</summary>
    bool CanElevate { get; }

    /// <summary>
    /// 提权后**是否需要输入管理员凭据**。
    /// 拆分令牌（Limited）只需点"是"；Default 分两种情况，若当前是标准用户则会要求输入管理员账号密码。
    /// </summary>
    bool WillPromptForCredentials { get; }

    /// <summary>把当前 exe 以管理员身份重新启动（会弹 UAC）。</summary>
    ElevationLaunch LaunchElevated(string arguments, bool waitForExit = false);

    /// <summary>当前权限受限的事实描述（用于界面解释"为什么用不了"）。</summary>
    string DescribeState(IPrivilegeProbe privileges);
}

/// <summary>
/// 提权服务。
///
/// 实现要点：
/// <list type="bullet">
/// <item>必须用 <c>UseShellExecute = true</c> + <c>Verb = "runas"</c>；用 <c>ProcessStartInfo.Arguments</c> 传**字符串**
/// （而不是 ArgumentList）—— <c>runas</c> 走的是 ShellExecute，不认 ArgumentList。</item>
/// <item>用户取消 UAC → <see cref="Win32Exception"/> 且 <c>NativeErrorCode == 1223</c>，必须与真正的失败分开报告。</item>
/// <item>提权得到的进程对象可以 <c>WaitForExit</c> 并读到退出码，因此命令行加 <c>--elevate</c> 时能原样转发退出码。</item>
/// </list>
/// </summary>
public sealed class ElevationService : IElevationService
{
    private const int ErrorCancelled = 1223;

    public static ElevationService Current { get; } = new();

    public ElevationService() => TokenKind = ElevationTypeProbe.Current;

    public bool IsElevated => Core.Privilege.Win32PrivilegeProbe.Current.IsElevated;

    public TokenElevationKind TokenKind { get; }

    public bool CanElevate => !IsElevated;

    public bool WillPromptForCredentials => !IsElevated && TokenKind == TokenElevationKind.Default;

    public ElevationLaunch LaunchElevated(string arguments, bool waitForExit = false)
    {
        var exe = Environment.ProcessPath;

        if (string.IsNullOrWhiteSpace(exe))
            return new ElevationLaunch(false, false, 0, "无法确定当前可执行文件路径（Environment.ProcessPath 为空）。");

        var startInfo = new ProcessStartInfo(exe)
        {
            // runas 是 ShellExecute 的动作，必须开 UseShellExecute；否则 Verb 被忽略、且不会弹 UAC。
            UseShellExecute = true,
            Verb = "runas",
            Arguments = arguments,
            WorkingDirectory = Environment.CurrentDirectory,
        };

        try
        {
            var process = Process.Start(startInfo);
            if (process is null)
                return new ElevationLaunch(false, false, 0, "系统未返回进程对象（可能被安全软件拦截）。");

            if (!waitForExit)
            {
                process.Dispose();
                return new ElevationLaunch(true, false, 0, null);
            }

            process.WaitForExit();
            var exitCode = process.ExitCode;
            process.Dispose();
            return new ElevationLaunch(true, false, exitCode, null);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            // 用户点了"否"：这是正常选择，不是错误。
            return new ElevationLaunch(false, true, 0, null);
        }
        catch (Exception ex)
        {
            return new ElevationLaunch(false, false, 0, ex.Message);
        }
    }

    public string DescribeState(IPrivilegeProbe privileges)
    {
        var kind = TokenKind switch
        {
            TokenElevationKind.Full => "Full（已提权）",
            TokenElevationKind.Limited => "Limited（管理员受限令牌，可通过 UAC 同意提权）",
            TokenElevationKind.Default => "Default（UAC 关闭，或当前为标准用户）",
            _ => "Unknown（查询失败）",
        };

        return $"Elevated={privileges.IsElevated}, TokenElevationType={kind}, "
               + $"DeveloperMode={privileges.IsDeveloperModeEnabled}, Manifest=asInvoker";
    }
}
