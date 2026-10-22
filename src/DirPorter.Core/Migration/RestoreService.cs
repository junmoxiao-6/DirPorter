using DirPorter.Core.Diagnostics;
using DirPorter.Core.FileSystem;
using DirPorter.Core.Journal;
using DirPorter.Core.Ledger;
using DirPorter.Core.Links;

namespace DirPorter.Core.Migration;

/// <summary>单条还原结果。</summary>
public sealed record RestoreEntryResult(
    string EntryId,
    bool Success,
    string Message,
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> Failures,
    bool Skipped);

/// <summary>批次还原结果。</summary>
public sealed record RestoreResult(string BatchId, IReadOnlyList<RestoreEntryResult> Entries)
{
    public bool Success => Entries.All(static e => e.Success);

    public string Describe()
    {
        var ok = Entries.Count(static e => e.Success);
        var failed = Entries.Count(static e => !e.Success && !e.Skipped);
        var skipped = Entries.Count(static e => e.Skipped);
        return $"还原完成：成功 {ok} 条，失败 {failed} 条，跳过 {skipped} 条。";
    }
}

/// <summary>
/// 还原服务：**以台账为输入**（需求约束 5）。
///
/// 流程（每条目）：
/// <code>
/// 1. Inspect(linkPath) → 必须确证是链接；不是链接则跳过并告警（绝不删非链接）
/// 2. 删除链接本体（ReparsePointOps.RemoveLinkBody：只删链接、绝不穿透目标）
/// 3. MoveFileEx(parkingPath → sourcePath)，同卷原子改名
/// 4. 校验：sourcePath 是真实目录（非重解析点）、文件数与台账一致
/// 5. 目标侧数据默认保留；仅当显式要求才清理
/// 6. 台账追加 restore 记录（append-only，不覆盖原记录）
/// </code>
///
/// 硬链接场景按台账条目直接删 <c>linkPath</c>，**不读取 NumberOfLinks**
/// （R1 已实证该计数在"删除被路由到回收站"的系统上不可信）。
/// </summary>
public sealed class RestoreService
{
    private readonly LinkService _links;
    private readonly IJournalStore _journal;
    private readonly MigrationIndexStore _index;

    public RestoreService(LinkService? links = null, IJournalStore? journal = null, MigrationIndexStore? index = null)
    {
        _links = links ?? new LinkService(new Win32VolumeResolver(), Privilege.Win32PrivilegeProbe.Current);
        _journal = journal ?? new JsonJournalStore();
        _index = index ?? new MigrationIndexStore();
    }

    public RestoreResult Restore(
        string batchId,
        string ledgerDirectory,
        IReadOnlyList<string>? entryIds = null,
        bool cleanTargetAfterRestore = false,
        CancellationToken cancellationToken = default)
    {
        var ledger = MigrationLedgerFactory.Open(ledgerDirectory, batchId);
        var batch = ledger.Read();

        var targets = batch.Entries
            .Where(e => entryIds is null || entryIds.Count == 0 || entryIds.Contains(e.EntryId, StringComparer.OrdinalIgnoreCase))
            // 逆序：避免父子条目之间的顺序问题
            .OrderByDescending(static e => e.EntryId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (targets.Count == 0)
            return new RestoreResult(batchId, Array.Empty<RestoreEntryResult>());

        var results = new List<RestoreEntryResult>();

        foreach (var entry in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(RestoreOne(ledger, entry, cleanTargetAfterRestore));
        }

        return new RestoreResult(batchId, results);
    }

    /// <summary>还原单条目（供 GUI 单条还原按钮直接调用）。</summary>
    public RestoreEntryResult RestoreOne(IMigrationLedger ledger, LedgerEntry entry, bool cleanTargetAfterRestore = false)
    {
        var steps = new List<string>();
        var failures = new List<string>();

        var sourceExtended = PathNormalizer.NormalizeAbsolute(entry.SourcePath);
        var parkingExtended = PathNormalizer.NormalizeAbsolute(entry.ParkingPath);

        // 同卷快速移动的台账里 parkingPath == targetPath（数据从未被复制，只是被改名到目标）。
        // 于是"把 parkingPath 改回 sourcePath"这条既有逻辑**无需改动**就正好是正确的还原动作；
        // 唯独第 5 步"清理目标侧"必须跳过 —— 目标就是刚刚被搬回去的那份数据，
        // 一旦执行就成了"还原后顺手把用户数据删掉"。
        var sameVolumeMove = string.Equals(
            PathNormalizer.TrimTrailingSeparator(parkingExtended),
            PathNormalizer.TrimTrailingSeparator(PathNormalizer.NormalizeAbsolute(entry.TargetPath)),
            StringComparison.OrdinalIgnoreCase);

        // ---- 1. 确证是链接 ----
        var linkInfo = _links.Inspect(entry.SourcePath);

        if (!linkInfo.PathExists)
        {
            return new RestoreEntryResult(entry.EntryId, false,
                $"源路径不存在，无法还原：{entry.SourcePath}。若停车目录 {entry.ParkingPath} 仍在，请人工把它改回原名。",
                steps, new[] { "源路径不存在" }, Skipped: true);
        }

        if (!linkInfo.IsReparsePoint)
        {
            // 绝不删非链接 —— 这可能是用户自己重建的真实目录
            return new RestoreEntryResult(entry.EntryId, false,
                $"拒绝还原：{entry.SourcePath} 不是链接（可能是用户重建的真实目录）。为避免误删数据，已跳过。",
                steps, new[] { "源路径不是链接" }, Skipped: true);
        }

        // ---- 2. 删除链接本体（只删链接、绝不穿透）----
        try
        {
            ReparsePointOps.RemoveLinkBody(sourceExtended, linkInfo.IsDirectory, linkInfo.ReparseTag, "还原：删除链接本体");
            steps.Add("删除链接本体");
        }
        catch (Exception ex)
        {
            failures.Add($"删除链接本体失败：{ex.Message}");
            return new RestoreEntryResult(entry.EntryId, false, $"还原失败（删除链接本体）：{ex.Message}", steps, failures, false);
        }

        // ---- 3. 同卷原子改名把停车目录改回原名 ----
        if (!ReparsePointOps.Inspect(parkingExtended).PathExists)
        {
            return new RestoreEntryResult(entry.EntryId, false,
                $"停车目录不存在，无法还原：{entry.ParkingPath}（数据可能已被清理或被人工移动）",
                steps, new[] { "停车目录不存在" }, false);
        }

        if (!Kernel32.MoveFileEx(parkingExtended, sourceExtended, 0))
        {
            var code = Win32Error.Last;
            failures.Add($"改回原名失败：{Win32Error.Describe(code)}");
            return new RestoreEntryResult(entry.EntryId, false,
                $"还原失败（改回原名）：{Win32Error.Describe(code)}。链接已删除，原始数据仍在 {entry.ParkingPath}，请人工改名。",
                steps, failures, false);
        }

        steps.Add(sameVolumeMove ? "同卷改名：目标目录改回原路径" : "停车目录改回原名");

        // ---- 4. 校验：源是真实目录、文件数与台账一致 ----
        var restored = ReparsePointOps.Inspect(sourceExtended);
        if (!restored.PathExists || restored.IsReparsePoint)
        {
            failures.Add("还原后源路径不是真实目录");
        }
        else
        {
            try
            {
                var probe = FileSystem.TreeProbe.Probe(sourceExtended, new TreeProbeOptions(), null, CancellationToken.None);
                if (entry.FileCount > 0 && probe.FileCount != entry.FileCount)
                    failures.Add($"文件数不一致：台账 {entry.FileCount}，实际 {probe.FileCount}");
                else
                    steps.Add($"校验通过（{probe.FileCount} 个文件）");
            }
            catch (Exception ex)
            {
                failures.Add($"还原后校验失败：{ex.Message}");
            }
        }

        // ---- 5. 目标侧数据默认保留 ----
        // 同卷模式下目标路径已经不存在（它就是第 3 步改回去的那份数据），没有可清理的对象。
        if (cleanTargetAfterRestore && !sameVolumeMove)
        {
            try
            {
                var cleanResult = SafeTreeDeleter.DeleteTree(PathNormalizer.NormalizeAbsolute(entry.TargetPath), CancellationToken.None);
                steps.Add($"清理目标侧：{cleanResult.Describe()}");
            }
            catch (Exception ex)
            {
                failures.Add($"清理目标侧失败：{ex.Message}");
            }
        }

        // ---- 6. 台账追加 restore 记录（append-only）----
        try
        {
            ledger.AppendRestore(entry.EntryId, failures.Count == 0
                ? $"还原成功：{string.Join(" → ", steps)}"
                : $"还原存在问题：{string.Join("；", failures)}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DirPorter] 追加还原记录失败：{ex.Message}");
        }

        UpdateIndex(entry, failures.Count == 0);

        return new RestoreEntryResult(
            entry.EntryId,
            failures.Count == 0,
            failures.Count == 0 ? $"已还原：{entry.SourcePath}" : $"还原未完全成功：{string.Join("；", failures)}",
            steps, failures, false);
    }

    private void UpdateIndex(LedgerEntry entry, bool success)
    {
        try
        {
            _index.Save(_index.LoadAll()
                .Select(r => r.BatchId == entry.BatchId && r.EntryId == entry.EntryId
                    ? r with { Status = success ? "restored" : "restore-failed" }
                    : r)
                .ToList());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DirPorter] 更新迁移记录失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 按批次 ID 自动定位台账目录：先查迁移记录索引，再回落到"源目录父目录下的 .migbak\.ledger"。
    /// </summary>
    public string? LocateLedgerDirectory(string batchId)
    {
        var record = _index.LoadAll().FirstOrDefault(r => string.Equals(r.BatchId, batchId, StringComparison.OrdinalIgnoreCase) && r.LedgerPath is not null);
        if (record?.LedgerPath is not null && File.Exists(record.LedgerPath))
            return Path.GetDirectoryName(record.LedgerPath);

        var source = _index.LoadAll().FirstOrDefault(r => string.Equals(r.BatchId, batchId, StringComparison.OrdinalIgnoreCase))?.SourcePath;
        if (source is null) return null;

        var candidate = MigrationLedgerFactory.ResolveLedgerDirectory(source);
        return Directory.Exists(candidate) ? candidate : null;
    }
}
