using DirPorter.App.Services;
using DirPorter.Core.Update;
using Xunit;

namespace DirPorter.Tests;

/// <summary>
/// R12.12：自更新执行器。
/// 要点：① 只有带 apply-update 参数才进入更新模式（不能误伤普通 CLI 调用）；
/// ② GitHub 源能同时给出 API 地址与 owner/repo（后者用于拼"无限流的 latest.json"地址）。
/// </summary>
public class R20_SelfUpdaterTests
{
    [Theory]
    [InlineData(new[] { "apply-update" }, true)]
    [InlineData(new[] { "APPLY-UPDATE", "--new", "a.exe" }, true)]
    [InlineData(new[] { "move", "--source", "C://a" }, false)]
    [InlineData(new[] { "selfcheck" }, false)]
    public void 只有_apply_update_参数才进入自更新模式(string[] args, bool expected)
    {
        Assert.Equal(expected, SelfUpdater.IsInvocation(args));
    }

    [Fact]
    public void 缺参数时不进入自更新()
    {
        Assert.False(SelfUpdater.IsInvocation(Array.Empty<string>()));
    }

    [Fact]
    public void GitHub_源同时给出_API_与仓库标识()
    {
        Assert.True(UpdateService.TryParseGitHub("github:junmoxiao-6/DirPorter", out var api, out var slug));

        Assert.Equal("https://api.github.com/repos/junmoxiao-6/DirPorter/releases/latest", api);
        Assert.Equal("junmoxiao-6/DirPorter", slug);
    }

    [Fact]
    public void 客户端首选地址是_Release_固定下载路径()
    {
        // 与 UpdateService.CheckAsync 里的拼接保持一致：
        // 固定路径不经过 API，因此没有 60 次/小时限流。
        Assert.True(UpdateService.TryParseGitHub("https://github.com/junmoxiao-6/DirPorter", out _, out var slug));

        var manifestUrl = $"https://github.com/{slug}/releases/latest/download/latest.json";
        Assert.Equal("https://github.com/junmoxiao-6/DirPorter/releases/latest/download/latest.json", manifestUrl);
    }
}
