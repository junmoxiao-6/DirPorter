using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace DirPorter.App.ViewModels;

/// <summary>
/// 表格行"主路径"的统一抽象。
///
/// 用途：**双击整行打开所在文件夹**。表格是懒加载的可视化树，双击事件只能在
/// code-behind 里拿，但"打开哪个路径"是每张表自己的事 —— 用这个接口把两者解耦，
/// 避免在 code-behind 里按类型 switch 或走反射。
/// </summary>
public interface IPathRow
{
    /// <summary>双击该行时打开的路径。</summary>
    string PrimaryPath { get; }
}

/// <summary>表格交互的共享实现（各视图 code-behind 一行调用）。</summary>
internal static class DataGridRowActions
{
    /// <summary>
    /// 双击整行 → 打开该行主路径所在文件夹。
    ///
    /// 说明：为什么不用 XAML 的 <c>MouseBinding</c>？<c>InputBinding</c> 不在可视树上，
    /// 既拿不到 DataContext，也解析不了 <c>RelativeSource FindAncestor</c>。
    /// 因此这类交互必须落在 code-behind。
    /// </summary>
    public static void OpenPrimaryPath(DependencyObject sender, object? windowDataContext)
    {
        if (sender is not DataGrid grid) return;
        if (grid.SelectedItem is not IPathRow row) return;
        if (string.IsNullOrWhiteSpace(row.PrimaryPath)) return;

        if (windowDataContext is MainViewModel main)
            main.OpenPathCommand.Execute(row.PrimaryPath);
    }

    /// <summary>双击落在按钮/复选框/输入框上时不应触发"打开路径"（用户是在点行内操作）。</summary>
    public static bool IsInteractiveElementHit(object? originalSource)
    {
        var current = originalSource as DependencyObject;

        while (current is not null)
        {
            // ButtonBase 覆盖 Button / ToggleButton / CheckBox / RadioButton / RepeatButton
            if (current is ButtonBase or TextBox or ComboBox or System.Windows.Controls.Primitives.ScrollBar)
                return true;

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }
}
