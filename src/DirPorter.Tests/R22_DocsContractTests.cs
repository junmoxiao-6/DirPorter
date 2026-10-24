using System.Text.Json;
using Xunit;

namespace DirPorter.Tests;

/// <summary>
/// R12.14：对外文档必须"说人话、说准话"。
/// ① README 必须写明产品定位（C 盘爆满 / 不想重装）与安全风险；
/// ② 每个版本必须有独立的发行说明（按版本对比），latest.json 只放一句话摘要，
///    不能把 HTML 注释/整段 markdown 塞给客户端。
/// </summary>
public class R22_DocsContractTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "DirPorter.App")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ReadRepoFile(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

    [Fact]
    public void README_必须写明定位与安全风险()
    {
        var readme = ReadRepoFile("README.md");

        // 定位：给"C 盘爆满但不想重装"的人用
        Assert.Contains("C 盘", readme, StringComparison.Ordinal);
        Assert.Contains("不想重装", readme, StringComparison.Ordinal);

        // 安全风险章节必须存在，且覆盖关键风险
        Assert.Contains("安全与风险", readme, StringComparison.Ordinal);
        Assert.Contains("不是备份", readme, StringComparison.Ordinal);      // 停车目录 ≠ 备份
        Assert.Contains("悬空", readme, StringComparison.Ordinal);          // 目标盘不可用
        Assert.Contains("无代码签名", readme, StringComparison.Ordinal);    // 签名现状要如实说
    }

    [Fact]
    public void 当前版本必须有独立发行说明_且按版本对比()
    {
        var version = ReadRepoFile("Directory.Build.props");
        var m = System.Text.RegularExpressions.Regex.Match(version, @"<Version>\s*([^<]+?)\s*</Version>");
        Assert.True(m.Success);
        var ver = m.Groups[1].Value;

        var notesPath = Path.Combine(RepoRoot(), "docs", "release-notes", $"v{ver}.md");
        Assert.True(File.Exists(notesPath), $"缺少发行说明：docs/release-notes/v{ver}.md（发版必须写「相对上一版改了什么」）");

        var notes = File.ReadAllText(notesPath);
        Assert.Contains("对比", notes, StringComparison.Ordinal);
        Assert.Contains("summary:", notes, StringComparison.Ordinal);
    }

    [Fact]
    public void 客户端清单的说明只能是一句话摘要()
    {
        var version = ReadRepoFile("Directory.Build.props");
        var m = System.Text.RegularExpressions.Regex.Match(version, @"<Version>\s*([^<]+?)\s*</Version>");
        var ver = m.Groups[1].Value;

        var manifestPath = Path.Combine(RepoRoot(), "dist", ver, "latest.json");
        if (!File.Exists(manifestPath)) return;   // 本地未发布时跳过（CI 上由发布步骤保证）

        var info = JsonSerializer.Deserialize<UpdateInfoStub>(File.ReadAllText(manifestPath));
        Assert.NotNull(info);
        Assert.False(string.IsNullOrWhiteSpace(info!.Notes));

        // 不能把注释/多段 markdown 直接塞给更新提示
        Assert.DoesNotContain("<!--", info.Notes!, StringComparison.Ordinal);
        Assert.DoesNotContain("##", info.Notes!, StringComparison.Ordinal);
        Assert.True(info.Notes!.Length <= 200, "notes 应是一句话摘要（<=200 字），完整说明放 Release 描述");
    }

    private sealed class UpdateInfoStub
    {
        [System.Text.Json.Serialization.JsonPropertyName("version")] public string? Version { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("notes")] public string? Notes { get; set; }
    }
}
