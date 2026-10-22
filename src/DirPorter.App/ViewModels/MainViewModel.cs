using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DirPorter.App.Services;

namespace DirPorter.App.ViewModels;

/// <summary>左侧导航项。</summary>
public sealed partial class NavItem : ObservableObject
{
    public NavItem(string key, string titleKey)
    {
        Key = key;
        TitleKey = titleKey;
        Refresh();
    }

    public string Key { get; }

    public string TitleKey { get; }

    [ObservableProperty] private string _title = string.Empty;

    public void Refresh() => Title = LocalizationService.Instance.Get(TitleKey);
}

/// <summary>
/// 外壳 ViewModel：持有五个页面，负责导航、跨页联动、语言切换与全局的
/// 「复制路径 / 打开所在文件夹」两条通用动作。
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppHost _host;
    private int _statusToken;

    private int _maintenanceStarted;

    /// <summary>
    /// 启动维护：按设置里的保留天数清理"已到期"的停车备份（后台线程，不阻塞界面）。
    ///
    /// R12.10 修复：此前 <c>BackupRetentionService</c> 只被 CLI 调用，GUI 从不触发 ——
    /// 用户把保留天数设成 1 天也不会清任何东西（实测反馈）。
    /// </summary>
    public void RunStartupMaintenance()
    {
        if (Interlocked.Exchange(ref _maintenanceStarted, 1) == 1) return;

        var host = _host;

        _ = Task.Run(() =>
        {
            try
            {
                if (host.Settings.BackupRetentionDays < 0) return;   // 永不清理

                var result = host.SweepExpiredBackups();
                if (result.Deleted > 0) host.Main.Settings.InvalidateParkings();
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                // 清理是"尽力而为"的后台维护，失败不影响启动。
            }
        });
    }

    public MainViewModel(AppHost host)
    {
        _host = host;

        Scan = new ScanViewModel(host);
        Migrate = new MigrateViewModel(host);
        Quick = new QuickMigrateViewModel(host, Migrate);
        Link = new LinkViewModel(host);
        History = new HistoryViewModel(host);
        LinkOverview = new LinkOverviewViewModel(host);
        Settings = new SettingsViewModel(host);

        // 跨页接线：
        //  · 扫描页勾选/行级「加入迁移队列」→ 交给迁移页，并自动跳到迁移页（让用户看到队列确实进去了）
        //  · 迁移批次跑完（任一入口：一键页/迁移页/扫描触发）→ 通知扫描页重算「已迁移」标记
        Scan.EnqueueRequested += paths =>
        {
            Migrate.ReplaceQueue(paths);
            NavigateTo("migrate");
        };
        Migrate.BatchFinished += paths => Scan.RefreshMigratedFlags(paths);

        NavItems = new ObservableCollection<NavItem>
        {
            new("quick", "nav.quick"),
            new("scan", "nav.scan"),
            new("migrate", "nav.migrate"),
            new("link", "nav.link"),
            new("history", "nav.history"),
            new("links", "nav.linksOverview"),
            new("settings", "nav.settings"),
        };

        _selectedNav = NavItems[0];
        _currentPage = Quick;

        _host.Loc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not ("Item[]" or nameof(LocalizationService.Language))) return;

            foreach (var item in NavItems) item.Refresh();
            Quick.RefreshLocalization();
            Scan.RefreshLocalization();
            Migrate.RefreshLocalization();
            Link.RefreshLocalization();
            History.RefreshLocalization();
            Settings.RefreshLocalization();
            RefreshElevationBanner();
            OnPropertyChanged(nameof(Title));
        };
    }

    public QuickMigrateViewModel Quick { get; }
    public ScanViewModel Scan { get; }
    public MigrateViewModel Migrate { get; }
    public LinkViewModel Link { get; }
    public HistoryViewModel History { get; }
    public LinkOverviewViewModel LinkOverview { get; }
    public SettingsViewModel Settings { get; }

    public ObservableCollection<NavItem> NavItems { get; }

    [ObservableProperty] private NavItem _selectedNav;

    [ObservableProperty] private ObservableObject _currentPage;

    /// <summary>外壳底部的一行临时提示（复制路径等即时反馈）。</summary>
    [ObservableProperty] private string _statusMessage = string.Empty;

    public string Title => LocalizationService.Instance.Get("app.title");

    // ---------------------------------------------------------------- 权限横幅

    /// <summary>
    /// 未提权时显示权限横幅（启动时若用户取消/拒绝了 UAC，也走这里降级提示）。
    /// 已提权则为 false，横幅隐藏——不拿"永久警告条"骚扰不需要它的人。
    /// </summary>
    public bool ElevationBannerVisible => !_host.Elevation.IsElevated;

    public string ElevationBannerText => LocalizationService.Instance.Get("priv.banner.body");

    /// <summary>横幅上的「以管理员身份重启」（与设置页同一条命令）。</summary>
    [RelayCommand]
    private void RestartElevated() => Settings.RestartElevatedCommand.Execute(null);

    /// <summary>供外壳在语言切换后刷新横幅文本。</summary>
    public void RefreshElevationBanner()
    {
        OnPropertyChanged(nameof(ElevationBannerVisible));
        OnPropertyChanged(nameof(ElevationBannerText));
    }

    partial void OnSelectedNavChanged(NavItem value)
    {
        // ListBox 清空选中时会回写 null，直接 return 而不是空引用。
        if (value is null) return;

        CurrentPage = value.Key switch
        {
            "quick" => Quick,
            "migrate" => Migrate,
            "link" => Link,
            "history" => History,
            "links" => LinkOverview,
            "settings" => Settings,
            _ => Scan,
        };

        // R12 启动性能：记录页 / 链接页改为**首次进入才加载**
        // （构造函数里的同步读盘 + 逐条重解析点探测不再拖慢应用启动）。
        switch (value.Key)
        {
            case "history": History.EnsureLoaded(); break;
            case "link": Link.EnsureLoaded(); break;
            case "links": if (LinkOverview.Links.Count == 0) LinkOverview.ScanCommand.Execute(null); break;
            case "settings": Settings.EnsureParkingsLoaded(); break;
        }
    }

    /// <summary>按 key 切换页面（<c>quick</c>/<c>scan</c>/<c>migrate</c>/<c>link</c>/<c>history</c>/<c>settings</c>）。</summary>
    [RelayCommand]
    public void NavigateTo(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;

        var item = NavItems.FirstOrDefault(n => string.Equals(n.Key, key, StringComparison.OrdinalIgnoreCase));
        if (item is not null) SelectedNav = item;
    }

    /// <summary>打开所在文件夹（表格里点路径、或行内「打开」按钮）。</summary>
    [RelayCommand]
    private void OpenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        _host.Interaction.OpenInExplorer(path);
    }

    /// <summary>复制路径到剪贴板，并在外壳底部给一行即时反馈。</summary>
    [RelayCommand]
    private void CopyPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        _host.Interaction.CopyToClipboard(path);
        Announce(string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            LocalizationService.Instance.Get("common.copied"), path));
    }

    private void Announce(string message)
    {
        StatusMessage = message;
        var token = ++_statusToken;
        _ = ClearStatusLaterAsync(token);
    }

    private async Task ClearStatusLaterAsync(int token)
    {
        await Task.Delay(TimeSpan.FromSeconds(4)).ConfigureAwait(true);
        if (token == _statusToken) StatusMessage = string.Empty;
    }

    /// <summary>窗口被再次启动的实例唤醒时调用。</summary>
    [RelayCommand]
    private void Refresh() => History.Reload();
}
