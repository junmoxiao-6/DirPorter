using DirPorter.Core.Copy;
using Xunit;

namespace DirPorter.Tests;

/// <summary>
/// R12.17：robocopy 的安全信息开关必须受 <c>--no-security</c> 控制。
///
/// 事故：<c>BuildArguments</c> 里写死 <c>useCopyAll ? /COPYALL : /COPY:DATSO</c>，
/// 完全忽略 <see cref="CopyOptions.CopySecurity"/>。于是在非提权/受限令牌环境里，
/// robocopy 仍逐文件复制所有者信息 → 每个文件报「错误 5（0x00000005）正在将 NTFS 安全复制到目标文件中」，
/// 退出码 8、整批回滚。实测迁移 985 MB / 3002 个文件时 1376 项失败、数据被全部清回滚。
/// </summary>
public class R24_CopySecuritySwitchTests
{
    private static CopyOptions Options(bool copySecurity) => CopyOptions.Default with { CopySecurity = copySecurity };

    [Fact]
    public void 关闭安全信息时必须使用_COPY_DAT_而不是_DATSO()
    {
        var args = RobocopyCopier.BuildArguments(@"C:\src", @"F:\dst", Options(copySecurity: false), @"C:\t\rc.log");

        Assert.Contains("/COPY:DAT", args);
        Assert.DoesNotContain("/COPY:DATSO", args);
        Assert.DoesNotContain("/COPYALL", args);
    }

    [Fact]
    public void 开启安全信息且非提权时使用_DATSO()
    {
        var args = RobocopyCopier.BuildArguments(@"C:\src", @"F:\dst", Options(copySecurity: true), @"C:\t\rc.log", useCopyAll: false);

        Assert.Contains("/COPY:DATSO", args);
        Assert.DoesNotContain("/COPY:DAT", args);
    }

    [Fact]
    public void 开启安全信息且提权时使用_COPYALL()
    {
        var args = RobocopyCopier.BuildArguments(@"C:\src", @"F:\dst", Options(copySecurity: true), @"C:\t\rc.log", useCopyAll: true);

        Assert.Contains("/COPYALL", args);
    }

    [Fact]
    public void 关闭安全信息优先于提权_不允许回落到_COPYALL()
    {
        // 用户显式 --no-security 时，即使管理员也不该去碰 SACL。
        var args = RobocopyCopier.BuildArguments(@"C:\src", @"F:\dst", Options(copySecurity: false), @"C:\t\rc.log", useCopyAll: true);

        Assert.Contains("/COPY:DAT", args);
        Assert.DoesNotContain("/COPYALL", args);
    }

    [Fact]
    public void 必带_XJ_防空链接递归()
    {
        var args = RobocopyCopier.BuildArguments(@"C:\src", @"F:\dst", Options(copySecurity: false), @"C:\t\rc.log");

        Assert.Contains("/XJ", args);
    }

    [Theory]
    [InlineData("2026/11/07 17:30:03 错误 5 (0x00000005) 正在将 NTFS 安全复制到目标文件中 C:\\a.dll")]
    [InlineData("ERROR 5 (0x00000005) Copying NTFS Security to Destination File C:\\a.dll")]
    [InlineData("Access is denied. ERROR 5")]
    public void 识别安全信息复制失败(string reason)
    {
        Assert.True(RobocopyCopier.IsSecurityCopyFailure(new CopyFailure(@"C:\a.dll", reason, null)));
    }

    [Theory]
    [InlineData("错误 3 (0x00000003) 正在创建目标文件")]
    [InlineData("文件被占用")]
    public void 普通失败不误判为安全信息问题(string reason)
    {
        Assert.False(RobocopyCopier.IsSecurityCopyFailure(new CopyFailure(@"C:\a.dll", reason, null)));
    }

    [Fact]
    public void 干跑文案必须反映_no_security()
    {
        var text = RobocopyCopier.DescribeCopySwitch(isElevated: false, copySecurity: false);

        Assert.Contains("/COPY:DAT", text);
        Assert.DoesNotContain("DATSO", text);
        Assert.DoesNotContain("COPYALL", text);
    }
}
