using System.Text.Json;
using DirPorter.Core.Ledger;
using Xunit;

namespace DirPorter.Tests;

/// <summary>迁移台账：格式、字段、保留与归档（需求约束 1–4）。</summary>
public class R3_LedgerTests
{
    private static LedgerEntry BuildEntry(string batchId, string entryId, string result = "done")
        => new()
        {
            BatchId = batchId,
            EntryId = entryId,
            SourcePath = @"C:\Users\Alice\Documents\ProjectA",
            ParkingPath = @"C:\Users\Alice\Documents\.migbak\ProjectA_20261008_143052_a1b2c3",
            TargetPath = @"D:\Archive\Users\Alice\Documents\ProjectA",
            LinkPath = @"C:\Users\Alice\Documents\ProjectA",
            LinkType = "Junction",
            VolumeSource = "C:",
            VolumeTarget = "D:",
            Operations = new List<LedgerOperation>
            {
                new() { Step = "PARK", At = DateTimeOffset.Parse("2026-10-08T14:31:00+08:00"), Status = "ok" },
                new() { Step = "COPY", At = DateTimeOffset.Parse("2026-10-08T14:31:05+08:00"), Status = "ok" },
                new() { Step = "LINK", At = DateTimeOffset.Parse("2026-10-08T14:31:09+08:00"), Status = "ok" },
                new() { Step = "VERIFY", At = DateTimeOffset.Parse("2026-10-08T14:31:10+08:00"), Status = "ok" },
            },
            ChecksumMethod = "xxHash3",
            Checksum = "a3f9c2d1e5b70846",
            FileCount = 42,
            TotalBytes = 1024 * 1024,
            LinkTarget = @"D:\Archive\Users\Alice\Documents\ProjectA",
            Result = result,
        };

    [Fact]
    public void JSONL_批次头_条目_事件_可完整回读()
    {
        using var ws = new TempWorkspace("ledger-jsonl");
        var ledger = MigrationLedgerFactory.Create(ws.PathOf("ledger"), entryCountHint: 1, batchId: "mig-20261008-143052-a1b2c3");

        ledger.AppendEvent("e-01", "PARK", "ok", "源已改名为 .migbak\\ProjectA_...");
        ledger.WriteEntry(BuildEntry(ledger.BatchId, "e-01"));
        ledger.CompleteBatch();

        var batch = ledger.Read();

        Assert.Equal("mig-20261008-143052-a1b2c3", batch.Header.BatchId);
        Assert.Equal(LedgerSchema.Current, batch.Header.SchemaVersion);
        Assert.NotNull(batch.Header.CompletedAt);
        Assert.Equal("e-01", Assert.Single(batch.Entries).EntryId);
        Assert.Single(batch.Events);

        var entry = batch.Entries[0];
        Assert.Equal(@"C:\Users\Alice\Documents\ProjectA", entry.SourcePath);
        Assert.Equal("Junction", entry.LinkType);
        Assert.Equal("C:", entry.VolumeSource);
        Assert.Equal("D:", entry.VolumeTarget);
        Assert.Equal("xxHash3", entry.ChecksumMethod);
        Assert.Equal(4, entry.Operations.Count);
        Assert.Equal(42, entry.FileCount);
    }

    [Fact]
    public void JSONL_第1行是批次头_第2行起是记录()
    {
        using var ws = new TempWorkspace("ledger-lines");
        var ledger = MigrationLedgerFactory.Create(ws.PathOf("ledger"), 1);
        ledger.WriteEntry(BuildEntry(ledger.BatchId, "e-01"));

        var lines = File.ReadAllLines(Path.Combine(ws.PathOf("ledger"), ledger.BatchId + ".jsonl"))
            .Where(static l => !string.IsNullOrWhiteSpace(l)).ToList();

        Assert.Equal("batch", RecordTypeOf(lines[0]));
        Assert.Equal("entry", RecordTypeOf(lines[1]));
    }

    [Fact]
    public void 完成后_生成嵌套JSON快照与Markdown摘要()
    {
        using var ws = new TempWorkspace("ledger-artifacts");
        var ledger = (JsonlMigrationLedger)MigrationLedgerFactory.Create(ws.PathOf("ledger"), 1);
        ledger.AppendEvent("e-01", "VERIFY", "ok", "校验通过");
        ledger.WriteEntry(BuildEntry(ledger.BatchId, "e-01"));
        ledger.CompleteBatch();

        Assert.True(File.Exists(ledger.SnapshotFilePath));
        Assert.True(File.Exists(ledger.MarkdownSummaryPath));

        // 快照必须是需求给定的嵌套结构
        using var document = JsonDocument.Parse(File.ReadAllText(ledger.SnapshotFilePath));
        var root = document.RootElement;
        Assert.True(root.TryGetProperty("batchId", out _));
        Assert.True(root.TryGetProperty("startedAt", out _));
        Assert.True(root.TryGetProperty("completedAt", out _));
        Assert.True(root.TryGetProperty("host", out _));
        Assert.True(root.TryGetProperty("entries", out var entries));
        Assert.Equal(1, entries.GetArrayLength());
        Assert.Equal("Junction", entries[0].GetProperty("linkType").GetString());

        var markdown = File.ReadAllText(ledger.MarkdownSummaryPath);
        Assert.Contains("迁移台账", markdown);
        Assert.Contains("e-01", markdown);
        Assert.Contains("Junction", markdown);
    }

    [Fact]
    public void 同一条目重复写入_以最后一条为准()
    {
        using var ws = new TempWorkspace("ledger-dedup");
        var ledger = MigrationLedgerFactory.Create(ws.PathOf("ledger"), 1);

        ledger.WriteEntry(BuildEntry(ledger.BatchId, "e-01") with { Result = "done" });
        ledger.WriteEntry(BuildEntry(ledger.BatchId, "e-01") with { Result = "rolled_back" });

        var batch = ledger.Read();

        Assert.Equal("rolled_back", Assert.Single(batch.Entries).Result);
    }

    [Fact]
    public void 单行损坏_不影响其余记录可读()
    {
        using var ws = new TempWorkspace("ledger-corrupt");
        var directory = ws.PathOf("ledger");
        var ledger = MigrationLedgerFactory.Create(directory, 1);

        ledger.WriteEntry(BuildEntry(ledger.BatchId, "e-01"));
        File.AppendAllText(Path.Combine(directory, ledger.BatchId + ".jsonl"), "{ 这不是合法 JSON" + Environment.NewLine);
        ledger.WriteEntry(BuildEntry(ledger.BatchId, "e-02"));

        var batch = ledger.Read();

        Assert.Equal(2, batch.Entries.Count);
    }

    [Fact]
    public void 还原记录_以append_only方式追加()
    {
        using var ws = new TempWorkspace("ledger-restore");
        var ledger = MigrationLedgerFactory.Create(ws.PathOf("ledger"), 1);
        ledger.WriteEntry(BuildEntry(ledger.BatchId, "e-01"));

        ledger.AppendRestore("e-01", "还原成功：删除链接本体 → 停车目录改回原名");

        var batch = ledger.Read();

        Assert.Single(batch.Restores);
        Assert.Equal("done", Assert.Single(batch.Entries).Result);   // 原始条目未被覆盖
        Assert.Contains("还原成功", batch.Restores[0].Detail);
    }

    [Fact]
    public void 条目数超过阈值_自动切换SQLite()
    {
        using var ws = new TempWorkspace("ledger-sqlite");

        var jsonl = MigrationLedgerFactory.Create(ws.PathOf("ledger"), LedgerSchema.SqliteThreshold);
        Assert.Equal(LedgerStorageKind.Jsonl, jsonl.StorageKind);

        var sqlite = MigrationLedgerFactory.Create(ws.PathOf("ledger"), LedgerSchema.SqliteThreshold + 1);
        Assert.Equal(LedgerStorageKind.Sqlite, sqlite.StorageKind);

        sqlite.AppendEvent("e-01", "PARK", "ok", "停车完成");
        sqlite.WriteEntry(BuildEntry(sqlite.BatchId, "e-01"));
        sqlite.WriteEntry(BuildEntry(sqlite.BatchId, "e-02"));
        sqlite.CompleteBatch();

        var batch = sqlite.Read();
        Assert.Equal(2, batch.Entries.Count);
        Assert.Equal("e-01", batch.Entries[0].EntryId);
        Assert.Equal(@"C:\Users\Alice\Documents\ProjectA", batch.Entries[0].SourcePath);
        Assert.Single(batch.Events);
        Assert.True(File.Exists(sqlite.PrimaryFilePath));
        Assert.True(File.Exists(sqlite.MarkdownSummaryPath));
    }

    [Fact]
    public void 归档_把台账复制到指定位置且不改动原文件()
    {
        using var ws = new TempWorkspace("ledger-archive");
        var ledger = (JsonlMigrationLedger)MigrationLedgerFactory.Create(ws.PathOf("ledger"), 1);
        ledger.WriteEntry(BuildEntry(ledger.BatchId, "e-01"));

        var archive = ws.PathOf("archive");
        ledger.CompleteBatch(archive);

        var archivedJsonl = Path.Combine(archive, ledger.BatchId, ledger.BatchId + ".jsonl");
        Assert.True(File.Exists(archivedJsonl));
        Assert.True(File.Exists(Path.Combine(archive, ledger.BatchId, ledger.BatchId + ".md")));
        Assert.True(File.Exists(ledger.PrimaryFilePath));   // 原位保留，绝不随 .migbak 一起消失
    }

    [Fact]
    public void 台账目录与停车路径_与需求示例一致()
    {
        const string source = @"C:\Users\Alice\Documents\ProjectA";
        const string batchId = "mig-20261008-143052-a1b2c3";

        Assert.Equal(@"C:\Users\Alice\Documents\.migbak", MigrationLedgerFactory.ResolveParkingDirectory(source));
        Assert.Equal(@"C:\Users\Alice\Documents\.migbak\.ledger", MigrationLedgerFactory.ResolveLedgerDirectory(source));
        Assert.Equal(
            @"C:\Users\Alice\Documents\.migbak\ProjectA_20261008_143052_a1b2c3",
            MigrationLedgerFactory.ResolveParkingPath(source, batchId));
    }

    [Fact]
    public void 批次ID格式_与需求一致()
    {
        var batchId = MigrationLedgerFactory.CreateBatchId(DateTimeOffset.Parse("2026-10-08T14:30:52+08:00"));

        Assert.StartsWith("mig-20261008-143052-", batchId);
        Assert.Equal(26, batchId.Length);
        Assert.Equal("20261008_143052_a1b2c3", MigrationLedgerFactory.BatchSuffix("mig-20261008-143052-a1b2c3"));
    }

    [Fact]
    public void 列出批次与加载文件()
    {
        using var ws = new TempWorkspace("ledger-list");
        var directory = ws.PathOf("ledger");
        var a = MigrationLedgerFactory.Create(directory, 1, batchId: "mig-20261008-143052-aaaaaa");
        var b = MigrationLedgerFactory.Create(directory, 1, batchId: "mig-20261008-150000-bbbbbb");
        a.WriteEntry(BuildEntry(a.BatchId, "e-01"));
        b.WriteEntry(BuildEntry(b.BatchId, "e-01"));

        var ids = MigrationLedgerFactory.ListBatchIds(directory);
        Assert.Equal(2, ids.Count);
        Assert.Equal("mig-20261008-150000-bbbbbb", ids[0]);   // 倒序

        var loaded = MigrationLedgerFactory.LoadFile(a.PrimaryFilePath);
        Assert.Equal("mig-20261008-143052-aaaaaa", loaded.Header.BatchId);
    }

    [Fact]
    public void 元数据指纹_内容相同则一致_改时间戳则变化()
    {
        using var ws = new TempWorkspace("fingerprint");
        var a = ws.CreateDirectory("A");
        ws.CreateFile(@"A\x.txt", "hello");
        ws.CreateFile(@"A\sub\y.txt", "world");

        var b = ws.CreateDirectory("B");
        ws.CreateFile(@"B\x.txt", "hello");
        ws.CreateFile(@"B\sub\y.txt", "world");

        // WriteAllText 的 mtime 天然不同；要验证"内容相同则指纹相同"，必须先把时间戳对齐。
        var stamp = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
        foreach (var file in Directory.EnumerateFiles(a, "*", SearchOption.AllDirectories)
                     .Concat(Directory.EnumerateFiles(b, "*", SearchOption.AllDirectories)))
        {
            File.SetLastWriteTimeUtc(file, stamp);
        }

        var fa = MetadataFingerprint.Compute(a, out var filesA, out var bytesA);
        var fb = MetadataFingerprint.Compute(b, out var filesB, out var bytesB);

        Assert.Equal(2, filesA);
        Assert.Equal(filesA, filesB);
        Assert.Equal(bytesA, bytesB);
        Assert.Equal(fa, fb);   // 长度/mtime 相同 ⇒ 指纹相同

        File.SetLastWriteTimeUtc(Path.Combine(b, "x.txt"), DateTime.UtcNow.AddMinutes(5));
        var fb2 = MetadataFingerprint.Compute(b, out _, out _);
        Assert.NotEqual(fa, fb2);
    }

    [Fact]
    public void 内容哈希档位_能发现内容差异()
    {
        using var ws = new TempWorkspace("content-hash");
        var a = ws.CreateDirectory("A");
        ws.CreateFile(@"A\x.txt", "hello");
        var b = ws.CreateDirectory("B");
        ws.CreateFile(@"B\x.txt", "hello");

        var fa = MetadataFingerprint.ComputeContentHash(a, null, CancellationToken.None, out _, out _);
        var fb = MetadataFingerprint.ComputeContentHash(b, null, CancellationToken.None, out _, out _);
        Assert.Equal(fa, fb);

        File.WriteAllText(Path.Combine(b, "x.txt"), "HELLO");
        var fb2 = MetadataFingerprint.ComputeContentHash(b, null, CancellationToken.None, out _, out _);
        Assert.NotEqual(fa, fb2);
    }

    private static string? RecordTypeOf(string jsonLine)
    {
        using var document = JsonDocument.Parse(jsonLine);
        return document.RootElement.TryGetProperty("recordType", out var value) ? value.GetString() : null;
    }
}
