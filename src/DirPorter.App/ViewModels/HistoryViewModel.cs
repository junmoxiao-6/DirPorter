using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DirPorter.App.Services;
using DirPorter.Core.FileSystem;
using DirPorter.Core.Journal;
using DirPorter.Core.Links;
using Microsoft.Win32;

namespace DirPorter.App.ViewModels;

/// <summary>迁移记录的一行。</summary>
public sealed partial class HistoryRow : ObservableObject, IPathRow
{
    public HistoryRow(MigrationRecord record) => Record = record;

    public MigrationRecord Record { get; }

    public string Id => Record.Id;
    public string BatchId => Record.BatchId;
    public string SourcePath => Record.SourcePath;
    public string TargetPath => Record.TargetPath;
    public string BackupPath => Record.BackupPath ?? string.Empty;
    public string Status => Record.Status;
    public string LinkType => Record.LinkType ?? string.Empty;
    public long FileCount => Record.FileCount;
    public long TotalBytes => Record.TotalBytes;
    public string SizeText => SpaceRequirement.Format(Record.TotalBytes);
    public string StartedText => Record.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
    public string FinishedText => Record.FinishedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)
                                  ?? LocalizationService.Instance.Get("common.dash");
    public string Checksum => Record.Checksum ?? string.Empty;
    public int SkippedCount => Record.Skipped.Count;
    public int WarningCount => Record.Warnings.Count;

    [ObservableProperty] private string _healthText = string.Empty;

    /// <summary>双击整行时打开源位置。</summary>
    public string PrimaryPath => Record.SourcePath;

    /// <summary>语言切换后刷新依赖本地化/文化的计算属性。</summary>
    public void RefreshComputed()
    {
        OnPropertyChanged(nameof(FinishedText));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(StartedText));
    }

    public override string ToString() => $"{Id} {SourcePath}";
}

/// <summary>
/// 记录页 ViewModel（需求「迁移记录页」）。
///
/// 数据源是 migrations.json（**仅供展示**）。回滚动作一律以台账为输入，
/// 由 <see cref="DirPorter.Core.Migration.RestoreService"/> 负责，UI 不自行拼装任何磁盘操作。
/// </summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly IUserInteraction _ui;
    private bool _loaded;

    public HistoryViewModel(AppHost host)
    {
        _host = host;
        _ui = host.Interaction;
        // 空状态与表格互斥显示（R11）：集合变化即通知。
        Records.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRecords));
        // R12 启动性能：改为**首次进入记录页才加载**（构造期不再同步读台账 + 逐条重解析点探测）。
    }

    /// <summary>由外壳在导航到记录页时调用（幂等）。</summary>
    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        Reload();
    }

    public ObservableCollection<HistoryRow> Records { get; } = new();

    /// <summary>是否有迁移记录（驱动空状态提示）。</summary>
    public bool HasRecords => Records.Count > 0;

    [ObservableProperty] private HistoryRow? _selected;

    [RelayCommand]
    public void Reload()
    {
        _loaded = true;
        Records.Clear();

        foreach (var record in _host.Index.LoadAll().OrderByDescending(static r => r.StartedUtc))
        {
            var row = new HistoryRow(record);
            row.HealthText = DescribeHealth(record.SourcePath);
            Records.Add(row);
        }

        if (ReferenceEquals(Selected, null) && Records.Count > 0) Selected = Records[0];
    }

    private string DescribeHealth(string sourcePath)
    {
        try
        {
            var info = _host.Links.Inspect(sourcePath);

            if (!info.PathExists) return LocalizationService.Instance.Get("hist.health.missing");
            if (!info.IsKnownLink) return LocalizationService.Instance.Get("hist.health.notLink");

            return info.IsDangling
                ? LocalizationService.Instance.Get("hist.health.dangling")
                : LocalizationService.Instance.Get("hist.health.healthy");
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    [RelayCommand]
    private void CheckHealth()
    {
        foreach (var row in Records) row.HealthText = DescribeHealth(row.SourcePath);
    }

    [RelayCommand]
    private void OpenSource() => Open(Selected?.SourcePath);

    [RelayCommand]
    private void OpenTarget() => Open(Selected?.TargetPath);

    [RelayCommand]
    private void CopyPath()
    {
        if (Selected is null) return;
        _ui.CopyToClipboard(Selected.SourcePath);
    }

    [RelayCommand]
    private void OpenRowTarget(HistoryRow? row) => Open(row?.TargetPath);

    [RelayCommand]
    private void OpenRowSource(HistoryRow? row) => Open(row?.SourcePath);

    /// <summary>
    /// 重建链接：源路径已经空了（例如链接被手工删掉、或还原后又想再迁一次）时，
    /// 用记录里的「源 → 目标」一步把链接补回来 —— 这是「迁移 → 建链」动线在记录页的补偿入口。
    /// </summary>
    [RelayCommand]
    private void Relink(HistoryRow? row)
    {
        row ??= Selected;
        if (row is null) return;

        var loc = LocalizationService.Instance;
        var source = row.Record.SourcePath;
        var target = row.Record.TargetPath;

        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target)) return;

        if (!Directory.Exists(target))
        {
            _ui.Error($"目标位置不存在，无法建链：{target}");
            return;
        }

        var info = _host.Links.Inspect(source);

        if (info.IsKnownLink)
        {
            _ui.Info($"源路径已经是链接（指向 {info.TargetAbsolute}），无需重建。");
            return;
        }

        if (info.PathExists)
        {
            // 源路径上有真实目录时**绝不覆盖**：那可能正是用户的数据。
            _ui.Error($"源路径上已存在真实目录，拒绝覆盖：{source}{Environment.NewLine}"
                      + "请先把该目录移走或改名，再重建链接。");
            return;
        }

        if (!_ui.Confirm(loc.Get("hist.relinkConfirm"))) return;

        try
        {
            // 重建链接用「默认建链类型」（与迁移一致：默认目录联接，可在设置/迁移页改选）。
            var request = new LinkRequest(source, target, _host.Settings.DefaultLinkKindValue);
            var resolution = _host.Links.Resolve(request);

            if (!resolution.Capability.CanHandle)
            {
                _ui.Error(resolution.Capability.Reason
                          + (string.IsNullOrWhiteSpace(resolution.Capability.Suggestion)
                              ? string.Empty
                              : Environment.NewLine + resolution.Capability.Suggestion));
                return;
            }

            _host.Links.Create(request, LinkCreateOptions.Default);

            _ui.Info(string.Format(loc.Get("hist.relinkDone"), _host.Links.Inspect(source).Describe()));
            CheckHealth();
        }
        catch (LinkPrivilegeRequiredException)
        {
            _ui.Error(loc.Get("link.needPrivilege"));
        }
        catch (Exception ex)
        {
            _ui.Error(string.Format(loc.Get("hist.relinkFailed"), ex.Message));
        }
    }

    private void Open(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        _ui.OpenInExplorer(path);
    }

    [RelayCommand]
    private void Restore(HistoryRow? row)
    {
        row ??= Selected;
        if (row is null) return;

        if (!_ui.Confirm(LocalizationService.Instance.Get("hist.restoreConfirm"))) return;

        try
        {
            var ledgerDirectory = _host.Restore.LocateLedgerDirectory(row.BatchId) ?? row.Record.LedgerPath;
            if (string.IsNullOrWhiteSpace(ledgerDirectory))
            {
                _ui.Error("找不到该批次的台账目录，无法还原。");
                return;
            }

            var result = _host.Restore.Restore(row.BatchId, ledgerDirectory!, new[] { row.Record.EntryId });
            var text = result.Describe();

            _ui.Info(string.Format(LocalizationService.Instance.Get("hist.restoreDone"), text));
            Reload();
        }
        catch (Exception ex)
        {
            _ui.Error(ex.Message);
        }
    }

    // ------------------------------------------------------------ 导出

    [RelayCommand]
    private void ExportJson()
    {
        var path = AskSavePath("json");
        if (path is null) return;

        try
        {
            var json = JsonSerializer.Serialize(Records.Select(static r => r.Record).ToList(),
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json, new UTF8Encoding(true));
            _ui.Info(string.Format(LocalizationService.Instance.Get("hist.exported"), path));
        }
        catch (Exception ex)
        {
            _ui.Error(ex.Message);
        }
    }

    [RelayCommand]
    private void ExportCsv()
    {
        var path = AskSavePath("csv");
        if (path is null) return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("id,batchId,entryId,startedUtc,finishedUtc,sourcePath,targetPath,backupPath,status,linkType,fileCount,totalBytes,checksumMethod,checksum,skippedCount,warningCount,appVersion");

            foreach (var row in Records)
            {
                var record = row.Record;
                sb.AppendLine(string.Join(',',
                    Csv(record.Id), Csv(record.BatchId), Csv(record.EntryId),
                    Csv(record.StartedUtc.ToString("O", CultureInfo.InvariantCulture)),
                    Csv(record.FinishedUtc?.ToString("O", CultureInfo.InvariantCulture)),
                    Csv(record.SourcePath), Csv(record.TargetPath), Csv(record.BackupPath),
                    Csv(record.Status), Csv(record.LinkType),
                    record.FileCount.ToString(CultureInfo.InvariantCulture),
                    record.TotalBytes.ToString(CultureInfo.InvariantCulture),
                    Csv(record.ChecksumMethod), Csv(record.Checksum),
                    record.Skipped.Count.ToString(CultureInfo.InvariantCulture),
                    record.Warnings.Count.ToString(CultureInfo.InvariantCulture),
                    Csv(record.AppVersion)));
            }

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            _ui.Info(string.Format(LocalizationService.Instance.Get("hist.exported"), path));
        }
        catch (Exception ex)
        {
            _ui.Error(ex.Message);
        }
    }

    private string? AskSavePath(string extension)
    {
        var dialog = new SaveFileDialog
        {
            Title = LocalizationService.Instance.Get("common.export"),
            Filter = extension == "json" ? "JSON (*.json)|*.json" : "CSV (*.csv)|*.csv",
            FileName = $"dirporter-migrations-{DateTime.Now:yyyyMMdd-HHmmss}.{extension}",
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static string Csv(string? value)
    {
        value ??= string.Empty;
        return value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"'
            : value;
    }

    public void RefreshLocalization()
    {
        foreach (var row in Records)
        {
            row.HealthText = DescribeHealth(row.SourcePath);
            row.RefreshComputed();
        }
    }
}
