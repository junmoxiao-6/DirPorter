using System.Text.RegularExpressions;
using DirPorter.App.Services;
using DirPorter.App.ViewModels;
using DirPorter.Core.Links;
using Xunit;

namespace DirPorter.Tests;

/// <summary>
/// R11：表格渲染事故修复的回归测试 + 迁移勾选语义。
///
/// 背景：R10 重写选中态模板时，DataGridRow 的 ControlTemplate 误用了裸 ContentPresenter
/// （DataGridRow 是 Control、没有 Content 属性），导致**四张表格整表不渲染单元格** ——
/// 用户看到"没有扫描结果""看不见复选框"。这类缺陷 XAML 能解析、程序不报错，
/// 只有渲染级检查能发现；本文件用静态扫描锁死模板元素，渲染实测由 UiProbe 断言负责。
///
/// 另含：迁移页「勾选 = 要迁移的条目」语义（用户拍板）与空状态绑定的守卫。
/// </summary>
[Collection(DataRootSequentialCollection.Name)]
public class R11_DataGridRenderAndSelectionTests
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

    /// <summary>剥掉 XML 注释：注释里解释性提到元素名是合理的，断言只盯实际结构（R10 教训）。</summary>
    private static string StripXmlComments(string xaml)
        => Regex.Replace(xaml, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

    private static string ExtractStyleBlock(string shared, string marker)
    {
        var start = shared.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Shared.xaml 中找不到：{marker}");

        var styleStart = shared.LastIndexOf("<Style", start, StringComparison.Ordinal);
        var styleEnd = shared.IndexOf("</Style>", start, StringComparison.Ordinal);
        Assert.True(styleStart >= 0 && styleEnd > styleStart, $"无法定位样式块：{marker}");

        return shared[styleStart..(styleEnd + "</Style>".Length)];
    }

    // ============================================================ 1) P0：表格行模板

    [Fact]
    public void 表格行模板_必须用DataGridCellsPresenter_不得再用裸ContentPresenter()
    {
        // 事故复现点：DataGridRow 继承自 Control（无 Content 属性）。
        // 裸 <ContentPresenter/> 会让单元格呈现器永不进入可视树 → 整表空白。
        var shared = ReadXaml("Themes/Shared.xaml");
        var block = StripXmlComments(ExtractStyleBlock(shared, "TargetType=\"DataGridRow\""));

        Assert.Contains("<DataGridCellsPresenter", block, StringComparison.Ordinal);
        Assert.DoesNotContain("<ContentPresenter", block, StringComparison.Ordinal);
    }

    [Fact]
    public void 表格行模板_必须绑定ItemsPanel_否则星号列无法分配宽度()
    {
        var shared = ReadXaml("Themes/Shared.xaml");
        var block = ExtractStyleBlock(shared, "TargetType=\"DataGridRow\"");

        Assert.Contains("ItemsPanel=\"{TemplateBinding ItemsPanel}\"", block, StringComparison.Ordinal);
    }

    [Fact]
    public void 单元格模板_继续用ContentPresenter_DataGridCell是ContentControl()
    {
        // 反向守卫：DataGridCell 是 ContentControl，ContentPresenter 才是正确元素。
        var shared = ReadXaml("Themes/Shared.xaml");
        var block = StripXmlComments(ExtractStyleBlock(shared, "TargetType=\"DataGridCell\""));

        Assert.Contains("<ContentPresenter", block, StringComparison.Ordinal);
        Assert.DoesNotContain("<DataGridCellsPresenter", block, StringComparison.Ordinal);
    }

    // ============================================================ 2) 分段控件

    [Fact]
    public void 分段控件样式_必须定义_且选中态写在ControlTemplate触发器里()
    {
        var shared = ReadXaml("Themes/Shared.xaml");

        Assert.Contains("x:Key=\"SegmentedControl\"", shared, StringComparison.Ordinal);

        var item = ExtractStyleBlock(shared, "x:Key=\"SegmentedItem\"");
        Assert.Contains("IsSelected", item, StringComparison.Ordinal);
        Assert.Contains("AccentBg", item, StringComparison.Ordinal);
    }

    [Fact]
    public void 分段样式_必须排在隐式ListBoxItem之后_否则选中态静态测试会取错样式块()
    {
        // 既有测试「选中态_必须写进ControlTemplate」用 IndexOf 取第一处 TargetType="ListBoxItem"。
        var shared = ReadXaml("Themes/Shared.xaml");

        var implicitItem = shared.IndexOf("TargetType=\"ListBoxItem\"", StringComparison.Ordinal);
        var segmentedItem = shared.IndexOf("x:Key=\"SegmentedItem\"", StringComparison.Ordinal);

        Assert.True(implicitItem >= 0 && segmentedItem >= 0, "两种 ListBoxItem 样式都必须存在。");
        Assert.True(segmentedItem > implicitItem, "SegmentedItem 必须排在隐式 ListBoxItem 样式之后。");
    }

    [Fact]
    public void 一键页_建链类型必须用分段控件展示_迁移页不再展示()
    {
        Assert.Contains("Style=\"{StaticResource SegmentedControl}\"", ReadXaml("Views/QuickMigrateView.xaml"), StringComparison.Ordinal);
        // R12.4：迁移页 = 纯迁移（不建链）——不得再有类型分段控件。
        Assert.DoesNotContain("SegmentedControl", ReadXaml("Views/MigrateView.xaml"), StringComparison.Ordinal);
    }

    // ============================================================ 3) 迁移勾选语义（勾选 = 要迁移的条目）

    private static MigrateViewModel NewMigrate(out AppHost host)
    {
        host = new AppHost(new SilentInteraction(), new UiSettings());
        return host.Main.Migrate;
    }

    [Fact]
    public void 迁移计划_只包含勾选的条目()
    {
        var migrate = NewMigrate(out _);
        migrate.TargetRoot = @"D:\Archive";

        migrate.Queue.Add(new QueueItem("e-01", @"C:\a", @"D:\Archive\a"));
        migrate.Queue.Add(new QueueItem("e-02", @"C:\b", @"D:\Archive\b"));
        migrate.Queue.Add(new QueueItem("e-03", @"C:\c", @"D:\Archive\c"));

        migrate.Queue[1].IsSelected = false;   // 取消勾选 = 跳过该项

        var plan = migrate.BuildPlan();

        Assert.Equal(2, plan.Entries.Count);
        Assert.DoesNotContain(plan.Entries, e => e.EntryId == "e-02");
    }

    [Fact]
    public void 迁移计划_全不勾选时为空()
    {
        var migrate = NewMigrate(out _);
        migrate.Queue.Add(new QueueItem("e-01", @"C:\a", @"D:\Archive\a"));
        migrate.Queue[0].IsSelected = false;

        Assert.Empty(migrate.BuildPlan().Entries);
    }

    [Fact]
    public void 新入队条目_必须默认勾选()
    {
        var migrate = NewMigrate(out _);
        migrate.TargetRoot = @"D:\Archive";
        migrate.ReplaceQueue(new[] { @"C:\a", @"C:\b" });

        Assert.True(migrate.Queue.Count >= 2, "ReplaceQueue 应把条目放入队列。");
        Assert.All(migrate.Queue, q => Assert.True(q.IsSelected, "新入队条目必须默认勾选。"));
    }

    [Fact]
    public async Task 全不勾选_运行队列必须拒绝并提示()
    {
        var migrate = NewMigrate(out _);
        migrate.TargetRoot = @"D:\Archive";
        migrate.Queue.Add(new QueueItem("e-01", @"C:\a", @"D:\Archive\a"));
        migrate.Queue[0].IsSelected = false;

        var summary = await migrate.RunQueueAsync();

        Assert.True(summary.IsError);
        Assert.Equal(LocalizationService.Instance.Get("mig.noSelection"), summary.StatusText);
    }

    // ============================================================ 4) 空状态与横条布局守卫

    [Fact]
    public void 空状态_扫描记录链接三页都必须有条件绑定()
    {
        Assert.Contains("HasResults", ReadXaml("Views/ScanView.xaml"), StringComparison.Ordinal);
        Assert.Contains("HasRecords", ReadXaml("Views/HistoryView.xaml"), StringComparison.Ordinal);
        Assert.Contains("HasExistingLinks", ReadXaml("Views/LinkView.xaml"), StringComparison.Ordinal);
    }

    [Fact]
    public void 一键迁移页_目标根输入框必须有保底宽度_防止被右侧控件挤到零()
    {
        // 事故（R11 前身）：右侧 Auto 列堆太宽把 * 列压到 ~0，输入框只剩一个字符。
        // R12 拆分为独立页后，此约束由一键迁移页承担。
        var xaml = ReadXaml("Views/QuickMigrateView.xaml");
        Assert.Contains("MinWidth=\"280\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void 扫描页空状态与表格_必须互斥绑定()
    {
        var scan = ReadXaml("Views/ScanView.xaml");
        Assert.Contains("ConverterParameter=invert", scan, StringComparison.Ordinal);
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
}
