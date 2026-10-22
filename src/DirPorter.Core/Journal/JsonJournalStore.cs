using System.Text.Json;
using System.Text.Json.Serialization;
using DirPorter.Core.Diagnostics;
using DirPorter.Core.FileSystem;
using DirPorter.Core.Migration;

namespace DirPorter.Core.Journal;

/// <summary>
/// 运行中任务的 Journal 记录。
///
/// 作用：进程被强杀后，重启时据此识别"卡在哪一步、磁盘上可能留下了什么"，
/// 并给出**可执行的修复建议**（绝不自动执行修复）。
///
/// 写入策略（需求 96 行）：先写 <c>.tmp</c> 再原子替换（MoveFileEx + REPLACE_EXISTING | WRITE_THROUGH），
/// 保证任意时刻磁盘上的 json 都是完整可解析的。
/// </summary>
public sealed record MigrationRunRecord
{
    [JsonPropertyName("taskId")] public required string TaskId { get; init; }

    [JsonPropertyName("batchId")] public required string BatchId { get; init; }

    [JsonPropertyName("entryId")] public required string EntryId { get; init; }

    [JsonPropertyName("sourcePath")] public required string SourcePath { get; init; }

    [JsonPropertyName("targetPath")] public required string TargetPath { get; init; }

    [JsonPropertyName("parkingPath")] public string? ParkingPath { get; init; }

    [JsonPropertyName("linkPath")] public string? LinkPath { get; init; }

    [JsonPropertyName("linkKind")] public string? LinkKind { get; init; }

    [JsonPropertyName("state")] public MigrationState State { get; init; } = MigrationState.Planned;

    [JsonPropertyName("lastCompletedStep")] public string? LastCompletedStep { get; init; }

    /// <summary>已完成步骤（按顺序），与 <see cref="State"/> 一起用于恢复判定与 Dry-run 复现。</summary>
    [JsonPropertyName("completedSteps")] public List<string> CompletedSteps { get; init; } = new();

    /// <summary>本任务在目标侧新建的目录（回滚时只允许删除这些目录，绝不删用户既有目录）。</summary>
    [JsonPropertyName("createdTargetDirectories")] public List<string> CreatedTargetDirectories { get; init; } = new();

    /// <summary>目标目录在本次任务开始前是否已存在（决定回滚是否删除它）。</summary>
    [JsonPropertyName("targetDirectoryExistedBefore")] public bool TargetDirectoryExistedBefore { get; init; }

    [JsonPropertyName("startedUtc")] public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("updatedUtc")] public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("appVersion")] public string? AppVersion { get; init; }

    [JsonPropertyName("error")] public string? Error { get; init; }

    [JsonPropertyName("rollbackFailures")] public List<string> RollbackFailures { get; init; } = new();
}

/// <summary>Journal 存储接口。</summary>
public interface IJournalStore
{
    void Save(MigrationRunRecord record);
    MigrationRunRecord? Load(string taskId);
    IReadOnlyList<MigrationRunRecord> LoadAll();
    void Delete(string taskId);
}

/// <summary>基于 JSON 文件的 Journal（每个任务一个文件，原子替换写入）。</summary>
public sealed class JsonJournalStore : IJournalStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _directory;

    public JsonJournalStore(string? directory = null)
        => _directory = directory ?? Config.DirPorterPaths.RunsDirectory;

    public string JournalDirectory => _directory;

    public void Save(MigrationRunRecord record)
    {
        System.IO.Directory.CreateDirectory(_directory);

        var path = PathFor(record.TaskId);
        var temporary = path + ".tmp";

        var json = JsonSerializer.Serialize(record, Options);

        // 先写临时文件（含 flush 到磁盘），再原子替换。
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        using (var writer = new StreamWriter(stream, System.Text.Encoding.UTF8))
        {
            writer.Write(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        var replaced = Kernel32.MoveFileEx(
            PathNormalizer.NormalizeAbsolute(temporary),
            PathNormalizer.NormalizeAbsolute(path),
            Kernel32.MoveFileReplaceExisting | Kernel32.MoveFileWriteThrough);

        if (!replaced)
        {
            var code = Win32Error.Last;
            throw new DirPorterNativeException("写入 Journal", code, $"原子替换 Journal 文件失败：{Win32Error.Describe(code)}");
        }
    }

    public MigrationRunRecord? Load(string taskId)
    {
        var path = PathFor(taskId);
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<MigrationRunRecord>(File.ReadAllText(path), Options);
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DirPorter] Journal 文件损坏：{path}（{ex.Message}）");
            return null;
        }
    }

    public IReadOnlyList<MigrationRunRecord> LoadAll()
    {
        if (!System.IO.Directory.Exists(_directory)) return Array.Empty<MigrationRunRecord>();

        var list = new List<MigrationRunRecord>();
        foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "*.json"))
        {
            var taskId = Path.GetFileNameWithoutExtension(file);
            var record = Load(taskId);
            if (record is not null) list.Add(record);
        }

        return list.OrderBy(static r => r.StartedUtc).ToList();
    }

    public void Delete(string taskId)
    {
        var path = PathFor(taskId);
        if (File.Exists(path)) File.Delete(path);
        var temporary = path + ".tmp";
        if (File.Exists(temporary)) File.Delete(temporary);
    }

    private string PathFor(string taskId) => System.IO.Path.Combine(_directory, Sanitize(taskId) + ".json");

    private static string Sanitize(string taskId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(taskId.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
