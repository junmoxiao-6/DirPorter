using DirPorter.Core.Backup;
using DirPorter.Core.Journal;
using DirPorter.Core.Ledger;
using Xunit;

namespace DirPorter.Tests;

/// <summary>
/// R4：备份保留策略（停车目录清理）。
/// 本组用例的重点不是"能删"，而是**只删该删的**：
/// 台账绝不清理、未到期绝不清理、非 .migbak 下的目录绝不碰。
/// </summary>
public class R4_RetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.FromHours(8));

    private static string CreateParkedBackup(TempWorkspace ws, string parentRelative, string name)
    {
        var parking = ws.CreateDirectory(Path.Combine(parentRelative, MigrationLedgerFactory.MigBakFolderName, name));
        ws.CreateFile(Path.Combine(parentRelative, MigrationLedgerFactory.MigBakFolderName, name, "payload.txt"), new string('y', 512));
        ws.CreateDirectory(Path.Combine(parentRelative, MigrationLedgerFactory.MigBakFolderName, name, "sub"));
        return parking;
    }

    private static string LedgerPath(TempWorkspace ws, string parentRelative)
        => ws.CreateDirectory(Path.Combine(parentRelative, MigrationLedgerFactory.MigBakFolderName, MigrationLedgerFactory.LedgerFolderName));

    private static BackupRetentionService CreateService(TempWorkspace ws)
        => new(new MigrationIndexStore(ws.PathOf("migrations.json")));

    private static string MigBakOf(TempWorkspace ws, string parentRelative)
        => ws.PathOf(Path.Combine(parentRelative, MigrationLedgerFactory.MigBakFolderName));

    [Fact]
    public void 到期清理_未到期的保留_台账永不清理()
    {
        using var ws = new TempWorkspace("r4-retain");
        ws.CreateDirectory("Work");

        // 19 天前（2026-10-01）→ 到期
        var expired = CreateParkedBackup(ws, "Work", "Expired_20261001_120000_aaaaaa");
        // 1 天前（2026-10-19）→ 保留
        var fresh = CreateParkedBackup(ws, "Work", "Fresh_20261019_120000_bbbbbb");
        var ledger = LedgerPath(ws, "Work");
        ws.CreateFile(@"Work\.migbak\.ledger\mig-x.jsonl", "{\"recordType\":\"batch\"}");

        var result = CreateService(ws).Sweep(new BackupSweepOptions
        {
            RetentionDays = 7,
            Now = Now,
            ScanRoots = new[] { ws.PathOf("Work") },
        });

        Assert.Equal(2, result.Examined);
        Assert.Equal(1, result.Deleted);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.Failed);

        Assert.False(Directory.Exists(expired), "到期的停车目录应被删除");
        Assert.True(Directory.Exists(fresh), "未到期的停车目录必须保留");
        Assert.True(Directory.Exists(ledger), "台账目录绝对不能被清理");
        Assert.True(File.Exists(ws.PathOf(@"Work\.migbak\.ledger\mig-x.jsonl")));
    }

    [Fact]
    public void 演练模式_只报告不删除()
    {
        using var ws = new TempWorkspace("r4-retain-dry");
        ws.CreateDirectory("Work");
        var expired = CreateParkedBackup(ws, "Work", "Expired_20261001_120000_aaaaaa");

        var result = CreateService(ws).Sweep(new BackupSweepOptions
        {
            RetentionDays = 7,
            DryRun = true,
            Now = Now,
            ScanRoots = new[] { ws.PathOf("Work") },
        });

        Assert.True(result.DryRun);
        Assert.Equal(1, result.Deleted);
        Assert.True(Directory.Exists(expired), "演练模式绝不能真的删除");
        Assert.Contains(result.Items, i => i.Action == "would-delete");
    }

    [Fact]
    public void 清理过程遇到链接只删链接本体_不穿透目标数据()
    {
        using var ws = new TempWorkspace("r4-retain-link");

        var outside = ws.CreateDirectory("Outside");
        var sentinel = ws.CreateFile(@"Outside\sentinel.txt", "must-survive");

        ws.CreateDirectory("Work");
        var parked = CreateParkedBackup(ws, "Work", "Expired_20261001_120000_aaaaaa");

        // 停车目录内部含嵌套链接（源目录本身含链接是合法场景）
        new DirPorter.Core.Links.JunctionLinkProvider(new DirPorter.Core.FileSystem.Win32VolumeResolver())
            .Create(new DirPorter.Core.Links.LinkRequest(
                Path.Combine(parked, "NestedLink"), outside),
                DirPorter.Core.Links.LinkCreateOptions.Default);

        var result = CreateService(ws).Sweep(new BackupSweepOptions
        {
            RetentionDays = 7,
            Now = Now,
            ScanRoots = new[] { ws.PathOf("Work") },
        });

        Assert.Equal(1, result.Deleted);
        Assert.False(Directory.Exists(parked));

        // ★ 关键：被链接指向的真实数据必须完好无损
        Assert.True(File.Exists(sentinel));
        Assert.Equal("must-survive", File.ReadAllText(sentinel));
    }

    [Fact]
    public void 结构闸门_拒绝删除非_migbak_下的目录()
    {
        Assert.Null(BackupRetentionService.Refuse(@"C:\Users\Alice\Documents\.migbak\Project_20261001_120000_aaaaaa"));

        Assert.NotNull(BackupRetentionService.Refuse(@"C:\Users\Alice\Documents\Important"));
        Assert.NotNull(BackupRetentionService.Refuse(@"C:\Users\Alice\Documents\.migbak"));
        Assert.NotNull(BackupRetentionService.Refuse(@"C:\Users\Alice\Documents\.migbak\.ledger"));
    }

    [Fact]
    public void 保留天数负数_表示永不清理()
    {
        using var ws = new TempWorkspace("r4-retain-never");
        ws.CreateDirectory("Work");
        var parked = CreateParkedBackup(ws, "Work", "Expired_20261001_120000_aaaaaa");

        var result = CreateService(ws).Sweep(new BackupSweepOptions
        {
            RetentionDays = -1,
            Now = Now,
            ScanRoots = new[] { ws.PathOf("Work") },
        });

        Assert.Equal(0, result.Examined);
        Assert.Equal(0, result.Deleted);
        Assert.True(Directory.Exists(parked));
    }

    [Fact]
    public void 从迁移记录发现候选并清理后置空备份路径()
    {
        using var ws = new TempWorkspace("r4-retain-record");
        ws.CreateDirectory("Work");

        var parkingRelative = Path.Combine("Work", MigrationLedgerFactory.MigBakFolderName, "Project_20261001_120000_aaaaaa");
        var parking = CreateParkedBackup(ws, "Work", "Project_20261001_120000_aaaaaa");

        var store = new MigrationIndexStore(ws.PathOf("migrations.json"));
        store.Upsert(new MigrationRecord
        {
            Id = "mig-20261001-120000-aaaaaa-e-01",
            BatchId = "mig-20261001-120000-aaaaaa",
            EntryId = "e-01",
            StartedUtc = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.FromHours(8)),
            FinishedUtc = new DateTimeOffset(2026, 10, 1, 12, 1, 0, TimeSpan.FromHours(8)),
            SourcePath = ws.PathOf(@"Work\Project"),
            TargetPath = ws.PathOf(@"Archive\Project"),
            BackupPath = parking,
            Status = "done",
        });

        var result = new BackupRetentionService(store).Sweep(new BackupSweepOptions
        {
            RetentionDays = 7,
            Now = Now,
        });

        Assert.Equal(1, result.Examined);
        Assert.Equal(1, result.Deleted);
        Assert.Contains(result.Items, i => i.ParkingPath.EndsWith(parkingRelative, StringComparison.OrdinalIgnoreCase));

        // 清理后备份路径应被置空，避免下次扫描重复命中一个已不存在的目录
        var record = Assert.Single(store.LoadAll());
        Assert.Null(record.BackupPath);
    }

    [Fact]
    public void 停车目录名可解析出创建时间()
    {
        var (createdAt, estimated) = BackupRetentionService.ResolveCreatedAt(
            @"C:\x\.migbak\ProjectA_20261001_143052_a1b2c3", "ProjectA_20261001_143052_a1b2c3");

        Assert.False(estimated);
        Assert.Equal(new DateTime(2026, 10, 1, 14, 30, 52), createdAt.DateTime);
    }

    [Fact]
    public void 名称无法解析时间时退化到目录时间并标记为估算()
    {
        using var ws = new TempWorkspace("r4-retain-estimate");
        var directory = ws.CreateDirectory("NoTimestampInName");

        var (_, estimated) = BackupRetentionService.ResolveCreatedAt(directory, "NoTimestampInName");
        Assert.True(estimated);
    }
}
