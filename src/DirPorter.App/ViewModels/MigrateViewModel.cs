using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DirPorter.App.Services;
using DirPorter.Core.FileSystem;
using DirPorter.Core.Links;
using DirPorter.Core.Migration;
using DirPorter.Core.Precheck;

namespace DirPorter.App.ViewModels;

/// <summary>迁移队列中的一项。</summary>
public sealed partial class QueueItem : ObservableObject, IPathRow
{
    public QueueItem(string entryId, string sourcePath, string targetPath, LinkKind linkKind = LinkKind.Junction, bool createLink = true)
    {
        EntryId = entryId;
        SourcePath = sourcePath;
        TargetPath = targetPath;
        LinkKind = linkKind;
        CreateLink = createLink;
        StateText = LocalizationService.Instance.Get("mig.state.waiting");
    }

    public string EntryId { get; }

    /// <summary>R12.4：false = 纯迁移（迁移页）——引擎跳过建链，类型列如实显示"不建链"。</summary>
    public bool CreateLink { get; }

    [ObservableProperty] private bool _isSelected = true;
    [ObservableProperty] private string _sourcePath;
    [ObservableProperty] private string _targetPath;
    [ObservableProperty] private LinkKind _linkKind;
    [ObservableProperty] private MigrationState _state = MigrationState.Planned;
    [ObservableProperty] private string _stateText;
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _message = string.Empty;

    /// <summary>双击整行时打开源路径（链接所在位置；资源管理器会自动跟随到真实数据）。</summary>
    public string PrimaryPath => SourcePath;

    /// <summary>R12.4：纯迁移（<see cref="CreateLink"/> = false）不建链 —— 类型列如实显示"不建链"。</summary>
    public string LinkKindText => !CreateLink
        ? LocalizationService.Instance.Get("mig.noLink")
        : LinkKind switch
        {
            LinkKind.Junction => LocalizationService.Instance.Get("link.type.junction"),
            LinkKind.DirectorySymlink => LocalizationService.Instance.Get("link.type.symlinkDir"),
            LinkKind.FileSymlink => LocalizationService.Instance.Get("link.type.symlinkFile"),
            _ => LocalizationService.Instance.Get("link.type.hardlink"),
        };

    /// <summary>把引擎状态映射为界面可读文案。</summary>
    public void ApplyState(MigrationState state, double percent, string? message)
    {
        State = state;
        Percent = Math.Clamp(percent, 0, 100);
        StateText = DescribeState(state);

        if (!string.IsNullOrWhiteSpace(message)) Message = message!;
        OnPropertyChanged(nameof(LinkKindText));
    }

    public static string DescribeState(MigrationState state) => state switch
    {
        MigrationState.Copying => LocalizationService.Instance.Get("mig.state.copying"),
        MigrationState.Verifying => LocalizationService.Instance.Get("mig.state.verifying"),
        MigrationState.Parking or MigrationState.Linking or MigrationState.LinkVerify or MigrationState.LedgerCommit
            => LocalizationService.Instance.Get("mig.state.linking"),
        MigrationState.Done or MigrationState.BackupCleanup => LocalizationService.Instance.Get("mig.state.done"),
        MigrationState.RolledBack or MigrationState.RollingBack => LocalizationService.Instance.Get("mig.state.rolledBack"),
        MigrationState.Failed or MigrationState.Interrupted => LocalizationService.Instance.Get("mig.state.failed"),
        _ => LocalizationService.Instance.Get("mig.state.waiting"),
    };
}

/// <summary>
/// 迁移页 ViewModel。
///
/// 关键点：
/// <list type="bullet">
/// <item>干跑与正式迁移走的是**同一个引擎**，区别只在 <c>DryRun</c> 标志 —— 保证预览所见即所得。</item>
/// <item>暂停/继续通过引擎的 <c>PauseGate</c> 实现（自研 CopyFileEx 引擎才支持真暂停）。</item>
/// <item>取消走完整回滚（引擎内部保证），UI 只负责把 CancellationToken 置位。</item>
/// <item>所有 IO 都在后台线程；UI 线程只消费进度事件（进度报告本身由引擎节流）。</item>
/// </list>
/// </summary>
public sealed partial class MigrateViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly IUserInteraction _ui;
    private readonly UiDebouncer _targetRootDebouncer = new(TimeSpan.FromMilliseconds(500));
    private CancellationTokenSource? _cts;

    /// <summary>
    /// 迁移批次跑完（含部分失败/取消）后通知外壳：携带本批源路径，
    /// 供其他页面（扫描页）刷新"已迁移"标记 —— R12 修复"迁移后仍显示否"的联动缺口。
    /// </summary>
    public event Action<IReadOnlyList<string>>? BatchFinished;

    /// <summary>R12.4：纯迁移成功后是否可前往「链接」页建链（衔接"迁移完就建链"的动线）。</summary>
    [ObservableProperty] private bool _canGoToLink;

    [ObservableProperty] private string _lastMigrationSource = string.Empty;
    [ObservableProperty] private string _lastMigrationTarget = string.Empty;

    /// <summary>跳转到「链接」页并预填最后一条已迁移记录的源/目标。</summary>
    [RelayCommand]
    private void GoToLink()
    {
        if (!CanGoToLink) return;

        _host.Main.Link.LinkPath = LastMigrationSource;
        _host.Main.Link.TargetPath = LastMigrationTarget;
        _host.Main.NavigateTo("link");
    }

    public MigrateViewModel(AppHost host)
    {
        _host = host;
        _ui = host.Interaction;

        _targetRoot = host.Settings.LastTargetRoot ?? string.Empty;
        _threads = host.Settings.DefaultThreads;
        _retentionDays = host.Settings.BackupRetentionDays;

        // 默认建链类型：目录联接（兼容性最好、普通权限即可）。
        // R12 起选项集由 BuildMigrationSet 统一提供：展示 mklink 全部四种类型，
        // 文件级两种（硬链接 / 文件符号链接）在 UI 置灰（目标必须是文件，不能用于目录迁移）。
        LinkKindOptions = LinkKindOption.BuildMigrationSet();

        _selectedLinkKind = LinkKindOptions.FirstOrDefault(o => o.Kind == host.Settings.DefaultLinkKindValue)
                            ?? LinkKindOptions[0];

        _statusText = LocalizationService.Instance.Get("common.ready");
        RefreshLinkKindHint();
    }

    public ObservableCollection<QueueItem> Queue { get; } = new();

    /// <summary>
    /// 建链类型选项 —— R12.4 起**仅作内部占位**：迁移页 = 纯迁移（不建链），UI 不再暴露类型选择；
    /// 真正的类型选择在「一键迁移建链」页（<see cref="QuickMigrateViewModel.LinkKindOptions"/>）。
    /// 保留本集合是为了队列条目的类型显示与历史兼容，不参与迁移页执行语义。
    /// </summary>
    public ObservableCollection<LinkKindOption> LinkKindOptions { get; } = new();

    [ObservableProperty] private string _targetRoot;
    [ObservableProperty] private string _manualSource = string.Empty;
    [ObservableProperty] private string _manualTarget = string.Empty;

    /// <summary>当前选用的建链类型；改动会持久化，并成为「一键迁移并建链」的默认类型。</summary>
    [ObservableProperty] private LinkKindOption _selectedLinkKind;

    /// <summary>关于当前建链类型的一句提示（含权限状态）。</summary>
    [ObservableProperty] private string _linkKindHint = string.Empty;

    [ObservableProperty] private bool _dryRun = true;
    [ObservableProperty] private int _threads;
    /// <summary>
    /// R12.6：默认 **不保留** 停车备份 —— 迁移即"剪切"，完成后源位置及其父目录不残留第二份数据。
    /// 需要可回滚时手动勾选（保留 N 天）。
    /// </summary>
    [ObservableProperty] private bool _keepBackup;
    [ObservableProperty] private int _retentionDays;
    [ObservableProperty] private bool _killLockingProcesses;

    /// <summary>
    /// 同卷时是否用"直接改名"代替复制（默认开，与资源管理器剪切一致）。
    /// 取消勾选即"永远保留一份备份"，用户可据此换回保守行为。
    /// </summary>
    [ObservableProperty] private bool _sameVolumeMove = true;
    [ObservableProperty] private bool _continueOnFailure = true;
    [ObservableProperty] private bool _createRestorePoint;
    [ObservableProperty] private bool _acknowledgeWarnings;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private double _overallPercent;
    [ObservableProperty] private string _statusText;
    [ObservableProperty] private string _speedText = string.Empty;
    [ObservableProperty] private string _etaText = string.Empty;
    [ObservableProperty] private string _previewText = string.Empty;

    partial void OnTargetRootChanged(string value)
    {
        // R12 性能修复：不再每敲一键同步写盘 + 重算全队列——防抖 500ms 合并为一次；
        // 执行迁移前由各入口 Flush 兜底，应用退出时统一保存。
        _targetRootDebouncer.Poke(() =>
        {
            _host.Settings.LastTargetRoot = value ?? string.Empty;
            _host.Settings.Save();
            RetargetQueueItems();
        });
    }

    /// <summary>
    /// 切换建链类型：持久化为默认值，并把队列里**尚未开始**的条目切到新类型
    /// （已成功/失败的条目保持原样——记录必须反映当时真实发生的事）。
    /// 迁移执行中禁止切换（引擎在开跑时已按条目快照，事中改会造成"显示的"和"执行的"不一致）。
    /// </summary>
    partial void OnSelectedLinkKindChanged(LinkKindOption value)
    {
        if (value is null) return;

        _host.Settings.DefaultLinkKind = LinkKinds.ToStorageString(value.Kind);
        _host.Settings.Save();

        foreach (var item in Queue.Where(static q =>
                     q.State is MigrationState.Planned))
        {
            item.LinkKind = value.Kind;
        }

        RefreshLinkKindHint();
    }

    private void RefreshLinkKindHint()
    {
        var loc = LocalizationService.Instance;
        var option = SelectedLinkKind ?? LinkKindOptions[0];

        // 目录符号链接要权限；没有权限时把话说在前头（预检也会拦，但那里已经是执行前一步了）。
        if (option.Kind == LinkKind.DirectorySymlink && !_host.Privileges.CanCreateSymbolicLink)
        {
            LinkKindHint = option.Hint + Environment.NewLine + loc.Get("mig.linkKindNeedPrivilege");
            return;
        }

        LinkKindHint = option.Hint;
    }

    /// <summary>迁移执行中不允许改建链类型（见 <see cref="OnSelectedLinkKindChanged"/>）。</summary>
    public bool CanChangeLinkKind => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanChangeLinkKind));
    }

    // 供 XAML 直接绑定的可执行性（避免反向转换器）。
    public bool CanStart => !IsBusy;
    public bool CanPause => IsBusy && !IsPaused;
    public bool CanResume => IsPaused;
    public bool CanCancel => IsBusy;

    partial void OnIsPausedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
    }

    // ------------------------------------------------------------ 队列

    /// <summary>R12.5：从队列移除单条（不想迁移它了）。执行中禁止移除，避免与批次快照打架。</summary>
    [RelayCommand]
    private void RemoveFromQueue(QueueItem? item)
    {
        if (item is null || IsBusy) return;

        Queue.Remove(item);
    }

    /// <summary>从扫描页转交过来的源目录。</summary>
    public void AddSources(IEnumerable<string> sources)
    {
        var kind = (SelectedLinkKind ?? LinkKindOptions[0]).Kind;

        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source)) continue;
            if (Queue.Any(q => string.Equals(q.SourcePath, source, StringComparison.OrdinalIgnoreCase))) continue;

            Queue.Add(new QueueItem(NextEntryId(), source, BuildTarget(source), kind, createLink: false));   // R12.4：迁移页 = 纯迁移
        }
    }

    [RelayCommand]
    private void AddManual()
    {
        // 粘贴进来的路径常带引号（资源管理器「复制为路径」），先清洗再校验。
        var source = DirPorter.Core.FileSystem.PathNormalizer.CleanInput(ManualSource);
        var target = DirPorter.Core.FileSystem.PathNormalizer.CleanInput(ManualTarget);

        if (source.Length == 0 || target.Length == 0) return;

        try
        {
            DirPorter.Core.FileSystem.PathNormalizer.NormalizeAbsolute(source);
            DirPorter.Core.FileSystem.PathNormalizer.NormalizeAbsolute(target);
        }
        catch (ArgumentException ex)
        {
            _ui.Error(ex.Message);
            return;
        }

        var kind = (SelectedLinkKind ?? LinkKindOptions[0]).Kind;
        Queue.Add(new QueueItem(NextEntryId(), source, target, kind, createLink: false));   // R12.4：纯迁移
        ManualSource = string.Empty;        ManualTarget = string.Empty;
    }

    [RelayCommand]
    private void BrowseManualSource()
    {
        var picked = _ui.PickFolder(null, LocalizationService.Instance.Get("mig.source"));
        if (picked is not null) ManualSource = picked;
    }

    [RelayCommand]
    private void BrowseManualTarget()
    {
        var picked = _ui.PickFolder(null, LocalizationService.Instance.Get("mig.target"));
        if (picked is not null) ManualTarget = picked;
    }

    [RelayCommand]
    private void BrowseTargetRoot()
    {
        var picked = _ui.PickFolder(TargetRoot, LocalizationService.Instance.Get("mig.targetRoot"));
        if (picked is not null) TargetRoot = picked;
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        foreach (var item in Queue.Where(static q => q.IsSelected).ToList()) Queue.Remove(item);
        Renumber();
    }

    [RelayCommand]
    /// <summary>
    /// 清空队列。
    /// ⚠️ 这个命令此前被错误地贴在「刷新」按钮上（标签与行为不符）——用户点"刷新"会丢掉整个队列。
    /// 现在标签改为「清空队列」，并且**必须先确认**（队列里可能有手动添加的条目与勾选状态）。
    /// </summary>
    private void ClearQueue()
    {
        if (Queue.Count == 0)
        {
            StatusText = LocalizationService.Instance.Get("common.ready");
            return;
        }

        if (!_ui.Confirm(LocalizationService.Instance.Get("mig.clearQueueConfirm"))) return;

        Queue.Clear();
        PreviewText = string.Empty;
        OverallPercent = 0;
        StatusText = LocalizationService.Instance.Get("common.ready");
    }

    private string NextEntryId() => $"e-{Queue.Count + 1:00}";

    private void Renumber()
    {
        // EntryId 是只读属性，重排时整体重建队列顺序即可（引擎侧用 ResolveEntryId 兜底）。
        for (var i = 0; i < Queue.Count; i++)
        {
            var item = Queue[i];
            if (item.EntryId == $"e-{i + 1:00}") continue;
        }
    }

    private string BuildTarget(string source)
    {
        var leaf = Path.GetFileName(source.TrimEnd('\\', '/'));
        var root = string.IsNullOrWhiteSpace(TargetRoot) ? string.Empty : TargetRoot.TrimEnd('\\', '/');
        return string.IsNullOrEmpty(root) ? string.Empty : Path.Combine(root, leaf);
    }

    private void RetargetQueueItems()
    {
        if (string.IsNullOrWhiteSpace(TargetRoot)) return;

        foreach (var item in Queue)
        {
            item.TargetPath = BuildTarget(item.SourcePath);
        }
    }

    // ------------------------------------------------------------ 干跑预览

    [RelayCommand]
    private async Task PreviewAsync()
    {
        if (IsBusy) return;

        if (Queue.Count == 0)
        {
            _ui.Info(LocalizationService.Instance.Get("mig.hintNoQueue"));
            return;
        }

        // R11：干跑预览与真实执行同源——只预览**勾选**的条目；全不勾选时明确提示。
        if (!Queue.Any(static q => q.IsSelected))
        {
            _ui.Info(LocalizationService.Instance.Get("mig.noSelection"));
            return;
        }

        IsBusy = true;
        PreviewText = string.Empty;

        try
        {
            // 预览与描述序列用同一份**快照**，避免等待预检期间勾选变动导致"描述与实际不一致"。
            var previewItems = Queue.Where(static q => q.IsSelected).ToList();
            var plan = BuildPlan(previewItems);
            var options = BuildOptions(dryRun: true);

            var result = await _host.Engine.RunBatchAsync(plan, options, null, CancellationToken.None)
                .ConfigureAwait(true);

            PreviewText = RenderPreview(result.Precheck, previewItems);

            foreach (var item in Queue.Where(static q => q.IsSelected))
            {
                item.ApplyState(MigrationState.Planned, 0, null);
            }

            StatusText = LocalizationService.Instance.Get("common.ready");
        }
        catch (Exception ex)
        {
            PreviewText = $"{LocalizationService.Instance.Get("scan.failed")}：{ex.Message}";
            _ui.Error(PreviewText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string RenderPreview(PrecheckReport report, IReadOnlyList<QueueItem>? items = null)
    {
        var loc = LocalizationService.Instance;
        var sb = new StringBuilder();

        sb.AppendLine($"{loc.Get("mig.effective").Replace("{0}", report.EffectiveEntries.Count.ToString(CultureInfo.CurrentCulture))}"
                      + $"  文件 {report.TotalSourceFileCount} 个 / {SpaceRequirement.Format(report.TotalSourceDiskBytes)}");
        sb.AppendLine();

        if (report.SkippedEntries.Count > 0)
        {
            sb.AppendLine($"[{loc.Get("mig.skippedCount").Replace("{0}", report.SkippedEntries.Count.ToString(CultureInfo.CurrentCulture))}]");
            foreach (var skipped in report.SkippedEntries)
                sb.AppendLine($"  · {skipped.SourcePath} — {skipped.Reason}");
            sb.AppendLine();
        }

        void Section(string title, IEnumerable<CheckResult> items)
        {
            var list = items.ToList();
            if (list.Count == 0) return;

            sb.AppendLine($"[{title}]");
            foreach (var item in list)
            {
                sb.AppendLine($"  · [{item.Code}] {item.Title}");
                if (!string.IsNullOrWhiteSpace(item.Detail)) sb.AppendLine($"      {item.Detail}");
                if (!string.IsNullOrWhiteSpace(item.RelatedPath)) sb.AppendLine($"      路径：{item.RelatedPath}");
                if (!string.IsNullOrWhiteSpace(item.UserGuide)) sb.AppendLine($"      建议：{item.UserGuide}");
            }

            sb.AppendLine();
        }

        Section("硬拒绝", report.BlockingResults);
        Section("需确认", report.AcknowledgeResults);

        var warnings = report.Results.Where(static r => !r.IsBlocking && !r.RequiresAcknowledge).ToList();
        Section("提示", warnings);

        if (report.EffectiveEntries.Count > 0)
        {
            sb.AppendLine("[操作序列]");
            foreach (var entry in report.EffectiveEntries)
            {
                sb.AppendLine($"  {entry.EntryId}: {entry.SourcePlainPath}");
                sb.AppendLine($"      → {entry.TargetPlainPath}（{entry.LinkKind}）");
            }
        }

        if (report.IsBlocked) sb.AppendLine().AppendLine("⇒ " + loc.Get("mig.blocked"));

        // 传输方式必须出现在干跑预览里：用户据此判断"这一次到底会不会真的搬数据"。
        // （CLI 的预览此前是写死的步骤表，引入同卷改名后就成了假话，两边都已改为按实际判定输出。）
        sb.AppendLine();
        sb.AppendLine($"[{loc.Get("mig.transferMode")}]");
        foreach (var line in _host.Engine.DescribeTransferModes(BuildPlan(items), BuildOptions(dryRun: true)))
            sb.AppendLine("  " + line);

        return sb.ToString();
    }

    // ------------------------------------------------------------ 迁移执行

    /// <summary>
    /// 交互式入口（迁移页的「迁移并建链」按钮）：跑完用对话框反馈结果。
    /// 真正的执行在 <see cref="RunQueueAsync"/> —— 「一键迁移」页与扫描页触发也复用它，
    /// 保证三条入口走的是**同一段代码**，不会出现"一键路径"和"迁移页路径"行为不一致。
    /// </summary>
    [RelayCommand]
    private async Task StartAsync()
    {
        // 防抖兜底：用户输完目标根立刻点按钮时，把最后值落盘 + 完成队列重定向。
        _targetRootDebouncer.Flush();

        // R12.4：迁移页 = 纯迁移（不建链）——"迁移 + 建链"由「一键迁移建链」页承担。
        var summary = await RunQueueAsync(createLink: false).ConfigureAwait(true);

        if (summary.IsError || summary.Failed > 0)
        {
            _ui.Error(summary.StatusText);
            return;
        }

        // R12.4：纯迁移成功 —— 记录一条已迁移条目供「去建链」预填（源路径已消失，建链能恢复它）。
        var firstDone = Queue.FirstOrDefault(static q => q.State == MigrationState.Done);
        if (firstDone is not null)
        {
            LastMigrationSource = firstDone.SourcePath;
            LastMigrationTarget = firstDone.TargetPath;
            CanGoToLink = true;
        }

        _ui.Info(summary.StatusText);
    }

    /// <summary>
    /// 执行整个队列并返回结果摘要（**不弹任何对话框**，供扫描页「一键迁移并建链」复用）。
    ///
    /// 流程：预检（零磁盘写入）→ 硬拒绝 / 需确认判定 → 可选系统还原点 →
    /// 引擎跑批（复制 → 校验 → 停车 → 建链 → 台账）→ 回填每项最终状态。
    /// </summary>
    public async Task<MigrationRunSummary> RunQueueAsync(bool createLink = true)
    {
        var loc = LocalizationService.Instance;

        if (IsBusy)
            return new MigrationRunSummary(0, 0, false, false, true, loc.Get("common.running"));

        if (Queue.Count == 0)
            return new MigrationRunSummary(0, 0, false, false, true, loc.Get("mig.hintNoQueue"));

        if (string.IsNullOrWhiteSpace(TargetRoot))
            return new MigrationRunSummary(0, 0, false, false, true, loc.Get("mig.targetRoot"));

        // R11：执行批次在开跑时**一次性快照**（勾选 = 要迁移的条目）；执行中改动勾选不影响本批。
        var runItems = Queue.Where(static q => q.IsSelected).ToList();
        if (runItems.Count == 0)
            return new MigrationRunSummary(0, 0, false, false, true, loc.Get("mig.noSelection"));

        // ---------- 1) 预检（零磁盘写入），结果同时渲染到干跑预览框 ----------
        var precheck = await RunPrecheckAsync(runItems, createLink).ConfigureAwait(true);
        if (precheck is null)
            return new MigrationRunSummary(0, 0, false, false, true, loc.Get("scan.failed"));

        PreviewText = RenderPreview(precheck, runItems);

        if (precheck.IsBlocked)
        {
            // 硬拒绝里如果夹着权限问题（受保护目录、需要符号链接），只回一句"被阻止"等于把问题丢回给用户。
            if (ElevationPrompt.LooksLikePrivilegeProblem(precheck)
                && ElevationPrompt.Offer(_host, _ui, loc.Get("mig.blocked")))
            {
                System.Windows.Application.Current?.Shutdown();
            }

            return new MigrationRunSummary(0, 0, true, false, true, loc.Get("mig.blocked"));
        }

        if (precheck.RequiresAcknowledge && !AcknowledgeWarnings)
            return new MigrationRunSummary(0, 0, false, true, true, loc.Get("mig.needsAck"));

        // ---------- 2) 可选：系统还原点（失败不阻塞主流程，按需求） ----------
        if (CreateRestorePoint)
        {
            var restorePoint = await SystemRestorePoint.TryCreateAsync("DirPorter 迁移前").ConfigureAwait(true);
            if (!restorePoint.Created)
                _ui.Info($"系统还原点创建失败（不影响迁移）：{restorePoint.Message}");
        }

        // ---------- 3) 跑批 ----------
        IsBusy = true;
        IsPaused = false;
        OverallPercent = 0;
        StatusText = loc.Get("common.running");
        _cts = new CancellationTokenSource();

        var totalBytes = Math.Max(1, precheck.TotalSourceDiskBytes);
        var itemMap = runItems.ToDictionary(static q => q.EntryId, StringComparer.OrdinalIgnoreCase);

        var progress = new Progress<MigrationProgress>(p =>
        {
            if (!itemMap.TryGetValue(p.EntryId, out var item)) return;

            item.ApplyState(p.State, p.Percent, p.Message);

            var weighted = (double)p.BytesDone / totalBytes * 100;
            OverallPercent = Math.Clamp(Math.Max(OverallPercent, weighted), 0, 100);

            SpeedText = SpaceRequirement.Format((long)p.BytesPerSecond) + "/s";
            if (p.BytesPerSecond > 1 && p.BytesTotal > p.BytesDone)
            {
                var seconds = (p.BytesTotal - p.BytesDone) / p.BytesPerSecond;
                EtaText = TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
            }
        });

        var ok = 0;
        var failed = 0;
        var isError = false;

        try
        {
            var plan = BuildPlan(runItems, createLink);
            var options = BuildOptions(dryRun: false);
            var result = await _host.Engine.RunBatchAsync(plan, options, progress, _cts.Token).ConfigureAwait(true);

            // 用最终状态兜底刷新队列（引擎的进度事件在结束时可能最后一次未到）。
            foreach (var entry in result.Entries)
            {
                if (!itemMap.TryGetValue(entry.EntryId, out var item)) continue;

                item.ApplyState(entry.FinalState, entry.Success ? 100 : item.Percent, entry.Message);

                if (!entry.Success && entry.RollbackSteps.Count > 0)
                    item.Message = $"已回滚：{string.Join("；", entry.RollbackSteps)}";
                if (entry.RollbackFailures.Count > 0)
                    item.Message = $"回滚失败：{string.Join("；", entry.RollbackFailures)}";
            }

            ok = result.Entries.Count(static e => e.Success);
            failed = result.Entries.Count - ok;

            OverallPercent = 100;
            StatusText = string.Format(CultureInfo.CurrentCulture, loc.Get("mig.done"), ok, failed);

            // 迁移记录页需要刷新（新记录已落盘）。
            _host.Main.History.Reload();
        }
        catch (OperationCanceledException)
        {
            isError = true;
            StatusText = loc.Get("common.cancel");
            // 只标记**本批**（快照）未完成的项；未勾选的条目根本没跑，不该被标成"已回滚"。
            foreach (var item in runItems.Where(static q => q.State is not (MigrationState.Done or MigrationState.Failed or MigrationState.RolledBack)))
                item.ApplyState(MigrationState.RolledBack, item.Percent, null);
        }
        catch (Exception ex)
        {
            isError = true;
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
            IsPaused = false;
            _cts?.Dispose();
            _cts = null;
        }

        // R12：批次跑完（成功/部分失败/取消都算"跑过"）→ 通知外壳刷新跨页状态。
        try { BatchFinished?.Invoke(runItems.Select(static q => q.SourcePath).ToList()); }
        catch { /* 跨页刷新失败不影响本批结果 */ }

        // R12.10：批次结束后顺带清理"已到期"的历史停车备份（保留天数来自设置）。
        // 只清理到期项，绝不碰本批刚产生的备份；失败不影响迁移结果。
        _ = Task.Run(() =>
        {
            try { _host.SweepExpiredBackups(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        });

        return new MigrationRunSummary(ok, failed, false, false, isError, StatusText);
    }

    /// <summary>
    /// 把队列**替换**为给定源目录（「一键迁移建链」页用；扫描页批量入队走 <see cref="AddSources"/>）。
    /// 刻意不做"追加"：一键执行的就是本次勾选的这批，迁移页展示的也就是这一批，避免隐性累加。
    /// </summary>
    /// <param name="sources">源目录路径。</param>
    /// <param name="linkKind">建链类型（仅 <paramref name="createLink"/> 为 true 时生效）。</param>
    /// <param name="createLink">true = 迁移并建链（一键页）；false = 纯迁移。</param>
    public void ReplaceQueue(
        IEnumerable<string> sources,
        LinkKind linkKind = LinkKind.Junction,
        bool createLink = true)
    {
        Queue.Clear();
        PreviewText = string.Empty;
        OverallPercent = 0;
        SpeedText = string.Empty;
        EtaText = string.Empty;

        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source)) continue;
            if (Queue.Any(q => string.Equals(q.SourcePath, source, StringComparison.OrdinalIgnoreCase))) continue;

            Queue.Add(new QueueItem(NextEntryId(), source, BuildTarget(source), linkKind, createLink));
        }
    }



    private async Task<PrecheckReport?> RunPrecheckAsync(IReadOnlyList<QueueItem>? items = null, bool createLink = true)
    {
        try
        {
            var plan = BuildPlan(items, createLink);
            var options = BuildOptions(dryRun: true);
            var result = await _host.Engine.RunBatchAsync(plan, options, null, CancellationToken.None)
                .ConfigureAwait(true);
            return result.Precheck;
        }
        catch (Exception ex)
        {
            _ui.Error(ex.Message);
            return null;
        }
    }

    [RelayCommand]
    private void Pause()
    {
        var gate = _host.Engine.PauseGate;
        if (gate is null)
        {
            _ui.Info("当前复制引擎不支持暂停（仅自研引擎支持）。");
            return;
        }

        gate.Pause();
        IsPaused = true;
    }

    [RelayCommand]
    private void Resume()
    {
        _host.Engine.PauseGate?.Resume();
        IsPaused = false;
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    /// <summary>
    /// 构造迁移计划。R11：只把**勾选**的条目纳入（勾选 = 要迁移的条目，与扫描页语义一致）。
    /// items 参数供执行方传入**一次性快照**，保证执行中改动勾选不影响已开跑的批次。
    /// internal 供测试直接断言。
    /// </summary>
    internal MigrationPlan BuildPlan(IReadOnlyList<QueueItem>? items = null, bool createLink = true)
    {
        var source = items ?? Queue.Where(static q => q.IsSelected).ToList();
        var entries = source.Select(q => new MigrationPlanEntry(
            q.SourcePath,
            q.TargetPath,
            // 纯迁移（createLink=false）不建链：LinkKind 用"目录联接"占位——
            // 预检按它判定能力（Junction 无权限要求），保证无符号链接权限的环境不被误阻断。
            createLink ? q.LinkKind : DirPorter.Core.Links.LinkKind.Junction,
            q.EntryId,
            createLink)).ToList();
        return new MigrationPlan(entries, new PrecheckOptions
        {
            AcknowledgeWarnings = AcknowledgeWarnings,
            ReuseEmptyTargetDirectory = true,
        });
    }

    private MigrationTaskOptions BuildOptions(bool dryRun) => new()
    {
        DryRun = dryRun,
        CopyEngine = CopyEngineKind.Native,
        CopyThreads = Math.Clamp(Threads, 1, 64),
        BackupPolicy = KeepBackup ? BackupCleanupPolicy.KeepDays : BackupCleanupPolicy.Immediate,
        BackupRetentionDays = Math.Max(0, RetentionDays),
        AcknowledgeWarnings = AcknowledgeWarnings,
        KillLockingProcesses = KillLockingProcesses,
        Verify = VerifyMode.Metadata,
        SameVolumeMove = SameVolumeMove,
    };

    public void RefreshLocalization()
    {
        foreach (var option in LinkKindOptions) option.Refresh();
        RefreshLinkKindHint();

        foreach (var item in Queue)
        {
            item.StateText = QueueItem.DescribeState(item.State);
            OnPropertyChanged(nameof(item));
        }

        if (!IsBusy && Queue.Count == 0) StatusText = LocalizationService.Instance.Get("common.ready");
    }
}

/// <summary>一次队列执行的结果摘要（供「一键迁移并建链」把结果回报给发起页）。</summary>
public readonly record struct MigrationRunSummary(
    int Ok,
    int Failed,
    bool Blocked,
    bool NeedsAcknowledge,
    bool IsError,
    string StatusText);
