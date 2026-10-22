using System.Globalization;
using DirPorter.Core.Config;
using DirPorter.Core.Diagnostics;
using DirPorter.Core.FileSystem;
using DirPorter.Core.Journal;
using DirPorter.Core.Ledger;

namespace DirPorter.Core.Backup;

/// <summary>候选项的发现来源。</summary>
public enum BackupCandidateOrigin
{
    /// <summary>来自迁移记录索引（最可靠：有明确的完成时间）。</summary>
    MigrationRecord = 0,

    /// <summary>来自目录扫描（用户指定根目录下的 `.migbak`）。</summary>
    DirectoryScan = 1,
}

/// <summary>一个待判定的停车目录。</summary>
public sealed record BackupCandidate(
    string ParkingPath,
    DateTimeOffset CreatedAt,
    string SourcePath,
    string? BatchId,
    string? EntryId,
    long ApproxBytes,
    BackupCandidateOrigin Origin,
    bool CreatedAtIsEstimated);

/// <summary>扫描与清理选项。</summary>
public sealed record BackupSweepOptions
{
    /// <summary>保留天数。&lt;0 表示"永不清理"（<see cref="Migration.BackupCleanupPolicy.Never"/>）。</summary>
    public int RetentionDays { get; init; } = 7;

    /// <summary>仅列出将要做什么，不实际删除。</summary>
    public bool DryRun { get; init; }

    /// <summary>忽略保留期，清理全部候选（需显式开启）。</summary>
    public bool Force { get; init; }

    /// <summary>判定基准时间（默认现在；测试可注入）。</summary>
    public DateTimeOffset? Now { get; init; }

    /// <summary>额外扫描这些根目录下的 `.migbak`（补充迁移记录索引的盲区）。</summary>
    public IReadOnlyList<string> ScanRoots { get; init; } = Array.Empty<string>();

    /// <summary>目录扫描的最大递归深度。</summary>
    public int ScanDepth { get; init; } = 3;

    public static BackupSweepOptions Default { get; } = new();
}

/// <summary>单个候选的处置结果。</summary>
public sealed record BackupSweepItem(
    string ParkingPath,
    string Action,
    string Detail,
    long Bytes,
    DateTimeOffset CreatedAt,
    int AgeDays);

/// <summary>一轮清理的结果。</summary>
public sealed record BackupSweepResult(
    int Examined,
    int Deleted,
    int Skipped,
    int Failed,
    long ReclaimedBytes,
    IReadOnlyList<BackupSweepItem> Items,
    bool DryRun)
{
    public string Describe()
    {
        var prefix = DryRun ? "（演练，未实际删除）" : string.Empty;
        return $"{prefix}扫描 {Examined} 个停车目录：删除 {Deleted}、保留 {Skipped}、失败 {Failed}，回收 {SpaceRequirement.Format(ReclaimedBytes)}。";
    }
}

/// <summary>
/// 备份保留策略执行器。
///
/// 定位（D5 决策）：停车目录（`.migbak`）是回滚的兜底，默认保留 7 天；
/// 到期后由本服务清理。**它是「BACKUP_CLEANUP 之后」的唯一删除入口**。
///
/// 三条不可违反的规矩：
/// <list type="number">
/// <item><b>绝不删除台账</b>：`.migbak\.ledger` 长期保留、不自动清理（用户明确要求）。
/// 因此本服务只删 `.migbak` 下的**停车目录**，且名称为 `.ledger` 的直接跳过。</item>
/// <item><b>只删"位于 .migbak 之下"的目录</b>：删除前强制校验父目录叶名等于 `.migbak`，
/// 从结构上杜绝"参数写错 → 删掉用户真实目录"。</item>
/// <item><b>走 SafeTreeDeleter</b>：停车目录内部可能含嵌套链接，
/// 普通递归删除会穿透链接删掉真实数据。</item>
/// </list>
/// </summary>
public sealed class BackupRetentionService
{
    private readonly MigrationIndexStore _index;

    public BackupRetentionService(MigrationIndexStore? index = null)
        => _index = index ?? new MigrationIndexStore();

    /// <summary>扫描候选（不删除）。</summary>
    public IReadOnlyList<BackupCandidate> Discover(BackupSweepOptions options)
    {
        var candidates = new Dictionary<string, BackupCandidate>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in FromMigrationRecords())
        {
            candidates.TryAdd(candidate.ParkingPath, candidate);
        }

        foreach (var root in options.ScanRoots)
        {
            foreach (var candidate in FromDirectoryScan(root, options.ScanDepth))
            {
                candidates.TryAdd(candidate.ParkingPath, candidate);
            }
        }

        return candidates.Values.OrderBy(static c => c.CreatedAt).ToList();
    }

    /// <summary>执行一轮清理。</summary>
    public BackupSweepResult Sweep(BackupSweepOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= BackupSweepOptions.Default;
        var now = options.Now ?? DateTimeOffset.Now;

        var items = new List<BackupSweepItem>();
        var examined = 0;
        var deleted = 0;
        var skipped = 0;
        var failed = 0;
        long reclaimed = 0;

        // RetentionDays < 0 表示"永不清理"：直接返回空结果，避免误删。
        if (options.RetentionDays < 0 && !options.Force)
        {
            return new BackupSweepResult(0, 0, 0, 0, 0, items, options.DryRun);
        }

        foreach (var candidate in Discover(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            examined++;

            var age = now - candidate.CreatedAt;
            var ageDays = (int)Math.Max(0, age.TotalDays);

            var expired = options.Force || ageDays >= options.RetentionDays;

            if (!expired)
            {
                skipped++;
                items.Add(new BackupSweepItem(candidate.ParkingPath, "keep",
                    $"未到期：{DescribeEstimated(candidate)} 已保留 {ageDays} 天（保留 {options.RetentionDays} 天）",
                    candidate.ApproxBytes, candidate.CreatedAt, ageDays));
                continue;
            }

            // ---- 安全闸门：只允许删除 .migbak 之下的目录 ----
            var guard = Refuse(candidate.ParkingPath);
            if (guard is not null)
            {
                failed++;
                items.Add(new BackupSweepItem(candidate.ParkingPath, "refuse", guard, 0, candidate.CreatedAt, ageDays));
                continue;
            }

            if (!ReparsePointOps.Inspect(PathNormalizer.NormalizeAbsolute(candidate.ParkingPath)).PathExists)
            {
                skipped++;
                items.Add(new BackupSweepItem(candidate.ParkingPath, "gone", "已不存在（可能已被手动清理）", 0, candidate.CreatedAt, ageDays));
                ClearRecordBackupPath(candidate);
                continue;
            }

            if (options.DryRun)
            {
                deleted++;
                reclaimed += candidate.ApproxBytes;
                items.Add(new BackupSweepItem(candidate.ParkingPath, "would-delete",
                    $"将删除（{DescribeEstimated(candidate)} 已保留 {ageDays} 天）", candidate.ApproxBytes, candidate.CreatedAt, ageDays));
                continue;
            }

            var result = SafeTreeDeleter.DeleteTree(PathNormalizer.NormalizeAbsolute(candidate.ParkingPath), cancellationToken);

            if (result.AnyFailure)
            {
                failed++;
                items.Add(new BackupSweepItem(candidate.ParkingPath, "partial",
                    $"删除未完全成功：{result.Failures[0].Path} — {result.Failures[0].Message}", 0, candidate.CreatedAt, ageDays));
            }
            else
            {
                deleted++;
                reclaimed += candidate.ApproxBytes;
                items.Add(new BackupSweepItem(candidate.ParkingPath, "deleted",
                    $"已删除（{DescribeEstimated(candidate)} 已保留 {ageDays} 天）", candidate.ApproxBytes, candidate.CreatedAt, ageDays));
                ClearRecordBackupPath(candidate);
            }

            // 空掉的 .migbak（只剩 .ledger 或全空）不删除：它是一个容器，
            // 下次迁移还要用；且"顺手清理空目录"属于多余动作，不做。
        }

        return new BackupSweepResult(examined, deleted, skipped, failed, reclaimed, items, options.DryRun);
    }

    private static string DescribeEstimated(BackupCandidate candidate)
        => candidate.CreatedAtIsEstimated ? "创建时间（估算）" : "迁移完成";

    /// <summary>
    /// 结构安全闸门：只接受"父目录叶名为 .migbak、且自身不是 .ledger / .migbak"的路径。
    /// 返回 null 表示通过；否则返回拒绝原因。
    /// </summary>
    internal static string? Refuse(string parkingPath)
    {
        var normalized = PathNormalizer.TrimTrailingSeparator(
            PathNormalizer.StripExtendedPrefix(PathNormalizer.NormalizeAbsolute(parkingPath)));

        var leaf = Path.GetFileName(normalized);
        if (string.IsNullOrEmpty(leaf))
            return "路径不是一个目录名，拒绝删除。";

        if (string.Equals(leaf, MigrationLedgerFactory.LedgerFolderName, StringComparison.OrdinalIgnoreCase))
            return "这是台账目录：台账长期保留、永不自动清理（用户明确要求）。";

        if (string.Equals(leaf, MigrationLedgerFactory.MigBakFolderName, StringComparison.OrdinalIgnoreCase))
            return "这是 .migbak 停车区容器本身，不是停车目录，拒绝删除。";

        var parent = Path.GetDirectoryName(normalized);
        var parentLeaf = string.IsNullOrEmpty(parent) ? null : Path.GetFileName(parent);

        if (!string.Equals(parentLeaf, MigrationLedgerFactory.MigBakFolderName, StringComparison.OrdinalIgnoreCase))
            return $"父目录不是 {MigrationLedgerFactory.MigBakFolderName}，拒绝删除（防止误删用户真实目录）。";

        return null;
    }

    private IEnumerable<BackupCandidate> FromMigrationRecords()
    {
        foreach (var record in _index.LoadAll())
        {
            if (string.IsNullOrWhiteSpace(record.BackupPath)) continue;
            if (!Directory.Exists(record.BackupPath)) continue;

            var createdAt = record.FinishedUtc ?? record.StartedUtc;
            yield return new BackupCandidate(
                record.BackupPath,
                createdAt,
                record.SourcePath,
                record.BatchId,
                record.EntryId,
                MeasureBytes(record.BackupPath),
                BackupCandidateOrigin.MigrationRecord,
                CreatedAtIsEstimated: record.FinishedUtc is null);
        }
    }

    private IEnumerable<BackupCandidate> FromDirectoryScan(string root, int maxDepth)
    {
        var normalized = PathNormalizer.TrimTrailingSeparator(PathNormalizer.NormalizeAbsolute(root));
        if (!Directory.Exists(normalized)) yield break;

        foreach (var migBak in FindMigBakDirectories(normalized, maxDepth))
        {
            foreach (var parked in Directory.EnumerateDirectories(migBak))
            {
                var name = Path.GetFileName(parked);
                if (string.Equals(name, MigrationLedgerFactory.LedgerFolderName, StringComparison.OrdinalIgnoreCase))
                    continue;   // 台账永不清理

                var (createdAt, estimated) = ResolveCreatedAt(parked, name);

                yield return new BackupCandidate(
                    parked,
                    createdAt,
                    SourcePath: string.Empty,
                    BatchId: null,
                    EntryId: null,
                    ApproxBytes: MeasureBytes(parked),
                    Origin: BackupCandidateOrigin.DirectoryScan,
                    CreatedAtIsEstimated: estimated);
            }
        }
    }

    private static IEnumerable<string> FindMigBakDirectories(string root, int maxDepth)
    {
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();

            var candidate = Path.Combine(current, MigrationLedgerFactory.MigBakFolderName);
            if (Directory.Exists(candidate)) yield return candidate;

            if (depth >= maxDepth) continue;

            foreach (var child in SafeEnumerateDirectories(current))
            {
                var name = Path.GetFileName(child);
                if (string.Equals(name, MigrationLedgerFactory.MigBakFolderName, StringComparison.OrdinalIgnoreCase)) continue;
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;   // 不穿越链接

                queue.Enqueue((child, depth + 1));
            }
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory).ToList();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>优先从停车目录名解析时间：<c>&lt;name&gt;_yyyyMMdd_HHmmss_xxxxxx</c>。</summary>
    internal static (DateTimeOffset CreatedAt, bool Estimated) ResolveCreatedAt(string parkingPath, string directoryName)
    {
        var parts = directoryName.Split('_');
        if (parts.Length >= 3)
        {
            var datePart = parts[^3];
            var timePart = parts[^2];

            if (DateTime.TryParseExact(
                    datePart + timePart,
                    "yyyyMMddHHmmss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsed))
            {
                var offset = TimeZoneInfo.Local.GetUtcOffset(parsed);
                return (new DateTimeOffset(parsed, offset), false);
            }
        }

        // 退化：用目录的写入时间（估算，UI 中必须标注）
        try
        {
            return (new DateTimeOffset(Directory.GetLastWriteTime(parkingPath)), true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DirPorter] 无法读取目录时间，按「现在」处理：{ex.Message}");
            return (DateTimeOffset.Now, true);
        }
    }

    internal static long MeasureBytes(string directory)
    {
        long total = 0;
        var stack = new Stack<string>();
        stack.Push(PathNormalizer.NormalizeAbsolute(directory));

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            var searchPath = PathNormalizer.EnsureTrailingSeparator(current) + "*";

            var handle = Kernel32.FindFirstFileEx(
                searchPath, Kernel32.FindExInfoBasic, out var data,
                Kernel32.FindExSearchNameMatch, IntPtr.Zero, Kernel32.FindFirstExLargeFetch);

            if (handle == Kernel32.InvalidHandleValue) continue;

            try
            {
                while (true)
                {
                    var name = Kernel32.GetFileName(data);
                    if (name is not ("." or ".."))
                    {
                        var attributes = data.dwFileAttributes;
                        var isDirectory = (attributes & Kernel32.FileAttributeDirectory) != 0;
                        var isReparse = (attributes & Kernel32.FileAttributeReparsePoint) != 0;

                        if (isDirectory)
                        {
                            if (!isReparse) stack.Push(PathNormalizer.Combine(current, name));
                        }
                        else if (!isReparse)
                        {
                            total += Kernel32.ToFileSize(data);
                        }
                    }

                    if (!Kernel32.FindNextFile(handle, out data)) break;
                }
            }
            finally
            {
                Kernel32.FindClose(handle);
            }
        }

        return total;
    }

    /// <summary>清理成功后把迁移记录里的备份路径置空，避免下次扫描重复命中。</summary>
    private void ClearRecordBackupPath(BackupCandidate candidate)
    {
        if (candidate.Origin != BackupCandidateOrigin.MigrationRecord) return;
        if (candidate.EntryId is null || candidate.BatchId is null) return;

        try
        {
            var records = _index.LoadAll().ToList();
            var id = $"{candidate.BatchId}-{candidate.EntryId}";
            var target = records.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
            if (target is null) return;

            _index.Upsert(target with { BackupPath = null });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DirPorter] 更新迁移记录（备份已清理）失败：{ex.Message}");
        }
    }
}
