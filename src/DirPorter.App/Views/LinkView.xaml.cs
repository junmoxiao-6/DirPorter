using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DirPorter.App.ViewModels;

namespace DirPorter.App.Views;

public partial class LinkView : UserControl
{
    public LinkView() => InitializeComponent();

    /// <summary>双击整行 → 打开该链接所在位置（点行内按钮时不触发）。</summary>
    private void Rows_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataGridRowActions.IsInteractiveElementHit(e.OriginalSource)) return;
        DataGridRowActions.OpenPrimaryPath((DependencyObject)sender, Window.GetWindow(this)?.DataContext);
    }
}
