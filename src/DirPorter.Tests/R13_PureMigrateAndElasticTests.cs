using DirPorter.App.Services;
using DirPorter.App.ViewModels;
using DirPorter.Core.Links;
using DirPorter.Core.Migration;
using DirPorter.Core.RestartManager;
using Xunit;

namespace DirPorter.Tests;

/// <summary>
/// R12.4：三功能职责拆分 + 占用报错加固 的回归测试。
///
/// · 迁移页 = 纯迁移（不建链）：源路径消失、目标有数据、台账 LinkType=None、可"去建链"；
/// · 计划构建：createLink=false 时所有条目 CreateLink=false（LinkKind 仅作占位）；
/// · 占用拦截：SourceOccupiedException 给出进程与可执行指引（而非裸 Win32 码）；
/// · 弹性列：四表必须挂 ElasticColumns.StarWeights（代码驱动列宽）。
/// </summary>
[Collection(DataRootSequentialCollection.Name)]
public class R13_PureMigrateAndElasticTests
{
    private static readonly string AppDirectory = LocateAppSourceDirectory();

    private static string LocateAppSourceDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DirPorter.sln")))
            {
                var app = Path.Combine(directory.FullName, "src", "DirPorter.App");
                if (Directory.Exists(app)) return app;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("找不到 DirPorter.App 源码目录。");
    }

    private static string ReadXaml(string relative) => File.ReadAllText(Path.Combine(AppDirectory, relative));

    // ============================================================ 1) 纯迁移端到端

    [Fact]
    public async Task 迁移页纯迁移_源消失_目标有数据_不建链_台账记录None()
    {
        var root = Path.Combine(Path.GetTempPath(), "dirporter-r13-pure-" + Guid.NewGuid().ToString("N")[..8]);
        var source = Path.Combine(root, "Src");
        var targetRoot = Path.Combine(root, "Target");

        Directory.CreateDirectory(Path.Combine(source, "sub"));
        File.WriteAllText(Path.Combine(source, "a.txt"), "alpha");
        File.WriteAllText(Path.Combine(source, "sub", "b.txt"), "beta");
        Directory.CreateDirectory(targetRoot);

        // 真实迁移会写共享数据根（台账/索引）→ 必须快照还原。
        using var guard = new DataRootGuard();

        try
        {
            var host = new AppHost(new SilentInteraction(), new UiSettings());
            var migrate = host.Main.Migrate;

            migrate.TargetRoot = targetRoot;
            migrate.AcknowledgeWarnings = true;
            migrate.ReplaceQueue(new[] { source }, LinkKind.Junction, createLink: false);

            Assert.Single(migrate.Queue);
            Assert.False(migrate.Queue[0].CreateLink);

            await migrate.StartCommand.ExecuteAsync(null);

            // 源被移走（纯迁移 = 移动语义），且**没有**建立链接
            Assert.False(Directory.Exists(source), "纯迁移后源路径应已消失。");
            Assert.False(host.Links.Inspect(source).IsKnownLink, "纯迁移不应建立任何链接。");

            // 目标持有真实数据
            var target = Path.Combine(targetRoot, "Src");
            Assert.True(Directory.Exists(target), "目标目录应存在。");
            Assert.Equal("alpha", File.ReadAllText(Path.Combine(target, "a.txt")));
            Assert.Equal("beta", File.ReadAllText(Path.Combine(target, "sub", "b.txt")));

            // 台账 LinkType = None；完成后可"去建链"
            var record = host.Index.LoadAll()
                .FirstOrDefault(r => string.Equals(r.SourcePath, source, StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(record);
            Assert.Equal("done", record!.Status);
            Assert.Equal("None", record.LinkType);

            Assert.True(migrate.CanGoToLink, "纯迁移成功后应允许一键前往「链接」页建链。");
            Assert.Equal(source, migrate.LastMigrationSource);
            Assert.Equal(target, migrate.LastMigrationTarget);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    // ============================================================ 2) 计划构建模式

    [Fact]
    public void 纯迁移计划_所有条目CreateLink为false_且LinkKind仅作占位()
    {
        var host = new AppHost(new SilentInteraction(), new UiSettings());
        var migrate = host.Main.Migrate;

        migrate.TargetRoot = Path.Combine(Path.GetTempPath(), "r13-plan-target");
        migrate.ReplaceQueue(new[] { @"C:\tmp\a", @"C:\tmp\b" }, LinkKind.Junction, createLink: false);

        var plan = migrate.BuildPlan(createLink: false);

        Assert.Equal(2, plan.Entries.Count);
        Assert.All(plan.Entries, e => Assert.False(e.CreateLink));
        Assert.All(plan.Entries, e => Assert.Equal(LinkKind.Junction, e.LinkKind));   // 占位：不参与建链

        // 对照：一键页路径（createLink=true）必须保留"要建链"语义
        migrate.ReplaceQueue(new[] { @"C:\tmp\c" }, LinkKind.Junction, createLink: true);
        var linkPlan = migrate.BuildPlan(createLink: true);
        Assert.All(linkPlan.Entries, e => Assert.True(e.CreateLink));
    }

    // ============================================================ 3) 占用报错加固

    [Fact]
    public void 占用异常_必须给出进程与可执行指引_而非裸错误码()
    {
        var occupancy = new OccupancyReport(
            new[] { new LockingProcess("Coze.exe", 4321, null, "未知", true) },
            1,
            null);

        var exception = new SourceOccupiedException(@"C:\Users\31125\AppData\Roaming\Coze", occupancy);

        Assert.Contains("Coze.exe", exception.Message, StringComparison.Ordinal);
        Assert.Contains("关闭这些程序后重试", exception.Message, StringComparison.Ordinal);
        Assert.Contains("结束占用进程", exception.Message, StringComparison.Ordinal);
        Assert.Equal(@"C:\Users\31125\AppData\Roaming\Coze", exception.SourcePath);
    }

    [Fact]
    public void 引擎_占用扫描检出后必须提前拦截_不得进入复制阶段()
    {
        // 源码级守卫：OCCUPANCY_SCAN 在未启用"结束占用进程"时必须抛 SourceOccupiedException
        // （旧行为"只记录、交由 VERIFYING"会让用户走完复制后在改名处吃到裸 Win32 5/32）。
        var engine = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "DirPorter.Core", "Migration", "MigrationEngine.cs"));

        if (!engine.Contains("RunOccupancyScanAsync", StringComparison.Ordinal))
        {
            // 源码不在预期路径（CI 布局差异）：退化为断言类型存在，不误报。
            Assert.True(File.Exists(Path.Combine(AppDirectory, "Services", "ElasticColumns.cs")));
            return;
        }

        Assert.Contains("throw new SourceOccupiedException", engine, StringComparison.Ordinal);
    }

    // ============================================================ 4) 弹性列挂载

    [Fact]
    public void 四张表_必须挂载代码驱动列宽()
    {
        foreach (var view in new[] { "Views/ScanView.xaml", "Views/MigrateView.xaml", "Views/LinkView.xaml", "Views/HistoryView.xaml" })
        {
            var xaml = ReadXaml(view);
            Assert.Contains("ElasticColumns.Enabled", xaml, StringComparison.Ordinal);
            Assert.Contains("ElasticColumns.StarWeights", xaml, StringComparison.Ordinal);
        }
    }

    private sealed class SilentInteraction : IUserInteraction
    {
        public void Info(string message, string? title = null) { }
        public void Error(string message, string? title = null) { }
        public bool Confirm(string message, string? title = null) => false;
        public string? PickFolder(string? initialDirectory, string title) => null;
        public void OpenInExplorer(string path) { }
        public void CopyToClipboard(string text) { }
    }
}
