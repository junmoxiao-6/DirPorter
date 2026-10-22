using DirPorter.Core.FileSystem;
using DirPorter.Core.Migration;
using DirPorter.Core.Links;

namespace DirPorter.Core.Journal;

/// <summary>恢复动作类型。全部为**建议**，绝不自动执行。</summary>
public enum RecoveryAction
{
    /// <summary>无需处理（任务已正常结束）。</summary>
    None,

    /// <summary>把停车目录改回原始路径（源路径为空、`.migbak` 存在）。</summary>
    RenameParkingBack,

    /// <summary>删除链接本体后把停车目录改回原始路径。</summary>
    DeleteLinkThenRenameBack,

    /// <summary>确认新位置数据完整后删除停车目录（完成收尾）。</summary>
    FinalizeByDeletingParking,

    /// <summary>清理目标侧的半成品残留。</summary>
    CleanTargetResidue,

    /// <summary>补写完成记录（数据已就位，仅记录缺失）。</summary>
    FinalizeRecord,

    /// <summary>需要人工检查（状态无法自动判定，或涉及混合状态）。</summary>
    ManualInspection,
}

/// <summary>一条恢复建议。</summary>
public sealed record RecoverySuggestion(
    string TaskId,
    string EntryId,
    MigrationState ObservedState,
    RecoveryAction Action,
    string Summary,
    string Detail,
    bool IsDestructive,
    IReadOnlyList<string> Evidence);

/// <summary>恢复分析结果。</summary>
public sealed record RecoveryAnalysis(
    IReadOnlyList<RecoverySuggestion> Suggestions,
    IReadOnlyList<MigrationRunRecord> HealthyRecords)
{
    public int NeedsAttentionCount => Suggestions.Count(s => s.Action != RecoveryAction.None);
}

/// <summary>
/// 中间态恢复分析（需求 96 行：进程被强杀后重启能识别中间态并给出修复建议）。
///
/// 判定原则：**只看磁盘实证**，不看 journal 里写的状态。
/// journal 可能落后于磁盘（写在步骤之后），磁盘不会骗人。
/// </summary>
public static class JournalRecovery
{
    public static RecoveryAnalysis Analyze(IEnumerable<MigrationRunRecord> records)
    {
        var suggestions = new List<RecoverySuggestion>();
        var healthy = new List<MigrationRunRecord>();

        foreach (var record in records)
        {
            var suggestion = AnalyzeOne(record);

            // Journal 停在中间态、即便磁盘无风险，也必须如实报告（用户需要知道"上次没跑完"）。
            // 只有"终态 + 无需动作"才算健康记录。
            var stale = record.State.IsIntermediate();
            if (suggestion.Action == RecoveryAction.None && !stale) healthy.Add(record);
            else suggestions.Add(suggestion);
        }

        return new RecoveryAnalysis(suggestions, healthy);
    }

    /// <summary>扫描 Journal 目录并分析。</summary>
    public static RecoveryAnalysis Scan(IJournalStore? store = null)
        => Analyze((store ?? new JsonJournalStore()).LoadAll());

    private static readonly Lazy<LinkService> LinkInspector =
        new(() => new LinkService(new Win32VolumeResolver(), Privilege.Win32PrivilegeProbe.Current), LazyThreadSafetyMode.ExecutionAndPublication);

    public static RecoverySuggestion AnalyzeOne(MigrationRunRecord record)
    {
        var source = SafeNormalize(record.SourcePath);
        var parking = record.ParkingPath is null ? null : SafeNormalize(record.ParkingPath);
        var target = SafeNormalize(record.TargetPath);

        var evidence = new List<string>();

        var sourceInfo = source is null ? default : ReparsePointOps.Inspect(source);
        var sourceExists = sourceInfo.PathExists;
        var sourceIsLink = sourceInfo.IsReparsePoint;
        var parkingExists = parking is not null && ReparsePointOps.Inspect(parking).PathExists;
        var targetExists = target is not null && Directory.Exists(target);
        var targetHasContent = targetExists && Directory.EnumerateFileSystemEntries(target!).Any();

        var linkTarget = sourceIsLink && source is not null ? LinkInspector.Value.Inspect(source).TargetAbsolute : null;
        var linkHealthy = !string.IsNullOrWhiteSpace(linkTarget)
                          && Directory.Exists(PathNormalizer.StripExtendedPrefix(linkTarget!));

        evidence.Add($"源路径 {(sourceExists ? "存在" : "不存在")}{(sourceIsLink ? "（是链接）" : string.Empty)}");
        evidence.Add($"停车目录 {(parkingExists ? "存在" : "不存在")}");
        evidence.Add($"目标目录 {(targetExists ? (targetHasContent ? "存在且非空" : "存在但为空") : "不存在")}");
        evidence.Add($"Journal 记录状态：{record.State.Describe()}");

        // ---------- 终态优先：记录说已经结束，就只看"磁盘是否与记录一致" ----------
        // 关键：迁移**成功**后停车目录按保留策略（默认保留 N 天）继续存在是**设计行为**，
        // 绝不能把它报成"待处理"，否则用户每次迁移完都会看到一条误导性的待办。
        switch (record.State)
        {
            case MigrationState.Done:
                return linkHealthy
                    ? new RecoverySuggestion(
                        record.TaskId, record.EntryId, record.State, RecoveryAction.None,
                        "迁移已完成，链接健康",
                        $"链接指向 {linkTarget}，可正常访问。"
                        + (parkingExists
                            ? $"停车目录 {record.ParkingPath} 按备份保留策略继续保留，属正常行为（到期由备份保留策略清理，或在记录页手动清理）。"
                            : string.Empty),
                        IsDestructive: false, Evidence: evidence)
                    : new RecoverySuggestion(
                        record.TaskId, record.EntryId, record.State, RecoveryAction.ManualInspection,
                        "记录显示已完成，但链接不可用",
                        $"链接目标 {linkTarget ?? "(无法解析)"} 不可访问，或源路径已不是链接。"
                        + (parkingExists ? $"停车目录 {record.ParkingPath} 仍在，可作为兜底数据源。" : "且没有停车目录，请人工确认数据位置。"),
                        IsDestructive: false, Evidence: evidence);

            case MigrationState.RolledBack:
                return sourceExists && !sourceIsLink
                    ? new RecoverySuggestion(
                        record.TaskId, record.EntryId, record.State, RecoveryAction.None,
                        "已回滚，源目录完好",
                        "回滚已完成：源路径是真实目录，无需处理。",
                        IsDestructive: false, Evidence: evidence)
                    : new RecoverySuggestion(
                        record.TaskId, record.EntryId, record.State, RecoveryAction.ManualInspection,
                        "记录显示已回滚，但源路径状态异常",
                        "请人工确认源路径与停车目录的实际内容后再做处理，工具不会自动改动数据。",
                        IsDestructive: false, Evidence: evidence);

            case MigrationState.Failed:
                return new RecoverySuggestion(
                    record.TaskId, record.EntryId, record.State, RecoveryAction.ManualInspection,
                    "回滚未完全成功，需要人工介入",
                    $"回滚失败的步骤：{(record.RollbackFailures.Count > 0 ? string.Join("；", record.RollbackFailures) : "(未记录)")}。"
                    + "请先人工确认源目录与目标目录的现状，再决定后续动作。",
                    IsDestructive: false, Evidence: evidence);
        }

        // ---------- 中间态：按磁盘实证判定 ----------
        // 情况 1：源是链接 + 停车目录存在 → 迁移实际已完成到 LINK 之后，只是没收尾
        if (sourceIsLink && parkingExists)
        {
            return new RecoverySuggestion(
                record.TaskId, record.EntryId, record.State,
                linkHealthy ? RecoveryAction.FinalizeByDeletingParking : RecoveryAction.ManualInspection,
                linkHealthy ? "迁移已完成，仅剩停车目录未清理" : "源路径是链接，但链接目标不可用",
                linkHealthy
                    ? $"链接已生效（指向 {linkTarget}），停车目录 {record.ParkingPath} 仍占用空间。确认新位置数据无误后，可删除停车目录完成收尾。"
                    : $"链接指向 {linkTarget ?? "(无法解析)"}，但该目标不可访问。请不要删除停车目录，先人工确认新位置的数据是否还在。",
                IsDestructive: linkHealthy,
                Evidence: evidence);
        }

        // 情况 2：源不存在 + 停车目录存在 → 停在 PARKING 之后、LINKING 之前（或建链失败并被杀）
        if (!sourceExists && parkingExists)
        {
            return new RecoverySuggestion(
                record.TaskId, record.EntryId, record.State,
                RecoveryAction.RenameParkingBack,
                "源路径为空、停车目录还在：迁移中断在建链之前",
                $"把 {record.ParkingPath} 改名回 {record.SourcePath} 即可完整还原（同卷重命名，毫秒级、零数据风险）。",
                IsDestructive: false,
                Evidence: evidence);
        }

        // 情况 3：源完好（真实目录）+ 目标有残留 → 停在 COPYING / VERIFYING
        if (sourceExists && !sourceIsLink && targetHasContent)
        {
            return new RecoverySuggestion(
                record.TaskId, record.EntryId, record.State,
                RecoveryAction.CleanTargetResidue,
                "源目录完好，目标侧有半成品残留",
                $"源目录一个字节都没少；目标目录 {record.TargetPath} 内是本任务复制到一半的内容。确认不需要后可用「清理目标残留」删除（只删本任务新建的目录，遇链接不穿透）。",
                IsDestructive: true,
                Evidence: evidence);
        }

        // 情况 4：源完好 + 目标为空/不存在 → 停在 TargetPrepare 或之前
        if (sourceExists && !sourceIsLink && !targetHasContent)
        {
            return new RecoverySuggestion(
                record.TaskId, record.EntryId, record.State,
                RecoveryAction.None,
                "无残留风险",
                "源目录完好，目标侧没有内容，无需处理（可直接重新发起迁移）。",
                IsDestructive: false,
                Evidence: evidence);
        }

        // 情况 5：源是链接 + 无停车目录 → 已完成，journal 未收尾
        if (sourceIsLink && !parkingExists)
        {
            return new RecoverySuggestion(
                record.TaskId, record.EntryId, record.State,
                RecoveryAction.FinalizeRecord,
                "迁移已完成，仅记录未收尾",
                "链接已生效且没有停车目录残留，属于「记录未写完」，补写迁移记录即可，无需触碰数据。",
                IsDestructive: false,
                Evidence: evidence);
        }

        // 情况 6：源不存在 + 无停车目录 + 目标有内容 → 数据可能只在目标侧
        if (!sourceExists && !parkingExists && targetHasContent)
        {
            return new RecoverySuggestion(
                record.TaskId, record.EntryId, record.State,
                RecoveryAction.ManualInspection,
                "源与停车目录都不存在，但目标侧有数据",
                $"数据可能只存在于 {record.TargetPath}。请人工确认后，决定是重建链接还是把数据搬回原位。",
                IsDestructive: false,
                Evidence: evidence);
        }

        // 兜底：源、停车目录都不存在 → 无法自动判定
        return new RecoverySuggestion(
            record.TaskId, record.EntryId, record.State,
            RecoveryAction.ManualInspection,
            "状态无法自动判定，需要人工检查",
            $"源 {record.SourcePath} 与停车目录 {record.ParkingPath ?? "(未记录)"} 都不存在。请人工确认数据是否仍在 {record.TargetPath}，再决定后续动作。",
            IsDestructive: false,
            Evidence: evidence);
    }

    private static string? SafeNormalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return PathNormalizer.NormalizeAbsolute(path);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
