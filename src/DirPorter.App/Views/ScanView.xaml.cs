using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DirPorter.App.ViewModels;

namespace DirPorter.App.Views;

public partial class ScanView : UserControl
{
    public ScanView() => InitializeComponent();

    /// <summary>双击整行 → 打开该行路径所在文件夹（点行内按钮时不触发）。</summary>
    private void Rows_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataGridRowActions.IsInteractiveElementHit(e.OriginalSource)) return;
        DataGridRowActions.OpenPrimaryPath((DependencyObject)sender, Window.GetWindow(this)?.DataContext);
    }
}
