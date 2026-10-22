using DirPorter.App.Services;
using Xunit;

namespace DirPorter.Tests;

/// <summary>
/// R12.10：GitHub 作为更新源 + 备份保留策略接入 GUI。
/// 这里只做"纯逻辑 + 静态契约"验证；真实的删除动作不放进单测（会碰用户磁盘）。
/// </summary>
public class R19_GitHubFeedAndBackupPurgeTests
{
    private static string ReadXaml(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "DirPorter.App")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "DirPorter.App", relative));
    }

    [Theory]
    [InlineData("github:someone/DirPorter", "https://api.github.com/repos/someone/DirPorter/releases/latest")]
    [InlineData("GITHUB:someone/DirPorter", "https://api.github.com/repos/someone/DirPorter/releases/latest")]
    [InlineData("https://github.com/someone/DirPorter", "https://api.github.com/repos/someone/DirPorter/releases/latest")]
    [InlineData("https://github.com/someone/DirPorter.git", "https://api.github.com/repos/someone/DirPorter/releases/latest")]
    [InlineData("https://github.com/someone/DirPorter/releases/latest", "https://api.github.com/repos/someone/DirPorter/releases/latest")]
    public void GitHub_地址被正确解析为_API(string input, string expected)
    {
        Assert.True(UpdateService.TryParseGitHub(input, out var api));
        Assert.Equal(expected, api);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://example.com/latest.json")]
    [InlineData("github:onlyowner")]
    [InlineData("C:\\temp\\latest.json")]
    public void 非_GitHub_地址不会被误判(string input)
    {
        Assert.False(UpdateService.TryParseGitHub(input, out _));
    }

    [Fact]
    public void 设置页必须提供立即清理到期备份的入口()
    {
        var xaml = ReadXaml("Views/SettingsView.xaml");

        Assert.Contains("PurgeExpiredBackupsCommand", xaml, StringComparison.Ordinal);
    }
}
