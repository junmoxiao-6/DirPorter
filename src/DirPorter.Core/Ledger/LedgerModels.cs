using System.Text.Json;
using System.Text.Json.Serialization;

namespace DirPorter.Core.Ledger;

/// <summary>台账记录类型（JSONL 每行一个 recordType）。</summary>
public static class LedgerRecordTypes
{
    public const string Batch = "batch";
    public const string Entry = "entry";
    public const string Event = "event";
    public const string Restore = "restore";
}

/// <summary>台账存储引擎。</summary>
public enum LedgerStorageKind
{
    /// <summary>默认：追加式 JSONL（人可读、断电可续、任意行可独立解析）。</summary>
    Jsonl = 0,

    /// <summary>单批次条目超过阈值时自动切换（需求：>1000 条）。</summary>
    Sqlite = 1,
}

/// <summary>台账 schema 版本。**只允许新增字段，不允许改语义或删字段。**</summary>
public static class LedgerSchema
{
    public const int Current = 1;

    /// <summary>超过该条目数时自动从 JSONL 切换到 SQLite。</summary>
    public const int SqliteThreshold = 1000;
}

/// <summary>批次头（JSONL 第 1 行 / SQLite batches 表）。</summary>
public sealed record LedgerBatchHeader
{
    [JsonPropertyName("recordType")] public string RecordType => LedgerRecordTypes.Batch;
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = LedgerSchema.Current;
    [JsonPropertyName("batchId")] public required string BatchId { get; init; }
    [JsonPropertyName("startedAt")] public required DateTimeOffset StartedAt { get; init; }
    [JsonPropertyName("completedAt")] public DateTimeOffset? CompletedAt { get; init; }
    [JsonPropertyName("host")] public required string Host { get; init; }
}

/// <summary>一次操作记录（PARK / COPY / LINK / VERIFY …）。</summary>
public sealed record LedgerOperation
{
    [JsonPropertyName("step")] public required string Step { get; init; }
    [JsonPropertyName("at")] public required DateTimeOffset At { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("detail")] public string? Detail { get; init; }
}

/// <summary>
/// 台账条目 —— **回滚/还原的唯一权威输入**。
/// 前 11 个字段严格采用需求给定的 schema，一字未改；其后的字段为新增（向后兼容）。
/// </summary>
public sealed record LedgerEntry
{
    [JsonPropertyName("recordType")] public string RecordType => LedgerRecordTypes.Entry;

    [JsonPropertyName("batchId")] public required string BatchId { get; init; }

    [JsonPropertyName("entryId")] public required string EntryId { get; init; }

    [JsonPropertyName("sourcePath")] public required string SourcePath { get; init; }

    [JsonPropertyName("parkingPath")] public required string ParkingPath { get; init; }

    [JsonPropertyName("targetPath")] public required string TargetPath { get; init; }

    [JsonPropertyName("linkPath")] public required string LinkPath { get; init; }

    [JsonPropertyName("linkType")] public required string LinkType { get; init; }

    [JsonPropertyName("volumeSource")] public required string VolumeSource { get; init; }

    [JsonPropertyName("volumeTarget")] public required string VolumeTarget { get; init; }

    [JsonPropertyName("operations")] public List<LedgerOperation> Operations { get; init; } = new();

    [JsonPropertyName("checksumMethod")] public string ChecksumMethod { get; init; } = "xxHash3";

    [JsonPropertyName("checksum")] public string Checksum { get; init; } = string.Empty;

    // ---- 以下为新增字段（向后兼容，旧解析器可忽略）----

    [JsonPropertyName("fileCount")] public long FileCount { get; init; }

    [JsonPropertyName("totalBytes")] public long TotalBytes { get; init; }

    [JsonPropertyName("linkTarget")] public string? LinkTarget { get; init; }

    [JsonPropertyName("result")] public string Result { get; init; } = "done";

    [JsonPropertyName("error")] public string? Error { get; init; }
}

/// <summary>关键步骤事件（与 entry 分离，保证"卡在哪一步"在文件里是独立可读的一行）。</summary>
public sealed record LedgerEventRecord
{
    [JsonPropertyName("recordType")] public string RecordType => LedgerRecordTypes.Event;
    [JsonPropertyName("batchId")] public required string BatchId { get; init; }
    [JsonPropertyName("entryId")] public required string EntryId { get; init; }
    [JsonPropertyName("step")] public required string Step { get; init; }
    [JsonPropertyName("at")] public required DateTimeOffset At { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("detail")] public string? Detail { get; init; }
}

/// <summary>还原记录（append-only，不覆盖原始 entry）。</summary>
public sealed record LedgerRestoreRecord
{
    [JsonPropertyName("recordType")] public string RecordType => LedgerRecordTypes.Restore;
    [JsonPropertyName("batchId")] public required string BatchId { get; init; }
    [JsonPropertyName("entryId")] public required string EntryId { get; init; }
    [JsonPropertyName("at")] public required DateTimeOffset At { get; init; }
    [JsonPropertyName("detail")] public required string Detail { get; init; }
}

/// <summary>读取出来的整批台账。</summary>
public sealed record LedgerBatch(
    LedgerBatchHeader Header,
    IReadOnlyList<LedgerEntry> Entries,
    IReadOnlyList<LedgerEventRecord> Events,
    IReadOnlyList<LedgerRestoreRecord> Restores)
{
    public LedgerEntry? FindEntry(string entryId) => Entries.FirstOrDefault(e => e.EntryId == entryId);
}

/// <summary>台账读写接口。JSONL 与 SQLite 两种实现共用该契约。</summary>
public interface IMigrationLedger
{
    string BatchId { get; }

    string LedgerDirectory { get; }

    LedgerStorageKind StorageKind { get; }

    /// <summary>主存储文件（jsonl 或 db）。</summary>
    string PrimaryFilePath { get; }

    /// <summary>可读 Markdown 摘要文件。</summary>
    string MarkdownSummaryPath { get; }

    /// <summary>追加一条步骤事件（每个关键步骤完成后调用）。</summary>
    void AppendEvent(string entryId, string step, string status, string? detail = null);

    /// <summary>写入权威 entry（**LINK_VERIFY 成功后立即调用**）。</summary>
    void WriteEntry(LedgerEntry entry);

    /// <summary>追加还原记录。</summary>
    void AppendRestore(string entryId, string detail);

    /// <summary>收尾：写嵌套 JSON 快照 + Markdown 摘要（+ 可选归档）。</summary>
    void CompleteBatch(string? archiveDirectory = null);

    LedgerBatch Read();
}

/// <summary>台账 JSON 序列化设置（两种实现共用，保证输出一致）。</summary>
internal static class LedgerJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };

    public static string Serialize<T>(T value, bool indented = false)
        => JsonSerializer.Serialize(value, indented ? Indented : Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
