using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DirPorter.App.Services;

/// <summary>
/// 滚轮链式滚动（R12.1 用户反馈："滚轮无响应，只能拖滚动条"）。
///
/// 背景：页面内容在主 ScrollViewer 内，而表格（DataGrid）/ 列表（ListBox）自带内部
/// ScrollViewer。鼠标悬停在表格/列表上滚轮时，事件可能被内层消费（甚至在内层无滚动
/// 范围时被某些控件吞掉），导致"外层页面滚不动"。
///
/// 本附加行为挂在**内层滚动控件**上（Shared.xaml 的 DataGrid / ListBox 隐式样式统一挂载）：
/// <list type="bullet">
/// <item>内层还有可滚余量 → 放行（保持"先滚表格"的自然行为）；</item>
/// <item>内层到头 / 无滚动范围 / 找不到内层滚动器 → 手动滚动最近的外层 ScrollViewer 并标记已处理。</item>
/// </list>
/// 在 Preview（隧道到达内层控件的最早时机）判断，绕开"内层吞轮"的一切中间态，且不会双重滚动。
/// </summary>
public static class WheelScrollChaining
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled",
            typeof(bool),
            typeof(WheelScrollChaining),
            new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;

        element.PreviewMouseWheel -= OnPreviewMouseWheel;   // 防重复挂载（样式重应用时）
        if (e.NewValue is true) element.PreviewMouseWheel += OnPreviewMouseWheel;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DependencyObject element) return;
        if (e.Handled) return;

        // 内层滚动器还有余量：放行，由内层自行滚动（"先滚表格"的自然行为）。
        var inner = FindInnerScrollViewer(element);
        if (inner is not null)
        {
            var up = e.Delta > 0;
            var canScroll = up
                ? inner.VerticalOffset > 0.5
                : inner.VerticalOffset < inner.ScrollableHeight - 0.5;

            if (canScroll) return;
        }

        // 内层到头 / 无滚动范围：把滚轮转交给最近的外层滚动容器。
        var outer = FindAncestorScrollViewer(element);
        if (outer is null || outer.ScrollableHeight <= 0) return;

        outer.ScrollToVerticalOffset(outer.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    /// <summary>找控件内部第一个可滚动方向的 ScrollViewer（DataGrid / ListBox 的模板内滚动器）。</summary>
    private static ScrollViewer? FindInnerScrollViewer(DependencyObject element)
    {
        var count = VisualTreeHelper.GetChildrenCount(element);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            if (child is ScrollViewer scrollViewer) return scrollViewer;

            var nested = FindInnerScrollViewer(child);
            if (nested is not null) return nested;
        }

        return null;
    }

    /// <summary>沿视觉树向上找最近的 ScrollViewer（内容与 ScrollViewer 之间隔着 ScrollContentPresenter）。</summary>
    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject? node)
    {
        // 从"父级"开始找：内层控件自己的第一个祖先滚动器就是外层。
        node = node is null ? null : VisualTreeHelper.GetParent(node);

        while (node is not null)
        {
            if (node is ScrollViewer scrollViewer) return scrollViewer;
            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }
}
