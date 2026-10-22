using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DirPorter.App.Services;
using DirPorter.App.ViewModels;
using DirPorter.Core.Journal;
using DirPorter.Core.Links;

namespace DirPorter.UiProbe;

/// <summary>
/// 界面布局探针。
///
/// 存在理由：WPF 里"内容被压扁到看不见"这类缺陷**不会报错**——布局照样成功，
/// 只是某个元素拿到了 0～几十像素的高度。肉眼看截图也容易漏（分不清"空"与"被压扁"）。
/// 所以需要一个能同时给出 **离屏渲染图** 与 **逐元素实测尺寸** 的工具：
///
///   dirporter-uiprobe [输出目录]        # 默认 <程序目录>/snapshots
///
/// 渲染尺寸刻意取 1220x700 —— 主窗口 1220x780 减去标题栏与内容区边距后的**真实可用区域**，
/// 这样量出来的数值就是用户实际看到的数值。
/// </summary>
internal static class Program
{
    private const double ContentWidth = 1220;
    private const double ContentHeight = 700;

    [STAThread]
    private static int Main(string[] args)
    {
        var outDir = args.Length > 0
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "snapshots");

        // 探针不得污染真实数据目录（会写 ui-settings / runs）。
        Environment.SetEnvironmentVariable(DirPorter.Core.Config.DirPorterPaths.DataRootEnvironmentVariable,
            Path.Combine(Path.GetTempPath(), "dirporter-uiprobe-" + Guid.NewGuid().ToString("N")[..8]));

        Directory.CreateDirectory(outDir);

        var report = new StringBuilder();
        var allOk = true;
        report.AppendLine("=== DirPorter UI 探针 ===");
        report.AppendLine($"渲染尺寸：{ContentWidth} x {ContentHeight}（主窗口 1220x780 的内容区实测值）");
        report.AppendLine($"输出目录：{outDir}");
        report.AppendLine();

        try
        {
            var app = new DirPorter.App.App();
            app.InitializeComponent();          // 必须：加载 App.xaml 的 Application.Resources（Shared.xaml）

            // 抓凶（R12.1 侦查）：谁把 Application 弄进关闭流程，直接打调用栈。
            app.Dispatcher.ShutdownStarted += (_, _) =>
                report.AppendLine("!! [抓凶] Dispatcher.ShutdownStarted：" + Environment.StackTrace);
            app.Dispatcher.ShutdownFinished += (_, _) => report.AppendLine("!! [抓凶] Dispatcher.ShutdownFinished");
            app.Exit += (_, _) => report.AppendLine("!! [抓凶] Application.Exit：" + Environment.StackTrace);
            report.AppendLine($"诊断：ShutdownMode={app.ShutdownMode}, MainWindow={app.MainWindow?.GetType().Name ?? "null"}");

            // 抓凶：任何 Window 的出现都会经过这个类级处理器。
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((sender, _) =>
                    report.AppendLine($"!! [抓凶] Window 出现：{sender.GetType().FullName}")));

            // 修复：探针是无头宿主，绝不能因为"某个（内部）窗口关闭"触发 OnLastWindowClose
            // 把 Application 送进关闭流程（之后任何 LoadComponent 都会抛"应用程序对象正在关闭"）。
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            ThemeService.Apply(ThemeService.Light);

            var host = new AppHost(new SilentInteraction(), new UiSettings());
            Seed(host);

            var pages = new (string Name, Func<FrameworkElement> Factory, object DataContext)[]
            {
                ("scan", () => new DirPorter.App.Views.ScanView(), host.Main.Scan),
                ("quick", () => new DirPorter.App.Views.QuickMigrateView(), host.Main.Quick),
                ("migrate", () => new DirPorter.App.Views.MigrateView(), host.Main.Migrate),
                ("link", () => new DirPorter.App.Views.LinkView(), host.Main.Link),
                ("history", () => new DirPorter.App.Views.HistoryView(), host.Main.History),
                ("settings", () => new DirPorter.App.Views.SettingsView(), host.Main.Settings),
            };

            foreach (var (name, factory, dataContext) in pages)
            {
                report.AppendLine($"--- 页面 {name} 处理前：HasShutdownStarted={app.Dispatcher.HasShutdownStarted} ---");

                var view = factory();
                view.DataContext = dataContext;

                Layout(view);
                Render(view, Path.Combine(outDir, $"{name}.png"));
                report.AppendLine(Measure(name, view));
                allOk &= AssertDataGridsRendered(name, view, report);
                report.AppendLine();
            }

            // 外壳（含左侧导航）：必须 Show 才能拿到 Window 的布局
            var window = new DirPorter.App.MainWindow { DataContext = host.Main };
            window.Show();
            window.UpdateLayout();
            Pump();
            window.UpdateLayout();
            Pump();

            Render(window, Path.Combine(outDir, "shell.png"));
            report.AppendLine("=== shell（完整主窗口）===");
            report.AppendLine(RenderSize(window));
            report.AppendLine(Measure("shell", window));
            allOk &= AssertDataGridsRendered("shell", window, report);

            // R12.3：主窗口 1220 档（用户现场）——四表必须"列合计 ≡ 表格控件宽"（右侧零空白）。
            foreach (var key in new[] { "scan", "migrate", "link", "history" })
            {
                host.Main.NavigateTo(key);
                Pump();
                if (key == "history") SeedHistory(host.Main.History);
                if (key == "link") SeedLink(host.Main.Link);
                window.UpdateLayout();
                Pump();

                foreach (var grid in Descendants(window).OfType<DataGrid>()
                             .Where(static g => g.IsVisible && g.ActualWidth > 0))
                {
                    var colSum = grid.Columns.Sum(static c => c.ActualWidth);
                    var blank = grid.ActualWidth - colSum;
                    report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  [1220|{0}] 表格宽 {1:0.#}，列合计 {2:0.#}，右侧空白 {3:0.#}",
                        key, grid.ActualWidth, colSum, blank));

                    if (blank > 26)
                    {
                        report.AppendLine("    !! 主窗口下右侧仍有空白——代码驱动列宽未生效");
                        allOk = false;
                    }


                }
            }

            // R12.1：窄窗档（860x540）+ 逐页渲染 + 表格宽度/列宽数据 + 滚轮事件链实测。
            window.Width = 860;
            window.Height = 540;
            window.UpdateLayout();
            Pump();
            window.UpdateLayout();

            // 偶发加固：窗口句柄刚创建的窗口在无消息循环环境里第一次 resize 可能未落地——幂等重试一次。
            if (window.ActualWidth > 865 || window.ActualHeight > 545)
            {
                window.Width = 860;
                window.Height = 540;
                window.UpdateLayout();
                Pump();
                window.UpdateLayout();
            }

            if (window.ActualWidth > 865 || window.ActualHeight > 545)
            {
                report.AppendLine($"  !! [narrow] 窗口未缩到位（实际 {window.ActualWidth:0}x{window.ActualHeight:0}）——最小尺寸限制仍在？");
                allOk = false;
            }
            else
            {
                report.AppendLine("  [OK] 窗口可缩至 860x540（内容区滚动兜底）。");
            }

            foreach (var key in new[] { "quick", "scan", "migrate", "link", "history" })
            {
                host.Main.NavigateTo(key);
                Pump();   // 懒加载（记录/链接页首次进入才加载）先跑完

                // 懒加载会清空种子数据（探针数据根为空）→ 重新灌一次，保证页面有内容可渲染。
                if (key == "history") SeedHistory(host.Main.History);
                if (key == "link") SeedLink(host.Main.Link);

                window.UpdateLayout();
                Pump();
                window.UpdateLayout();

                Render(window, Path.Combine(outDir, $"narrow-{key}.png"));

                report.AppendLine($"=== narrow-{key}（860x540，视口约 635）===");
                if (window.FindName("PageHost") is FrameworkElement narrowHost)
                {
                    report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  PageHost: {0:0.#} x {1:0.#}", narrowHost.ActualWidth, narrowHost.ActualHeight));

                    // 撑宽元凶：期望宽 Top N 的控件级元素（被拉伸的容器期望小 → 剩下的大期望就是需求源）。
                    foreach (var wide in Descendants(narrowHost).OfType<FrameworkElement>()
                                 .Where(static e => e.DesiredSize.Width > 300)
                                 .Where(static e => e is not (ContentPresenter or ScrollViewer or Grid or Border or DockPanel))
                                 .OrderByDescending(static e => e.DesiredSize.Width)
                                 .Take(8))
                    {
                        var label = string.IsNullOrEmpty(wide.Name) ? wide.GetType().Name : $"{wide.GetType().Name}[{wide.Name}]";
                        var text = wide is TextBlock tb
                            ? "「" + (tb.Text.Length > 30 ? tb.Text[..30] : tb.Text) + "」"
                            : wide is Panel panel ? "含文本「" + FirstTextIn(panel) + "」" : string.Empty;
                        report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                            "    · 期望 {0,7:0.#}  {1}{2}", wide.DesiredSize.Width, label, text));
                    }
                }

                foreach (var grid in Descendants(window).OfType<DataGrid>())
                {
                    var colSum = grid.Columns.Sum(static c => c.ActualWidth);
                    var inner = Descendants(grid).OfType<ScrollViewer>().FirstOrDefault();
                    var scrollable = inner?.ScrollableWidth ?? 0;
                    var viewport = inner?.ViewportWidth ?? grid.ActualWidth;

                    report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  DataGrid: 实际宽 {0:0.#}，列合计 {1:0.#}，视口 {2:0.#}，横滚 {3:0.#}，可见={4}",
                        grid.ActualWidth, colSum, viewport, scrollable, grid.IsVisible));

                    if (grid.IsVisible && grid.ActualWidth > 0)
                    {
                        DumpColumns(report, grid);

                        // R12.2 弹性列验收：860 档（视口 ≥ 600）下不允许出现横向滚动。
                        if (scrollable > 1)
                        {
                            report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                                "    !! 表格出现横向滚动（{0:0.#}px，视口 {1:0.#}）——弹性列未达标", scrollable, viewport));
                            allOk = false;
                        }

                        // R12.3 判据修正：铺满基准 = **表格控件宽**（ActualWidth），不是内部滚动视口。
                        // 旧判据用 viewport——在"列被压 Min、右侧空白"的缺陷态下视口恒等于列合计，
                        // 断言永远通过（这是 R12.2 误判"已修复"的根因）。容差 26 = 边框 + 纵向滚动条。
                        var blank = grid.ActualWidth - colSum;
                        if (blank > 26)
                        {
                            report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                                "    !! 右侧空白 {0:0.#}px（列合计 {1:0.#} vs 表格宽 {2:0.#}）——列宽未铺满",
                                blank, colSum, grid.ActualWidth));
                            allOk = false;
                        }
                    }
                }
            }

            // 滚轮链实测：在 history 表格上向下滚一格，外层 ScrollViewer 应滚动。
            // （表格自身无纵向滚动范围；若外层偏移不变，说明滚轮事件被表格吞掉。）
            {
                var pageHost = window.FindName("PageHost") as FrameworkElement;
                var outerScroll = FindAncestorScrollViewer(pageHost);

                if (pageHost is not null && outerScroll is not null)
                {
                    var wheelTarget = Descendants(pageHost).OfType<DataGrid>().FirstOrDefault() ?? pageHost;
                    var before = outerScroll.VerticalOffset;

                    wheelTarget.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                    {
                        RoutedEvent = UIElement.MouseWheelEvent,
                    });
                    Pump();

                    var after = outerScroll.VerticalOffset;
                    report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "=== 滚轮链测试（history 表格上向下滚一格）：外层偏移 {0:0.#} → {1:0.#}", before, after));

                    if (after > before)
                    {
                        report.AppendLine("  [OK] 滚轮触发了外层滚动。");
                    }
                    else
                    {
                        report.AppendLine("  !! 滚轮在表格上被吞（外层未滚动）——需要链式滚轮处理。");
                        allOk = false;
                    }
                }
                else
                {
                    report.AppendLine($"  !! 滚轮测试跳过（pageHost={pageHost is not null}，outerScroll={outerScroll is not null}）");
                    allOk = false;
                }
            }

            // R12.2：极限档（660x480 = 最小窗口）——"左右移动不受限制"与"偏移自动归零"回归。
            window.Width = 660;
            window.Height = 480;
            window.UpdateLayout();
            Pump();
            window.UpdateLayout();

            if (window.ActualWidth > 665)
            {
                window.Width = 660;
                window.Height = 480;
                window.UpdateLayout();
                Pump();
            }

            report.AppendLine("=== 极限档 660x480（最小窗口）===");
            foreach (var key in new[] { "scan", "migrate", "link", "history" })
            {
                host.Main.NavigateTo(key);
                Pump();
                if (key == "history") SeedHistory(host.Main.History);
                if (key == "link") SeedLink(host.Main.Link);
                window.UpdateLayout();
                Pump();
                window.UpdateLayout();

                if (key == "history") Render(window, Path.Combine(outDir, "extreme-history.png"));

                foreach (var grid in Descendants(window).OfType<DataGrid>()
                             .Where(static g => g.IsVisible && g.ActualWidth > 0))
                {
                    var inner = Descendants(grid).OfType<ScrollViewer>().FirstOrDefault();
                    if (inner is null) continue;

                    if (inner.ScrollableWidth <= 1)
                    {
                        report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                            "  [{0}] 无横滚（弹性列铺满，视口 {1:0.#}）", key, inner.ViewportWidth));
                        continue;
                    }

                    // "不受限制"：横向滚动必须两端可达。
                    inner.ScrollToHorizontalOffset(double.MaxValue);
                    Pump();
                    var atRight = inner.HorizontalOffset >= inner.ScrollableWidth - 0.5;

                    inner.ScrollToHorizontalOffset(0);
                    Pump();
                    var atLeft = inner.HorizontalOffset <= 0.5;

                    report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  [{0}] 横滚量 {1:0.#}px：到最右={2}、回最左={3}",
                        key, inner.ScrollableWidth, atRight ? "OK" : "失败", atLeft ? "OK" : "失败"));

                    if (!atRight || !atLeft) allOk = false;
                }
            }

            // "不受限制"的机器证明 + "拉宽归零"回归：把记录表临时强制到 240px 宽（必然产生横滚），
            // 验证两端可达；随后恢复宽度并断言滚动偏移自动归零（不允许残留"假空白"）。
            {
                host.Main.NavigateTo("history");
                Pump();
                window.UpdateLayout();

                var grid = Descendants(window).OfType<DataGrid>().FirstOrDefault(static g => g.IsVisible);
                var inner = grid is null ? null : Descendants(grid).OfType<ScrollViewer>().FirstOrDefault();

                if (grid is not null && inner is not null)
                {
                    grid.Width = 240;
                    window.UpdateLayout();
                    Pump();

                    report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "=== 强制窄测试（记录表压到 240px，必然横滚）：横滚量 {0:0.#} ===", inner.ScrollableWidth));

                    inner.ScrollToHorizontalOffset(double.MaxValue);
                    Pump();
                    var atRight = inner.HorizontalOffset >= inner.ScrollableWidth - 0.5;

                    inner.ScrollToHorizontalOffset(0);
                    Pump();
                    var atLeft = inner.HorizontalOffset <= 0.5;

                    report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  两端可达：到最右={0}、回最左={1}（不受限制要求：均为 OK）",
                        atRight ? "OK" : "失败", atLeft ? "OK" : "失败"));
                    if (!atRight || !atLeft) allOk = false;

                    // 滚到最右 → 恢复宽度（模拟"把窗口拉大"）→ 偏移必须自动归零。
                    inner.ScrollToHorizontalOffset(double.MaxValue);
                    Pump();
                    var rightOffset = inner.HorizontalOffset;

                    grid.ClearValue(FrameworkElement.WidthProperty);
                    window.UpdateLayout();
                    Pump();

                    var after = inner.HorizontalOffset;
                    report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  拉宽归零：最右 {0:0.#} → 恢复后 {1:0.#}（要求 0）", rightOffset, after));

                    if (after > 0.5 && inner.ScrollableWidth <= 0.5)
                    {
                        report.AppendLine("  !! 偏移残留（假空白态）——需要收敛处理");
                        allOk = false;
                    }
                    else
                    {
                        report.AppendLine("  [OK] 偏移自动归零（无残留空白）。");
                    }
                }
            }

            window.Close();
        }
        catch (Exception ex)
        {
            report.AppendLine("!! 探针失败：" + ex);
            File.WriteAllText(Path.Combine(outDir, "report.txt"), report.ToString(), Encoding.UTF8);
            Console.Error.WriteLine(report.ToString());
            return 1;
        }

        File.WriteAllText(Path.Combine(outDir, "report.txt"), report.ToString(), Encoding.UTF8);
        Console.WriteLine(report.ToString());
        return allOk ? 0 : 1;
    }

    // ------------------------------------------------------------------ 数据

    private static void Seed(AppHost host)
    {
        SeedQuick(host.Main.Quick);
        SeedScan(host.Main.Scan);
        SeedMigrate(host.Main.Migrate);
        SeedHistory(host.Main.History);
        SeedLink(host.Main.Link);
    }

    private static void SeedQuick(QuickMigrateViewModel vm)
    {
        vm.Sources.Add(new QuickSourceItem(@"C:\Users\31125\AppData\Local\Packages"));
        vm.Sources.Add(new QuickSourceItem(@"C:\Users\31125\AppData\Local\Microsoft"));
        Set(vm, nameof(QuickMigrateViewModel.TargetRoot), @"D:\Archive\Migrated");
        Set(vm, nameof(QuickMigrateViewModel.Status), "完成：2 项已迁移并建立链接。");
    }

    private static void SeedLink(LinkViewModel vm)
    {
        // 链接页的"已存在的链接"平时来自迁移记录；探针数据根为空，直接注入示例行（含长路径，验证截断）。
        vm.LinkPath = @"C:\Users\31125\AppData\Local\Packages";
        vm.TargetPath = @"D:\Archive\Migrated\Packages";

        if (vm.ExistingLinks.Count > 0) return;

        vm.ExistingLinks.Add(new ExistingLinkRow(
            @"C:\Users\31125\AppData\Local\Packages",
            @"D:\Archive\Migrated\Packages",
            "目录联接 (Junction)", "链接健康"));
        vm.ExistingLinks.Add(new ExistingLinkRow(
            @"C:\Users\31125\AppData\Local\Microsoft",
            @"D:\Archive\Migrated\Microsoft",
            "目录符号链接", "链接健康"));
    }

    private static void SeedScan(ScanViewModel vm)
    {
        vm.Roots.Add(new ScanRootItem(@"C:\Users\31125\AppData\Local", isPreset: true));
        vm.Roots.Add(new ScanRootItem(@"D:\Archive", isPreset: false));

        var rows = new List<ScanRow>
        {
            New(new ScanRow { Path = @"C:\Users\31125\AppData\Local", SizeBytes = 21_474_836_480, FileCount = 48_213, DirectoryCount = 3_114, IsRoot = true, HasRisk = true, RiskText = "嵌套目录链接×2、硬链接文件×7、备用数据流×13" }),
            New(new ScanRow { Path = @"C:\Users\31125\AppData\Local\Packages", SizeBytes = 9_126_805_504, FileCount = 21_002 }),
            New(new ScanRow { Path = @"C:\Users\31125\AppData\Local\Microsoft", SizeBytes = 6_871_947_264, FileCount = 15_331 }),
            New(new ScanRow { Path = @"C:\Users\31125\AppData\Local\Temp", SizeBytes = 3_221_225_472, FileCount = 4_120, IsMigrated = true }),
            New(new ScanRow { Path = @"C:\Users\31125\AppData\Local\Google", SizeBytes = 1_073_741_824, FileCount = 902 }),
            New(new ScanRow { Path = @"C:\Users\31125\AppData\Local\Programs", SizeBytes = 536_870_912, FileCount = 411 }),
        };

        // Results 只能由 ApplyFilter 填充，这里用反射注入（探针专用，不污染产品 API）
        var field = typeof(ScanViewModel).GetField("_all", BindingFlags.NonPublic | BindingFlags.Instance);
        var list = (IList)field!.GetValue(vm)!;
        foreach (var row in rows) list.Add(row);

        typeof(ScanViewModel).GetMethod("ApplyFilter", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(vm, null);

        Set(vm, nameof(ScanViewModel.Status), "扫描完成：48 213 个文件 / 3 114 个子目录 / 20.0 GB，耗时 4.12s。");
        Set(vm, nameof(ScanViewModel.Progress), 100.0);
    }

    private static ScanRow New(ScanRow row)
    {
        row.RefreshLocalization();
        return row;
    }

    private static void SeedMigrate(MigrateViewModel vm)
    {
        Set(vm, nameof(MigrateViewModel.TargetRoot), @"D:\Archive\Migrated");

        vm.Queue.Add(new QueueItem("e-01", @"C:\Users\31125\AppData\Local\Packages", @"D:\Archive\Migrated\Packages"));
        vm.Queue.Add(new QueueItem("e-02", @"C:\Users\31125\AppData\Local\Microsoft", @"D:\Archive\Migrated\Microsoft"));
        vm.Queue.Add(new QueueItem("e-03", @"C:\Users\31125\AppData\Local\Temp", @"D:\Archive\Migrated\Temp"));

        vm.Queue[0].ApplyState(DirPorter.Core.Migration.MigrationState.Done, 100, "迁移完成。");
        vm.Queue[1].ApplyState(DirPorter.Core.Migration.MigrationState.Copying, 42.5, "正在复制 packages.bin…");
        vm.Queue[2].ApplyState(DirPorter.Core.Migration.MigrationState.Planned, 0, null);

        Set(vm, nameof(MigrateViewModel.OverallPercent), 47.5);
        Set(vm, nameof(MigrateViewModel.SpeedText), "412.6 MB/s");
        Set(vm, nameof(MigrateViewModel.EtaText), "00:00:38");
        Set(vm, nameof(MigrateViewModel.StatusText), "执行中：e-02 正在复制（42.5%）");

        Set(vm, nameof(MigrateViewModel.PreviewText),
            "实际迁移 3 项  文件 41 435 个 / 17.0 GB\r\n\r\n[硬拒绝]\r\n  · [TGT_NOT_FIXED] 目标不是本地固定卷\r\n      建议：请选择本地磁盘（D:、E: 等）上的目录。\r\n\r\n[需确认]\r\n  · [SRC_WARN_PROGRAMFILES] 源位于警告名单：Program Files\r\n      建议：确认该目录未被正在运行的程序引用。\r\n\r\n[操作序列]\r\n  e-01: C:\\Users\\31125\\AppData\\Local\\Packages\r\n      → D:\\Archive\\Migrated\\Packages（Junction）");
    }

    private static void SeedHistory(HistoryViewModel vm)
    {
        vm.Records.Clear();

        for (var i = 0; i < 4; i++)
        {
            var record = new MigrationRecord
            {
                Id = $"mig-20261011-1930{i:D2}-a1b2c{i}-e-01",
                BatchId = $"mig-20261011-1930{i:D2}-a1b2c{i}",
                EntryId = "e-01",
                StartedUtc = DateTimeOffset.Now.AddHours(-i),
                FinishedUtc = DateTimeOffset.Now.AddHours(-i).AddSeconds(11),
                SourcePath = @"C:\Users\31125\AppData\Local\Packages",
                TargetPath = @"D:\Archive\Migrated\Packages",
                BackupPath = @"C:\Users\31125\AppData\Local\.migbak\Packages_20261011_1930",
                Status = i == 0 ? "done" : "rolled_back",
                LinkType = "Junction",
                FileCount = 21_002,
                TotalBytes = 9_126_805_504,
                ChecksumMethod = "xxHash3",
                Checksum = "a62b3cb3c81d5c59",
                AppVersion = "0.1.0.0",
            };

            var row = new HistoryRow(record);
            row.HealthText = LocalizationService.Instance.Get("hist.health.healthy");
            vm.Records.Add(row);
        }

        Set(vm, nameof(HistoryViewModel.Selected), vm.Records[0]);
    }

    private static void Set(object target, string property, object? value)
    {
        var prop = target.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
        if (prop is null || !prop.CanWrite) return;
        prop.SetValue(target, value);
    }

    // ------------------------------------------------------------------ 渲染与度量

    private static void Layout(FrameworkElement element)
    {
        element.Width = ContentWidth;
        element.Height = ContentHeight;
        element.Measure(new Size(ContentWidth, ContentHeight));
        element.Arrange(new Rect(0, 0, ContentWidth, ContentHeight));
        element.UpdateLayout();

        // ⚠️ 关键：DataGrid 的星号（*）列宽是在 LayoutUpdated / Loaded 这类**低优先级回调**里重算的。
        // 只做一次 Measure/Arrange 会量到"尚未分配"的中间态（星号列 = MinWidth），
        // 从而把"布局没收敛"误判成"列塌了"。所以必须把调度队列跑到空闲，再量。
        Pump();
    }

    /// <summary>把调度队列跑到 ContextIdle：所有更高优先级（Loaded/Render/DataBind…）的排队工作都会先执行完。</summary>
    private static void Pump()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;

        for (var i = 0; i < 4; i++)
        {
            dispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
            dispatcher.Invoke(static () => { }, DispatcherPriority.SystemIdle);
        }

        // 布局可能因为上一步的重算再次失效，再补一轮显式布局
        dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
    }

    private static string RenderSize(FrameworkElement element)
        => $"实际尺寸：{element.ActualWidth:0} x {element.ActualHeight:0}";

    private static void Render(FrameworkElement element, string path)
    {
        var width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth == 0 ? ContentWidth : element.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight == 0 ? ContentHeight : element.ActualHeight));

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>逐元素实测尺寸——重点看"该大的元素是不是被压扁了"。</summary>
    private static string Measure(string page, DependencyObject root)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== {page} ===");
        sb.AppendLine(RenderSize((FrameworkElement)root));
        sb.AppendLine("关键元素实测尺寸：");

        var interesting = new[]
        {
            typeof(DataGrid), typeof(ScrollViewer), typeof(Expander),
            typeof(ListBox), typeof(ItemsControl), typeof(ProgressBar), typeof(TextBox),
        };

        foreach (var node in Descendants(root))
        {
            if (node is not FrameworkElement element) continue;

            var type = element.GetType();
            if (!interesting.Any(t => t.IsAssignableFrom(type))) continue;

            // 只报"容器级"元素，避免把 DataGrid 内部几十个 ScrollViewer 都刷出来
            if (element.ActualHeight <= 0 && element.ActualWidth <= 0) continue;

            var label = string.IsNullOrEmpty(element.Name) ? type.Name : $"{type.Name}[{element.Name}]";
            var flag = element.ActualHeight is > 0 and < 80 ? "   ← 高度偏小，可能被压扁" : string.Empty;

            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  {0,-34} {1,7:0.#} x {2,-7:0.#}{3}", label, element.ActualWidth, element.ActualHeight, flag));

            // DataGrid 的关键在"每一列拿到多少宽度"——塌列现象只有这一层量得出来
            if (element is DataGrid grid) DumpColumns(sb, grid);
        }

        return sb.ToString();
    }

    /// <summary>逐列实际宽度。宽高都对但某列是 0/极小，就是"列塌了"。</summary>
    private static void DumpColumns(StringBuilder sb, DataGrid grid)
    {
        sb.AppendLine($"    ── {grid.Name switch { "" => "DataGrid", _ => grid.Name }} 列宽（表格宽 {grid.ActualWidth:0.#}）");

        foreach (var column in grid.Columns.OrderBy(static c => c.DisplayIndex))
        {
            var header = column.Header?.ToString() ?? "(空)";
            if (header.Length > 18) header = header[..18] + "…";

            var declared = column.Width.IsStar
                ? $"Star({column.Width.Value:0.##})"
                : column.Width.IsAuto ? "Auto" : $"Fixed({column.Width.DisplayValue:0.#})";

            var flag = column.ActualWidth < 8 ? "   ← 列已塌陷！" : string.Empty;

            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "      [{0,2}] {1,-20} 声明={2,-12} 实际={3,8:0.#} Min={4:0.#}{5}",
                column.DisplayIndex, header, declared, column.ActualWidth, column.MinWidth, flag));
        }
    }

    /// <summary>
    /// 渲染级断言（R11 事故的直接守卫）：每个 DataGridRow 必须真的含 DataGridCell。
    /// 背景：DataGridRow 模板若误用 ContentPresenter（缺 DataGridCellsPresenter），
    /// XAML 能解析、程序不报错，但整表不渲染任何单元格——只有布局后的可视树能揭示。
    /// </summary>
    private static bool AssertDataGridsRendered(string page, DependencyObject root, StringBuilder report)
    {
        var ok = true;

        foreach (var grid in Descendants(root).OfType<DataGrid>())
        {
            var rows = Descendants(grid).OfType<DataGridRow>().ToList();
            if (rows.Count == 0) continue;   // 空表不算失败（由空状态 UI 负责）

            var badRows = rows.Where(r => !Descendants(r).OfType<DataGridCell>().Any()).ToList();
            if (badRows.Count > 0)
            {
                report.AppendLine($"  !! [{page}] {badRows.Count}/{rows.Count} 行没有任何单元格 —— DataGridRow 模板缺少 DataGridCellsPresenter！");
                ok = false;
            }

            foreach (var column in grid.Columns.Where(static c => c.Width.IsStar && c.ActualWidth < 8))
            {
                report.AppendLine($"  !! [{page}] 星号列「{column.Header}」塌陷（实际宽 {column.ActualWidth:0.#}）");
                ok = false;
            }

            if (badRows.Count == 0)
                report.AppendLine($"  [OK] [{page}] DataGrid {rows.Count} 行 × {grid.Columns.Count} 列：单元格均已渲染。");
        }

        return ok;
    }

    /// <summary>面板内第一个非空文本（帮助识别"是谁在撑宽"）。</summary>
    private static string FirstTextIn(DependencyObject root)
    {
        foreach (var node in Descendants(root))
        {
            if (node is TextBlock { Text.Length: > 0 } textBlock)
                return textBlock.Text.Length > 24 ? textBlock.Text[..24] : textBlock.Text;
        }

        return "(无文本)";
    }

    /// <summary>沿视觉树向上找最近的 ScrollViewer（内容与 ScrollViewer 之间隔着 ScrollContentPresenter，不能只取直接父级）。</summary>
    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is ScrollViewer scrollViewer) return scrollViewer;
            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;

            foreach (var grandChild in Descendants(child)) yield return grandChild;
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
