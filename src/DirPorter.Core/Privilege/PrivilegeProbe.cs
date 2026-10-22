using System.Runtime.InteropServices;
using DirPorter.Core.FileSystem;

namespace DirPorter.Core.Privilege;

/// <summary>
/// 权限能力探测接口。抽象出来以便在无管理员权限的机器上做单元测试。
/// 探测结果在启动时执行一次并缓存（需求约束 2）。
/// </summary>
public interface IPrivilegeProbe
{
    /// <summary>当前进程令牌是否已提升（UAC 已同意）。</summary>
    bool IsElevated { get; }

    /// <summary>令牌中是否存在并启用了 SeCreateSymbolicLinkPrivilege。</summary>
    bool HasSeCreateSymbolicLinkPrivilege { get; }

    /// <summary>令牌中是否存在并启用了 SeSecurityPrivilege（读写 SACL 审计信息）。</summary>
    bool HasSeSecurityPrivilege { get; }

    /// <summary>令牌中是否存在并启用了 SeRestorePrivilege（写入"不属于自己"的所有者）。</summary>
    bool HasSeRestorePrivilege { get; }

    /// <summary>系统是否已开启开发者模式（注册表 AllowDevelopmentWithoutDevLicense）。</summary>
    bool IsDeveloperModeEnabled { get; }

    /// <summary>能否创建符号链接（管理员 或 开发者模式）。</summary>
    bool CanCreateSymbolicLink { get; }

    /// <summary>当前环境的可读描述，用于日志与 UI 提示。</summary>
    string Describe();
}

/// <summary>基于 Win32 的权限探测实现（结果缓存，线程安全）。</summary>
public sealed class Win32PrivilegeProbe : IPrivilegeProbe
{
    private readonly Lazy<bool> _elevated;
    private readonly Lazy<bool> _sePrivilege;
    private readonly Lazy<bool> _developerMode;
    private readonly Lazy<bool> _seSecurity;
    private readonly Lazy<bool> _seRestore;

    public Win32PrivilegeProbe()
    {
        _elevated = new Lazy<bool>(static () => QueryElevation(), LazyThreadSafetyMode.ExecutionAndPublication);
        _sePrivilege = new Lazy<bool>(static () => QueryTokenPrivilege(Advapi32.SeCreateSymbolicLink), LazyThreadSafetyMode.ExecutionAndPublication);
        _developerMode = new Lazy<bool>(static () => QueryDeveloperMode(), LazyThreadSafetyMode.ExecutionAndPublication);
        _seSecurity = new Lazy<bool>(static () => QueryTokenPrivilege(Advapi32.SeSecurity), LazyThreadSafetyMode.ExecutionAndPublication);
        _seRestore = new Lazy<bool>(static () => QueryTokenPrivilege(Advapi32.SeRestore), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public static Win32PrivilegeProbe Current { get; } = new();

    public bool IsElevated => _elevated.Value;
    public bool HasSeCreateSymbolicLinkPrivilege => _sePrivilege.Value;
    public bool IsDeveloperModeEnabled => _developerMode.Value;
    public bool HasSeSecurityPrivilege => _seSecurity.Value;
    public bool HasSeRestorePrivilege => _seRestore.Value;

    public bool CanCreateSymbolicLink
        => IsElevated && HasSeCreateSymbolicLinkPrivilege || IsDeveloperModeEnabled;

    /// <summary>当前环境的可读描述，用于日志与 UI 提示。</summary>
    public string Describe()
        => $"Elevated={IsElevated}, SeCreateSymbolicLinkPrivilege={HasSeCreateSymbolicLinkPrivilege}, "
           + $"SeSecurityPrivilege={HasSeSecurityPrivilege}, SeRestorePrivilege={HasSeRestorePrivilege}, "
           + $"DeveloperMode={IsDeveloperModeEnabled}";

    private static bool QueryElevation()
    {
        if (!Advapi32.OpenProcessToken(Kernel32.GetCurrentProcess(), Advapi32.TokenQuery, out var token))
            return false;

        var buffer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            if (!Advapi32.GetTokenInformation(token, Advapi32.TokenElevation, buffer, sizeof(int), out _))
                return false;
            return Marshal.ReadInt32(buffer) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Kernel32.CloseHandle(token);
        }
    }

    private static bool QueryTokenPrivilege(string privilegeName)
    {
        if (!Advapi32.LookupPrivilegeValue(null, privilegeName, out var target))
            return false;
        if (!Advapi32.OpenProcessToken(Kernel32.GetCurrentProcess(), Advapi32.TokenQuery, out var token))
            return false;

        try
        {
            // 第一次调用取所需长度
            Advapi32.GetTokenInformation(token, Advapi32.TokenPrivileges, IntPtr.Zero, 0, out var needed);
            if (needed == 0) return false;

            var buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!Advapi32.GetTokenInformation(token, Advapi32.TokenPrivileges, buffer, needed, out _))
                    return false;

                var count = Marshal.ReadInt32(buffer);
                var elementSize = Marshal.SizeOf<Advapi32.LuidAndAttributes>();
                for (var i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<Advapi32.LuidAndAttributes>(buffer + sizeof(int) + i * elementSize);
                    if (item.Luid.Equals(target) && (item.Attributes & Advapi32.SePrivilegeEnabled) != 0)
                        return true;
                }

                return false;
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

    private static bool QueryDeveloperMode()
    {
        var buffer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            uint size = sizeof(int);
            var status = Advapi32.RegGetValue(
                Advapi32.HkeyLocalMachine,
                Advapi32.AppModelUnlockKey,
                Advapi32.AllowDevelopmentWithoutDevLicense,
                Advapi32.RrfRtRegDword,
                out _,
                buffer,
                ref size);

            if (status != Kernel32.ErrorSuccess) return false;
            return Marshal.ReadInt32(buffer) != 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
