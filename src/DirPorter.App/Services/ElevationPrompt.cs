using DirPorter.Core.Precheck;

namespace DirPorter.App.Services;

/// <summary>
/// 权限不足时的统一引导：**不只报错，而是给出"怎么解开"**。
///
/// 这里刻意做成一件事：把"权限问题"识别出来，然后提供一键提权重启。
/// 用户实测反馈"希望能以管理员权限执行"，而在此之前程序只会说"需要管理员权限"就结束了。
/// </summary>
internal static class ElevationPrompt
{
    private static readonly string[] PrivilegeSignals =
    {
        "PERMISSION", "DENIED", "PRIVILEGE", "ACCESS", "UNAUTHORIZED", "权限", "访问被拒绝",
    };

    /// <summary>预检的硬拒绝里是否包含"权限类"原因。</summary>
    public static bool LooksLikePrivilegeProblem(PrecheckReport report)
        => report.BlockingResults.Any(IsPrivilegeRelated);

    private static bool IsPrivilegeRelated(CheckResult result)
    {
        // Win32 5 = ACCESS_DENIED，1314 = ERROR_PRIVILEGE_NOT_HELD
        if (result.Win32 is not null
            && (result.Win32.Contains("(5)", StringComparison.Ordinal)
                || result.Win32.Contains("1314", StringComparison.Ordinal)))
        {
            return true;
        }

        var haystack = (result.Code + " " + result.Title + " " + result.Detail).ToUpperInvariant();
        return PrivilegeSignals.Any(s => haystack.Contains(s, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 询问并执行"以管理员身份重启"。返回 true 表示已启动提权实例（调用方应尽快退出当前实例）。
    /// </summary>
    public static bool Offer(AppHost host, IUserInteraction ui, string reason)
    {
        var elevation = host.Elevation;

        if (!elevation.CanElevate)
        {
            ui.Error(reason);
            return false;
        }

        var loc = LocalizationService.Instance;
        var body = reason + Environment.NewLine + Environment.NewLine + loc.Get("priv.needAdminBody");

        if (elevation.WillPromptForCredentials)
        {
            body += Environment.NewLine + Environment.NewLine
                    + "注意：当前不是管理员账户，UAC 会要求输入管理员账号与密码。";
        }

        if (!ui.Confirm(body, loc.Get("priv.needAdminTitle"))) return false;

        // 必须先释放单实例锁，否则提权实例会认为"已有实例"而直接退出。
        App.ReleaseSingleInstance();

        var launch = elevation.LaunchElevated(string.Empty);

        if (launch.Canceled)
        {
            ui.Error(loc.Get("priv.elevationCancelled"));
            return false;
        }

        if (!launch.Started)
        {
            ui.Error(string.Format(System.Globalization.CultureInfo.CurrentCulture,
                loc.Get("priv.elevationFailed"), launch.Error));
            return false;
        }

        return true;
    }
}
