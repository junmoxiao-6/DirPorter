using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DirPorter.App.Services;
using DirPorter.App.ViewModels;

namespace DirPorter.App;

/// <summary>
/// 主窗口（外壳）。DataContext 由 App.OnStartup 注入 MainViewModel。
///
/// R12.3：滚轮统一入口 —— 内容区包了 ScrollViewer，表格/列表内部还有各自的滚动器，
/// 各种"滚轮被内层吞掉 / 无内层时外层不响应"的组合会导致"滚轮完全没反应"。
/// 这里在窗口级（隧道最早时机）统一裁决：
///   · 鼠标下最近的内层滚动器在该方向还有余量 → 放行（由内层自己滚，保持"先滚表格"）；
///   · 内层没有 / 到头 / 无滚动范围 → 直接滚动主内容区（页面），并标记已处理。
/// 结果：任何页面（含无表格的设置页）滚轮都必然生效，且不会双重滚动。
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        AddHandler(PreviewMouseWheelEvent, new MouseWheelEventHandler(OnPreviewMouseWheel), handledEventsToo: true);

        // R12.3：弹性列幅在主窗口三个确定性时机重算（加载完成 / 尺寸变化 / 页面切换）。
        // 只靠 DataGrid 自身的 SizeChanged 会漏掉"页面重建后再灌数据"的时机，
        // 实测表现为列宽停在 MinWidth、右侧留白。
        Loaded += (_, _) =>
        {
            ReflowColumns();

            // R12.10：启动后按设置的保留天数清理到期备份（后台执行）。
            (DataContext as DirPorter.App.ViewModels.MainViewModel)?.RunStartupMaintenance();
        };
        SizeChanged += (_, _) => ReflowColumns();
        DataContextChanged += (_, _) => HookPageChanges();

        // 兜底：页面布局变化时按 200ms 冷却重算（跟随"数据灌入后宽度才变化"的时序，不会自旋）。
        if (FindName("PageHost") is FrameworkElement pageHost)
        {
            pageHost.LayoutUpdated += (_, _) => ElasticColumns.SyncIfNeeded(pageHost);
        }

    }

    private void HookPageChanges()
    {
        if (DataContext is not INotifyPropertyChanged source) return;

        source.PropertyChanged -= OnShellPropertyChanged;
        source.PropertyChanged += OnShellPropertyChanged;
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.CurrentPage)) ReflowColumns();
    }

    /// <summary>
    /// 重算全部弹性列。分三拍（立即 / 120ms / 400ms）：页面切换后表格往往先 0 宽、
    /// 灌入数据后才有真实宽度；只跑一次会在"早期宽度"上定型。三拍后不再重算，不会自旋。
    /// </summary>
    private void ReflowColumns()
    {
        foreach (var priority in new[] { DispatcherPriority.Background, DispatcherPriority.Loaded, DispatcherPriority.ContextIdle })
        {
            Dispatcher.BeginInvoke(new Action(() => ElasticColumns.ApplyAll(this)), priority);
        }
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;

        var chain = CollectScrollViewers(e.OriginalSource as DependencyObject);

        if (chain.Count == 0)
        {
            return;   // 不在任何滚动容器内（例如左侧导航、顶部横幅）——交给默认处理
        }

        var inner = chain[0];      // 鼠标下最近的滚动器
        var outer = chain[^1];     // 最外层（主内容区 ScrollViewer）

        if (inner != outer)
        {
            var up = e.Delta > 0;
            var canScrollInner = up
                ? inner.VerticalOffset > 0.5
                : inner.VerticalOffset < inner.ScrollableHeight - 0.5;

            if (canScrollInner) return;   // 内层还能滚：放行，由内层处理（不干预）
        }

        if (outer.ScrollableHeight <= 0) return;   // 页面本身不可滚（内容未超高）

        outer.ScrollToVerticalOffset(outer.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    /// <summary>从原始命中元素沿视觉树向上收集所有 ScrollViewer（由内到外）。</summary>
    private static List<ScrollViewer> CollectScrollViewers(DependencyObject? node)
    {
        var chain = new List<ScrollViewer>();

        while (node is not null)
        {
            if (node is ScrollViewer scrollViewer) chain.Add(scrollViewer);
            node = VisualTreeHelper.GetParent(node);
        }

        return chain;
    }
}
