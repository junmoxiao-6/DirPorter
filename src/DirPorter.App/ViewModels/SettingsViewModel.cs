using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DirPorter.App.Services;
using DirPorter.Core.Backup;
using DirPorter.Core.Config;
using DirPorter.Core.FileSystem;
using DirPorter.Core.Links;

namespace DirPorter.App.ViewModels;

/// <summary>语言选项。</summary>
public sealed record LanguageOption(string Code, string NameKey)
{
    public string Display => LocalizationService.Instance.Get(NameKey);
}

/// <summary>主题选项。</summary>
public sealed record ThemeOption(string Code, string NameKey)
{
    public string Display => LocalizationService.Instance.Get(NameKey);
}

/// <summary>一行权限能力展示。</summary>
public sealed record PrivilegeLine(string Name, bool Available)
{
    public string State => LocalizationService.Instance.Get(Available ? "common.supported" : "common.unsupported");
}

/// <summary>
/// 设置页 ViewModel：语言 / 主题 / 默认项 / 环境与权限自检 / 数据目录入口。
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly IUserInteraction _ui;

    public SettingsViewModel(AppHost host)
    {
        _host = host;
        _ui = host.Interaction;

        LanguageOptions = new ObservableCollection<LanguageOption>
        {
            new(LocalizationService.Languages.Chinese, "set.lang.zh"),
            new(LocalizationService.Languages.English, "set.lang.en"),
        };

        ThemeOptions = new ObservableCollection<ThemeOption>
        {
            new(ThemeService.Light, "set.theme.light"),
            new(ThemeService.Dark, "set.theme.dark"),
        };

        // R12：与迁移页/一键页共用同一份选项集（四种类型全展示，文件级两种置灰）。
        LinkKindOptions = LinkKindOption.BuildMigrationSet();

        _language = LanguageOptions.FirstOrDefault(o => o.Code == host.Settings.Language) ?? LanguageOptions[0];
        _theme = ThemeOptions.FirstOrDefault(o => o.Code == ThemeService.Normalize(host.Settings.Theme)) ?? ThemeOptions[0];
        _selectedLinkKind = LinkKindOptions.FirstOrDefault(o => o.Kind == host.Settings.DefaultLinkKindValue)
                            ?? LinkKindOptions[0];

        _threads = host.Settings.DefaultThreads;
        _retentionDays = host.Settings.BackupRetentionDays;

        _version = typeof(SettingsViewModel).Assembly.GetName().Version?.ToString() ?? "0.1.0";
        _dataRoot = DirPorterPaths.DataRoot;

        BuildPrivilegeLines();

        _updateFeedUrl = host.Settings.UpdateFeedUrl;
        _updateStatus = LocalizationService.Instance.Get("set.updateIdle");
    }

    // ---------------- R12.6 / R12.8：一键更新 ----------------

    private readonly UiDebouncer _feedUrlDebouncer = new(TimeSpan.FromMilliseconds(500));

    [ObservableProperty] private string _updateFeedUrl;
    [ObservableProperty] private string _updateStatus;
    [ObservableProperty] private bool _isUpdating;

    /// <summary>更新源提示（让用户知道"现在会去哪个地址取清单"）。</summary>
    public string UpdateSourceText =>
        string.IsNullOrWhiteSpace(UpdateService.ResolveFeedUrl(UpdateFeedUrl))
            ? string.Empty
            : string.Format(System.Globalization.CultureInfo.CurrentCulture,
                LocalizationService.Instance.Get("set.updateSource"), UpdateService.ResolveFeedUrl(UpdateFeedUrl));

    public bool CanUpdate => !IsUpdating;

    partial void OnIsUpdatingChanged(bool value) => OnPropertyChanged(nameof(CanUpdate));

    partial void OnUpdateFeedUrlChanged(string value)
    {
        _feedUrlDebouncer.Poke(() =>
        {
            _host.Settings.UpdateFeedUrl = value ?? string.Empty;
            _host.Settings.Save();
            OnPropertyChanged(nameof(UpdateSourceText));
        });
    }

    /// <summary>
    /// 一次点击完成"检查 → 下载 → 校验 → 替换 → 重启"。
    /// 用户不需要先点检查再点安装，也不需要填写任何地址（地址已内置）。
    /// </summary>
    [RelayCommand]
    private async Task UpdateNowAsync()
    {
        var loc = LocalizationService.Instance;
        _feedUrlDebouncer.Flush();

        IsUpdating = true;
        UpdateStatus = loc.Get("set.updateChecking");

        try
        {
            var feed = UpdateService.ResolveFeedUrl(UpdateFeedUrl);
            if (string.IsNullOrWhiteSpace(feed))
            {
                UpdateStatus = loc.Get("set.updateNoFeed");
                return;
            }

            var (hasUpdate, info) = await UpdateService.CheckAsync(feed).ConfigureAwait(true);
            if (!hasUpdate || info is null)
            {
                UpdateStatus = string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    loc.Get("set.updateUpToDate"), UpdateService.CurrentVersionText);
                return;
            }

            UpdateStatus = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                loc.Get("set.updateFoundShort"), info.Version);

            var progress = new Progress<double>(p => UpdateStatus = string.Format(
                System.Globalization.CultureInfo.CurrentCulture, loc.Get("set.updateProgress"), p));

            var file = await UpdateService.DownloadAsync(info, progress).ConfigureAwait(true);
            UpdateStatus = loc.Get("set.updateVerifying");

            if (UpdateService.ApplyAndRestart(file))
            {
                UpdateStatus = loc.Get("set.updateRestarting");
                System.Windows.Application.Current?.Shutdown();
            }
            else
            {
                UpdateStatus = loc.Get("set.updateApplyFailed");
                _host.Interaction.Error(loc.Get("set.updateApplyFailed"));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException
                                       or JsonException or IOException or UnauthorizedAccessException)
        {
            UpdateStatus = loc.Get("set.updateFailed") + " " + ex.Message;
        }
        finally
        {
            IsUpdating = false;
        }
    }

    // ---------------- R12.5：.migbak 停车区（迁移备份）清点与清理 ----------------

    private bool _parkingsLoaded;

    public ObservableCollection<ParkingInfo> Parkings { get; } = new();

    /// <summary>标记为"需要重新清点"（清理了一批备份后调用，下次进入设置页会刷新）。</summary>
    public void InvalidateParkings() => _parkingsLoaded = false;

    /// <summary>首次进入设置页时扫描一次（避免启动期做重 IO）。</summary>
    public void EnsureParkingsLoaded()
    {
        if (_parkingsLoaded) return;

        _parkingsLoaded = true;
        RefreshParkings();
    }

    [ObservableProperty] private string _parkingSummary = string.Empty;

    /// <summary>重新清点停车区：常见用户目录 + 台账里出现过的其它盘位置。</summary>
    [RelayCommand]
    public void RefreshParkings()
    {
        var loc = LocalizationService.Instance;
        var roots = new List<string>(ParkingInventory.DefaultRoots(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)));

        try
        {
            foreach (var record in _host.Index.LoadAll())
            {
                if (string.IsNullOrWhiteSpace(record.BackupPath)) continue;

                var parent = System.IO.Path.GetDirectoryName(record.BackupPath);
                if (!string.IsNullOrWhiteSpace(parent)) roots.Add(parent);
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
        }

        Parkings.Clear();
        foreach (var info in ParkingInventory.Scan(roots)) Parkings.Add(info);

        ParkingSummary = Parkings.Count == 0
            ? loc.Get("set.parkingEmpty")
            : string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                loc.Get("set.parkingSummary"),
                Parkings.Count,
                new ParkingInfo(string.Empty, 0, Parkings.Sum(static p => p.TotalBytes), DateTime.UtcNow).SizeText);
    }

    [ObservableProperty] private bool _isPurgingBackups;

    public bool CanPurgeBackups => !IsPurgingBackups;

    partial void OnIsPurgingBackupsChanged(bool value) => OnPropertyChanged(nameof(CanPurgeBackups));

    /// <summary>
    /// 立即按"保留天数"清理已到期的停车备份（保留 .ledger）。
    /// 与启动时自动清理走同一个服务，但这里给出明确结果反馈。
    /// </summary>
    [RelayCommand]
    private async Task PurgeExpiredBackupsAsync()
    {
        var loc = LocalizationService.Instance;
        IsPurgingBackups = true;

        try
        {
            var result = await Task.Run(() => _host.SweepExpiredBackups()).ConfigureAwait(true);

            RefreshParkings();
            ParkingSummary = result.Describe();
            _ui.Info(result.Describe());
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            _ui.Error(ex.Message);
        }
        finally
        {
            IsPurgingBackups = false;
        }
    }

    [RelayCommand]
    private void OpenParking(ParkingInfo? info)
    {
        if (info is null) return;
        _ui.OpenInExplorer(info.Path);
    }

    /// <summary>删除停车区内容（保留 .ledger 台账归档）——这是唯一的数据副本来源，必须二次确认。</summary>
    [RelayCommand]
    private void DeleteParking(ParkingInfo? info)
    {
        if (info is null) return;

        var loc = LocalizationService.Instance;
        if (!_ui.Confirm(string.Format(System.Globalization.CultureInfo.CurrentCulture, loc.Get("set.parkingDeleteConfirm"), info.Path), loc.Get("set.parkingDelete")))
        {
            return;
        }

        var (deleted, failed) = ParkingInventory.Delete(info.Path);
        RefreshParkings();

        _ui.Info(failed == 0
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture, loc.Get("set.parkingDeleteDone"), deleted)
            : string.Format(System.Globalization.CultureInfo.CurrentCulture, loc.Get("set.parkingDeletePartial"), deleted, failed));
    }

    public ObservableCollection<LanguageOption> LanguageOptions { get; }

    public ObservableCollection<ThemeOption> ThemeOptions { get; }

    /// <summary>可选的默认建链类型（迁移页/扫描页一键迁移共用）。</summary>
    public ObservableCollection<LinkKindOption> LinkKindOptions { get; }

    public ObservableCollection<PrivilegeLine> PrivilegeLines { get; } = new();

    [ObservableProperty] private LanguageOption _language;
    [ObservableProperty] private ThemeOption _theme;
    [ObservableProperty] private LinkKindOption _selectedLinkKind;
    [ObservableProperty] private int _threads;
    [ObservableProperty] private int _retentionDays;
    [ObservableProperty] private string _version;
    [ObservableProperty] private string _dataRoot;
    [ObservableProperty] private string _envText = string.Empty;

    partial void OnLanguageChanged(LanguageOption value)
    {
        // 立即生效（需求：中英双语可切换），同时持久化。
        LocalizationService.Instance.Language = value.Code;
        _host.Settings.Language = value.Code;
        _host.Settings.Save();
        RefreshLocalization();
    }

    partial void OnThemeChanged(ThemeOption value)
    {
        ThemeService.Apply(value.Code);
        _host.Settings.Theme = ThemeService.Normalize(value.Code);
        _host.Settings.Save();
    }

    /// <summary>默认建链类型：立即持久化，并同步迁移页的当前选择（两处本来就是同一个设置）。</summary>
    partial void OnSelectedLinkKindChanged(LinkKindOption value)
    {
        if (value is null) return;

        _host.Settings.DefaultLinkKind = LinkKinds.ToStorageString(value.Kind);
        _host.Settings.Save();

        // 同步迁移页当前选择（队列未执行项随之切换，见 MigrateViewModel.OnSelectedLinkKindChanged）。
        // Main 在组合根装配完成前为 null（构造期不触发本回调，绑定期才触发），仍做防御。
        if (_host.Main?.Migrate is not { } migrate) return;

        migrate.SelectedLinkKind =
            migrate.LinkKindOptions.FirstOrDefault(o => o.Kind == value.Kind)
            ?? migrate.SelectedLinkKind;
    }

    partial void OnThreadsChanged(int value)
    {
        _host.Settings.DefaultThreads = Math.Clamp(value, 1, 64);
        _host.Settings.Save();
    }

    partial void OnRetentionDaysChanged(int value)
    {
        _host.Settings.BackupRetentionDays = Math.Max(0, value);
        _host.Settings.Save();
        _host.Main.Migrate.RetentionDays = Math.Max(0, value);
    }

    private void BuildPrivilegeLines()
    {
        PrivilegeLines.Clear();

        var p = _host.Privileges;
        PrivilegeLines.Add(new PrivilegeLine(LocalizationService.Instance.Get("priv.elevated"), p.IsElevated));
        PrivilegeLines.Add(new PrivilegeLine(LocalizationService.Instance.Get("priv.devMode"), p.IsDeveloperModeEnabled));
        PrivilegeLines.Add(new PrivilegeLine(LocalizationService.Instance.Get("priv.symlink"), p.CanCreateSymbolicLink));
        PrivilegeLines.Add(new PrivilegeLine(LocalizationService.Instance.Get("priv.seSecurity"), p.HasSeSecurityPrivilege));
        PrivilegeLines.Add(new PrivilegeLine(LocalizationService.Instance.Get("priv.seRestore"), p.HasSeRestorePrivilege));

        EnvText = $"{p.Describe()}"
                  + $"{Environment.NewLine}{LocalizationService.Instance.Get("priv.dataRoot")}：{DirPorterPaths.DataRoot}"
                  + (DirPorterPaths.IsFallback
                      ? $"{Environment.NewLine}{LocalizationService.Instance.Get("priv.fallback")}：{DirPorterPaths.FallbackReason}"
                      : string.Empty);
    }

    [RelayCommand]
    private void Save()
    {
        _host.Settings.Language = Language.Code;
        _host.Settings.Theme = ThemeService.Normalize(Theme.Code);
        _host.Settings.DefaultThreads = Math.Clamp(Threads, 1, 64);
        _host.Settings.BackupRetentionDays = Math.Max(0, RetentionDays);
        _host.Settings.DefaultLinkKind = LinkKinds.ToStorageString((SelectedLinkKind ?? LinkKindOptions[0]).Kind);
        _host.Settings.Save();

        _ui.Info(LocalizationService.Instance.Get("set.saved"));
    }

    /// <summary>是否提供"以管理员身份重启"（已提权就没必要了）。</summary>
    public bool CanRestartElevated => _host.Elevation.CanElevate;

    /// <summary>当前提权状态的事实描述（令牌类型 + 清单 + 开发者模式）。</summary>
    public string ElevationText => _host.Elevation.DescribeState(_host.Privileges);

    /// <summary>
    /// 以管理员身份重启。
    /// 顺序很重要：先释放单实例锁 → 再启动提权实例 → 最后退出当前实例。
    /// 顺序反了会撞上单实例互斥，表现为"点了没反应"。
    /// </summary>
    [RelayCommand]
    public void RestartElevated()
    {
        var loc = LocalizationService.Instance;
        var elevation = _host.Elevation;

        if (!elevation.CanElevate)
        {
            _ui.Info(loc.Get("priv.full"));
            return;
        }

        var body = loc.Get("priv.restartElevatedHint");
        if (elevation.WillPromptForCredentials)
            body += Environment.NewLine + Environment.NewLine
                    + "注意：当前不是管理员账户，UAC 会要求输入管理员账号与密码。";

        if (!_ui.Confirm(body, loc.Get("priv.needAdminTitle"))) return;

        App.ReleaseSingleInstance();

        var launch = elevation.LaunchElevated(string.Empty);

        if (launch.Canceled)
        {
            _ui.Error(loc.Get("priv.elevationCancelled"));
            return;
        }

        if (!launch.Started)
        {
            _ui.Error(string.Format(System.Globalization.CultureInfo.CurrentCulture,
                loc.Get("priv.elevationFailed"), launch.Error));
            return;
        }

        System.Windows.Application.Current?.Shutdown();
    }

    [RelayCommand]
    private void CopyDataRoot() => _ui.CopyToClipboard(DirPorterPaths.DataRoot);

    [RelayCommand]
    private void OpenDataRoot()
    {
        Directory.CreateDirectory(DirPorterPaths.DataRoot);
        _ui.OpenInExplorer(DirPorterPaths.DataRoot);
    }

    [RelayCommand]
    private void OpenCrashFolder()
    {
        var directory = Path.Combine(DirPorterPaths.DataRoot, "crashes");
        Directory.CreateDirectory(directory);

        if (CrashReporter.LastCrashLogPath is { } path && File.Exists(path))
            _ui.OpenInExplorer(path);
        else
            _ui.OpenInExplorer(directory);
    }

    /// <summary>按保留策略清理到期的停车目录（默认干跑，需确认后执行）。</summary>
    [RelayCommand]
    private void SweepBackups()
    {
        var preview = _host.Backups.Sweep(new BackupSweepOptions
        {
            RetentionDays = Math.Max(0, RetentionDays),
            DryRun = true,
        });

        if (preview.Deleted == 0)
        {
            _ui.Info(preview.Describe());
            return;
        }

        if (!_ui.Confirm(preview.Describe() + Environment.NewLine + Environment.NewLine
                         + string.Join(Environment.NewLine, preview.Items.Take(20).Select(i => $"[{i.Action}] {i.ParkingPath}"))))
            return;

        var result = _host.Backups.Sweep(new BackupSweepOptions { RetentionDays = Math.Max(0, RetentionDays) });
        _ui.Info(result.Describe());
    }

    public void RefreshLocalization()
    {
        BuildPrivilegeLines();
        foreach (var option in LanguageOptions) OnPropertyChanged(nameof(option.Display));
        foreach (var option in ThemeOptions) OnPropertyChanged(nameof(option.Display));
        foreach (var option in LinkKindOptions) option.Refresh();
    }
}
