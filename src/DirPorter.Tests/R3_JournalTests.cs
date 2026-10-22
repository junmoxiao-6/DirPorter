using DirPorter.Core.FileSystem;
using DirPorter.Core.Journal;
using DirPorter.Core.Links;
using DirPorter.Core.Migration;
using Xunit;

namespace DirPorter.Tests;

/// <summary>Journal 与中间态恢复。</summary>
public class R3_JournalTests
{
    private static JsonJournalStore CreateStore(TempWorkspace ws) => new(ws.PathOf("runs"));

    private static MigrationRunRecord BuildRecord(string taskId, string source, string target, string? parking = null, MigrationState state = MigrationState.Copying)
        => new()
        {
            TaskId = taskId,
            BatchId = "mig-20261008-143052-a1b2c3",
            EntryId = "e-01",
            SourcePath = source,
            TargetPath = target,
            ParkingPath = parking,
            State = state,
            StartedUtc = DateTimeOffset.UtcNow,
        };

    [Fact]
    public void 保存与读取_往返一致()
    {
        using var ws = new TempWorkspace("journal-roundtrip");
        var store = CreateStore(ws);
        var record = BuildRecord("t-1", @"C:\src", @"D:\dst", @"C:\.migbak\src_20261008_143052_a1b2c3");

        store.Save(record);
        var loaded = store.Load("t-1");

        Assert.NotNull(loaded);
        Assert.Equal(MigrationState.Copying, loaded!.State);
        Assert.Equal(@"C:\.migbak\src_20261008_143052_a1b2c3", loaded.ParkingPath);
    }

    [Fact]
    public void 原子替换_不留临时文件()
    {
        using var ws = new TempWorkspace("journal-atomic");
        var store = CreateStore(ws);
        store.Save(BuildRecord("t-1", @"C:\src", @"D:\dst"));
        store.Save(BuildRecord("t-1", @"C:\src", @"D:\dst") with { State = MigrationState.Done });

        var files = Directory.GetFiles(ws.PathOf("runs"));
        Assert.Single(files);
        Assert.EndsWith(".json", files[0]);
        Assert.Equal(MigrationState.Done, store.Load("t-1")!.State);
    }

    [Fact]
    public void 多余的任务_全部能列出()
    {
        using var ws = new TempWorkspace("journal-all");
        var store = CreateStore(ws);
        store.Save(BuildRecord("t-1", @"C:\a", @"D:\a"));
        store.Save(BuildRecord("t-2", @"C:\b", @"D:\b"));

        Assert.Equal(2, store.LoadAll().Count);
    }

    [Fact]
    public void 删除_移除文件()
    {
        using var ws = new TempWorkspace("journal-delete");
        var store = CreateStore(ws);
        store.Save(BuildRecord("t-1", @"C:\a", @"D:\a"));
        store.Delete("t-1");

        Assert.Null(store.Load("t-1"));
        Assert.Empty(store.LoadAll());
    }

    // ==================== 中间态恢复判定 ====================

    [Fact]
    public void 源不存在且停车目录存在_建议改回原名()
    {
        using var ws = new TempWorkspace("recover-parked");
        var parking = ws.PathOf("parking");
        Directory.CreateDirectory(parking);

        var suggestion = JournalRecovery.AnalyzeOne(
            BuildRecord("t-1", ws.PathOf("gone"), ws.PathOf("target"), parking, MigrationState.Linking));

        Assert.Equal(RecoveryAction.RenameParkingBack, suggestion.Action);
        Assert.False(suggestion.IsDestructive);
        Assert.Contains("毫秒级", suggestion.Detail);
    }

    [Fact]
    public void 源是链接且停车目录存在_建议确认后删停车目录()
    {
        using var ws = new TempWorkspace("recover-linked");
        var data = ws.CreateDirectory("Data");
        var link = ws.PathOf("Migrated");
        new JunctionLinkProvider(new Win32VolumeResolver())
            .Create(new LinkRequest(link, data), LinkCreateOptions.Default);

        var parking = ws.PathOf("parking");
        Directory.CreateDirectory(parking);

        var suggestion = JournalRecovery.AnalyzeOne(
            BuildRecord("t-1", link, ws.PathOf("target"), parking, MigrationState.LinkVerify));

        Assert.Equal(RecoveryAction.FinalizeByDeletingParking, suggestion.Action);
        Assert.True(suggestion.IsDestructive);
        Assert.Contains("链接已生效", suggestion.Detail);
    }

    [Fact]
    public void 源完好且目标有残留_建议清理目标残留()
    {
        using var ws = new TempWorkspace("recover-residue");
        var source = ws.CreateDirectory("Source");
        ws.CreateFile(@"Source\a.txt", "x");
        var target = ws.CreateDirectory("Target");
        ws.CreateFile(@"Target\half.txt", "half");

        var suggestion = JournalRecovery.AnalyzeOne(
            BuildRecord("t-1", source, target, ws.PathOf("parking"), MigrationState.Copying));

        Assert.Equal(RecoveryAction.CleanTargetResidue, suggestion.Action);
        Assert.Contains("一个字节都没少", suggestion.Detail);
    }

    [Fact]
    public void 源完好且目标为空_无需处理()
    {
        using var ws = new TempWorkspace("recover-clean");
        var source = ws.CreateDirectory("Source");
        var target = ws.CreateDirectory("Target");

        var suggestion = JournalRecovery.AnalyzeOne(
            BuildRecord("t-1", source, target, ws.PathOf("parking"), MigrationState.TargetPrepare));

        Assert.Equal(RecoveryAction.None, suggestion.Action);
    }

    [Fact]
    public void 源是链接且无停车目录_建议补写记录()
    {
        using var ws = new TempWorkspace("recover-finalize");
        var data = ws.CreateDirectory("Data");
        var link = ws.PathOf("Migrated");
        new JunctionLinkProvider(new Win32VolumeResolver())
            .Create(new LinkRequest(link, data), LinkCreateOptions.Default);

        var suggestion = JournalRecovery.AnalyzeOne(
            BuildRecord("t-1", link, ws.PathOf("target"), ws.PathOf("no-parking"), MigrationState.LedgerCommit));

        Assert.Equal(RecoveryAction.FinalizeRecord, suggestion.Action);
        Assert.False(suggestion.IsDestructive);
    }

    [Fact]
    public void 源与停车目录都不存在_需要人工检查()
    {
        using var ws = new TempWorkspace("recover-manual");

        var suggestion = JournalRecovery.AnalyzeOne(
            BuildRecord("t-1", ws.PathOf("gone"), ws.PathOf("target"), ws.PathOf("also-gone"), MigrationState.Parking));

        Assert.Equal(RecoveryAction.ManualInspection, suggestion.Action);
        Assert.NotEmpty(suggestion.Evidence);
    }

    [Fact]
    public void 扫描_终态且磁盘一致才算健康_中间态一律上报()
    {
        using var ws = new TempWorkspace("recover-scan");
        var store = CreateStore(ws);
        var source = ws.CreateDirectory("Source");
        var target = ws.CreateDirectory("Target");

        // 终态 + 源是真实目录 → 健康
        store.Save(BuildRecord("t-ok", source, target, ws.PathOf("p"), MigrationState.RolledBack));

        // 中间态（哪怕磁盘无风险）→ 必须上报，让用户知道"上次没跑完"
        store.Save(BuildRecord("t-stale", source, target, ws.PathOf("p"), MigrationState.TargetPrepare));

        // 中间态 + 数据位置不明 → 需要人工检查
        store.Save(BuildRecord("t-bad", ws.PathOf("gone"), ws.PathOf("t2"), null, MigrationState.Parking));

        var analysis = JournalRecovery.Scan(store);

        Assert.Single(analysis.HealthyRecords);
        Assert.Equal(2, analysis.Suggestions.Count);
        Assert.Contains(analysis.Suggestions, s => s.Action == RecoveryAction.None && s.TaskId == "t-stale");
        Assert.Contains(analysis.Suggestions, s => s.Action == RecoveryAction.ManualInspection && s.TaskId == "t-bad");
        Assert.Equal(1, analysis.NeedsAttentionCount);   // Action=None 的陈旧中间态不计入"待处理"
    }

    /// <summary>
    /// ★ 回归：迁移**成功**后停车目录按保留策略继续存在是设计行为，
    /// 绝不能被 recover 报成"待处理"（否则每次迁移完都会出现一条误导性待办）。
    /// 该 bug 由中断/恢复演练脚本捕获（tests/drill-interrupt-recovery.ps1）。
    /// </summary>
    [Fact]
    public void 迁移成功且保留停车目录_不应被报为待处理()
    {
        using var ws = new TempWorkspace("recover-done");
        var data = ws.CreateDirectory("Target");
        var link = ws.PathOf("ProjectA");
        new JunctionLinkProvider(new Win32VolumeResolver())
            .Create(new LinkRequest(link, data), LinkCreateOptions.Default);

        var parking = ws.CreateDirectory("parking");
        ws.CreateFile(@"parking\already.txt", "backup");

        var suggestion = JournalRecovery.AnalyzeOne(
            BuildRecord("t-done", link, data, parking, MigrationState.Done));

        Assert.Equal(RecoveryAction.None, suggestion.Action);
        Assert.Contains("按备份保留策略继续保留", suggestion.Detail);
    }

    [Fact]
    public void 记录显示已完成但链接不可用_需要人工检查()
    {
        using var ws = new TempWorkspace("recover-done-broken");
        var data = ws.CreateDirectory("Target");
        var link = ws.PathOf("ProjectA");
        new JunctionLinkProvider(new Win32VolumeResolver())
            .Create(new LinkRequest(link, data), LinkCreateOptions.Default);
        Directory.Move(data, ws.PathOf("Target-Moved"));   // 链接悬空

        var suggestion = JournalRecovery.AnalyzeOne(
            BuildRecord("t-done-bad", link, data, null, MigrationState.Done));

        Assert.Equal(RecoveryAction.ManualInspection, suggestion.Action);
        Assert.Contains("链接不可用", suggestion.Summary);
    }

    [Fact]
    public void 中间态判定_覆盖所有需要恢复的状态()
    {
        Assert.True(MigrationState.Copying.IsIntermediate());
        Assert.True(MigrationState.Parking.IsIntermediate());
        Assert.True(MigrationState.RollingBack.IsIntermediate());
        Assert.False(MigrationState.Done.IsIntermediate());
        Assert.False(MigrationState.RolledBack.IsIntermediate());
        Assert.True(MigrationState.Done.IsTerminal());
        Assert.True(MigrationState.TargetPrepare.MayHaveWrittenDisk());
        Assert.False(MigrationState.Precheck.MayHaveWrittenDisk());
    }
}
