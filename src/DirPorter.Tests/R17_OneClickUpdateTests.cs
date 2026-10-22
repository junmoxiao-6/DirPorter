using System.Text.Json;
using DirPorter.App.Services;
using Xunit;

namespace DirPorter.Tests;

/// <summary>
/// R12.8：一键更新。
/// 要点：① 更新地址必须"内置"，用户不用填；② 一次点击完成全程；
/// ③ 支持本地清单（离线/内网分发），且仍做版本比较与字段校验。
/// </summary>
public class R17_OneClickUpdateTests
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

    [Fact]
    public void 更新地址必须内置_不能要求用户填写()
    {
        Assert.False(string.IsNullOrWhiteSpace(UpdateService.BakedFeedUrl),
            "编译期更新地址为空 —— 最终用户点「检查并更新」会直接失败，必须由 csproj 的 UpdateFeedUrl 注入。");
    }

    [Fact]
    public void 更新地址优先级_设置页填写优先()
    {
        Assert.Equal("https://example.com/x.json", UpdateService.ResolveFeedUrl("https://example.com/x.json"));
    }

    [Fact]
    public async Task 本地清单也能读取_并正确判断有无更新()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dp-feed-{Guid.NewGuid():N}.json");

        try
        {
            var payload = new
            {
                version = "99.0.0",
                url = Path.Combine(Path.GetTempPath(), "dirporter.exe"),
                sha256 = new string('0', 64),
                notes = "test",
            };

            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(payload));

            var (hasUpdate, info) = await UpdateService.CheckAsync(path);
            Assert.True(hasUpdate);
            Assert.Equal("99.0.0", info!.Version);

            var older = new { version = "0.0.1", url = "x.exe" };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(older));

            var (hasOlder, _) = await UpdateService.CheckAsync(path);
            Assert.False(hasOlder);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task 清单缺字段时明确报错而不是静默通过()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dp-feed-bad-{Guid.NewGuid():N}.json");

        try
        {
            await File.WriteAllTextAsync(path, "{\"version\":\"1.0.0\"}");

            await Assert.ThrowsAsync<InvalidDataException>(() => UpdateService.CheckAsync(path));
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    [Fact]
    public void 设置页只暴露一个更新按钮_不存在先检查再安装的两步流程()
    {
        var xaml = ReadXaml("Views/SettingsView.xaml");

        Assert.Contains("UpdateNowCommand", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CheckUpdateCommand", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyUpdateCommand", xaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0.2.0", "0.1.9", true)]
    [InlineData("v0.2.0", "0.1.9", true)]
    [InlineData("0.1.9", "0.1.9", false)]
    [InlineData("0.1.8", "0.1.9", false)]
    public void 版本比较对_v_前缀与相等情形都正确(string remote, string current, bool expected)
    {
        Assert.Equal(expected, UpdateService.IsNewer(remote, Version.Parse(current)));
    }
}
