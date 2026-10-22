using System.Text.RegularExpressions;
using DirPorter.App.Services;
using DirPorter.App.ViewModels;
using DirPorter.Core.Links;
using Xunit;

namespace DirPorter.Tests;

/// <summary>
/// R10：第二波用户实测反馈的回归测试。
///
/// 对应四条反馈：
/// <list type="number">
/// <item>「启动时自动请求管理员权限」→ GUI 入口的提权分支（静态守卫：Program.RunGui 必须真的调用 LaunchElevated）。</item>
/// <item>「点击目录后选中项变蓝、遮挡内容」→ 选中态必须在 ControlTemplate 里（模板触发器优先级高于样式触发器，
///       只写 Style.Triggers 盖不住系统高亮蓝 —— 这是前一轮的认知错误，必须有静态守卫防止回退）。</item>
/// <item>「按钮不够精致」→ 按钮四样式的模板/hover 反馈齐备（静态守卫）。</item>
/// <item>「mklink 类型要能选，默认目录联接」→ LinkKinds 解析、UiSettings 默认值、Migrate 切换联动。</item>
/// </list>
/// </summary>
[Collection(DataRootSequentialCollection.Name)]
public class R10_LinkKindSelectionTests
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

    // ============================================================ 1) LinkKinds 解析（单一事实来源）

    [Theory]
    [InlineData("junction", LinkKind.Junction)]
    [InlineData("J", LinkKind.Junction)]
    [InlineData("symlink-dir", LinkKind.DirectorySymlink)]
    [InlineData("dirsymlink", LinkKind.DirectorySymlink)]
    [InlineData("symlink-file", LinkKind.FileSymlink)]
    [InlineData("hardlink", LinkKind.HardLink)]
    public void 链接类型解析_别名与规范名都能识别(string input, LinkKind expected)
    {
        Assert.True(LinkKinds.TryParse(input, out var kind));
        Assert.Equal(expected, kind);
    }

    [Fact]
    public void 链接类型解析_auto与空值为自动_垃圾值被拒绝()
    {
        Assert.True(LinkKinds.TryParse("auto", out var auto));
        Assert.Null(auto);

        Assert.True(LinkKinds.TryParse("", out var empty));
        Assert.Null(empty);

        Assert.False(LinkKinds.TryParse("teleport", out _));
    }

    [Fact]
    public void 链接类型存储往返_枚举与字符串一一对应()
    {
        foreach (var kind in Enum.GetValues<LinkKind>())
        {
            var text = LinkKinds.ToStorageString(kind);
            Assert.True(LinkKinds.TryParse(text, out var parsed), $"解析失败：{text}");
            Assert.Equal(kind, parsed);
        }
    }

    [Fact]
    public void 迁移候选只含目录级两种类型()
    {
        // 硬链接与文件符号链接不支持目录 —— 绝不能让它们出现在迁移建链的选择里。
        Assert.Equal(new[] { LinkKind.Junction, LinkKind.DirectorySymlink }, LinkKinds.MigrationKinds);
    }

    [Fact]
    public void 垃圾默认值回退到目录联接()
    {
        Assert.Equal(LinkKind.Junction, LinkKinds.ParseOr("garbage", LinkKind.Junction));
        Assert.Equal(LinkKind.DirectorySymlink, LinkKinds.ParseOr(null, LinkKind.DirectorySymlink));
    }

    // ============================================================ 2) 设置默认值

    [Fact]
    public void 设置_默认建链类型是目录联接()
    {
        var settings = new UiSettings();

        Assert.Equal("junction", settings.DefaultLinkKind);
        Assert.Equal(LinkKind.Junction, settings.DefaultLinkKindValue);
    }

    [Fact]
    public void 设置_手改坏的类型字段不导致崩溃_回退目录联接()
    {
        var settings = new UiSettings { DefaultLinkKind = "not-a-kind" };
        Assert.Equal(LinkKind.Junction, settings.DefaultLinkKindValue);
    }

    // ============================================================ 3) MigrateViewModel 联动

    [Fact]
    public void 迁移页_纯迁移_不得出现建链类型选择器()
    {
        // R12.4：迁移页只做迁移（不建链）。建链类型选择已移至「一键迁移建链」页。
        var migrate = ReadXaml("Views/MigrateView.xaml");

        Assert.DoesNotContain("LinkKindOptions", migrate, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedLinkKind", migrate, StringComparison.Ordinal);
        Assert.Contains("mig.pureHint", migrate, StringComparison.Ordinal);   // 明确的"不建链"提示
    }

    [Fact]
    public void 迁移页_设置里的默认类型会被带入()
    {
        var settings = new UiSettings { DefaultLinkKind = "symlink-dir" };
        var host = new AppHost(new SilentInteraction(), settings);

        Assert.Equal(LinkKind.DirectorySymlink, host.Main.Migrate.SelectedLinkKind.Kind);
    }

    [Fact]
    public void 迁移页_切换类型后新入队项使用新类型()
    {
        var root = Path.Combine(Path.GetTempPath(), "dirporter-r10-kind-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);

        try
        {
            var host = new AppHost(new SilentInteraction(), new UiSettings());
            var migrate = host.Main.Migrate;
            migrate.TargetRoot = Path.Combine(root, "target");

            migrate.AddSources(new[] { Path.Combine(root, "A") });
            Assert.Equal(LinkKind.Junction, migrate.Queue[0].LinkKind);

            // 切到目录符号链接：队列里未执行的项随动，新加项也用新类型。
            migrate.SelectedLinkKind = migrate.LinkKindOptions.First(o => o.Kind == LinkKind.DirectorySymlink);

            Assert.Equal(LinkKind.DirectorySymlink, migrate.Queue[0].LinkKind);

            migrate.AddSources(new[] { Path.Combine(root, "B") });
            Assert.Equal(LinkKind.DirectorySymlink, migrate.Queue[1].LinkKind);

            // 持久化：设置已同步（下次启动默认用符号链接）。
            Assert.Equal("symlink-dir", host.Settings.DefaultLinkKind);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void 设置页_切换默认类型会同步迁移页当前选择()
    {
        var host = new AppHost(new SilentInteraction(), new UiSettings());
        var settings = host.Main.Settings;

        settings.SelectedLinkKind = settings.LinkKindOptions.First(o => o.Kind == LinkKind.DirectorySymlink);

        Assert.Equal(LinkKind.DirectorySymlink, host.Main.Migrate.SelectedLinkKind.Kind);
        Assert.Equal("symlink-dir", host.Settings.DefaultLinkKind);
    }

    [Fact]
    public void 选择器与提示_中英文都必须非空()
    {
        foreach (var option in new MigrateViewModel(new AppHost(new SilentInteraction(), new UiSettings()))
                     .LinkKindOptions)
        {
            Assert.False(string.IsNullOrWhiteSpace(option.Display));
            Assert.False(string.IsNullOrWhiteSpace(option.Hint));
        }
    }

    // ============================================================ 4) 选中态：必须在 ControlTemplate 里

    /// <summary>
    /// 回归用户实测「点击目录后选中项变蓝、遮挡内容」。
    ///
    /// 根因：Aero/WinUI 的控件模板里自带 IsSelected → 系统高亮蓝 的**模板触发器**，
    /// 而模板触发器的优先级高于样式触发器 —— 前一版只在 Style.Triggers 里写选中色，盖不住它。
    /// 守卫：这四种控件必须自带 ControlTemplate，且模板里必须有 IsSelected 触发器。
    /// </summary>
    [Theory]
    [InlineData("ListBoxItem")]
    [InlineData("DataGridCell")]
    [InlineData("DataGridRow")]
    [InlineData("ComboBoxItem")]
    public void 选中态_必须写进ControlTemplate_才能盖住系统高亮蓝(string control)
    {
        var shared = ReadXaml("Themes/Shared.xaml");
        var block = ExtractStyleBlock(shared, $"TargetType=\"{control}\"");

        Assert.Contains("<ControlTemplate", block, StringComparison.Ordinal);
        Assert.Contains("Property=\"IsSelected\" Value=\"True\"", block, StringComparison.Ordinal);
        Assert.Contains("SelectionBg", block, StringComparison.Ordinal);
    }

    [Fact]
    public void 选中态_不得再引用系统高亮画笔()
    {
        var shared = ReadXaml("Themes/Shared.xaml");

        // 断言的是**实际引用**（x:Static SystemColors.…）——注释里解释"为什么不用系统高亮蓝"是允许的。
        Assert.DoesNotContain("Static SystemColors", shared, StringComparison.Ordinal);
        Assert.DoesNotContain("HighlightBrushKey", shared, StringComparison.Ordinal);
    }

    // ============================================================ 5) 按钮精致化（静态守卫）

    [Theory]
    [InlineData("PrimaryButton")]
    [InlineData("SecondaryButton")]
    [InlineData("GhostButton")]
    [InlineData("TinyButton")]
    public void 按钮样式_四档都必须自带模板(string styleKey)
    {
        var shared = ReadXaml("Themes/Shared.xaml");
        var block = ExtractStyleBlock(shared, $"x:Key=\"{styleKey}\"");

        Assert.Contains("<ControlTemplate", block, StringComparison.Ordinal);
        Assert.Contains("CornerRadius", block, StringComparison.Ordinal);

        // hover 反馈必须有（"精致感"的核心）：四档都写了 IsMouseOver 触发器。
        Assert.Contains("IsMouseOver", block, StringComparison.Ordinal);
    }

    [Fact]
    public void 按钮样式_高度标尺根在PrimaryButton()
    {
        var shared = ReadXaml("Themes/Shared.xaml");

        // 四档的高度继承自 PrimaryButton（Secondary/Ghost/Tiny 是 BasedOn），
        // 因此 MinHeight 只需且必须出现在 PrimaryButton 上。
        var primary = ExtractStyleBlock(shared, "x:Key=\"PrimaryButton\"");
        Assert.Contains("MinHeight", primary, StringComparison.Ordinal);

        foreach (var styleKey in new[] { "SecondaryButton", "GhostButton", "TinyButton" })
        {
            var block = ExtractStyleBlock(shared, $"x:Key=\"{styleKey}\"");
            Assert.Contains("BasedOn=\"{StaticResource PrimaryButton}\"", block, StringComparison.Ordinal);
        }
    }

    /// <summary>从 Shared.xaml 里取出指定样式的完整文本块（从 &lt;Style 到对应的 &lt;/Style&gt;）。</summary>
    private static string ExtractStyleBlock(string shared, string marker)
    {
        var start = shared.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Shared.xaml 中找不到：{marker}");

        var styleStart = shared.LastIndexOf("<Style", start, StringComparison.Ordinal);
        var styleEnd = shared.IndexOf("</Style>", start, StringComparison.Ordinal);
        Assert.True(styleStart >= 0 && styleEnd > styleStart, $"无法定位样式块：{marker}");

        return shared[styleStart..(styleEnd + "</Style>".Length)];
    }

    [Fact]
    public void 按钮hover_必须有点亮反馈()
    {
        var shared = ReadXaml("Themes/Shared.xaml");

        // 次级按钮 hover 边框点亮主题色 —— "白色椭圆边框按钮"精致化的核心。
        Assert.Contains("ControlBgHover", shared, StringComparison.Ordinal);
    }

    [Fact]
    public void 新增画笔_两个主题都必须定义_否则深色主题会丢色()
    {
        var light = ReadXaml("Themes/Light.xaml");
        var dark = ReadXaml("Themes/Dark.xaml");

        foreach (var key in new[] { "ControlBgHover", "WarnBannerBg" })
        {
            Assert.Contains($"x:Key=\"{key}\"", light, StringComparison.Ordinal);
            Assert.Contains($"x:Key=\"{key}\"", dark, StringComparison.Ordinal);
        }
    }

    // ============================================================ 6) 启动自动提权（静态守卫）

    /// <summary>
    /// 需求「每次启动应用时自动请求管理员权限」目前由 GUI 入口实现
    /// （清单保持 asInvoker：CLI 只读命令绝不能弹 UAC）。
    /// 这条守卫防止后人误删提权分支，或误把清单改成 requireAdministrator。
    /// </summary>
    [Fact]
    public void GUI入口_必须包含自动提权分支()
    {
        var program = File.ReadAllText(Path.Combine(AppDirectory, "Program.cs"));

        Assert.Contains("LaunchElevated", program, StringComparison.Ordinal);

        // RunGui 里必须有 IsElevated 判定 —— 否则提权分支可能被挪去别处、GUI 不再自动请求。
        var runGuiStart = program.IndexOf("private static int RunGui()", StringComparison.Ordinal);
        Assert.True(runGuiStart >= 0, "Program.cs 中找不到 RunGui。");

        var snippet = program[runGuiStart..Math.Min(program.Length, runGuiStart + 2000)];
        Assert.Contains("IsElevated", snippet, StringComparison.Ordinal);
    }

    [Fact]
    public void 清单_必须保持asInvoker_否则CLI会弹UAC()
    {
        var manifest = File.ReadAllText(Path.Combine(AppDirectory, "app.manifest"));

        // 显式偏离记录：单 exe 同时承载 GUI 与 CLI，requireAdministrator 会让只读命令也弹 UAC 并挂起管道。
        // 自动提权由 Program.RunGui 在代码里完成，清单不得写 requireAdministrator。
        // 注意只断言 XML 声明本身 —— 注释里解释"为什么不用 requireAdministrator"是允许且必要的。
        Assert.Contains("level=\"asInvoker\"", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("level=\"requireAdministrator\"", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void 权限横幅_未提权时显示_已提权时隐藏()
    {
        var host = new AppHost(new SilentInteraction(), new UiSettings());
        var main = host.Main;

        // 横幅可见性与提权状态必须互为反相（不硬编码具体值：测试可能跑在两种环境里）。
        Assert.Equal(!host.Elevation.IsElevated, main.ElevationBannerVisible);
        Assert.False(string.IsNullOrWhiteSpace(main.ElevationBannerText));
    }

    // ============================================================ 7) 类型选择器归属（静态守卫）

    [Theory]
    [InlineData("Views/QuickMigrateView.xaml")]
    [InlineData("Views/SettingsView.xaml")]
    public void 一键迁移页与设置页都必须提供建链类型选择器(string relative)
    {
        var xaml = ReadXaml(relative);

        Assert.Contains("LinkKindOptions", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedLinkKind", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void 一键迁移页_必须带类型选择器与一键按钮_扫描页保留入队入口()
    {
        // R12 完全拆分：一键迁移独立成页；扫描页只保留「加入迁移队列」。
        // R12.4：类型选择归属一键页自己的选项集；迁移页 = 纯迁移（不得再出现选择器）。
        var quick = ReadXaml("Views/QuickMigrateView.xaml");
        Assert.Contains("MigrateNowCommand", quick, StringComparison.Ordinal);
        Assert.Contains("LinkKindOptions", quick, StringComparison.Ordinal);
        Assert.Contains("SelectedLinkKind", quick, StringComparison.Ordinal);

        var scan = ReadXaml("Views/ScanView.xaml");
        Assert.Contains("AddToQueueCommand", scan, StringComparison.Ordinal);
        Assert.DoesNotContain("MigrateNowCommand", scan, StringComparison.Ordinal);

        Assert.DoesNotContain("LinkKindOptions", ReadXaml("Views/MigrateView.xaml"), StringComparison.Ordinal);
    }

    // ============================================================ 8) 迁移页新布局的守卫

    [Fact]
    public void 一键页_类型选择器必须把文件级两种置灰并给原因()
    {
        var xaml = ReadXaml("Views/QuickMigrateView.xaml");

        // 文件级两种（硬链接 / 文件符号链接）目标必须是文件，不能用于目录迁移：
        // UI 置灰 + 悬停给出原因，避免"选了却用不了"。
        Assert.Contains("CanUseForMigration", xaml, StringComparison.Ordinal);
        Assert.Contains("DisabledHint", xaml, StringComparison.Ordinal);
    }

    // ============================================================ 桩

    private sealed class SilentInteraction : IUserInteraction
    {
        public void Info(string message, string? title = null) { }
        public void Error(string message, string? title = null) { }
        public bool Confirm(string message, string? title = null) => true;
        public string? PickFolder(string? initialDirectory, string title) => null;
        public void OpenInExplorer(string path) { }
        public void CopyToClipboard(string text) { }
    }
}
