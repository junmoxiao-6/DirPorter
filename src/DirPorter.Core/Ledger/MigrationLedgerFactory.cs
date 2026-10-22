using System.Text;
using DirPorter.Core.FileSystem;

namespace DirPorter.Core.Ledger;

/// <summary>
/// 台账与停车目录的命名/定位规则（**唯一来源**，避免各模块各写一套）。
///
/// 布局（与需求给定的 ledger schema 示例一致）：
/// <code>
/// &lt;源目录的父目录&gt;\.migbak\                        ← 停车区（同卷，RENAME 瞬时）
/// &lt;源目录的父目录&gt;\.migbak\.ledger\&lt;batchId&gt;.jsonl    ← 台账（PARKING 同级）
/// &lt;源目录的父目录&gt;\.migbak\&lt;name&gt;_&lt;ts_suffix&gt;\      ← 被停车的源目录
/// </code>
/// 说明：R0 设计里停车名是同级目录的 `&lt;name&gt;.migbak-&lt;ts&gt;`，R3 改为集中到 `.migbak\` 子目录，
/// 原因是与台账 schema 的 `parkingPath` 示例保持一致，且不把用户目录搞乱。
/// **重命名仍然是同卷操作，回滚依然是毫秒级**（这是不可动摇的前提）。
/// </summary>
public static class MigrationLedgerFactory
{
    public const string MigBakFolderName = ".migbak";
    public const string LedgerFolderName = ".ledger";

    /// <summary>生成批次 ID：<c>mig-yyyyMMdd-HHmmss-&lt;6 位随机&gt;</c>。</summary>
    public static string CreateBatchId(DateTimeOffset? now = null)
    {
        var timestamp = (now ?? DateTimeOffset.Now).ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var suffix = Guid.NewGuid().ToString("N")[..6];
        return $"mig-{timestamp}-{suffix}";
    }

    /// <summary>批次 ID 的"紧凑后缀"：<c>20261008_143052_a1b2c3</c>（用于停车目录名）。</summary>
    public static string BatchSuffix(string batchId)
    {
        var body = batchId.StartsWith("mig-", StringComparison.OrdinalIgnoreCase) ? batchId[4..] : batchId;
        return body.Replace('-', '_');
    }

    /// <summary>
    /// 停车目录（源目录的父目录下 `.migbak`）。
    /// **返回普通路径（不带 <c>\\?\</c> 前缀）**：这些路径会写进台账与迁移记录，
    /// 必须与需求给定的 schema 示例一致、且人可读；需要 Win32 调用时由调用方自行规范化。
    /// </summary>
    public static string ResolveParkingDirectory(string sourcePath)
    {
        var source = PathNormalizer.StripExtendedPrefix(
            PathNormalizer.TrimTrailingSeparator(PathNormalizer.NormalizeAbsolute(sourcePath)));
        var parent = Path.GetDirectoryName(source) ?? throw new ArgumentException($"无法确定源目录的父目录：{sourcePath}");
        return Path.Combine(parent, MigBakFolderName);
    }

    /// <summary>某条目的停车路径：<c>&lt;父目录&gt;\.migbak\&lt;name&gt;_&lt;suffix&gt;</c>（普通路径）。</summary>
    public static string ResolveParkingPath(string sourcePath, string batchId)
    {
        var source = PathNormalizer.StripExtendedPrefix(
            PathNormalizer.TrimTrailingSeparator(PathNormalizer.NormalizeAbsolute(sourcePath)));
        var name = Path.GetFileName(source);
        return Path.Combine(ResolveParkingDirectory(sourcePath), $"{name}_{BatchSuffix(batchId)}");
    }

    /// <summary>台账目录：停车目录下的 `.ledger`。</summary>
    public static string ResolveLedgerDirectory(string sourcePath)
        => Path.Combine(ResolveParkingDirectory(sourcePath), LedgerFolderName);

    /// <summary>
    /// 按条目数选择存储引擎：默认 JSONL；超过阈值切换 SQLite（需求约束 1）。
    /// </summary>
    public static IMigrationLedger Create(string ledgerDirectory, int entryCountHint, string? batchId = null, string? host = null, DateTimeOffset? startedAt = null)
    {
        var id = batchId ?? CreateBatchId(startedAt);
        var machine = host ?? Environment.MachineName;

        return entryCountHint > LedgerSchema.SqliteThreshold
            ? new SqliteMigrationLedger(ledgerDirectory, id, machine, startedAt)
            : new JsonlMigrationLedger(ledgerDirectory, id, machine, startedAt);
    }

    /// <summary>打开既有批次（按目录内已存在的文件类型自动判定引擎）。</summary>
    public static IMigrationLedger Open(string ledgerDirectory, string batchId)
    {
        if (File.Exists(Path.Combine(ledgerDirectory, batchId + ".db")))
            return new SqliteMigrationLedger(ledgerDirectory, batchId, Environment.MachineName);

        return new JsonlMigrationLedger(ledgerDirectory, batchId, Environment.MachineName);
    }

    /// <summary>列出指定台账目录下的全部批次 ID（按名称倒序 = 时间倒序）。</summary>
    public static IReadOnlyList<string> ListBatchIds(string ledgerDirectory)
    {
        if (!Directory.Exists(ledgerDirectory)) return Array.Empty<string>();

        return Directory.EnumerateFiles(ledgerDirectory)
            .Where(static f => f.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
            .Select(static f => Path.GetFileNameWithoutExtension(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>读取任意台账文件（.jsonl 或 .db）。</summary>
    public static LedgerBatch LoadFile(string path)
    {
        if (path.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
            return SqliteMigrationLedger.OpenExisting(path).Read();

        var directory = Path.GetDirectoryName(path)!;
        var batchId = Path.GetFileNameWithoutExtension(path);
        return new JsonlMigrationLedger(directory, batchId, Environment.MachineName).Read();
    }
}

/// <summary>台账的可读 Markdown 摘要（需求约束 1）。</summary>
public static class LedgerMarkdown
{
    public static string Build(LedgerBatch batch)
    {
        var builder = new StringBuilder();

        builder.AppendLine($"# 迁移台账：{batch.Header.BatchId}");
        builder.AppendLine();
        builder.AppendLine("| 字段 | 值 |");
        builder.AppendLine("|---|---|");
        builder.AppendLine($"| 批次 ID | `{batch.Header.BatchId}` |");
        builder.AppendLine($"| 主机 | {batch.Header.Host} |");
        builder.AppendLine($"| 开始时间 | {batch.Header.StartedAt:yyyy-MM-dd HH:mm:ss zzz} |");
        builder.AppendLine($"| 完成时间 | {(batch.Header.CompletedAt is { } completed ? $"{completed:yyyy-MM-dd HH:mm:ss zzz}" : "（未完成）")} |");
        builder.AppendLine($"| schema 版本 | {batch.Header.SchemaVersion} |");
        builder.AppendLine($"| 条目数 | {batch.Entries.Count} |");
        builder.AppendLine();

        builder.AppendLine("## 条目");
        builder.AppendLine();
        builder.AppendLine("| entryId | 结果 | 源路径 | 停车路径 | 目标路径 | 链接 | 校验 |");
        builder.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var entry in batch.Entries)
        {
            builder.AppendLine(
                $"| `{entry.EntryId}` | {entry.Result}{(entry.Error is null ? string.Empty : $"（{entry.Error}）")} "
                + $"| `{entry.SourcePath}` | `{entry.ParkingPath}` | `{entry.TargetPath}` "
                + $"| {entry.LinkType} | {entry.ChecksumMethod}:{Shorten(entry.Checksum)} |");
        }

        builder.AppendLine();
        builder.AppendLine("## 操作时间线");
        builder.AppendLine();
        builder.AppendLine("| 时间 | entryId | 步骤 | 状态 | 备注 |");
        builder.AppendLine("|---|---|---|---|---|");
        foreach (var ev in batch.Events.OrderBy(static e => e.At))
        {
            builder.AppendLine($"| {ev.At:HH:mm:ss} | `{ev.EntryId}` | {ev.Step} | {ev.Status} | {ev.Detail ?? string.Empty} |");
        }

        if (batch.Restores.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## 还原记录（append-only，不覆盖原始条目）");
            builder.AppendLine();
            builder.AppendLine("| 时间 | entryId | 说明 |");
            builder.AppendLine("|---|---|---|");
            foreach (var restore in batch.Restores.OrderBy(static r => r.At))
                builder.AppendLine($"| {restore.At:yyyy-MM-dd HH:mm:ss} | `{restore.EntryId}` | {restore.Detail} |");
        }

        builder.AppendLine();
        builder.AppendLine("> 回滚/还原的唯一权威输入是台账（jsonl / db）。`migrations.json` 仅供展示。");
        builder.AppendLine("> 本文件可读但不可作为机器输入；机器输入请用 jsonl / db。");

        return builder.ToString();
    }

    private static string Shorten(string checksum)
        => string.IsNullOrEmpty(checksum) ? "(空)" : checksum.Length <= 12 ? checksum : checksum[..12] + "…";
}
