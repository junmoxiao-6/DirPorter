using DirPorter.Core.Config;
using DirPorter.Core.Copy;
using DirPorter.Core.FileSystem;
using DirPorter.Core.Journal;
using DirPorter.Core.Ledger;
using DirPorter.Core.Links;
using DirPorter.Core.Migration;
using DirPorter.Core.Precheck;
using DirPorter.Core.RestartManager;
using Xunit;

namespace DirPorter.Tests;

/// <summary>测试用：不查询占用（保持用例与环境无关）。</summary>
internal sealed class NoLockService : IFileLockService
{
    public OccupancyReport FindLockingProcesses(IEnumerable<string> paths) => OccupancyReport.Empty;

    public LockAttribution FindLockedResources(IEnumerable<string> paths, int batchSize = 64, int maxResources = 5000)
        => new(Array.Empty<LockedBatch>(), 0, batchSize, null);

    public KillGuardResult CanKill(LockingProcess process)
        => KillGuardResult.Refuse(KillRefusal.SelfOrAncestor, "测试环境不结束进程。");

    public bool TryKill(LockingProcess process, out string error) { error = "测试环境不结束进程。"; return false; }
}

/// <summary>测试用：复制成功、但故意改动目标侧一个文件的 mtime，用于触发 VERIFYING 不一致。</summary>
internal sealed class CorruptingCopier : IFileCopier
{
    private readonly IFileCopier _inner = new RobocopyCopier();

    public string Name => "robocopy+corrupt";

    public CopyCapabilities Capabilities => _inner.Capabilities;

    public async Task<CopyResult> CopyAsync(CopyRequest request, IProgress<CopyProgress>? progress, CancellationToken cancellationToken)
    {
        var result = await _inner.CopyAsync(request, progress, cancellationToken);

        var first = Directory.EnumerateFiles(request.TargetPath, "*", SearchOption.AllDirectories).FirstOrDefault();
        if (first is not null) File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddHours(3));

        return result;
    }
}

/// <summary>测试用：只复制两个文件就抛取消，模拟"复制中途取消"。</summary>
internal sealed class PartialThenCancelCopier : IFileCopier
{
    public string Name => "partial+cancel";
    public CopyCapabilities Capabilities { get; } = new(false, false, false, false, false, false, true, false, "无");

    public Task<CopyResult> CopyAsync(CopyRequest request, IProgress<CopyProgress>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(request.TargetPath);

        var copied = 0;
        foreach (var file in Directory.EnumerateFiles(request.SourcePath, "*", SearchOption.AllDirectories).Take(2))
        {
            var relative = Path.GetRelativePath(request.SourcePath, file);
            var destination = Path.Combine(request.TargetPath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
            copied++;
        }

        progress?.Report(new CopyProgress(10, copied, 1000, 100, 0, null, null));
        throw new OperationCanceledException("测试注入：复制中途取消。");
    }
}

/// <summary>
/// 状态机与回滚（R3 核心）。每个用例都在断言"失败后磁盘是否回到与开始前完全一致"。
/// </summary>
public class R3_EngineTests
{
    private sealed class Harness : IDisposable
    {
        public TempWorkspace Source { get; } = new("eng-src");
        public TempWorkspace Target { get; } = new("eng-tgt");
        public TempWorkspace State { get; } = new("eng-state");

        public JsonJournalStore Journal { get; }
        public MigrationIndexStore Index { get; }

        public Harness()
        {
            Journal = new JsonJournalStore(State.PathOf("runs"));
            Index = new MigrationIndexStore(State.PathOf("index.json"));
        }

        /// <summary>
        /// 迁移源放在工作区根下的子目录（而不是工作区根本身）：
        /// 这样 `.migbak` / `.ledger` 会落在工作区内、随用例清理，
        /// 不会污染 %TEMP%。
        /// </summary>
        public string SourceDir => Path.Combine(Source.Root, "ProjectA");

        public string TargetDir => Path.Combine(Target.Root, "Target");

        public string LedgerDirectory => MigrationLedgerFactory.ResolveLedgerDirectory(SourceDir);
        public string ParkingDirectory => MigrationLedgerFactory.ResolveParkingDirectory(SourceDir);

        public MigrationEngine CreateEngine(IFileCopier? copier = null)
            => new(
                precheck: new PrecheckService(new Win32VolumeResolver(), new BlacklistPolicy(), new NoWarningPolicy(), new TreeProbe()),
                copier: copier ?? new RobocopyCopier(),
                lockService: new NoLockService(),
                journal: Journal,
                index: Index);

        public MigrationTaskOptions Options(MigrationTaskOptions? overrides = null)
            // 本组用例覆盖的是"复制 → 停车 → 备份清理"这条路径，因此**显式关闭**同卷快速改名；
            // 同卷快路径由 R9_SameVolumeMoveTests 专门覆盖。
            => (overrides ?? new MigrationTaskOptions { SameVolumeMove = false }) with { SameVolumeMove = false };

        public void Dispose()
        {
            Source.Dispose();
            Target.Dispose();
            State.Dispose();
        }
    }

    private static void SeedSource(Harness harness, int files = 3)
    {
        Directory.CreateDirectory(Path.Combine(harness.SourceDir, "sub"));
        for (var i = 0; i < files; i++)
            File.WriteAllText(Path.Combine(harness.SourceDir, i % 2 == 0 ? $"f{i}.txt" : Path.Combine("sub", $"f{i}.txt")), new string('x', 100 + i));
    }

    private static MigrationBatchResult Run(Harness harness, IFileCopier? copier = null, MigrationTaskOptions? options = null)
        => harness.CreateEngine(copier)
            .RunBatchAsync(MigrationPlan.Single(harness.SourceDir, harness.TargetDir, LinkKind.Junction), harness.Options(options), null, CancellationToken.None)
            .GetAwaiter().GetResult();

    private static void AssertSourceIntact(Harness harness, int files = 3)
    {
        Assert.True(Directory.Exists(harness.SourceDir), "源目录必须仍然存在");
        Assert.False(ReparsePointOps.Inspect(PathNormalizer.NormalizeAbsolute(harness.SourceDir)).IsReparsePoint, "源目录不该是链接");
        Assert.Equal(files, Directory.EnumerateFiles(harness.SourceDir, "*", SearchOption.AllDirectories).Count());
    }

    private static void AssertNoResidue(Harness harness)
    {
        Assert.False(Directory.Exists(harness.TargetDir), "目标侧不应残留半成品");

        // 注意：`.migbak\.ledger` 是台账，属于正常产物；要断言的是**没有残留被停车的源目录**。
        if (!Directory.Exists(harness.ParkingDirectory)) return;

        var leftovers = Directory.EnumerateDirectories(harness.ParkingDirectory)
            .Where(d => !string.Equals(Path.GetFileName(d), ".ledger", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(leftovers);
    }

    /// <summary>列出被停车的源目录（排除 `.ledger`）。</summary>
    private static List<string> ParkedDirectories(Harness harness)
        => Directory.Exists(harness.ParkingDirectory)
            ? Directory.EnumerateDirectories(harness.ParkingDirectory)
                .Where(d => !string.Equals(Path.GetFileName(d), ".ledger", StringComparison.OrdinalIgnoreCase))
                .ToList()
            : new List<string>();

    // ==================== 正常路径 ====================

    [Fact]
    public void 成功迁移_源变成链接_目标有数据_台账与记录齐备()
    {
        using var harness = new Harness();
        SeedSource(harness);

        var result = Run(harness);

        Assert.True(result.Success, string.Join("；", result.Entries.Select(e => e.Message)));

        // 源路径变成 Junction，且指向目标
        var info = LinkService.CreateDefault().Inspect(harness.SourceDir);
        Assert.Equal(LinkKind.Junction, info.Kind);
        Assert.False(info.IsDangling);
        Assert.Equal(harness.TargetDir, info.TargetAbsolute!, StringComparer.OrdinalIgnoreCase);

        // 通过原路径读写落到新卷
        File.WriteAllText(Path.Combine(harness.SourceDir, "through-link.txt"), "ok");
        Assert.True(File.Exists(Path.Combine(harness.TargetDir, "through-link.txt")));

        // Journal 收尾
        var record = Assert.Single(harness.Journal.LoadAll());
        Assert.Equal(MigrationState.Done, record.State);

        // 台账：权威记录 + 折叠快照 + Markdown
        var ledgerFile = Directory.EnumerateFiles(harness.LedgerDirectory, "*.jsonl").Single();
        var batch = MigrationLedgerFactory.LoadFile(ledgerFile);
        var entry = Assert.Single(batch.Entries);
        Assert.Equal("e-01", entry.EntryId);
        Assert.Equal("Junction", entry.LinkType);
        Assert.Equal(3, entry.FileCount);
        Assert.Equal(4, entry.Operations.Count);
        Assert.False(string.IsNullOrWhiteSpace(entry.Checksum));
        Assert.Equal("xxHash3", entry.ChecksumMethod);
        Assert.True(File.Exists(Path.Combine(harness.LedgerDirectory, batch.Header.BatchId + ".md")));

        // 迁移记录索引
        var indexRecord = Assert.Single(harness.Index.LoadAll());
        Assert.Equal("done", indexRecord.Status);
    }

    [Fact]
    public void 干跑_零磁盘写入()
    {
        using var harness = new Harness();
        SeedSource(harness);
        var before = Directory.EnumerateFileSystemEntries(harness.SourceDir, "*", SearchOption.AllDirectories).OrderBy(x => x).ToList();

        var result = Run(harness, options: new MigrationTaskOptions { DryRun = true });

        Assert.True(result.DryRun);
        Assert.Empty(result.Entries);
        Assert.Equal(before, Directory.EnumerateFileSystemEntries(harness.SourceDir, "*", SearchOption.AllDirectories).OrderBy(x => x).ToList());
        Assert.False(Directory.Exists(harness.TargetDir));
        Assert.False(Directory.Exists(harness.ParkingDirectory));
    }

    [Fact]
    public void 备份策略_默认保留停车目录_立即删除策略则不保留()
    {
        using var h1 = new Harness();
        SeedSource(h1);
        Run(h1, options: new MigrationTaskOptions { BackupPolicy = BackupCleanupPolicy.KeepDays });
        Assert.Single(ParkedDirectories(h1));   // 停车目录保留

        using var h2 = new Harness();
        SeedSource(h2);
        Run(h2, options: new MigrationTaskOptions { BackupPolicy = BackupCleanupPolicy.Immediate });
        Assert.Empty(ParkedDirectories(h2));    // 立即删除策略下不保留
    }

    // ==================== 回滚路径（逐步骤注入失败）====================

    [Theory]
    [InlineData("TargetPrepare")]
    [InlineData("Copying")]
    [InlineData("Parking")]
    public void 任一步失败_源目录一个字节都不少_且无残留(string failBefore)
    {
        using var harness = new Harness();
        SeedSource(harness);

        var result = Run(harness, options: new MigrationTaskOptions { FailBeforeState = failBefore });

        Assert.False(result.Success);
        var entry = Assert.Single(result.Entries);
        Assert.Equal(MigrationState.RolledBack, entry.FinalState);
        AssertSourceIntact(harness);
        AssertNoResidue(harness);
        Assert.Empty(entry.RollbackFailures);

        // 在 TargetPrepare 之前失败时，尚未登记任何回滚动作 —— 这是正确行为（无损可回滚）。
        // Copying / Parking 之前失败则必须已有回滚动作在执行。
        if (!string.Equals(failBefore, "TargetPrepare", StringComparison.Ordinal))
            Assert.NotEmpty(entry.RollbackSteps);
    }

    [Fact]
    public void 校验不一致_判失败并回滚_源数据零丢失()
    {
        using var harness = new Harness();
        SeedSource(harness);

        var result = Run(harness, copier: new CorruptingCopier());

        var entry = Assert.Single(result.Entries);
        Assert.False(entry.Success);
        Assert.Equal(MigrationState.RolledBack, entry.FinalState);
        Assert.Contains("校验", entry.Error, StringComparison.OrdinalIgnoreCase);
        AssertSourceIntact(harness);
        AssertNoResidue(harness);
    }

    [Fact]
    public void 建链失败_回滚后源目录完好_目标被清空()
    {
        using var harness = new Harness();
        SeedSource(harness);

        // 目录符号链接在未提权且未开开发者模式的环境下会失败（本机即是），这是真实的失败路径
        if (NativeTestHelpers.RealSymlinkCreationAvailable)
        {
            // 已提权环境：该场景不成立，跳过（用例价值在另一台机器上体现）
            Assert.True(true);
            return;
        }

        var result = harness.CreateEngine()
            .RunBatchAsync(
                MigrationPlan.Single(harness.SourceDir, harness.TargetDir, LinkKind.DirectorySymlink),
                new MigrationTaskOptions { SameVolumeMove = false }, null, CancellationToken.None)
            .GetAwaiter().GetResult();

        var entry = Assert.Single(result.Entries);
        Assert.False(entry.Success);
        Assert.Equal(MigrationState.RolledBack, entry.FinalState);
        AssertSourceIntact(harness);
        AssertNoResidue(harness);

        var ledgerFile = Directory.EnumerateFiles(harness.LedgerDirectory, "*.jsonl").Single();
        var batch = MigrationLedgerFactory.LoadFile(ledgerFile);
        Assert.Equal("rolled_back", Assert.Single(batch.Entries).Result);
    }

    [Fact]
    public void 复制中途取消_走完整回滚()
    {
        using var harness = new Harness();
        SeedSource(harness);

        var result = Run(harness, copier: new PartialThenCancelCopier());

        var entry = Assert.Single(result.Entries);
        Assert.False(entry.Success);
        Assert.Equal(MigrationState.RolledBack, entry.FinalState);
        AssertSourceIntact(harness);
        AssertNoResidue(harness);
    }

    [Fact]
    public void 回滚动作按逆序执行_先删链接再改名()
    {
        using var harness = new Harness();
        SeedSource(harness);

        var rollback = new RollbackPlan();
        var order = new List<string>();

        rollback.Register("A", "第一步", _ => { order.Add("A"); return Task.CompletedTask; });
        rollback.Register("B", "第二步", _ => { order.Add("B"); return Task.CompletedTask; });
        rollback.Register("C", "第三步", _ => { order.Add("C"); return Task.CompletedTask; });

        var outcome = rollback.ExecuteAsync().GetAwaiter().GetResult();

        Assert.True(outcome.Success);
        Assert.Equal(new[] { "C", "B", "A" }, order);
    }

    [Fact]
    public void 回滚失败_被如实记录且不掩盖其它回滚()
    {
        var rollback = new RollbackPlan();
        var executed = new List<string>();

        rollback.Register("A", "会成功", _ => { executed.Add("A"); return Task.CompletedTask; });
        rollback.Register("B", "会失败", _ => throw new IOException("故意失败"));
        rollback.Register("C", "先执行", _ => { executed.Add("C"); return Task.CompletedTask; });

        var outcome = rollback.ExecuteAsync().GetAwaiter().GetResult();

        Assert.False(outcome.Success);
        Assert.Single(outcome.Failures);
        Assert.Equal(new[] { "C", "A" }, executed);
    }

    // ==================== 还原（以台账为输入）====================

    [Fact]
    public void 还原_删除链接并把停车目录改回原名()
    {
        using var harness = new Harness();
        SeedSource(harness);
        Run(harness);

        var ledgerFile = Directory.EnumerateFiles(harness.LedgerDirectory, "*.jsonl").Single();
        var batchId = MigrationLedgerFactory.LoadFile(ledgerFile).Header.BatchId;

        var restore = new RestoreService(journal: harness.Journal, index: harness.Index)
            .Restore(batchId, harness.LedgerDirectory);

        Assert.True(restore.Success, string.Join("；", restore.Entries.SelectMany(e => e.Failures)));

        // 还原后：源是真实目录（不是链接），数据完好；目标侧保留
        var info = ReparsePointOps.Inspect(PathNormalizer.NormalizeAbsolute(harness.SourceDir));
        Assert.True(info.PathExists);
        Assert.False(info.IsReparsePoint);
        Assert.Equal(3, Directory.EnumerateFiles(harness.SourceDir, "*", SearchOption.AllDirectories).Count());
        Assert.True(Directory.Exists(harness.TargetDir));

        // 台账追加了 restore 记录，原始 entry 未被覆盖
        var batch = MigrationLedgerFactory.LoadFile(ledgerFile);
        Assert.Single(batch.Restores);
        Assert.Equal("done", Assert.Single(batch.Entries).Result);
    }

    [Fact]
    public void 还原_拒绝处理非链接路径()
    {
        using var harness = new Harness();
        SeedSource(harness);
        Run(harness);

        var ledgerFile = Directory.EnumerateFiles(harness.LedgerDirectory, "*.jsonl").Single();
        var batchId = MigrationLedgerFactory.LoadFile(ledgerFile).Header.BatchId;

        // 先手工还原一次，再把源路径变成一个"真实目录"（模拟用户自己重建了目录）
        var service = new RestoreService(journal: harness.Journal, index: harness.Index);
        service.Restore(batchId, harness.LedgerDirectory);

        var again = service.Restore(batchId, harness.LedgerDirectory);

        var entry = Assert.Single(again.Entries);
        Assert.True(entry.Skipped);
        Assert.Contains("不是链接", entry.Message);
        AssertSourceIntact(harness);
    }
}
