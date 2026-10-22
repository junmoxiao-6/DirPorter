using DirPorter.App.Services;
using DirPorter.App.ViewModels;
using DirPorter.Core.Links;
using Xunit;

namespace DirPorter.Tests;

/// <summary>
/// R12：第四轮反馈（六问题）的回归测试。
///
/// 覆盖：
/// <list type="number">
/// <item>「已迁移」仍显示"否" → 状态变化即同步显示文本（不再等语言切换）。</item>
/// <item>功能完全拆分 → 一键迁移独立页 + 启动默认页 = quick；扫描页不再含一键入口。</item>
/// <item>mklink 类型补充 → 四种全展示，文件级两种置灰（带原因）；默认仍为目录联接 /J。</item>
/// <item>窗口缩不下去 → 内容区可滚动 + 最小尺寸放宽。</item>
/// <item>性能 → 防抖器行为；记录/链接页构造时不再同步加载（懒加载）。</item>
/// <item>文案 → "不是链接"改为"未建链接"。</item>
/// </list>
/// </summary>
[Collection(DataRootSequentialCollection.Name)]
public class R12_QuickMigrateAndPerfTests
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

    private static string Loc(string key) => LocalizationService.Instance.Get(key);

    // ============================================================ 1) 迁移标记文本同步（问题一）

    [Fact]
    public void 迁移标记_状态变化即同步显示文本_不再等语言切换()
    {
        var row = new ScanRow { Path = @"C:\x" };
        row.RefreshLocalization();
        Assert.Equal(Loc("common.no"), row.MigratedText);

        row.IsMigrated = true;

        Assert.Equal(Loc("common.yes"), row.MigratedText);   // R12 修复点
    }

    // ============================================================ 2) 一键迁移独立页（问题二）

    [Fact]
    public void 一键迁移页_是启动默认页()
    {
        var host = new AppHost(new SilentInteraction(), new UiSettings());

        Assert.Equal("quick", host.Main.SelectedNav.Key);
        Assert.Same(host.Main.Quick, host.Main.CurrentPage);
    }

    [Fact]
    public async Task 一键页_空源时拒绝并提示_不静默()
    {
        var interaction = new RecordingInteraction();
        var host = new AppHost(interaction, new UiSettings());

        await host.Main.Quick.MigrateNowCommand.ExecuteAsync(null);

        Assert.Contains(interaction.Messages, m => m.Contains("源目录"));
    }

    [Fact]
    public void 一键页_添加不存在的源目录被拒绝()
    {
        var interaction = new RecordingInteraction();
        var host = new AppHost(interaction, new UiSettings());
        var quick = host.Main.Quick;

        quick.NewSourcePath = @"Z:\definitely-not-exists-" + Guid.NewGuid().ToString("N")[..6];
        quick.AddSourceTextCommand.Execute(null);

        Assert.Empty(quick.Sources);
        Assert.NotEmpty(interaction.Messages);
    }

    [Fact]
    public void 扫描页_不再包含一键迁移入口()
    {
        var scan = ReadXaml("Views/ScanView.xaml");
        Assert.DoesNotContain("MigrateNowCommand", scan, StringComparison.Ordinal);
        Assert.DoesNotContain("Migrate.SelectedLinkKind", scan, StringComparison.Ordinal);
    }

    // ============================================================ 3) 链接类型四种展示（问题二）

    [Fact]
    public void 迁移类型选项_四种全展示_文件级两种置灰_默认目录联接()
    {
        var set = LinkKindOption.BuildMigrationSet();

        Assert.Equal(4, set.Count);
        Assert.True(set[0].CanUseForMigration);
        Assert.True(set[1].CanUseForMigration);
        Assert.False(set[2].CanUseForMigration);
        Assert.False(set[3].CanUseForMigration);

        Assert.Null(set[0].DisabledHint);              // 可用项不显示空提示
        Assert.False(string.IsNullOrWhiteSpace(set[2].DisabledHint));

        Assert.Equal(LinkKind.Junction, set[0].Kind);  // 默认 /J 目录联接
    }

    [Fact]
    public void 设置页与一键页_默认类型都是目录联接()
    {
        var host = new AppHost(new SilentInteraction(), new UiSettings());

        // R12.4：类型选择归属「一键迁移建链」页（迁移页已是纯迁移，无类型选择）。
        Assert.Equal(LinkKind.Junction, host.Main.Quick.SelectedLinkKind.Kind);
        Assert.Equal(LinkKind.Junction, host.Main.Settings.SelectedLinkKind.Kind);
    }

    [Fact]
    public void 链接页_必须有选文件入口()
    {
        Assert.Contains("BrowseTargetFileCommand", ReadXaml("Views/LinkView.xaml"), StringComparison.Ordinal);
    }

    // ============================================================ 4) 防抖与懒加载（问题四）

    [Fact]
    public void 防抖器_多次Poke合并为一次_Flush可手动结算()
    {
        var count = 0;
        var debouncer = new UiDebouncer(TimeSpan.FromMilliseconds(50));

        debouncer.Poke(() => count++);
        debouncer.Poke(() => count++);
        debouncer.Poke(() => count++);

        debouncer.Flush();
        Assert.Equal(1, count);

        debouncer.Flush();                 // 无待执行动作：空操作
        Assert.Equal(1, count);
    }

    [Fact]
    public void 记录与链接页_构造时不再同步加载_懒加载幂等()
    {
        var host = new AppHost(new SilentInteraction(), new UiSettings());

        Assert.Empty(host.Main.History.Records);
        Assert.Empty(host.Main.Link.ExistingLinks);

        host.Main.History.EnsureLoaded();
        host.Main.Link.EnsureLoaded();
        host.Main.History.EnsureLoaded();   // 幂等，不抛
    }

    // ============================================================ 5) 窗口可缩放（问题三）

    [Fact]
    public void 窗口_内容可滚动_最小尺寸已放宽()
    {
        var xaml = ReadXaml("MainWindow.xaml");

        Assert.Contains("ScrollViewer", xaml, StringComparison.Ordinal);
        Assert.Contains("MinWidth=\"660\"", xaml, StringComparison.Ordinal);   // R12.1：820 → 660
        Assert.Contains("MinHeight=\"480\"", xaml, StringComparison.Ordinal);  // R12.1：540 → 480
    }

    [Fact]
    public void 滚轮链_表格与列表必须挂载转交行为()
    {
        // R12.1（用户反馈"滚轮无响应"）：DataGrid / ListBox 滚到头或没范围时，
        // 滚轮必须转交给外层页面滚动容器，避免"只能拖滚动条"。
        var shared = ReadXaml("Themes/Shared.xaml");

        Assert.Contains("WheelScrollChaining", shared, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(AppDirectory, "Services", "WheelScrollChaining.cs")),
            "缺少 WheelScrollChaining 附加行为实现。");
    }

    // ============================================================ 6) 文案（问题五）

    [Fact]
    public void 未建链接_文案已更新()
    {
        Assert.Equal("未建链接", Loc("hist.health.notLink"));
    }

    // ============================================================ 工具

    private sealed class SilentInteraction : IUserInteraction
    {
        public void Info(string message, string? title = null) { }
        public void Error(string message, string? title = null) { }
        public bool Confirm(string message, string? title = null) => false;
        public string? PickFolder(string? initialDirectory, string title) => null;
        public void OpenInExplorer(string path) { }
        public void CopyToClipboard(string text) { }
    }

    private sealed class RecordingInteraction : IUserInteraction
    {
        public List<string> Messages { get; } = new();

        public void Info(string message, string? title = null) => Messages.Add(message);
        public void Error(string message, string? title = null) => Messages.Add(message);
        public bool Confirm(string message, string? title = null) => true;
        public string? PickFolder(string? initialDirectory, string title) => null;
        public void OpenInExplorer(string path) { }
        public void CopyToClipboard(string text) { }
    }
}
