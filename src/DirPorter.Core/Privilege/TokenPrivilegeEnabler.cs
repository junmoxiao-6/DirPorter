using System.Runtime.InteropServices;
using DirPorter.Core.Diagnostics;
using DirPorter.Core.FileSystem;

namespace DirPorter.Core.Privilege;

/// <summary>启用特权的结果。</summary>
public readonly record struct PrivilegeEnableResult(
    bool Enabled,
    bool WasPresentInToken,
    int Error,
    string Message)
{
    public bool Degraded => !Enabled;
}

/// <summary>
/// 令牌特权启用器。
///
/// 为什么必须有它：Windows 管理员令牌里的 <c>SeRestorePrivilege</c> / <c>SeSecurityPrivilege</c>
/// 默认处于 **disabled** 状态。"令牌里有"不等于"已启用"，而 <c>SetNamedSecurityInfo</c>
/// 写所有者/SACL 要求特权**已启用**，否则报 1314。
///
/// 因此复制引擎 B 在搬运所有者和审计信息之前必须：
/// 1. 判定令牌是否持有该特权；
/// 2. 尝试启用；
/// 3. 启用失败 → 如实降级并写进结果说明，**绝不假装成功**。
///
/// 该流程与 robocopy 内部的自我提权一致（robocopy 也是自己 enable 再复制 SACL）。
/// </summary>
public static class TokenPrivilegeEnabler
{
    /// <summary>
    /// 尝试启用一个特权。<paramref name="privileges"/> 只用于"是否持有"的快速判定；
    /// 真正的启用动作走 <c>AdjustTokenPrivileges</c>。
    /// </summary>
    public static PrivilegeEnableResult TryEnable(string privilegeName)
    {
        if (!Advapi32.LookupPrivilegeValue(null, privilegeName, out var luid))
        {
            var lookupError = Win32Error.Last;
            return new PrivilegeEnableResult(false, false, lookupError,
                $"无法解析特权名 {privilegeName}：{Win32Error.Describe(lookupError)}");
        }

        if (!Advapi32.OpenProcessToken(
                Kernel32.GetCurrentProcess(),
                Advapi32.TokenQuery | Advapi32.TokenAdjustPrivileges,
                out var token))
        {
            var openError = Win32Error.Last;
            return new PrivilegeEnableResult(false, false, openError,
                $"无法打开进程令牌：{Win32Error.Describe(openError)}");
        }

        try
        {
            var state = new Advapi32.SingleTokenPrivileges
            {
                PrivilegeCount = 1,
                Privileges = new Advapi32.LuidAndAttributes { Luid = luid, Attributes = Advapi32.SePrivilegeEnabled },
            };

            var size = Marshal.SizeOf<Advapi32.SingleTokenPrivileges>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(state, buffer, fDeleteOld: false);

                if (!Advapi32.AdjustTokenPrivileges(token, false, buffer, (uint)size, IntPtr.Zero, IntPtr.Zero))
                {
                    var adjustError = Win32Error.Last;
                    return new PrivilegeEnableResult(false, false, adjustError,
                        $"启用 {privilegeName} 失败：{Win32Error.Describe(adjustError)}");
                }

                // ⚠️ AdjustTokenPrivileges 即使没全部生效也返回 TRUE，
                //    必须看 GetLastError 是否为 ERROR_NOT_ALL_ASSIGNED —— 这是经典陷阱。
                var last = Win32Error.Last;
                if (last == Kernel32.ErrorNotAllAssigned)
                {
                    return new PrivilegeEnableResult(false, false, last,
                        $"令牌不持有 {privilegeName}（ERROR_NOT_ALL_ASSIGNED），已按降级处理。");
                }

                return new PrivilegeEnableResult(true, true, Kernel32.ErrorSuccess,
                    $"已启用 {privilegeName}。");
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            Kernel32.CloseHandle(token);
        }
    }
}
