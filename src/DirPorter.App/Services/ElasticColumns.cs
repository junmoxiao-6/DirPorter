using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace DirPorter.App.Services;

/// <summary>
/// 代码驱动列宽（R12.3）+ 列边界拖拽（R12.8）。
///
/// 背景：WPF DataGrid 的星列（Width="k*"）在页面 DataTemplate 重建 / 延迟布局时，
/// 会以"某次极小宽"为基准把列压到 MinWidth 后**不再重算** —— 实测表现：
/// 列全压 Min、右侧留大片空白、表格丧失滚动量（"左右移动"失效）。
/// 星列算法不可控，故改为代码确定性计算。
///
/// 用法：DataGrid 上挂 Enabled=True，并用 StarWeights 声明与列一一对应的权重
/// （如 "0,4,1,1,1,2,1"）：
///   · 权重 0  = 固定列（保持 XAML 声明宽，如勾选列 30px）；
///   · 权重 >0 = 弹性列（按比例分得"可用宽 − 固定列"）。
///
/// 性质：
///   · 每列下限取列自身 MinWidth；Σ列宽恒等于表格控件宽（右侧永不留空白）；
///   · 窗口拉宽时列同步变宽（不设上限，"不受任何限制"）；
///   · 仅当 ΣMin 超过可用宽时才出现横向滚动（此时滚动范围不受限）。
///
/// R12.8 列边界拖拽（相邻两列此消彼长）：
///   · 用系统自带的列头拖拽手柄（鼠标在边界显示 SizeWE 双向箭头）；
///   · **拖拽开始**（DragStarted）时快照各列实际宽 —— 这是唯一可靠的"起点"，
///     此前用"上一次分配值"当基准，拖动过程中基准始终不变，导致被拖列被反复写回、
///     表现为"拖不动"；
///   · 拖动中只调整**邻列**（被拖列由系统拖拽放在鼠标位置，不再二次写入，
///     否则会与拖拽打架），两者合计保持不变；
///   · 邻列触底（MinWidth）时把被拖列夹回，绝不拖穿；
///   · **手动模式只在真实拖拽开始时进入**（此前任何列宽变化都进手动模式，
///     造成自动铺满永久停止、右侧留大片空白）；
///   · 手动模式下列表宽度变化仍按"最后一列吸收差额"重新铺满，所以不会留白。
/// </summary>
public static class ElasticColumns
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled",
            typeof(bool),
            typeof(ElasticColumns),
            new PropertyMetadata(false, OnEnabledChanged));

    public static readonly DependencyProperty StarWeightsProperty =
        DependencyProperty.RegisterAttached(
            "StarWeights",
            typeof(string),
            typeof(ElasticColumns),
            new PropertyMetadata(string.Empty, OnWeightsChanged));

    /// <summary>上次分配时使用的表格宽（用于"宽度是否变了"判定，避免无谓重算）。</summary>
    private static readonly DependencyProperty LastAppliedWidthProperty =
        DependencyProperty.RegisterAttached(
            "LastAppliedWidth",
            typeof(double),
            typeof(ElasticColumns),
            new PropertyMetadata(0d));

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    public static void SetStarWeights(DependencyObject element, string value) => element.SetValue(StarWeightsProperty, value);

    public static string GetStarWeights(DependencyObject element) => (string)element.GetValue(StarWeightsProperty);

    private static readonly HashSet<DataGrid> Applying = new();      // 防重入（按表格）
    private static readonly HashSet<DataGrid> Adjusting = new();     // 正在做相邻列补偿
    private static readonly HashSet<DataGrid> ManualGrids = new();   // 用户真正拖过列宽的表
    private static readonly Dictionary<DataGrid, double[]> DragSnapshots = new();   // 拖拽起点宽度
    private static readonly Dictionary<DataGrid, double[]> ManualWidths = new();    // 拖拽结束后的宽度
    private static readonly Dictionary<DataGrid, long> LastSyncTicks = new();
    private static readonly ConditionalWeakTable<DataGrid, object> WeightCache = new();

    /// <summary>诊断：最近一次 Apply 的决策摘要（由探针打印）。</summary>
    public static string LastDebug { get; private set; } = string.Empty;

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid) return;

        grid.Loaded -= OnLoaded;
        grid.SizeChanged -= OnSizeChanged;
        grid.Unloaded -= OnUnloaded;

        if (e.NewValue is true)
        {
            grid.Loaded += OnLoaded;
            grid.SizeChanged += OnSizeChanged;
            grid.Unloaded += OnUnloaded;
        }
    }

    private static void OnWeightsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid) return;

        if (e.NewValue is string text && !string.IsNullOrWhiteSpace(text)) WeightCache.AddOrUpdate(grid, text);

        if (grid.IsLoaded) Apply(grid);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DataGrid grid) return;

        HookGrippers(grid);
        ScheduleApply(grid);
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is DataGrid grid) ScheduleApply(grid);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DataGrid grid) return;

        ManualGrids.Remove(grid);
        DragHookGrids.Remove(grid);
        DragSnapshots.Remove(grid);
        ManualWidths.Remove(grid);
        LastSyncTicks.Remove(grid);
    }

    // ============================================================ 拖拽手柄

    /// <summary>
    /// 接管列头拖拽手柄的起止事件。
    /// 用**冒泡的路由事件**挂在 DataGrid 上，而不是去视觉树里找 Thumb：
    /// 列头容器是延迟生成的（Loaded 时往往还没有），逐个找会漏挂，表现为"拖了但邻列不动"。
    /// 滚动条里也有 Thumb，故必须用 OriginalSource 是否位于列头来过滤。
    /// </summary>
    private static void HookGrippers(DataGrid grid)
    {
        HookColumnWidths(grid);

        if (!DragHookGrids.Add(grid)) return;

        grid.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnGripDragStarted), handledEventsToo: true);
        grid.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnGripDragCompleted), handledEventsToo: true);
    }

    private static readonly HashSet<DataGrid> DragHookGrids = new();

    private static void OnGripDragStarted(object sender, DragStartedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        if (!IsInsideColumnHeader(e.OriginalSource as DependencyObject)) return;

        BeginDrag(grid);
    }

    private static void OnGripDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        if (!IsInsideColumnHeader(e.OriginalSource as DependencyObject)) return;

        EndDrag(grid);
    }

    private static bool IsInsideColumnHeader(DependencyObject? node)
    {
        for (var current = node; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is DataGridColumnHeader) return true;
            if (current is DataGrid) return false;
        }

        return false;
    }

    private static readonly Dictionary<DataGridColumn, EventHandler> WidthHooks = new();
    private static readonly Dictionary<DataGridColumn, System.ComponentModel.DependencyPropertyDescriptor> WidthDescriptorCache = new();

    /// <summary>每列挂一次宽度变更监听（DataGrid 本身没有"列宽变化"事件）。</summary>
    private static void HookColumnWidths(DataGrid grid)
    {
        var descriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
            DataGridColumn.WidthProperty, typeof(DataGridColumn));

        if (descriptor is null) return;

        foreach (var column in grid.Columns)
        {
            if (WidthHooks.ContainsKey(column)) continue;

            var captured = column;
            EventHandler handler = (_, _) => OnColumnWidthChanged(grid, captured);
            descriptor.AddValueChanged(captured, handler);
            WidthHooks[column] = handler;
            WidthDescriptorCache[column] = descriptor;
        }
    }

    private static void BeginDrag(DataGrid grid)
    {
        // 只有真正从手柄拖起，才算"用户手动调整"。
        ManualGrids.Add(grid);

        // 用"绝对宽度值"（原生拖拽写入的就是它），避免 ActualWidth 在布局前滞后一拍。
        var snapshot = grid.Columns.Select(EffectiveWidth).ToArray();
        if (snapshot.Length == grid.Columns.Count) DragSnapshots[grid] = snapshot;
    }

    private static void EndDrag(DataGrid grid)
    {
        DragSnapshots.Remove(grid);

        var widths = grid.Columns.Select(static c => c.ActualWidth).ToArray();
        if (widths.Length == grid.Columns.Count) ManualWidths[grid] = widths;

        // 拖拽结束后补一次铺满校正（吸收舍入误差，防 1~2px 缝隙）。
        ReflowManual(grid);
    }

    private static void OnColumnWidthChanged(DataGrid grid, DataGridColumn column)
    {
        if (Applying.Contains(grid) || Adjusting.Contains(grid) || !grid.IsLoaded) return;

        // 没有拖拽起点 = 不是用户拖的（可能是系统解析星列 / 代码写入）；
        // 直接交给自动铺满，绝不因此进入"手动模式"（那是 R12.7 留白的根因）。
        if (!DragSnapshots.TryGetValue(grid, out var snapshot) || snapshot.Length != grid.Columns.Count) return;

        var index = grid.Columns.IndexOf(column);
        if (index < 0) return;

        var current = EffectiveWidth(column);
        var delta = current - snapshot[index];
        if (Math.Abs(delta) < 0.3) return;

        var neighborIndex = index + 1 < grid.Columns.Count ? index + 1 : index - 1;
        if (neighborIndex < 0) return;

        var neighbor = grid.Columns[neighborIndex];
        var (newLeft, newRight) = SolveAdjacent(
            snapshot[index], snapshot[neighborIndex], delta, column.MinWidth, neighbor.MinWidth);

        Adjusting.Add(grid);
        try
        {
            // ① 只动邻列；② 仅当邻列触底（newLeft != current）时把被拖列夹回。
            // 绝不在未触底时写被拖列 —— 那会与系统拖拽打架，表现就是"拖不动"。
            if (Math.Abs(newRight - EffectiveWidth(neighbor)) > 0.1)
            {
                neighbor.SetCurrentValue(
                    DataGridColumn.WidthProperty,
                    new DataGridLength(newRight, DataGridLengthUnitType.Pixel));
            }

            if (Math.Abs(newLeft - current) > 0.3)
            {
                column.SetCurrentValue(
                    DataGridColumn.WidthProperty,
                    new DataGridLength(newLeft, DataGridLengthUnitType.Pixel));
            }
        }
        finally
        {
            Adjusting.Remove(grid);
        }

        snapshot[index] = newLeft;
        snapshot[neighborIndex] = newRight;
    }

    /// <summary>
    /// 列的"当前宽度"：原生拖拽写的是 Width（绝对像素），此时 ActualWidth 可能还没更新；
    /// 取 Width.Value 才能实时跟随鼠标。非绝对宽（自动/星号）时才退回 ActualWidth。
    /// </summary>
    private static double EffectiveWidth(DataGridColumn column) =>
        column.Width.IsAbsolute && column.Width.Value > 0 ? column.Width.Value : column.ActualWidth;

    /// <summary>
    /// 相邻两列"此消彼长"的纯计算：拖动 <paramref name="delta"/> 时，
    /// 邻列让出/吞下同样的量；两者各自不低于 MinWidth，超出部分回退给被拖列。
    /// 返回 (新左宽, 新右宽)，保证两者之和与原来相等。
    /// </summary>
    public static (double Left, double Right) SolveAdjacent(
        double left, double right, double delta, double leftMin, double rightMin)
    {
        var sum = left + right;
        var wantedLeft = left + delta;
        var wantedRight = right - delta;

        // 邻列先触底：把剩余量还给被拖列
        if (wantedRight < rightMin)
        {
            wantedRight = rightMin;
            wantedLeft = sum - wantedRight;
        }

        // 被拖列触底
        if (wantedLeft < leftMin)
        {
            wantedLeft = leftMin;
            wantedRight = sum - wantedLeft;
        }

        return (Math.Round(wantedLeft, 1), Math.Round(wantedRight, 1));
    }

    /// <summary>
    /// 手动模式下的铺满：把"可用宽 − Σ列宽"的差额分给弹性列（从最后一列往前摊，
    /// 每列不低于 MinWidth）。返回新宽度数组；无需变化时原样返回。
    /// </summary>
    public static double[] SolveFill(double[] widths, bool[] elastic, double[] mins, double delta)
    {
        var result = (double[])widths.Clone();
        if (Math.Abs(delta) < 0.5) return result;

        var remaining = delta;
        for (var i = widths.Length - 1; i >= 0 && Math.Abs(remaining) > 0.5; i--)
        {
            if (!elastic[i]) continue;

            var clamped = Math.Max(mins[i], result[i] + remaining);
            remaining -= clamped - result[i];
            result[i] = clamped;
        }

        return result;
    }

    private static void ReflowManual(DataGrid grid)
    {
        if (grid.Columns.Count == 0) return;

        var available = grid.ActualWidth - 2;
        if (available <= 0) return;

        var current = grid.Columns.Select(static c => c.ActualWidth).ToArray();
        if (current.Length != grid.Columns.Count || current.Any(static w => w <= 0)) return;

        var delta = available - current.Sum();
        if (Math.Abs(delta) < 1) return;

        var weights = Parse(GetWeightsText(grid));
        var elastic = new bool[current.Length];
        var mins = new double[current.Length];
        for (var i = 0; i < current.Length; i++)
        {
            elastic[i] = weights.Length == current.Length && weights[i] > 0;
            mins[i] = grid.Columns[i].MinWidth;
        }

        if (!elastic.Any(static e => e)) return;

        var target = SolveFill(current, elastic, mins, delta);

        Applying.Add(grid);
        try
        {
            for (var i = 0; i < target.Length; i++)
            {
                if (Math.Abs(target[i] - current[i]) < 0.5) continue;

                grid.Columns[i].SetCurrentValue(
                    DataGridColumn.WidthProperty,
                    new DataGridLength(Math.Round(target[i], 1), DataGridLengthUnitType.Pixel));
            }
        }
        finally
        {
            Applying.Remove(grid);
        }

        ManualWidths[grid] = target;
    }

    // ============================================================ 自动铺满

    /// <summary>按权重把"可用宽"分配到各列（clamp MinWidth；正差额补给权重最大的弹性列）。</summary>
    public static void Apply(DataGrid grid)
    {
        // 用户拖过：不按权重重排（会把用户调整覆盖掉），但仍要吸收宽度变化保证铺满。
        if (ManualGrids.Contains(grid))
        {
            ReflowManual(grid);
            return;
        }

        if (!Applying.Add(grid))
        {
            LastDebug = $"skip:reentrant cols={grid.Columns.Count}";
            return;
        }

        try
        {
            var weights = Parse(GetWeightsText(grid));
            if (weights.Length == 0 || weights.Length != grid.Columns.Count)
            {
                LastDebug = $"skip:weights {weights.Length}/{grid.Columns.Count}";
                return;
            }

            var available = grid.ActualWidth;

            if (grid.Parent is FrameworkElement parent
                && parent.ActualWidth > 0
                && parent.ActualWidth >= grid.ActualWidth - 8)
            {
                available = Math.Min(available, parent.ActualWidth);
            }

            var total = available - 2;   // 扣边框
            if (total <= 0) return;

            LastDebug = $"cols={grid.Columns.Count} w={grid.ActualWidth:0.#} avail={available:0.#}";

            // 固定列（权重 0）：保持声明宽度，并从可用宽中扣除。
            for (var i = 0; i < weights.Length; i++)
            {
                if (weights[i] != 0) continue;

                var column = grid.Columns[i];
                var width = column.ActualWidth > 0 ? column.ActualWidth : column.Width.Value;
                total -= width;
            }

            var starSum = weights.Where(static w => w > 0).Sum();
            if (starSum <= 0 || total <= 0) return;

            var widths = new double[weights.Length];
            var allocated = 0.0;
            for (var i = 0; i < weights.Length; i++)
            {
                if (weights[i] <= 0) continue;

                widths[i] = Math.Max(total * weights[i] / starSum, grid.Columns[i].MinWidth);
                allocated += widths[i];
            }

            var delta = total - allocated;
            if (delta > 0.5)
            {
                // 正差额（clamp 造成或舍入）补给权重最大的弹性列 → Σ列宽 == 可用宽。
                var heaviest = -1;
                for (var i = 0; i < weights.Length; i++)
                {
                    if (weights[i] <= 0) continue;
                    if (heaviest < 0 || weights[i] > weights[heaviest]) heaviest = i;
                }

                if (heaviest >= 0) widths[heaviest] += delta;
            }
            else if (delta < -0.5)
            {
                // 超出可用宽：从"高于 Min 的列"按余量比例回缩（直接放弃会让列停在旧值 → 右侧留白）。
                var slack = 0.0;
                for (var i = 0; i < weights.Length; i++)
                {
                    if (weights[i] > 0) slack += Math.Max(0, widths[i] - grid.Columns[i].MinWidth);
                }

                if (slack > 0.5)
                {
                    var ratio = Math.Min(1, -delta / slack);
                    for (var i = 0; i < weights.Length; i++)
                    {
                        if (weights[i] <= 0) continue;

                        var room = Math.Max(0, widths[i] - grid.Columns[i].MinWidth);
                        widths[i] -= room * ratio;
                    }
                }
                else
                {
                    // 真的放不下（ΣMin > 可用宽）：全部取 MinWidth，让表格出现横向滚动。
                    for (var i = 0; i < weights.Length; i++)
                    {
                        if (weights[i] > 0) widths[i] = grid.Columns[i].MinWidth;
                    }
                }
            }

            for (var i = 0; i < weights.Length; i++)
            {
                if (weights[i] <= 0 || widths[i] < 1) continue;

                grid.Columns[i].SetCurrentValue(
                    DataGridColumn.WidthProperty,
                    new DataGridLength(Math.Round(widths[i], 1), DataGridLengthUnitType.Pixel));
            }

            grid.SetValue(LastAppliedWidthProperty, grid.ActualWidth);
        }
        finally
        {
            Applying.Remove(grid);
        }
    }

    /// <summary>
    /// 对可视树中所有开启本行为的 DataGrid 重新计算列宽。
    /// 由主窗口在「加载完成 / 尺寸变化 / 页面切换」时调用。
    /// </summary>
    public static void ApplyAll(DependencyObject root)
    {
        if (root is DataGrid self && GetEnabled(self))
        {
            HookGrippers(self);
            Apply(self);
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            ApplyAll(VisualTreeHelper.GetChild(root, i));
        }
    }

    /// <summary>
    /// 只对"当前宽度与上次分配时不一致"的表格重算（收敛即停，不会自旋）。
    /// </summary>
    public static void SyncIfNeeded(DependencyObject root)
    {
        if (root is DataGrid grid && GetEnabled(grid) && grid.IsLoaded && grid.ActualWidth > 0)
        {
            var last = (double)grid.GetValue(LastAppliedWidthProperty);
            var now = Environment.TickCount64;
            var cooled = !LastSyncTicks.TryGetValue(grid, out var prev) || now - prev > 200;

            if (Math.Abs(grid.ActualWidth - last) >= 1 && cooled)
            {
                LastSyncTicks[grid] = now;
                Apply(grid);
            }
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            SyncIfNeeded(VisualTreeHelper.GetChild(root, i));
        }
    }

    /// <summary>撤销手动模式（重新按权重铺满）。</summary>
    public static void ResetManual(DataGrid grid)
    {
        ManualGrids.Remove(grid);
        ManualWidths.Remove(grid);
        Apply(grid);
    }

    // ============================================================ 基础设施

    private static void ScheduleApply(DataGrid grid)
    {
        grid.Dispatcher.BeginInvoke(new Action(() => Apply(grid)), DispatcherPriority.Background);
    }

    private static string GetWeightsText(DataGrid grid)
    {
        var text = GetStarWeights(grid);
        if (!string.IsNullOrWhiteSpace(text)) return text;

        return WeightCache.TryGetValue(grid, out var cached) ? (string)cached : string.Empty;
    }

    private static int[] Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<int>();

        try
        {
            return text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static s => (int)Math.Round(double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)))
                .ToArray();
        }
        catch (FormatException)
        {
            return Array.Empty<int>();
        }
    }


}
