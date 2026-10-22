using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DirPorter.App.Services;
using DirPorter.Core.FileSystem;
using DirPorter.Core.Journal;
using DirPorter.Core.Links;
using DirPorter.Core.Scan;

namespace DirPorter.App.ViewModels;

/// <summary>一个扫描根目录。</summary>
public sealed partial class ScanRootItem : ObservableObject
{
    public ScanRootItem(string path, bool isPreset)
    {
        Path = path;
        IsPreset = isPreset;
    }

    public string Path { get; }
    public bool IsPreset { get; }

    [ObservableProperty] private string _summary = string.Empty;

    public override string ToString() => Path;
}

/// <summary>扫描结果一行（根目录或根下的一级子目录）。</summary>
public sealed partial class ScanRow : ObservableObject, IPathRow
{
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _path = string.Empty;
    [ObservableProperty] private long _sizeBytes;
    [ObservableProperty] private long _fileCount;
    [ObservableProperty] private long _directoryCount;
    [ObservableProperty] private bool _isMigrated;
    [ObservableProperty] private string? _linkTarget;
    [ObservableProperty] private bool _hasRisk;
    [ObservableProperty] private string _riskText = string.Empty;
    [ObservableProperty] private bool _isRoot;
    [ObservableProperty] private string _migratedText = string.Empty;

    public string SizeText => SpaceRequirement.Format(SizeBytes);

    /// <summary>双击整行时打开这个路径的所在文件夹。</summary>
    public string PrimaryPath => Path;

    partial void OnSizeBytesChanged(long value) => OnPropertyChanged(nameof(SizeText));

    /// <summary>
    /// R12 修复：状态一变，绑定用的显示文本**立即**同步。
    /// 此前 MigratedText 只在语言切换（RefreshLocalization）时重算，
    /// 迁移完成后 IsMigrated 已为 true、界面却仍显示"否"——这就是用户的现场。
    /// </summary>
    partial void OnIsMigratedChanged(bool value)
        => MigratedText = LocalizationService.Instance.Get(value ? "common.yes" : "common.no");

    /// <summary>语言切换后刷新依赖本地化的文本。</summary>
    public void RefreshLocalization()
    {
        MigratedText = LocalizationService.Instance.Get(IsMigrated ? "common.yes" : "common.no");
    }
}

/// <summary>扫描页预置项（路径 + 本地化名称键）。</summary>
public sealed partial class ScanPreset : ObservableObject
{
    public ScanPreset(string nameKey, string path)
    {
        NameKey = nameKey;
        Path = path;
        Refresh();
    }

    public string NameKey { get; }
    public string Path { get; }

    [ObservableProperty] private string _display = string.Empty;

    public void Refresh() => Display = LocalizationService.Instance.Get(NameKey);
}

/// <summary>
/// 扫描页 ViewModel（R12 起为**纯扫描分析**）。
///
/// 内存策略：扫描本身走 <see cref="DirectoryScanner"/> 的流式聚合（O(1) 内存）；
/// 需要"按一级子目录看占用"时，只用一个**只汇总顶层子目录**的 collector，
/// 其内存占用与"顶层子目录数量"成正比，而不是与文件总数成正比。
///
/// 职责边界（R12 完全拆分）：
///  · 本页只做"扫描分析 + 勾选/行级 → 加入迁移队列"；
///  · 一键迁移在独立的「一键迁移」页（<see cref="QuickMigrateViewModel"/>）；
///  · 队列精细控制在「迁移」页；建链工具在「链接」页。
/// </summary>
public sealed partial class ScanViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly IUserInteraction _ui;
    private readonly List<ScanRow> _all = new();
    private readonly UiDebouncer _filterDebouncer = new(TimeSpan.FromMilliseconds(250));
    private CancellationTokenSource? _cts;

    /// <summary>请求把选中的路径加入迁移队列（由外壳转交给迁移页并跳转）。</summary>
    public event Action<IReadOnlyList<string>>? EnqueueRequested;

    public ScanViewModel(AppHost host)
    {
        _host = host;
        _ui = host.Interaction;

        Presets = BuildPresets();

        // 用户上次自定义的根目录
        foreach (var root in host.Settings.ScanRoots.Where(Directory.Exists))
            Roots.Add(new ScanRootItem(root, isPreset: false));

        MinSizeText = string.Empty;
        Status = LocalizationService.Instance.Get("common.ready");

        // 空状态提示与结果表格互斥显示（R11）：集合变化即通知。
        Results.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasResults));
    }

    public ObservableCollection<ScanRootItem> Roots { get; } = new();

    public ObservableCollection<ScanRow> Results { get; } = new();

    /// <summary>结果区是否有内容（驱动空状态引导文案）。</summary>
    public bool HasResults => Results.Count > 0;

    public IReadOnlyList<ScanPreset> Presets { get; }

    [ObservableProperty] private ScanRootItem? _selectedRoot;
    [ObservableProperty] private string _newRootPath = string.Empty;
    [ObservableProperty] private string _minSizeText = string.Empty;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;

    /// <summary>是否可执行（扫描用）。</summary>
    public bool CanScan => !IsBusy;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanScan));

    private static IReadOnlyList<ScanPreset> BuildPresets()
    {
        var list = new List<ScanPreset>();

        void Add(string key, Environment.SpecialFolder folder)
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                list.Add(new ScanPreset(key, path));
        }

        Add("scan.preset.localAppData", Environment.SpecialFolder.LocalApplicationData);
        Add("scan.preset.roamingAppData", Environment.SpecialFolder.ApplicationData);
        Add("scan.preset.programFiles", Environment.SpecialFolder.ProgramFiles);
        Add("scan.preset.programFilesX86", Environment.SpecialFolder.ProgramFilesX86);
        Add("scan.preset.programData", Environment.SpecialFolder.CommonApplicationData);

        return list;
    }

    // ------------------------------------------------------------ 根目录管理

    [RelayCommand]
    private void AddPreset(ScanPreset? preset)
    {
        if (preset is null) return;
        AddRootCore(preset.Path, isPreset: true);
    }

    [RelayCommand]
    private void AddRoot()
    {
        if (string.IsNullOrWhiteSpace(NewRootPath)) return;
        AddRootCore(NewRootPath.Trim(), isPreset: false);
        NewRootPath = string.Empty;
    }

    [RelayCommand]
    private void BrowseRoot()
    {
        var picked = _ui.PickFolder(null, LocalizationService.Instance.Get("scan.roots"));
        if (picked is not null) AddRootCore(picked, isPreset: false);
    }

    /// <summary>
    /// 移除选中的扫描根目录。
    ///
    /// ⚠️ 曾经的崩溃点：`Roots.Remove(SelectedRoot)` 会让 ListBox 的 SelectedItem 变成 null，
    /// 双向绑定立刻把 null 回写到 `SelectedRoot`，紧接着的 `SelectedRoot.IsPreset` 就空引用了
    /// （用户实测的崩溃日志：`ScanViewModel.RemoveRoot()` NullReferenceException）。
    /// 修法：先取到**局部变量**，之后一律用局部变量，不再碰这个会被绑定时序改写的属性。
    /// </summary>
    [RelayCommand]
    private void RemoveRoot()
    {
        var root = SelectedRoot;
        if (root is null) return;

        Roots.Remove(root);

        // 移除后列表选中项可能落到别的项上，这里主动清空，避免"看起来还选中着上一个"。
        SelectedRoot = null;

        if (root.IsPreset) return;

        _host.Settings.ScanRoots.RemoveAll(p =>
            string.Equals(p, root.Path, StringComparison.OrdinalIgnoreCase));
        _host.Settings.Save();
    }

    /// <summary>清除全部扫描根目录。</summary>
    [RelayCommand]
    private void ClearRoots()
    {
        Roots.Clear();
        SelectedRoot = null;
        _host.Settings.ScanRoots.Clear();
        _host.Settings.Save();
    }

    private void AddRootCore(string path, bool isPreset)
    {
        path = path.TrimEnd('\\', '/');

        if (!Directory.Exists(path))
        {
            _ui.Error(LocalizationService.Instance.Get("scan.hintNoRoots"));
            return;
        }

        if (Roots.Any(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase))) return;

        Roots.Add(new ScanRootItem(path, isPreset));

        if (!isPreset && !_host.Settings.ScanRoots.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            _host.Settings.ScanRoots.Add(path);
            _host.Settings.Save();
        }
    }

    // ------------------------------------------------------------ 扫描

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsBusy) return;

        if (Roots.Count == 0)
        {
            _ui.Info(LocalizationService.Instance.Get("scan.hintNoRoots"));
            return;
        }

        IsBusy = true;
        Progress = 0;
        _all.Clear();
        Results.Clear();
        Status = LocalizationService.Instance.Get("scan.scanning");

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            var totalElapsed = TimeSpan.Zero;
            long totalFiles = 0, totalDirs = 0, totalBytes = 0;

            for (var i = 0; i < Roots.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var root = Roots[i];

                var progress = new Progress<ScanProgress>(p =>
                {
                    var basePercent = (double)i / Roots.Count * 100;
                    var slice = 100.0 / Roots.Count;
                    var inner = Math.Min(1.0, p.FilesFound / 200_000.0);
                    Progress = basePercent + slice * inner;
                    Status = $"{LocalizationService.Instance.Get("scan.scanning")} {p.CurrentDirectory}";
                });

                var topLevel = new ConcurrentDictionary<string, (long Files, long Bytes, long Disk)>(StringComparer.OrdinalIgnoreCase);

                var options = new ScanOptions
                {
                    Threads = Math.Max(2, Environment.ProcessorCount),
                    DetectSpecialObjects = true,
                    CollectEntries = false,
                    FollowReparsePoints = false,
                };

                var rootPath = root.Path;

                var summary = await Task.Run(() => DirectoryScanner.Scan(
                    rootPath,
                    options,
                    progress,
                    token,
                    entry =>
                    {
                        // 只汇总"顶层子目录"：内存与顶层目录数成正比，与文件总数无关。
                        if (entry.Kind != ScanEntryKind.File) return;

                        var rel = entry.RelativePath;
                        var sep = rel.IndexOfAny(new[] { '\\', '/' });
                        var key = sep < 0 ? string.Empty : rel[..sep];

                        topLevel.AddOrUpdate(
                            key,
                            _ => (1, entry.Size, entry.DiskBytes),
                            (_, old) => (old.Files + 1, old.Bytes + entry.Size, old.Disk + entry.DiskBytes));
                    }), token).ConfigureAwait(true);

                totalElapsed += summary.Elapsed;
                totalFiles += summary.FileCount;
                totalDirs += summary.DirectoryCount;
                totalBytes += summary.DiskBytes;

                root.Summary = summary.Describe();

                // 根目录整行
                _all.Add(CreateRow(root.Path, summary.DiskBytes, summary.FileCount, summary.DirectoryCount,
                    isRoot: true,
                    riskText: BuildRiskText(summary),
                    hasRisk: summary.Truncated || summary.SpecialObjects.Count > 0 || summary.Errors.Count > 0));

                // 一级子目录行（只对根下真实存在的子目录建行）
                foreach (var pair in topLevel.OrderByDescending(static p => p.Value.Disk))
                {
                    if (string.IsNullOrEmpty(pair.Key)) continue;   // 根下散落的文件

                    var childPath = Path.Combine(root.Path, pair.Key);
                    _all.Add(CreateRow(childPath, pair.Value.Disk, pair.Value.Files, 0,
                        isRoot: false, riskText: string.Empty, hasRisk: false));
                }

                Progress = (double)(i + 1) / Roots.Count * 100;
            }

            ApplyFilter();

            Status = string.Format(CultureInfo.CurrentCulture,
                LocalizationService.Instance.Get("scan.done"),
                totalFiles, totalDirs, SpaceRequirement.Format(totalBytes), totalElapsed.TotalSeconds);

            Summary = Status;
        }
        catch (OperationCanceledException)
        {
            Status = LocalizationService.Instance.Get("common.cancel");
        }
        catch (Exception ex)
        {
            Status = $"{LocalizationService.Instance.Get("scan.failed")}：{ex.Message}";
            _ui.Error(Status);
        }
        finally
        {
            IsBusy = false;
            Progress = 100;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void CancelScan() => _cts?.Cancel();

    private ScanRow CreateRow(string path, long size, long files, long dirs, bool isRoot, string riskText, bool hasRisk)
    {
        var row = new ScanRow
        {
            Path = path,
            SizeBytes = size,
            FileCount = files,
            DirectoryCount = dirs,
            IsRoot = isRoot,
            RiskText = riskText,
            HasRisk = hasRisk,
        };

        RefreshMigratedFlag(row);
        row.RefreshLocalization();
        return row;
    }

    /// <summary>重新判定某行是否已是链接（迁移完成后刷新用）。</summary>
    private void RefreshMigratedFlag(ScanRow row)
    {        row.IsMigrated = false;
        row.LinkTarget = null;

        try
        {
            var info = _host.Links.Inspect(row.Path);
            if (info.IsKnownLink && info.Kind is LinkKind.Junction or LinkKind.DirectorySymlink)
            {
                row.IsMigrated = true;
                row.LinkTarget = info.TargetAbsolute;
            }
            else if (!info.PathExists && FindDoneRecord(row.Path) is { } record)
            {
                // R12.4：纯迁移（不建链）后源路径**不存在**——以台账兜底判定"已迁移"：
                // 该源有 done 记录且目标仍存在。否则迁移页搬走的数据在扫描页会误显示"否"。
                row.IsMigrated = true;
                row.LinkTarget = record.TargetPath;
            }
        }
        catch
        {
            // 检视失败不影响展示
        }
    }

    private static string BuildRiskText(ScanSummary summary)
    {
        var parts = new List<string>();

        if (summary.Truncated) parts.Add(LocalizationService.Instance.Get("scan.truncated"));

        parts.AddRange(summary.SpecialObjects.Select(s => $"{ScanSummary.Describe(s.Kind)}×{s.Count}"));

        if (summary.DeepProbeSkippedForScale) parts.Add("深入探测已按规模跳过");

        return string.Join("、", parts);
    }

    // ------------------------------------------------------------ 过滤 / 选择

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var row in Results) row.IsSelected = true;
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var row in _all) row.IsSelected = false;
    }

    // R12 性能修复：过滤输入防抖——不再每敲一键全量重建结果集。
    partial void OnMinSizeTextChanged(string value) => _filterDebouncer.Poke(ApplyFilter);

    partial void OnSearchTextChanged(string value) => _filterDebouncer.Poke(ApplyFilter);

    private void ApplyFilter()
    {
        var minBytes = ParseSize(MinSizeText);
        var needle = SearchText?.Trim() ?? string.Empty;

        var filtered = _all
            .Where(r => r.SizeBytes >= minBytes)
            .Where(r => needle.Length == 0 || r.Path.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static r => r.SizeBytes)
            .ToList();

        Results.Clear();
        foreach (var row in filtered) Results.Add(row);

        if (Results.Count == 0 && _all.Count > 0)
            Status = LocalizationService.Instance.Get("scan.hintEmpty");
    }

    // ------------------------------------------------------------ 加入迁移队列（本页唯一出口）

    /// <summary>勾选项加入迁移队列（由外壳转交迁移页并跳转）。</summary>
    [RelayCommand]
    private void AddToQueue()
    {
        var paths = SelectedPaths();
        if (paths.Count == 0)
        {
            _ui.Info(LocalizationService.Instance.Get("scan.noSelection"));
            return;
        }

        EnqueueRequested?.Invoke(paths);
        Status = string.Format(CultureInfo.CurrentCulture,
            LocalizationService.Instance.Get("mig.effective"), paths.Count);
    }

    /// <summary>整行加入迁移队列（表格行内按钮）。</summary>
    [RelayCommand]
    private void EnqueueRow(ScanRow? row)
    {
        if (row is null) return;

        EnqueueRequested?.Invoke(new[] { row.Path });
        Status = string.Format(CultureInfo.CurrentCulture,
            LocalizationService.Instance.Get("mig.effective"), 1);
    }

    // ------------------------------------------------------------ 已迁移标记刷新（R12 跨页联动入口）

    /// <summary>
    /// 由外壳在**迁移批次完成后**调用（无论从哪个入口执行：一键页 / 迁移页）：
    /// 对命中路径的行重新判定"已迁移"，让扫描列表立刻反映真实状态。
    /// </summary>
    public void RefreshMigratedFlags(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;

        foreach (var row in _all.Where(r => paths.Contains(r.Path, StringComparer.OrdinalIgnoreCase)))
            RefreshMigratedFlag(row);
    }

    /// <summary>纯迁移（不建链）后的"已迁移"依据：台账里有该源的 done 记录且目标仍存在。</summary>
    private MigrationRecord? FindDoneRecord(string sourcePath)
    {
        try
        {
            return _host.Index.LoadAll().FirstOrDefault(r =>
                string.Equals(r.Status, "done", StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(r.TargetPath)
                && string.Equals(r.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;   // 台账不可读：退回"未迁移"，不阻断页面
        }
    }

    private List<string> SelectedPaths()
        => _all.Where(static r => r.IsSelected).Select(static r => r.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>解析 "100MB" / "1.5GB" / "500KB" / "1024" / "" 等体积写法。</summary>
    internal static long ParseSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;

        var value = text.Trim().ToUpperInvariant().Replace(" ", string.Empty, StringComparison.Ordinal);

        var multiplier = 1L;
        foreach (var (suffix, factor) in new (string Suffix, long Factor)[]
                 {
                     ("TB", 1024L * 1024 * 1024 * 1024),
                     ("GB", 1024L * 1024 * 1024),
                     ("MB", 1024L * 1024),
                     ("KB", 1024L),
                     ("B", 1L),
                 })
        {
            if (!value.EndsWith(suffix, StringComparison.Ordinal)) continue;
            value = value[..^suffix.Length];
            multiplier = factor;
            break;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? (long)(number * multiplier)
            : 0;
    }

    public void RefreshLocalization()
    {
        foreach (var preset in Presets) preset.Refresh();
        foreach (var row in _all) row.RefreshLocalization();

        if (!IsBusy && Results.Count == 0) Status = LocalizationService.Instance.Get("common.ready");
    }
}
