using System.Runtime.InteropServices;
using DirPorter.Core.Diagnostics;
using DirPorter.Core.Privilege;

namespace DirPorter.Core.FileSystem;

/// <summary>安全信息复制能力（复制引擎 B 的能力声明由此驱动，必须如实）。</summary>
public sealed record SecurityCopyCapability(
    bool CanCopyDacl,
    bool CanCopyOwnerAndGroup,
    bool CanCopyAuditing,
    string Describe)
{
    /// <summary>是否存在降级（用于生成 SecurityNote）。</summary>
    public bool IsDegraded => !CanCopyOwnerAndGroup || !CanCopyAuditing;

    public string DegradedReason
    {
        get
        {
            if (!IsDegraded) return "完整：DACL + 所有者 + 组 + 审计 SACL。";

            var missing = new List<string>();
            if (!CanCopyOwnerAndGroup) missing.Add("所有者/组（需 SeRestorePrivilege，且该特权必须已启用）");
            if (!CanCopyAuditing) missing.Add("审计 SACL（需 SeSecurityPrivilege，且该特权必须已启用）");
            return "降级：仅复制 DACL；未复制 " + string.Join("、", missing) + "。";
        }
    }
}

/// <summary>单个对象的安全信息搬运结果。</summary>
public readonly record struct SecurityCopyOutcome(
    bool DaclCopied,
    bool OwnerCopied,
    bool AuditingCopied,
    bool ProtectionFlagApplied,
    int? Win32Code,
    string? DegradedReason)
{
    public static SecurityCopyOutcome Failed(int code, string reason)
        => new(false, false, false, false, code, reason);
}

/// <summary>
/// 安全描述符搬运（复制引擎 B 的元数据关键部分）。
///
/// 背景：<c>CopyFileEx</c> **不复制安全描述符**（文档化行为）——新文件默认继承目标目录的 ACL。
/// 因此要"像 robocopy /COPYALL 那样保留权限"，必须自己搬 DACL / 所有者 / SACL。
///
/// 能力边界（这就是"非管理员 vs 管理员"的分界线）：
/// <list type="table">
/// <item><term>DACL</term><description>需要目标对象 WRITE_DAC。创建者通常天然具备 → **非提权可做**。</description></item>
/// <item><term>所有者 / 组</term><description>需要 <c>SeRestorePrivilege</c> **已启用** → 需管理员。</description></item>
/// <item><term>审计 SACL</term><description>读和写都需要 <c>SeSecurityPrivilege</c> **已启用** → 需管理员。</description></item>
/// </list>
///
/// 另外会把源 DACL 的 <c>SE_DACL_PROTECTED</c>（禁止继承）标志一并搬运，
/// 否则"源上手工加的、禁止继承的 ACE"到目标侧会被继承规则冲掉 —— 这是权限漂移的常见来源。
/// </summary>
public static class SecurityInfoOps
{
    // SECURITY_DESCRIPTOR 固定布局（与位宽无关）：Revision(1) Sbz1(1) Control(2) Owner(4) Group(4) Sacl(4) Dacl(4)
    private const int ControlWordOffset = 2;
    private const ushort SeDaclProtected = 0x1000;
    private const ushort SeSaclProtected = 0x2000;

    private static readonly Lazy<SecurityCopyCapability> Cached = new(
        static () => Prepare(Privilege.Win32PrivilegeProbe.Current), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// 本进程的安全信息复制能力（进程内只探测/启用一次）。
    ///
    /// ⚠️ 会**修改进程令牌**（启用 SeRestore / SeSecurity）——这是必要的，
    /// 因为管理员令牌默认不启用这两个特权，不启用就写不了所有者/SACL。
    /// </summary>
    public static SecurityCopyCapability Current => Cached.Value;

    /// <summary>
    /// 按给定权限环境准备能力（供测试注入）。
    /// <paramref name="enablePrivilege"/> 是"启用特权"这个动作的注入点：
    /// 真实运行走 <see cref="TokenPrivilegeEnabler"/>（会改进程令牌），
    /// 测试可传入一个恒真委托来模拟已提权环境，而**不去动测试宿主的令牌**。
    /// </summary>
    public static SecurityCopyCapability Prepare(Privilege.IPrivilegeProbe privileges, Func<string, bool>? enablePrivilege = null)
    {
        enablePrivilege ??= static name => TokenPrivilegeEnabler.TryEnable(name).Enabled;

        // 非提权时连尝试都不必：这些特权不存在于受限令牌中。
        var owner = privileges.IsElevated && privileges.HasSeRestorePrivilege && enablePrivilege(Advapi32.SeRestore);
        var auditing = privileges.IsElevated && privileges.HasSeSecurityPrivilege && enablePrivilege(Advapi32.SeSecurity);

        var capability = new SecurityCopyCapability(
            CanCopyDacl: true,
            CanCopyOwnerAndGroup: owner,
            CanCopyAuditing: auditing,
            Describe: BuildDescribe(privileges, owner, auditing));

        return capability;
    }

    private static string BuildDescribe(Privilege.IPrivilegeProbe privileges, bool owner, bool auditing)
    {
        var parts = new List<string> { $"DACL=可复制" };
        parts.Add(owner ? "所有者/组=可复制" : "所有者/组=跳过（需管理员 + SeRestorePrivilege）");
        parts.Add(auditing ? "审计SACL=可复制" : "审计SACL=跳过（需管理员 + SeSecurityPrivilege）");
        parts.Add($"Elevated={privileges.IsElevated}");
        return string.Join("，", parts);
    }

    private static bool Enable(string privilegeName)
        => TokenPrivilegeEnabler.TryEnable(privilegeName).Enabled;

    /// <summary>当前进程的安全信息复制能力（含"允许写入 SACL/所有者"的真实探测）。</summary>
    public static SecurityCopyCapability Detect() => Current;

    /// <summary>
    /// 把 <paramref name="sourcePath"/> 的安全信息复制到 <paramref name="destinationPath"/>。
    /// 支持降级：能力不足时跳过对应部分，并把原因写进结果。
    /// </summary>
    public static SecurityCopyOutcome Copy(string sourcePath, string destinationPath, SecurityCopyCapability capability)
    {
        var source = PathNormalizer.NormalizeAbsolute(sourcePath);
        var destination = PathNormalizer.NormalizeAbsolute(destinationPath);

        var readFlags = Advapi32.OwnerSecurityInformation
                        | Advapi32.GroupSecurityInformation
                        | Advapi32.DaclSecurityInformation
                        | (capability.CanCopyAuditing ? Advapi32.SaclSecurityInformation : 0u);

        var status = Advapi32.GetNamedSecurityInfo(
            source, Advapi32.SeFileObject, readFlags,
            out var owner, out var group, out var dacl, out var sacl, out var descriptor);

        if (status != Kernel32.ErrorSuccess)
        {
            return SecurityCopyOutcome.Failed(status, $"读取源安全描述符失败：{Win32Error.Describe(status)}");
        }

        try
        {
            var setFlags = Advapi32.DaclSecurityInformation;
            var protectionApplied = false;

            if (descriptor != IntPtr.Zero)
            {
                var control = unchecked((ushort)Marshal.ReadInt16(descriptor, ControlWordOffset));

                if ((control & SeDaclProtected) != 0)
                {
                    setFlags |= Advapi32.ProtectedDaclSecurityInformation;
                    protectionApplied = true;
                }

                if (capability.CanCopyAuditing && (control & SeSaclProtected) != 0)
                {
                    setFlags |= Advapi32.ProtectedSaclSecurityInformation;
                }
            }

            var wantOwner = capability.CanCopyOwnerAndGroup && owner != IntPtr.Zero;
            if (wantOwner) setFlags |= Advapi32.OwnerSecurityInformation | Advapi32.GroupSecurityInformation;

            var wantSacl = capability.CanCopyAuditing && sacl != IntPtr.Zero;
            if (wantSacl) setFlags |= Advapi32.SaclSecurityInformation;

            var setStatus = Advapi32.SetNamedSecurityInfo(
                destination, Advapi32.SeFileObject, setFlags,
                wantOwner ? owner : IntPtr.Zero,
                wantOwner ? group : IntPtr.Zero,
                dacl,
                wantSacl ? sacl : IntPtr.Zero);

            if (setStatus == Kernel32.ErrorSuccess)
            {
                return new SecurityCopyOutcome(true, wantOwner, wantSacl, protectionApplied, null, capability.DegradedReason);
            }

            // 失败重试：退到"只写 DACL"。这一步能救回绝大多数场景
            // （例如目标对象的所有者/组不属于当前用户 → 1314，但 DACL 仍可写）。
            if (wantOwner || wantSacl)
            {
                var fallbackFlags = Advapi32.DaclSecurityInformation
                                    | (protectionApplied ? Advapi32.ProtectedDaclSecurityInformation : 0u);

                var fallback = Advapi32.SetNamedSecurityInfo(
                    destination, Advapi32.SeFileObject, fallbackFlags, IntPtr.Zero, IntPtr.Zero, dacl, IntPtr.Zero);

                if (fallback == Kernel32.ErrorSuccess)
                {
                    return new SecurityCopyOutcome(
                        true, false, false, protectionApplied, setStatus,
                        $"所有者/审计信息写入失败（{Win32Error.Describe(setStatus)}），已退化为仅复制 DACL。");
                }

                return SecurityCopyOutcome.Failed(fallback,
                    $"安全信息写入失败：完整写入 {Win32Error.Describe(setStatus)}；仅 DACL 也失败 {Win32Error.Describe(fallback)}。");
            }

            return SecurityCopyOutcome.Failed(setStatus, $"安全信息写入失败：{Win32Error.Describe(setStatus)}");
        }
        finally
        {
            if (descriptor != IntPtr.Zero) Kernel32.LocalFree(descriptor);
        }
    }
}
