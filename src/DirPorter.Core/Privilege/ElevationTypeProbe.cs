using System.Runtime.InteropServices;
using DirPorter.Core.FileSystem;

namespace DirPorter.Core.Privilege;

/// <summary>
/// 令牌提升类型（对应 Win32 <c>TOKEN_ELEVATION_TYPE</c>）。
/// </summary>
public enum TokenElevationKind
{
    /// <summary>查询失败或返回了未知值。</summary>
    Unknown = 0,

    /// <summary>UAC 关闭，或当前账户是标准用户（令牌未被拆分）。</summary>
    Default = 1,

    /// <summary>已提权（管理员完整令牌）。</summary>
    Full = 2,

    /// <summary>管理员账户的受限令牌（拆分令牌）。**这一种最值得关注：能通过 UAC 同意直接提权**。</summary>
    Limited = 3,
}

/// <summary>
/// 查询令牌提升类型。
///
/// 为什么要单独探测它（而不是只看"是否已提权"）：
/// <list type="bullet">
/// <item><see cref="TokenElevationKind.Limited"/> 说明当前是管理员账户的**受限令牌** —— 提权只需用户在 UAC 上点"是"，
/// 这是"以管理员身份重启"能够成功的**确定性依据**。</item>
/// <item><see cref="TokenElevationKind.Default"/> 说明要么 UAC 关闭，要么是标准用户 ——
/// 后者提权会弹出**凭据输入框**（需要知道管理员密码），所以不能向用户承诺"点一下就能提权"。</item>
/// </list>
/// 这两个分支的用户体验完全不同，必须在界面上如实区分，不能笼统地说"需要管理员权限"。
/// </summary>
public static class ElevationTypeProbe
{
    private static readonly Lazy<TokenElevationKind> Cached =
        new(Query, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>当前进程的令牌提升类型（进程内常量，缓存一次）。</summary>
    public static TokenElevationKind Current => Cached.Value;

    private static TokenElevationKind Query()
    {
        if (!Advapi32.OpenProcessToken(Kernel32.GetCurrentProcess(), Advapi32.TokenQuery, out var token))
            return TokenElevationKind.Unknown;

        var buffer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            if (!Advapi32.GetTokenInformation(token, Advapi32.TokenElevationType, buffer, sizeof(int), out _))
                return TokenElevationKind.Unknown;

            return Marshal.ReadInt32(buffer) switch
            {
                1 => TokenElevationKind.Default,
                2 => TokenElevationKind.Full,
                3 => TokenElevationKind.Limited,
                _ => TokenElevationKind.Unknown,
            };
        }
        catch
        {
            return TokenElevationKind.Unknown;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Kernel32.CloseHandle(token);
        }
    }
}
