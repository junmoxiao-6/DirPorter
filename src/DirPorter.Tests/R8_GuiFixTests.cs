using System.Threading;
using DirPorter.App.Services;
using DirPorter.App.ViewModels;
using DirPorter.Core.Links;
using Xunit;

namespace DirPorter.Tests;

/// <summary>
/// R8：用户实测反馈的四个问题的回归测试。
///
/// 覆盖的因果链（每条都能对应一个具体反馈）：
/// <list type="number">
/// <item>「移除扫描根目录」崩溃 → <c>ScanViewModel.RemoveRoot</c> 空引用（崩溃日志实证）。</item>
/// <item>「迁移队列无法正常展示」→ 布局预算被 Auto 行吃光（静态守卫 + 结构断言）。</item>
/// <item>「模块之间缺少关联」→ 扫描页「一键迁移并建链」端到端。</item>
/// <item>「点击目录复制路径 / 打开文件夹」→ 通用命令 + 行模型主路径。</item>
/// </list>
/// </summary>
[Collection(DataRootSequentialCollection.Name)]
public class R8_GuiFixTests
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

    // ================================================================ 1) 崩溃回归

    /// <summary>
    /// 回归用户实测崩溃：`ScanViewModel.RemoveRoot()` NullReferenceException。
    ///
    /// 根因是"先 `Roots.Remove(SelectedRoot)` 再读 `SelectedRoot.IsPreset`" ——
    /// 从集合里移除会让 ListBox 的 SelectedItem 变 null，双向绑定立刻把 null 回写，
    /// 于是紧接着的属性读取就空引用了（典型的数据绑定时序陷阱）。
    /// </summary>
    [Fact]
    public void 移除扫描根目录_不得空引用_并真的移除()
    {
        // 用「预设根目录」构造：IsPreset = true，因此不会写用户偏好文件，测试零副作用。
        var workspace = Path.Combine(Path.GetTempPath(), "dirporter-r8-remove-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workspace);

        try
        {
            var host = new AppHost(new RecordingInteraction(), new UiSettings());
            var scan = host.Main.Scan;

            var preset = new ScanPreset("scan.preset.programData", workspace);
            scan.AddPresetCommand.Execute(preset);

            Assert.Single(scan.Roots);

            // 关键前置：一定要有选中项，否则旧代码会因为 null 提前 return，测不出这个缺陷。
            scan.SelectedRoot = scan.Roots[0];

            var exception = Record.Exception(() => scan.RemoveRootCommand.Execute(null));

            Assert.Null(exception);
            Assert.Empty(scan.Roots);
            Assert.Null(scan.SelectedRoot);
        }
        finally
        {
            try { Directory.Delete(workspace, true); } catch { /* 清理失败不影响断言 */ }
        }
    }

    // ================================================================ 2) 布局回归（静态守卫）

    [Fact]
    public void 迁移页_队列必须保底高度且预览默认折叠()
    {
        var xaml = File.ReadAllText(Path.Combine(AppDirectory, "Views", "MigrateView.xaml"));

        // 队列区必须能撑住 —— 曾经因为 Auto 行太高被挤到只剩表头。
        Assert.Contains("MinHeight=\"150\"", xaml);

        // 预览区默认折叠：展开态的固定高度会直接吃掉队列的高度预算。
        Assert.Contains("IsExpanded=\"False\"", xaml);
        Assert.DoesNotContain("IsExpanded=\"True\"", xaml);
    }

    [Fact]
    public void 扫描页与记录页_表格也必须保底高度()
    {
        foreach (var (file, min) in new[] { ("ScanView.xaml", "150"), ("HistoryView.xaml", "150"), ("LinkView.xaml", "120") })
        {
            var xaml = File.ReadAllText(Path.Combine(AppDirectory, "Views", file));
            Assert.Contains($"MinHeight=\"{min}\"", xaml);
        }
    }

    [Fact]
    public void 主窗口_最小尺寸可继续缩小且内容由滚动兜底()
    {
        var xaml = File.ReadAllText(Path.Combine(AppDirectory, "MainWindow.xaml"));

        // 历史：780 → 700 → 540（R12）→ 480（R12.1 用户反馈"缩到固定值卡住"）。
        // 内容区由 ScrollViewer + PageHost（MinWidth 420）兜底，不再依赖大 MinHeight 防塌。
        Assert.Contains("MinHeight=\"480\"", xaml);
        Assert.Contains("MinWidth=\"660\"", xaml);
        Assert.Contains("ScrollViewer", xaml);
        Assert.Contains("Height=\"820\"", xaml);
    }

    // ================================================================ 3) 字号统一（静态守卫）

    /// <summary>
    /// 用户反馈「文本框与文字的字号未调整好」的根因是各处写字面量字号（12/13/14 混排）。
    /// 现在字号只能来自 <c>Themes/Shared.xaml</c> 里的四个标尺，任何视图都不许再写字面量。
    /// </summary>
    [Fact]
    public void 界面_不得再出现字面量字号_必须使用统一标尺()
    {
        // 只匹配"数字字面量"：FontSize="{StaticResource FontSizeBody}" 是合法用法，
        // 而 FontSize="12" 这种硬编码才是破坏统一标尺的元凶。
        var literal = new System.Text.RegularExpressions.Regex(@"FontSize\s*=\s*""\s*[0-9]");
        var offenders = new List<string>();

        foreach (var path in Directory.EnumerateFiles(AppDirectory, "*.xaml", SearchOption.AllDirectories))
        {
            var matches = literal.Matches(File.ReadAllText(path));
            if (matches.Count == 0) continue;

            offenders.Add($"{Path.GetFileName(path)}（{matches.Count} 处）");
        }

        Assert.True(offenders.Count == 0,
            "以下文件仍在使用数字字面量字号（会破坏统一标尺）：" + string.Join("、", offenders));
    }

    [Fact]
    public void 字号标尺_四个档位齐备且递增()
    {
        var shared = File.ReadAllText(Path.Combine(AppDirectory, "Themes", "Shared.xaml"));

        var title = ReadDouble(shared, "FontSizeTitle");
        var section = ReadDouble(shared, "FontSizeSection");
        var body = ReadDouble(shared, "FontSizeBody");
        var small = ReadDouble(shared, "FontSizeSmall");

        Assert.True(small < body, $"正文字号应大于次要字号：{body} / {small}");
        Assert.True(body < section, $"区块标题应大于正文：{section} / {body}");
        Assert.True(section < title, $"页面标题应大于区块标题：{title} / {section}");

        // 中文在 13.5px 以下是明显偏小的一档（用户反馈"字号未调整好"的取值区间）。
        Assert.True(body >= 13.0, $"正文字号偏小：{body}");
    }

    private static double ReadDouble(string xaml, string key)
    {
        var marker = $"x:Key=\"{key}\">";
        var index = xaml.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, $"字号标尺缺失：{key}");

        var start = index + marker.Length;
        var end = xaml.IndexOf('<', start);
        return double.Parse(xaml[start..end], System.Globalization.CultureInfo.InvariantCulture);
    }

    // ================================================================ 4) 一键迁移并建链（端到端）

    [Fact]
    public async Task 一键迁移并建链_源变成链接_目标有数据_并写入迁移记录()
    {
        var root = Path.Combine(Path.GetTempPath(), "dirporter-r8-oc-" + Guid.NewGuid().ToString("N")[..8]);
        var scanRoot = Path.Combine(root, "scan");
        var source = Path.Combine(scanRoot, "ProjectA");
        var targetRoot = Path.Combine(root, "target");

        Directory.CreateDirectory(Path.Combine(source, "sub"));
        File.WriteAllText(Path.Combine(source, "a.txt"), "alpha");
        File.WriteAllText(Path.Combine(source, "sub", "b.txt"), "beta");
        Directory.CreateDirectory(targetRoot);

        // ⚠️ 这个用例会做一次**真实迁移**，因此会写入共享数据根（migrations.json / runs / 台账）。
        // 用完必须还原（DataRootGuard），否则后续用例（如 R6 的 verify）会读到"源是链接、目标已消失"的残留记录而失败。
        using var guard = new DataRootGuard();

        try
        {
            var interaction = new RecordingInteraction();
            var host = new AppHost(interaction, new UiSettings());
            var scan = host.Main.Scan;

            // ---------- 扫描 ----------
            scan.AddPresetCommand.Execute(new ScanPreset("scan.preset.programData", scanRoot));
            await scan.ScanCommand.ExecuteAsync(null);

            var row = scan.Results.FirstOrDefault(r => string.Equals(r.Path, source, StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(row);
            Assert.False(row!.IsMigrated);

            // ---------- 一键（独立页）：源目录 → 目标根 → 一个按钮 ----------
            var quick = host.Main.Quick;
            quick.Sources.Add(new QuickSourceItem(source));
            quick.TargetRoot = targetRoot;
            quick.AcknowledgeWarnings = true;

            await quick.MigrateNowCommand.ExecuteAsync(null);

            // 源路径变成目录联接
            var linkInfo = host.Links.Inspect(source);
            Assert.True(linkInfo.IsKnownLink, $"源路径未变成链接：{linkInfo.Describe()}");
            Assert.Equal(LinkKind.Junction, linkInfo.Kind);
            Assert.False(linkInfo.IsDangling);

            // 目标位置有真实数据，且内容一致
            var target = Path.Combine(targetRoot, "ProjectA");
            Assert.True(Directory.Exists(target));
            Assert.Equal("alpha", File.ReadAllText(Path.Combine(target, "a.txt")));
            Assert.Equal("beta", File.ReadAllText(Path.Combine(target, "sub", "b.txt")));

            // 队列状态回填为成功
            Assert.All(host.Main.Migrate.Queue, q => Assert.Equal(DirPorter.Core.Migration.MigrationState.Done, q.State));

            // 迁移记录已落盘（记录页数据源）
            var record = host.Index.LoadAll().FirstOrDefault(r => string.Equals(r.SourcePath, source, StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(record);
            Assert.Equal("done", record!.Status);
            Assert.Equal(LinkKind.Junction.ToString(), record.LinkType);

            // 扫描页的"已迁移"标记已就地刷新（跨页状态一致；R12 起批次完成会通知扫描页）
            Assert.True(row.IsMigrated);
            // R12：显示文本必须同步（此前只在语言切换时重算，界面会停留在"否"）
            Assert.Equal(DirPorter.App.Services.LocalizationService.Instance.Get("common.yes"), row.MigratedText);
        }
        finally
        {
            SafeCleanup(root);
        }
    }

    [Fact]
    public void 一键迁移_目标根为空时不得静默什么都不做()
    {
        var interaction = new RecordingInteraction();
        var host = new AppHost(interaction, new UiSettings());
        var quick = host.Main.Quick;

        // 有源目录但目标根为空：必须给出明确提示，且不得崩溃。
        quick.Sources.Add(new QuickSourceItem(Path.GetTempPath()));
        var exception = Record.Exception(() => quick.MigrateNowCommand.Execute(null));
        Assert.Null(exception);
        Assert.NotEmpty(interaction.Messages);
    }

    // ================================================================ 5) 队列替换语义

    [Fact]
    public void 仅加入队列_是替换而不是追加()
    {
        var root = Path.Combine(Path.GetTempPath(), "dirporter-r8-q-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);

        try
        {
            var host = new AppHost(new RecordingInteraction(), new UiSettings());
            var migrate = host.Main.Migrate;
            migrate.TargetRoot = root;

            migrate.ReplaceQueue(new[] { Path.Combine(root, "A") });
            migrate.ReplaceQueue(new[] { Path.Combine(root, "B") });

            // 第二次替换必须清掉第一次的内容，否则"一键执行的就是本次勾选的这批"这句话就是假的。
            Assert.Single(migrate.Queue);
            Assert.EndsWith("B", migrate.Queue[0].SourcePath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    // ================================================================ 6) 路径交互

    [Fact]
    public void 四个行模型都实现了主路径接口()
    {
        var scanRow = new ScanRow { Path = @"C:\a\b" };
        Assert.Equal(@"C:\a\b", ((IPathRow)scanRow).PrimaryPath);

        var queueItem = new QueueItem("e-01", @"C:\old", @"D:\new");
        Assert.Equal(@"C:\old", ((IPathRow)queueItem).PrimaryPath);

        var linkRow = new ExistingLinkRow(@"C:\link", @"D:\real", "Junction", "healthy");
        Assert.Equal(@"C:\link", ((IPathRow)linkRow).PrimaryPath);
    }

    [Fact]
    public void 复制路径命令_会把路径交给交互层并在底部给出提示()
    {
        var interaction = new RecordingInteraction();
        var host = new AppHost(interaction, new UiSettings());

        host.Main.CopyPathCommand.Execute(@"C:\some\path");

        Assert.Equal(@"C:\some\path", interaction.LastClipboard);
        Assert.Contains(@"C:\some\path", host.Main.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void 打开路径命令_会把路径交给交互层()
    {
        var interaction = new RecordingInteraction();
        var host = new AppHost(interaction, new UiSettings());

        host.Main.OpenPathCommand.Execute(@"C:\some\path");

        Assert.Equal(@"C:\some\path", interaction.LastOpenedPath);
    }

    [Fact]
    public void 双击命中按钮时_不得触发打开路径()
    {
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                // WPF 控件必须在 STA 线程创建。
                var button = new System.Windows.Controls.Button();
                var text = new System.Windows.Controls.TextBlock();

                Assert.True(DataGridRowActions.IsInteractiveElementHit(button));
                Assert.False(DataGridRowActions.IsInteractiveElementHit(text));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));

        Assert.True(failure is null, failure?.ToString());
    }

    // ================================================================ 7) 跨页联动

    [Fact]
    public async Task 仅加入队列会切到迁移页_让用户看得到队列确实进去了()
    {
        var root = Path.Combine(Path.GetTempPath(), "dirporter-r8-nav-" + Guid.NewGuid().ToString("N")[..8]);
        var scanRoot = Path.Combine(root, "scan");
        var source = Path.Combine(scanRoot, "ProjectA");

        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.txt"), "alpha");

        try
        {
            var host = new AppHost(new RecordingInteraction(), new UiSettings());
            var main = host.Main;

            // 必须先扫描出可勾选的行 —— 没有行时命令会提前返回（这本身就是正确行为）。
            main.Scan.AddPresetCommand.Execute(new ScanPreset("scan.preset.programData", scanRoot));
            await main.Scan.ScanCommand.ExecuteAsync(null);

            var row = main.Scan.Results.FirstOrDefault(r => string.Equals(r.Path, source, StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(row);

            row!.IsSelected = true;
            main.Scan.AddToQueueCommand.Execute(null);

            // 链路：勾选 → 入队 → 自动跳到迁移页（否则用户会怀疑"按钮没反应"）
            Assert.Equal("migrate", main.SelectedNav.Key);
            Assert.Single(main.Migrate.Queue);
            Assert.Equal(source, main.Migrate.Queue[0].SourcePath);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void SafeCleanup(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        catch
        {
            // 迁移后源路径是链接，递归删除可能因链接而失败；测试清理失败不影响断言。
        }
    }

    /// <summary>记录调用痕迹的交互桩：不弹窗、不开资源管理器，但把参数留下来供断言。</summary>
    private sealed class RecordingInteraction : IUserInteraction
    {
        public string? LastClipboard { get; private set; }
        public string? LastOpenedPath { get; private set; }
        public List<string> Messages { get; } = new();

        public void Info(string message, string? title = null) => Messages.Add(message);
        public void Error(string message, string? title = null) => Messages.Add(message);
        public bool Confirm(string message, string? title = null) => true;
        public string? PickFolder(string? initialDirectory, string title) => null;
        public void OpenInExplorer(string path) => LastOpenedPath = path;
        public void CopyToClipboard(string text) => LastClipboard = text;
    }
}
