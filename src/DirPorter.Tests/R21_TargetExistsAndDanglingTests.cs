using DirPorter.Core.Config;
using DirPorter.Core.FileSystem;
using DirPorter.Core.Links;
using DirPorter.Core.Migration;
using DirPorter.Core.Precheck;
using Xunit;

namespace DirPorter.Tests;

/// <summary>
/// R12.13：两个真实踩坑的修复。
/// ① 目标已存在非空目录时，指引必须告诉用户"该建链接，不是迁移"
///    （实测：用户把数据搬过去后又点迁移，只看到"目标已存在"，不知道下一步该干嘛）；
/// ② 链接总览要能一眼看出"悬空链接"（目标被搬走后程序读不到数据）。
/// </summary>
public class R21_TargetExistsAndDanglingTests
{
    private static PrecheckService CreateService()
        => new(new Win32VolumeResolver(), new BlacklistPolicy(), new NoWarningPolicy(), new TreeProbe());

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
    public async Task 目标已存在非空目录_指引引导去建链接而不是迁移()
    {
        var root = Path.Combine(Path.GetTempPath(), "dp-pc-" + Guid.NewGuid().ToString("N")[..6]);
        var source = Path.Combine(root, "cli");
        var target = Path.Combine(root, ".zcode", "cli");

        try
        {
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "a.txt"), "alpha");

            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "b.txt"), "beta");

            var report = await CreateService().RunAsync(
                MigrationPlan.Single(source, target, LinkKind.Junction), null, CancellationToken.None);

            Assert.True(report.IsBlocked);

            var hit = report.Results.Single(r => r.Code == PrecheckCodes.TgtExistsNonEmpty.Code);
            Assert.Contains("链接", hit.UserGuide ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void 链接总览页必须展示悬空统计()
    {
        var xaml = ReadXaml("Views/LinkOverviewView.xaml");

        Assert.Contains("HasDangling", xaml, StringComparison.Ordinal);
    }
}
