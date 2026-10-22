using System.Text.Json;
using System.Text.Json.Serialization;
using DirPorter.Core.Diagnostics;
using DirPorter.Core.FileSystem;

namespace DirPorter.Core.Journal;

/// <summary>
/// 迁移记录（GUI「迁移记录页」的数据来源）。
///
/// ⚠️ 定位：**仅供展示**。回滚/还原的唯一权威输入是台账（`Ledger`，见 docs/03 §4）。
/// 两者以 <see cref="BatchId"/> 关联；若不一致，以台账为准并在日志中记录差异。
/// </summary>
public sealed record MigrationRecord
{
    [JsonPropertyName("id")] public required string Id { get; init; }

    [JsonPropertyName("batchId")] public required string BatchId { get; init; }

    [JsonPropertyName("entryId")] public required string EntryId { get; init; }

    [JsonPropertyName("startedUtc")] public DateTimeOffset StartedUtc { get; init; }

    [JsonPropertyName("finishedUtc")] public DateTimeOffset? FinishedUtc { get; init; }

    [JsonPropertyName("sourcePath")] public required string SourcePath { get; init; }

    [JsonPropertyName("targetPath")] public required string TargetPath { get; init; }

    [JsonPropertyName("backupPath")] public string? BackupPath { get; init; }

    [JsonPropertyName("ledgerPath")] public string? LedgerPath { get; init; }

    [JsonPropertyName("status")] public required string Status { get; init; }

    [JsonPropertyName("linkType")] public string? LinkType { get; init; }

    [JsonPropertyName("fileCount")] public long FileCount { get; init; }

    [JsonPropertyName("totalBytes")] public long TotalBytes { get; init; }

    [JsonPropertyName("checksumMethod")] public string? ChecksumMethod { get; init; }

    [JsonPropertyName("checksum")] public string? Checksum { get; init; }

    [JsonPropertyName("skipped")] public List<string> Skipped { get; init; } = new();

    [JsonPropertyName("warnings")] public List<string> Warnings { get; init; } = new();

    [JsonPropertyName("appVersion")] public string? AppVersion { get; init; }
}

/// <summary>迁移记录索引的读写（原子替换写入，避免半截 JSON）。</summary>
public sealed class MigrationIndexStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath;
    private readonly object _lock = new();

    public MigrationIndexStore(string? filePath = null)
        => _filePath = filePath ?? Config.DirPorterPaths.MigrationsFile;

    public string FilePath => _filePath;

    public IReadOnlyList<MigrationRecord> LoadAll()
    {
        lock (_lock)
        {
            if (!File.Exists(_filePath)) return Array.Empty<MigrationRecord>();

            try
            {
                return JsonSerializer.Deserialize<List<MigrationRecord>>(File.ReadAllText(_filePath), Options)
                       ?? new List<MigrationRecord>();
            }
            catch (JsonException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DirPorter] 迁移记录索引损坏：{ex.Message}");
                return Array.Empty<MigrationRecord>();
            }
        }
    }

    /// <summary>追加一条记录（同 id 覆盖）。</summary>
    public void Upsert(MigrationRecord record)
    {
        lock (_lock)
        {
            var list = LoadAll().Where(r => !string.Equals(r.Id, record.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            list.Add(record);
            Save(list);
        }
    }

    public void Save(IReadOnlyList<MigrationRecord> records)
    {
        lock (_lock)
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var temporary = _filePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(records, Options));

            // 先写 .tmp 再原子替换（与 Journal 同一策略）
            if (!Kernel32.MoveFileEx(
                    PathNormalizer.NormalizeAbsolute(temporary),
                    PathNormalizer.NormalizeAbsolute(_filePath),
                    Kernel32.MoveFileReplaceExisting | Kernel32.MoveFileWriteThrough))
            {
                var code = Win32Error.Last;
                throw new DirPorterNativeException("写入迁移记录索引", code, $"原子替换失败：{Win32Error.Describe(code)}");
            }
        }
    }
}
