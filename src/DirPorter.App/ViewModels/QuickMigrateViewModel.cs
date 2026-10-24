using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DirPorter.App.Services;

namespace DirPorter.App.ViewModels;

/// <summary>一键迁移页的源目录条目。</summary>
public sealed record QuickSourceItem(string Path);

/// <summary>
/// 「一键迁移」独立页（R12）：主功能、最短动线——
/// 选源目录（支持多个）→ 选目标根 → 一个按钮完成「复制 → 校验 → 建链」。
///
/// 与扫描页/迁移页完全解耦：不需要先扫描、不经过队列手动操作；
/// 执行复用 <see cref="MigrateViewModel.RunQueueAsync"/>（扫描页一键、迁移页、本页
/// 三条入口走同一段执行代码，行为不会分叉）。链接类型与迁移页共享同一份选择（默认 /J 目录联接）。
/// </summary>
public sealed partial class QuickMigrateViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly IUserInteraction _ui;
    private readonly UiDebouncer _targetRootDebouncer = new(TimeSpan.FromMilliseconds(500));
    private bool _statusIsIdle = true;

    public QuickMigrateViewModel(AppHost host, MigrateViewModel migrate)
    {
        _host = host;
        _ui = host.Interaction;
        Migrate = migrate;

        _targetRoot = host.Settings.LastTargetRoot ?? string.Empty;
        _status = LocalizationService.Instance.Get("quick.resultIdle");

        // R12.4：建链类型选择归属本页（迁移页已是纯迁移）——四种全展示，文件级两种置灰。
        LinkKindOptions = LinkKindOption.BuildMigrationSet();
        _selectedLinkKind = LinkKindOptions.FirstOrDefault(o => o.Kind == host.Settings.DefaultLinkKindValue)
                            ?? LinkKindOptions[0];
    }

    /// <summary>迁移页引用：共享其队列执行（本页传 createLink=true 走"迁移并建链"）。</summary>
    public MigrateViewModel Migrate { get; }

    /// <summary>建链类型选项（R12.4：本页独立持有，迁移页不再展示类型选择）。</summary>
    public ObservableCollection<LinkKindOption> LinkKindOptions { get; }

    [ObservableProperty] private LinkKindOption _selectedLinkKind;
    [ObservableProperty] private string _linkKindHint = string.Empty;

    public ObservableCollection<QuickSourceItem> Sources { get; } = new();

    [ObservableProperty] private string _targetRoot;
    [ObservableProperty] private string _newSourcePath = string.Empty;
    [ObservableProperty] private bool _acknowledgeWarnings;

    /// <summary>R12.4：与迁移页对齐——占用源目录时允许本工具结束占用进程（默认关，慎用）。</summary>
    [ObservableProperty] private bool _killLockingProcesses;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status;

    /// <summary>是否可执行（一键按钮与浏览共用）。</summary>
    public bool CanRun => !IsBusy;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanRun));

    partial void OnTargetRootChanged(string value)
    {
        // R12 性能修复：不再每敲一键同步写盘——防抖 500ms 合并为一次；
        // 执行迁移前会 Flush 兜底，退出时由 App 统一保存。
        _targetRootDebouncer.Poke(() =>
        {
            _host.Settings.LastTargetRoot = value ?? string.Empty;
            _host.Settings.Save();
        });
    }

    // ------------------------------------------------------------ 源目录

    [RelayCommand]
    private void BrowseSource()
    {
        var picked = _ui.PickFolder(null, LocalizationService.Instance.Get("quick.sources"));
        if (!string.IsNullOrWhiteSpace(picked)) AddSourceCore(picked);
    }

    [RelayCommand]
    private void AddSourceText()
    {
        if (!string.IsNullOrWhiteSpace(NewSourcePath))
        {
            AddSourceCore(NewSourcePath);
            NewSourcePath = string.Empty;
        }
    }

    private void AddSourceCore(string path)
    {
        path = path.TrimEnd('\\', '/');

        if (!Directory.Exists(path))
        {
            _ui.Error(LocalizationService.Instance.Get("quick.sourceMissing"));
            return;
        }

        if (Sources.Any(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase))) return;

        Sources.Add(new QuickSourceItem(path));
    }

    [RelayCommand]
    private void RemoveSource(QuickSourceItem? item)
    {
        if (item is not null) Sources.Remove(item);
    }

    [RelayCommand]
    private void ClearSources() => Sources.Clear();

    [RelayCommand]
    private void BrowseTargetRoot()
    {
        var picked = _ui.PickFolder(TargetRoot, LocalizationService.Instance.Get("mig.targetRoot"));
        if (picked is not null) TargetRoot = picked;
    }

    // ------------------------------------------------------------ 一键执行

    [RelayCommand]
    private async Task MigrateNowAsync()
    {
        if (IsBusy) return;

        var loc = LocalizationService.Instance;

        if (Sources.Count == 0)
        {
            _ui.Error(loc.Get("quick.needSource"));
            return;
        }

        if (string.IsNullOrWhiteSpace(TargetRoot))
        {
            _ui.Error(loc.Get("scan.needTargetRoot"));
            return;
        }

        IsBusy = true;
        _statusIsIdle = false;
        var previousStatus = Status;

        try
        {
            // 持久化兜底：把防抖中的最后值落盘（用户可能输完立刻点按钮）。
            _targetRootDebouncer.Flush();

            // 目标根与风险确认一并带过去；队列替换为本批（与扫描页一键同一模式）。
            Migrate.TargetRoot = TargetRoot;
            Migrate.AcknowledgeWarnings = AcknowledgeWarnings;
            Migrate.KillLockingProcesses = KillLockingProcesses;   // R12.4：占用时可选结束进程
            var linkKind = (SelectedLinkKind ?? LinkKindOptions[0]).Kind;
            Migrate.ReplaceQueue(Sources.Select(static s => s.Path), linkKind, createLink: true);   // 一键 = 迁移 + 建链

            Status = string.Format(CultureInfo.CurrentCulture, loc.Get("scan.linking"), Sources.Count);

            var summary = await Migrate.RunQueueAsync().ConfigureAwait(true);

            if (summary.NeedsAcknowledge)
            {
                Status = loc.Get("mig.needsAck");
                _ui.Error(Status);
                return;
            }

            if (summary.Blocked)
            {
                // summary.StatusText 里已带"哪一项被拒 + 建议怎么做"，别再覆盖成固定文案。
                Status = summary.StatusText;
                _ui.Error(Status);
                return;
            }

            // 前置拒绝（无勾选/预检失败/取消 等）：如实报告，绝不显示"完成 0 项"这种误导文案。
            if (summary.IsError && summary.Ok == 0 && summary.Failed == 0)
            {
                Status = summary.StatusText;
                _ui.Error(Status);
                return;
            }

            Status = summary.Failed == 0
                ? string.Format(CultureInfo.CurrentCulture, loc.Get("scan.doneAndLinked"), summary.Ok)
                : string.Format(CultureInfo.CurrentCulture, loc.Get("scan.partial"), summary.Ok, summary.Failed);

            if (summary.Failed > 0) _ui.Error(Status);
        }
        catch (Exception ex)
        {
            Status = $"{loc.Get("scan.failed")}：{ex.Message}";
            _ui.Error(Status);
        }
        finally
        {
            IsBusy = false;
            if (string.IsNullOrEmpty(Status)) Status = previousStatus;
        }
    }

    public void RefreshLocalization()
    {
        if (_statusIsIdle) Status = LocalizationService.Instance.Get("quick.resultIdle");
    }
}
