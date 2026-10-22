using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using DirPorter.App.Services;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/dirporter;component/Themes/Shared.xaml"),
        });

        var grid = new DataGrid();
        grid.Columns.Add(new DataGridTextColumn { Header = "A", MinWidth = 60, Width = 260 });
        grid.Columns.Add(new DataGridTextColumn { Header = "B", MinWidth = 60, Width = 260 });
        grid.Columns.Add(new DataGridTextColumn { Header = "C", MinWidth = 60, Width = 260 });
        ElasticColumns.SetEnabled(grid, true);
        ElasticColumns.SetStarWeights(grid, "2,2,2");

        var window = new Window { Width = 800, Height = 300, Content = grid };
        window.Show();
        Pump();

        var ok = true;

        Console.WriteLine($"CanUserResizeColumns={grid.CanUserResizeColumns}");

        // ---------- 1) 手柄是否真的存在（缺失 = 用户"拖不动"的直接原因） ----------
        var thumbs = FindThumbs(grid);
        Console.WriteLine($"gripperThumbs={thumbs.Count}");
        if (thumbs.Count < 2)
        {
            Console.WriteLine("[FAIL] 列头没有拖拽手柄 —— 鼠标在边界不会出现双向箭头，必然拖不动");
            ok = false;
        }

        var before = Widths(grid);
        var sumBefore = before.Sum();
        Console.WriteLine($"before=[{Fmt(before)}] sum={sumBefore:0.#}");

        // ---------- 2) 用真实手柄拖 +40（走系统原生拖拽路径） ----------
        var gripper = RightGripperOf(grid, 1);
        if (gripper is null)
        {
            Console.WriteLine("[FAIL] 第二列没有右手柄");
            ok = false;
        }
        else
        {
            gripper.RaiseEvent(new DragStartedEventArgs(0, 0));
            gripper.RaiseEvent(new DragDeltaEventArgs(40, 0));
            gripper.RaiseEvent(new DragCompletedEventArgs(40, 0, false));
            Pump();
        }

        var after = Widths(grid);
        var sumAfter = after.Sum();
        var grew = after[1] - before[1];
        var shrank = before[2] - after[2];

        Console.WriteLine($"after=[{Fmt(after)}] sum={sumAfter:0.#}");
        Console.WriteLine($"grew={grew:0.#} shrank={shrank:0.#}");

        var dragOk = grew > 20 && Math.Abs(grew - shrank) < 4 && Math.Abs(sumAfter - sumBefore) < 4;
        Console.WriteLine(dragOk ? "[OK] 拖动生效：本列变宽、邻列等量变窄、总宽不变" : "[FAIL] 拖动联动不正确");
        ok &= dragOk;

        // ---------- 3) 自动重排不得覆盖用户调整 ----------
        ElasticColumns.ApplyAll(grid);
        Pump();
        var afterReflow = Widths(grid);
        var kept = Math.Abs(afterReflow[1] - after[1]) < 2;
        Console.WriteLine(kept ? "[OK] 自动重排未覆盖用户调整" : "[FAIL] 自动重排覆盖了用户调整");
        ok &= kept;

        // ---------- 4) 手动模式下窗口变宽：必须继续铺满，右侧不留白 ----------
        window.Width = 1000;
        Pump();
        ElasticColumns.ApplyAll(grid);
        Pump();

        var wide = Widths(grid);
        var blank = grid.ActualWidth - wide.Sum();
        Console.WriteLine($"resized=[{Fmt(wide)}] gridW={grid.ActualWidth:0.#} blank={blank:0.#}");
        var fillOk = Math.Abs(blank) < 6;
        Console.WriteLine(fillOk ? "[OK] 手动模式下变宽仍铺满（无右侧留白）" : "[FAIL] 手动模式下出现右侧留白");
        ok &= fillOk;

        Console.WriteLine(ok ? "[PASS] column resize suite" : "[FAILED] column resize suite");
        window.Close();
        return ok ? 0 : 1;

        static string Fmt(double[] values) => string.Join(", ", values.Select(v => v.ToString("0.#")));

        static double[] Widths(DataGrid g) => g.Columns.Select(c => c.ActualWidth).ToArray();
    }

    private static List<Thumb> FindThumbs(DependencyObject root)
    {
        var found = new List<Thumb>();
        Walk(root);
        return found;

        void Walk(DependencyObject node)
        {
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is Thumb thumb) found.Add(thumb);
                Walk(child);
            }
        }
    }

    private static Thumb? RightGripperOf(DataGrid grid, int columnIndex)
    {
        var presenter = FindChild<DataGridColumnHeadersPresenter>(grid);
        if (presenter?.ItemContainerGenerator.ContainerFromIndex(columnIndex) is not DataGridColumnHeader header)
            return null;

        var thumbs = new List<Thumb>();
        Walk(header, thumbs);

        // 右侧手柄在系统模板里叫 PART_RightHeaderGripper；退化为"最靠右的那个"。
        return thumbs.FirstOrDefault(t => t.Name.Contains("Right", StringComparison.OrdinalIgnoreCase))
               ?? thumbs.OrderByDescending(t => t.TranslatePoint(new Point(0, 0), header).X).FirstOrDefault();

        static void Walk(DependencyObject node, List<Thumb> into)
        {
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is Thumb thumb) into.Add(thumb);
                Walk(child, into);
            }
        }
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;

            var nested = FindChild<T>(child);
            if (nested is not null) return nested;
        }

        return null;
    }

    private static void Pump()
    {
        for (var i = 0; i < 8; i++)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        }
    }
}
