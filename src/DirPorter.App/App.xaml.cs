using System.Windows;
using System.Windows.Threading;
using DirPorter.App.Services;

namespace DirPorter.App;

/// <summary>
/// WPF 应用：单实例、崩溃兜底、主题/语言初始化、主窗口装配。
///
/// 注意：本类**不使用 StartupUri**，主窗口在 <see cref="OnStartup"/> 里手工创建 ——
/// 这样单实例判定可以在任何窗口出现之前完成（第二个实例不会闪一下窗口再退出）。
/// </summary>
public partial class App : Application
{
    private static SingleInstanceGuard? _guard;
    private AppHost? _host;

    /// <summary>
    /// 释放单实例锁。
    /// ⚠️ 提权重启之前**必须**先调用它：否则新启动的提权实例会发现"已有实例在跑"，
    /// 于是只发个激活信号就自己退出 —— 用户看到的现象是"点了重启，什么都没发生"。
    /// </summary>
    public static void ReleaseSingleInstance()
    {
        _guard?.Dispose();
        _guard = null;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ---------- 单实例：不是第一个实例就唤醒已有窗口并退出 ----------
        _guard = SingleInstanceGuard.Acquire();
        if (!_guard.IsOwner)
        {
            _guard.SignalExistingInstance();
            Shutdown(0);
            return;
        }

        InstallGlobalExceptionHandlers();

        // ---------- 组合根 + 首选项 ----------
        var settings = UiSettings.Load();
        _host = new AppHost(new WpfUserInteraction(), settings);

        LocalizationService.Instance.Language = settings.Language;

        // 主题是"锦上添花"：加载失败也必须让程序能起来（否则一个资源字典错误就把整个界面挡在门外）。
        try
        {
            ThemeService.Apply(settings.Theme);
        }
        catch (Exception ex)
        {
            CrashReporter.WriteCrashLog("ThemeService.Apply", ex);
        }

        var window = new MainWindow { DataContext = _host.Main };
        MainWindow = window;
        window.Show();

        // 启动提权被取消/失败时，把原因落到外壳底部的即时提示行（程序本身已降级继续运行）。
        if (Program.ElevationNotice is { } notice)
        {
            var loc = LocalizationService.Instance;
            _host.Main.StatusMessage = notice.Arg is null
                ? loc.Get(notice.Key)
                : string.Format(System.Globalization.CultureInfo.CurrentCulture, loc.Get(notice.Key), notice.Arg);
        }

        // 之后再有实例启动时，把本窗口带到前台（而不是无声无息）。
        _guard.ListenForActivation(() => Dispatcher.Invoke(() => BringToFront(window)));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // R12：输入持久化改为防抖后，退出前统一保存一次——保证用户最后一次输入不丢。
        try { _host?.Settings.Save(); } catch { /* 退出路径尽力而为，失败不阻塞关闭 */ }
        ReleaseSingleInstance();
        base.OnExit(e);
    }

    private static void BringToFront(Window window)
    {
        if (!window.IsVisible) window.Show();

        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;

        window.Activate();

        // 常见做法：临时置顶再取消，绕过 Windows 的"前台窗口锁定"。
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }

    private void InstallGlobalExceptionHandlers()
    {
        // UI 线程异常：记日志 + 提示，但不杀进程（用户可能正在迁移中）。
        DispatcherUnhandledException += (_, args) =>
        {
            ReportCrash("DispatcherUnhandledException", args.Exception);
            args.Handled = true;
        };

        // 后台线程未捕获异常（CLR 2.0+ 起不可恢复，只能留下证据）。
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
                ReportCrash("AppDomain.UnhandledException", exception);
        };

        // 未观察的 Task 异常：观察掉，避免进程被终止。
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ReportCrash("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };
    }

    private static void ReportCrash(string source, Exception exception)
    {
        var path = CrashReporter.WriteCrashLog(source, exception);

        var message = path is null
            ? LocalizationService.Instance.Get("msg.crashNoLog")
            : string.Format(System.Globalization.CultureInfo.CurrentCulture,
                LocalizationService.Instance.Get("msg.crash"), path);

        try
        {
            MessageBox.Show(message + Environment.NewLine + Environment.NewLine + exception.Message,
                LocalizationService.Instance.Get("msg.title"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // 连弹窗都失败（例如 UI 线程已崩）：日志已经写下了，不再纠缠。
        }
    }
}
