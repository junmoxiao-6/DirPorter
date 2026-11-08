using DirPorter.Core.Copy;
using Xunit;

namespace DirPorter.Tests;

/// <summary>
/// R12.18：安全信息档位的降级链。
///
/// 背景：R12.17 修好了 <c>--no-security</c> 无效的问题，但当时的降级是一步到位的
/// <c>/COPY:DATSO → /COPY:DAT</c> —— 直接丢掉整份安全描述符（DACL + 所有者）。
/// 实际上 DACL（谁能访问）才是用户关心的，所有者只是"归谁名下"。
/// 因此改为四级：<c>/COPYALL → /COPY:DATSO → /COPY:DATS → /COPY:DAT</c>，
/// 优先只放弃所有者、保住 DACL。
/// </summary>
public class R25_SecurityDegradeLadderTests
{
    private static CopyResult Failed(int exitCode, params string[] reasons) => new(
        CopyOutcome.Failed,
        exitCode,
        0, 0, 0,
        reasons.Length,
        reasons.Select(r => new CopyFailure(@"C:\a.dll", r, null)).ToArray(),
        null,
        TimeSpan.FromSeconds(1));

    private static CopyResult Ok() => new(
        CopyOutcome.Success, 1, 10, 0, 100, 0, Array.Empty<CopyFailure>(), null, TimeSpan.FromSeconds(1));

    [Fact]
    public void 降级顺序必须是_ALL_DATSO_DATS_DAT()
    {
        var mode = RobocopyCopier.SecurityMode.CopyAll;

        Assert.Equal(RobocopyCopier.SecurityMode.Datso, RobocopyCopier.NextDegrade(mode));
        mode = RobocopyCopier.NextDegrade(mode);
        Assert.Equal(RobocopyCopier.SecurityMode.Dats, RobocopyCopier.NextDegrade(mode));
        mode = RobocopyCopier.NextDegrade(mode);
        Assert.Equal(RobocopyCopier.SecurityMode.Dat, RobocopyCopier.NextDegrade(mode));
    }

    [Fact]
    public void 已到最低档不再降级()
    {
        Assert.Equal(RobocopyCopier.SecurityMode.Dat, RobocopyCopier.NextDegrade(RobocopyCopier.SecurityMode.Dat));
    }

    [Theory]
    [InlineData(RobocopyCopier.SecurityMode.CopyAll, "/COPYALL")]
    [InlineData(RobocopyCopier.SecurityMode.Datso, "/COPY:DATSO")]
    [InlineData(RobocopyCopier.SecurityMode.Dats, "/COPY:DATS")]
    [InlineData(RobocopyCopier.SecurityMode.Dat, "/COPY:DAT")]
    public void 档位映射到正确的开关(RobocopyCopier.SecurityMode mode, string expected)
    {
        Assert.Equal(expected, RobocopyCopier.SwitchOf(mode));
    }

    [Fact]
    public void DATS_档必须出现在参数里_且保住_DACL()
    {
        var args = RobocopyCopier.BuildArguments(
            @"C:\src", @"F:\dst", CopyOptions.Default, @"C:\t\rc.log", RobocopyCopier.SecurityMode.Dats);

        Assert.Contains("/COPY:DATS", args);
        Assert.DoesNotContain("/COPY:DAT", args);          // 不能退化成最低档
        Assert.DoesNotContain("/COPY:DATSO", args);
    }

    [Fact]
    public void 退出码16在_COPYALL_时要降级()
    {
        Assert.True(RobocopyCopier.ShouldDegrade(Failed(16, "(未定位) 严重错误"), RobocopyCopier.SecurityMode.CopyAll));
    }

    [Fact]
    public void 安全信息被拒在_DATSO_时要降级到_DATS()
    {
        var result = Failed(8, "错误 5 (0x00000005) 正在将 NTFS 安全复制到目标文件中 C:\\a.dll");

        Assert.True(RobocopyCopier.ShouldDegrade(result, RobocopyCopier.SecurityMode.Datso));
    }

    [Fact]
    public void 到最低档后不再降级_避免死循环()
    {
        var result = Failed(8, "错误 5 (0x00000005) 正在将 NTFS 安全复制到目标文件中 C:\\a.dll");

        Assert.False(RobocopyCopier.ShouldDegrade(result, RobocopyCopier.SecurityMode.Dat));
    }

    [Fact]
    public void 用户取消不触发降级()
    {
        var canceled = Failed(8, "错误 5") with { Outcome = CopyOutcome.Canceled };

        Assert.False(RobocopyCopier.ShouldDegrade(canceled, RobocopyCopier.SecurityMode.Datso));
    }

    [Fact]
    public void 成功结果不触发降级()
    {
        Assert.False(RobocopyCopier.ShouldDegrade(Ok(), RobocopyCopier.SecurityMode.Datso));
    }

    [Fact]
    public void 普通失败_如文件被占用_不触发降级()
    {
        var result = Failed(8, "错误 32 (0x00000020) 另一个程序正在使用此文件");

        Assert.False(RobocopyCopier.ShouldDegrade(result, RobocopyCopier.SecurityMode.Datso));
    }
}
