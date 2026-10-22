using System.Text.RegularExpressions;
using System.Threading;
using DirPorter.App.Services;
using Xunit;

namespace DirPorter.Tests;

/// <summary>
/// WPF 宿主的**界面契约测试**（R7）。
///
/// 为什么需要它：XAML 的大多数错误（资源键写错、本地化键漏定义、注释里出现非法双连字符）
/// **编译期完全不报错**，只在实际点开那个页面时炸。这类"点不到就发现不了"的缺陷
/// 必须靠静态扫描 + 真实实例化来兜住。
///
/// 三条防线：
/// <list type="number">
/// <item>静态扫描：XAML 里用到的每个 <c>Loc</c> 键 / <c>StaticResource</c> 键 / <c>DynamicResource</c> 画笔都必须有定义。</item>
/// <item>XML 合法性：xaml/manifest 注释里不得出现两个连续连字符（R6 的血泪教训）。</item>
/// <item>真实实例化：在 STA 线程上把五个页面与主窗口真的 new 出来（XAML 解析错误无处可藏）。</item>
/// </list>
/// </summary>
public class R7_XamlContractTests
{
    private static readonly string AppDirectory = LocateAppSourceDirectory();

    private static string LocateAppSourceDirectory()
    {
        // 从测试程序集位置向上找到仓库根（含 DirPorter.sln），再进 src\DirPorter.App
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

        throw new DirectoryNotFoundException("找不到 DirPorter.App 源码目录（DirPorter.sln 未定位到）。");
    }

    private static IEnumerable<string> XamlFiles()
        => Directory.EnumerateFiles(AppDirectory, "*.xaml", SearchOption.AllDirectories);

    private static string Read(string path) => File.ReadAllText(path);

    private static IEnumerable<(string Key, string File)> UsedLocKeys()
    {
        foreach (var path in XamlFiles())
        {
            var text = Read(path);
            var name = Path.GetFileName(path);

            foreach (Match match in Regex.Matches(text, @"markup:Loc\s+([A-Za-z0-9_.]+)"))
                yield return (match.Groups[1].Value, name);

            foreach (Match match in Regex.Matches(text, @"Loc\s+Key=""([^""]+)"""))
                yield return (match.Groups[1].Value, name);
        }
    }

    // ------------------------------------------------------------------ 1) 本地化键

    [Fact]
    public void 本地化_XAML用到的每个键都必须在表里有定义()
    {
        var known = LocalizationService.Keys;

        var missing = UsedLocKeys()
            .Where(item => !known.Contains(item.Key))
            .Select(item => $"{item.Key}（{item.File}）")
            .Distinct()
            .ToList();

        Assert.True(missing.Count == 0,
            "以下本地化键在 XAML 里被使用但未定义（界面会直接显示键名）：" + string.Join("、", missing));
    }

    [Fact]
    public void 本地化_每条键的中英文都必须非空()
    {
        var chinese = LocalizationService.ChineseTable;
        var english = LocalizationService.EnglishTable;

        Assert.Equal(chinese.Count, english.Count);
        Assert.True(chinese.Count > 100, "本地化表条目过少，疑似读取失败。");

        var emptyZh = chinese.Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key).ToList();
        var emptyEn = english.Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key).ToList();

        Assert.Empty(emptyZh);
        Assert.Empty(emptyEn);
    }

    [Fact]
    public void 本地化_语言切换后索引器返回对应语言()
    {
        var service = new LocalizationService();
        var key = "nav.scan";

        service.Language = LocalizationService.Languages.Chinese;
        Assert.Equal(LocalizationService.ChineseTable[key], service[key]);

        service.Language = LocalizationService.Languages.English;
        Assert.Equal(LocalizationService.EnglishTable[key], service[key]);

        // 未知语言回退到中文，不抛异常
        service.Language = "fr";
        Assert.Equal(LocalizationService.ChineseTable[key], service[key]);

        // 未定义的键回退为键名本身（界面上立刻能看出遗漏）
        Assert.Equal("no.such.key", service["no.such.key"]);
    }

    // ------------------------------------------------------------------ 2) 资源键

    private static HashSet<string> DefinedResourceKeys()
    {
        var defined = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in XamlFiles())
        {
            foreach (Match match in Regex.Matches(Read(path), @"x:Key=""([^""]+)"""))
                defined.Add(match.Groups[1].Value);
        }

        return defined;
    }

    [Fact]
    public void 资源_XAML用到的每个StaticResource都必须有定义()
    {
        var defined = DefinedResourceKeys();

        var missing = new List<string>();
        foreach (var path in XamlFiles())
        {
            foreach (Match match in Regex.Matches(Read(path), @"\{StaticResource\s+([A-Za-z0-9_.]+)\}"))
            {
                if (!defined.Contains(match.Groups[1].Value))
                    missing.Add($"{match.Groups[1].Value}（{Path.GetFileName(path)}）");
            }
        }

        Assert.True(missing.Count == 0,
            "以下 StaticResource 键无定义（页面加载时会抛异常）：" + string.Join("、", missing.Distinct()));
    }

    [Fact]
    public void 主题_每个DynamicResource画笔都必须由深浅主题提供()
    {
        var brushes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var theme in new[] { "Light.xaml", "Dark.xaml" })
        {
            var path = Path.Combine(AppDirectory, "Themes", theme);
            Assert.True(File.Exists(path), $"缺少主题文件：{path}");

            foreach (Match match in Regex.Matches(Read(path), @"x:Key=""([^""]+)"""))
                brushes.Add(match.Groups[1].Value);
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in XamlFiles())
        {
            foreach (Match match in Regex.Matches(Read(path), @"\{DynamicResource\s+([A-Za-z0-9_.]+)\}"))
                used.Add(match.Groups[1].Value);
        }

        var missing = used.Where(key => !brushes.Contains(key)).ToList();
        Assert.True(missing.Count == 0,
            "以下画笔被 DynamicResource 引用但主题未定义：" + string.Join("、", missing));
    }

    [Fact]
    public void 主题_深浅两套必须定义同一组画笔键()
    {
        static HashSet<string> KeysOf(string path)
            => Regex.Matches(File.ReadAllText(path), @"x:Key=""([^""]+)""")
                .Select(m => m.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);

        var light = KeysOf(Path.Combine(AppDirectory, "Themes", "Light.xaml"));
        var dark = KeysOf(Path.Combine(AppDirectory, "Themes", "Dark.xaml"));

        // 深色少一个键 → 切到深色时那个画笔丢失（DynamicResource 静默失败，界面发白）。
        Assert.Equal(light.OrderBy(k => k, StringComparer.Ordinal), dark.OrderBy(k => k, StringComparer.Ordinal));
    }

    // ------------------------------------------------------------------ 3) XML 合法性

    [Fact]
    public void XML注释里不得出现两个连续连字符()
    {
        var offenders = new List<string>();

        foreach (var path in XamlFiles().Concat(Directory.EnumerateFiles(AppDirectory, "*.manifest")))
        {
            var text = Read(path);
            foreach (Match match in Regex.Matches(text, @"<!--(.*?)-->", RegexOptions.Singleline))
            {
                if (match.Groups[1].Value.Contains("--", StringComparison.Ordinal))
                    offenders.Add(Path.GetFileName(path));
            }
        }

        // R6：CLI 清单曾因注释里写了命令行参数的双连字符形式，导致 exe 报"并行配置不正确"。
        Assert.True(offenders.Count == 0,
            "以下文件的 XML 注释含非法双连字符（会让清单/资源字典非法）：" + string.Join("、", offenders));
    }

    [Fact]
    public void 七个页面_DataTemplate与View文件一一对应()
    {
        var appXaml = Read(Path.Combine(AppDirectory, "App.xaml"));

        var viewModels = Regex.Matches(appXaml, @"DataType=""\{x:Type vm:(\w+)\}""")
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.Equal(7, viewModels.Count);
        Assert.Contains("QuickMigrateViewModel", viewModels);
        Assert.Contains("ScanViewModel", viewModels);
        Assert.Contains("MigrateViewModel", viewModels);
        Assert.Contains("LinkViewModel", viewModels);
        Assert.Contains("HistoryViewModel", viewModels);
        Assert.Contains("LinkOverviewViewModel", viewModels);
        Assert.Contains("SettingsViewModel", viewModels);

        foreach (var name in new[] { "QuickMigrateView", "ScanView", "MigrateView", "LinkView", "HistoryView", "LinkOverviewView", "SettingsView" })
        {
            Assert.True(File.Exists(Path.Combine(AppDirectory, "Views", name + ".xaml")), $"缺少视图：{name}.xaml");
            Assert.Contains($"views:{name}", appXaml);
        }
    }

    [Fact]
    public void 应用清单_必须是合法XML且声明DPI与长路径()
    {
        foreach (var name in new[] { "app.manifest", "app.admin.manifest" })
        {
            var path = Path.Combine(AppDirectory, name);
            Assert.True(File.Exists(path), $"缺少清单：{name}");

            var document = System.Xml.Linq.XDocument.Load(path);   // 非法即抛
            Assert.NotNull(document);
            Assert.Contains("PerMonitorV2", File.ReadAllText(path));
            Assert.Contains("longPathAware", File.ReadAllText(path));
        }
    }

    // ------------------------------------------------------------------ 4) 真实实例化

    /// <summary>
    /// 在 STA 线程上把六个页面与主窗口真的 new 出来。
    /// 这一步能把"编译期不报错、运行时才炸"的 XAML 缺陷（资源解析、类型不匹配）全部暴露出来。
    /// </summary>
    [Fact]
    public void 界面_六个页面与主窗口都能在STA线程上成功构造()
    {
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                // ⚠️ 必须用真正的 App 并调用 InitializeComponent()：
                // 裸 new Application() 不会加载 App.xaml 的 Application.Resources，
                // 于是 Shared.xaml 里的样式（PageTitle / Card / Hint …）全都找不到，
                // 五个页面会集体抛"无法找到资源"。测试必须复刻生产路径，否则测的是个假环境。
                if (System.Windows.Application.Current is null)
                {
                    var app = new DirPorter.App.App();
                    app.InitializeComponent();
                }

                ThemeService.Apply(ThemeService.Light);

                // 六个页面（R12 起含独立的一键迁移页）
                _ = new DirPorter.App.Views.QuickMigrateView();
                _ = new DirPorter.App.Views.ScanView();
                _ = new DirPorter.App.Views.MigrateView();
                _ = new DirPorter.App.Views.LinkView();
                _ = new DirPorter.App.Views.HistoryView();
                _ = new DirPorter.App.Views.SettingsView();

                // 主窗口（含左侧导航模板）
                var host = new AppHost(new FakeUserInteraction(), new UiSettings());
                var window = new DirPorter.App.MainWindow { DataContext = host.Main };

                // 刻意不调用 Show()/Measure()：测试环境没有消息泵，只是验证 XAML 能解析、资源能解析。
                Assert.NotNull(window.DataContext);
                window.Close();

                // 深色主题同样能切换（资源字典可替换）
                ThemeService.Apply(ThemeService.Dark);
                Assert.Equal(ThemeService.Dark, ThemeService.Current);

                ThemeService.Apply(ThemeService.Light);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromMinutes(2)), "界面构造超时。");
        Assert.True(failure is null, "界面构造失败：" + failure);
    }

    /// <summary>测试用交互桩：不弹窗、不开资源管理器。</summary>
    private sealed class FakeUserInteraction : IUserInteraction
    {
        public void Info(string message, string? title = null) { }
        public void Error(string message, string? title = null) { }
        public bool Confirm(string message, string? title = null) => false;
        public string? PickFolder(string? initialDirectory, string title) => null;
        public void OpenInExplorer(string path) { }
        public void CopyToClipboard(string text) { }
    }
}
