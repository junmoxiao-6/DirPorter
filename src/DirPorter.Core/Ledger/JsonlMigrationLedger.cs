using System.Text;
using System.Text.Json;

namespace DirPorter.Core.Ledger;

/// <summary>
/// 追加式 JSONL 台账（默认存储引擎）。
///
/// 为什么是 JSONL：
/// - **追加写**：进程被强杀也不会破坏已写入的记录（每行独立完整）；
/// - **人可读**：出问题时可 `type` / `findstr` 直接看；
/// - **断电可续**：崩溃后从最后一行继续追加，不需要事务恢复。
///
/// 文件布局（同一批次目录内）：
/// <code>
/// {batchId}.jsonl   主存储：batch 头 + 逐行 entry / event / restore
/// {batchId}.json    批次结束时的嵌套快照（需求给定的 schema 原样）
/// {batchId}.md      可读 Markdown 摘要
/// </code>
/// </summary>
public sealed class JsonlMigrationLedger : IMigrationLedger
{
    private readonly object _writeLock = new();
    private readonly string _filePath;
    private readonly LedgerBatchHeader _header;

    public JsonlMigrationLedger(string ledgerDirectory, string batchId, string host, DateTimeOffset? startedAt = null)
    {
        LedgerDirectory = ledgerDirectory;
        BatchId = batchId;
        Directory.CreateDirectory(ledgerDirectory);

        _filePath = Path.Combine(ledgerDirectory, batchId + ".jsonl");
        _header = new LedgerBatchHeader
        {
            BatchId = batchId,
            Host = host,
            StartedAt = startedAt ?? DateTimeOffset.Now,
        };

        if (!File.Exists(_filePath))
        {
            File.WriteAllText(_filePath, LedgerJson.Serialize(_header) + Environment.NewLine, new UTF8Encoding(false));
        }
        else
        {
            // 续写既有批次：以文件里的 batch 头为准
            _header = Read().Header;
        }
    }

    public string BatchId { get; }
    public string LedgerDirectory { get; }
    public LedgerStorageKind StorageKind => LedgerStorageKind.Jsonl;
    public string PrimaryFilePath => _filePath;
    public string MarkdownSummaryPath => Path.Combine(LedgerDirectory, BatchId + ".md");
    public string SnapshotFilePath => Path.Combine(LedgerDirectory, BatchId + ".json");

    public void AppendEvent(string entryId, string step, string status, string? detail = null)
        => AppendLine(LedgerJson.Serialize(new LedgerEventRecord
        {
            BatchId = BatchId,
            EntryId = entryId,
            Step = step,
            Status = status,
            At = DateTimeOffset.Now,
            Detail = detail,
        }));

    /// <summary>写入权威 entry（**LINK_VERIFY 成功后立即调用**）。append-only：同 entryId 后写覆盖前写。</summary>
    public void WriteEntry(LedgerEntry entry) => AppendLine(LedgerJson.Serialize(entry));

    public void AppendRestore(string entryId, string detail)
        => AppendLine(LedgerJson.Serialize(new LedgerRestoreRecord
        {
            BatchId = BatchId,
            EntryId = entryId,
            At = DateTimeOffset.Now,
            Detail = detail,
        }));

    public void CompleteBatch(string? archiveDirectory = null)
    {
        // append-only 之下无法改写第 1 行，因此追加一条带 completedAt 的 batch 记录；
        // 读取时以最后一条 batch 记录为准（Read 已如此实现）。
        AppendLine(LedgerJson.Serialize(_header with { CompletedAt = DateTimeOffset.Now }));

        var batch = Read();

        var snapshot = new
        {
            batchId = batch.Header.BatchId,
            startedAt = batch.Header.StartedAt,
            completedAt = batch.Header.CompletedAt,
            host = batch.Header.Host,
            schemaVersion = batch.Header.SchemaVersion,
            entries = batch.Entries,
        };

        File.WriteAllText(SnapshotFilePath, LedgerJson.Serialize(snapshot, indented: true), new UTF8Encoding(false));
        File.WriteAllText(MarkdownSummaryPath, LedgerMarkdown.Build(batch), new UTF8Encoding(false));

        if (!string.IsNullOrWhiteSpace(archiveDirectory))
            LedgerArchive.CopyTo(LedgerDirectory, BatchId, archiveDirectory!);
    }

    public LedgerBatch Read()
    {
        var entries = new List<LedgerEntry>();
        var events = new List<LedgerEventRecord>();
        var restores = new List<LedgerRestoreRecord>();
        var header = _header;

        if (!File.Exists(_filePath))
            return new LedgerBatch(header, entries, events, restores);

        foreach (var line in File.ReadLines(_filePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            string? recordType;
            try
            {
                using var document = JsonDocument.Parse(line);
                recordType = document.RootElement.TryGetProperty("recordType", out var value) ? value.GetString() : null;
            }
            catch (JsonException)
            {
                // 单行损坏（例如断电写了半行）不应让整批台账不可读
                continue;
            }

            switch (recordType)
            {
                case LedgerRecordTypes.Entry:
                    var entry = LedgerJson.Deserialize<LedgerEntry>(line);
                    if (entry is not null) entries.Add(entry);
                    break;

                case LedgerRecordTypes.Event:
                    var ev = LedgerJson.Deserialize<LedgerEventRecord>(line);
                    if (ev is not null) events.Add(ev);
                    break;

                case LedgerRecordTypes.Restore:
                    var restore = LedgerJson.Deserialize<LedgerRestoreRecord>(line);
                    if (restore is not null) restores.Add(restore);
                    break;

                case LedgerRecordTypes.Batch:
                    var batchHeader = LedgerJson.Deserialize<LedgerBatchHeader>(line);
                    if (batchHeader is not null) header = batchHeader;
                    break;
            }
        }

        return new LedgerBatch(header, DeduplicateByEntryId(entries), events, restores);
    }

    private static List<LedgerEntry> DeduplicateByEntryId(List<LedgerEntry> entries)
        => entries
            .GroupBy(static e => e.EntryId, StringComparer.OrdinalIgnoreCase)
            .Select(static g => g.Last())
            .ToList();

    private void AppendLine(string json)
    {
        lock (_writeLock)
        {
            // FileShare.Read 允许外部边跑边读（排障时很有用）；追加模式保证"行"是原子单位。
            using var stream = new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(json);
            writer.Write('\n');
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
    }
}

/// <summary>台账归档：复制到用户指定位置或目标侧。**绝不随 .migbak 一起删除**（需求约束 4）。</summary>
public static class LedgerArchive
{
    /// <summary>
    /// 把台账文件复制到归档目录，返回实际落点。
    /// 归档顺序要求：**先复制成功、再删 .migbak**，否则会出现"备份没了、台账也没了"的双输局面。
    /// </summary>
    public static string CopyTo(string ledgerDirectory, string batchId, string archiveDirectory)
    {
        var target = Path.Combine(archiveDirectory, batchId);
        Directory.CreateDirectory(target);

        var copied = 0;
        foreach (var file in Directory.EnumerateFiles(ledgerDirectory, batchId + ".*"))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            copied++;
        }

        if (copied == 0)
            throw new IOException($"台账归档失败：在 {ledgerDirectory} 未找到批次 {batchId} 的任何文件。");

        return target;
    }
}
