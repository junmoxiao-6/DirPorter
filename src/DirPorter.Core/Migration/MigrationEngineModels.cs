using DirPorter.Core.Config;
using DirPorter.Core.Copy;
using DirPorter.Core.Diagnostics;
using DirPorter.Core.FileSystem;
using DirPorter.Core.Journal;
using DirPorter.Core.Ledger;
using DirPorter.Core.Links;
using DirPorter.Core.Precheck;
using DirPorter.Core.RestartManager;

namespace DirPorter.Core.Migration;

/// <summary>复制引擎选择。</summary>
public enum CopyEngineKind
{
    /// <summary>自动：使用默认注入的引擎（当前为 robocopy）。</summary>
    Auto = 0,

    /// <summary>引擎 A：系统 robocopy 子进程。</summary>
    Robocopy = 1,

    /// <summary>引擎 B：自研 CopyFileEx（支持真暂停、字节级进度）。</summary>
    Native = 2,
}

/// <summary>备份清理策略。</summary>
public enum BackupCleanupPolicy
{
    /// <summary>完成后立即删除停车目录（磁盘立即释放，但失去回滚兜底）。</summary>
    Immediate = 0,

    /// <summary>默认：保留 N 天，由 BackupRetentionService 到期清理。</summary>
    KeepDays = 1,

    /// <summary>永不自动删除。</summary>
    Never = 2,
}

/// <summary>任务级选项。</summary>
public sealed record MigrationTaskOptions
{
    /// <summary>干跑：只做 PRECHECK + 预览，**零磁盘写入**。</summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// 同卷时用「直接改名」代替「复制 + 校验 + 停车」——与资源管理器剪切同卷文件的行为一致。
    ///
    /// 为什么更好：改名是**原子且瞬时**的，且 ACL / 所有者 / ADS / 稀疏 / EFS / 时间戳**全部天然保留**
    /// （比任何复制引擎都保真，因为根本没有第二份数据）。
    ///
    /// 代价必须说清楚：**不产生备份**（没有停车目录）。回滚靠"把目标改名回源路径"，
    /// 依然是原子操作。需要"保留一份备份"的场景请设 false 强制走复制路径。
    /// </summary>
    public bool SameVolumeMove { get; init; } = true;

    public CopyEngineKind CopyEngine { get; init; } = CopyEngineKind.Auto;

    public int CopyThreads { get; init; } = 8;

    /// <summary>是否搬运安全信息（DACL/所有者/审计）。关闭后目标将继承父目录的 ACL。</summary>
    public bool CopySecurity { get; init; } = true;

    /// <summary>是否对稀疏文件补做稀疏优化（保持磁盘占用不膨胀）。</summary>
    public bool ReplicateSparse { get; init; } = true;

    /// <summary>是否允许解密复制 EFS 加密文件（默认拒绝：静默解密是安全降级）。</summary>
    public bool AllowDecryptEncryptedFiles { get; init; }

    /// <summary>用户已勾选"我理解风险"。</summary>
    public bool AcknowledgeWarnings { get; init; }

    public bool ReuseEmptyTargetDirectory { get; init; }

    /// <summary>默认保留备份（D5 决策：回滚 SLA 优先）。</summary>
    public BackupCleanupPolicy BackupPolicy { get; init; } = BackupCleanupPolicy.KeepDays;

    public int BackupRetentionDays { get; init; } = 7;

    /// <summary>台账归档方式。</summary>
    public LedgerArchiveMode LedgerArchive { get; init; } = LedgerArchiveMode.AlongsideBackup;

    /// <summary>用户指定归档目录（<see cref="LedgerArchiveMode.UserSpecified"/> 时生效）。</summary>
    public string? LedgerArchivePath { get; init; }

    /// <summary>校验档位（用户决策：默认 metadata，content 可选）。</summary>
    public VerifyMode Verify { get; init; } = VerifyMode.Metadata;

    /// <summary>是否结束占用进程（默认 false；勾选后逐个二次确认）。</summary>
    public bool KillLockingProcesses { get; init; }

    /// <summary>深度占用检测（逐文件注册到 Restart Manager，慢但覆盖全）。</summary>
    public bool DeepOccupancyScan { get; init; }

    /// <summary>预检的昂贵探测上限。</summary>
    public int DeepProbeFileLimit { get; init; } = 20_000;

    /// <summary>
    /// **测试专用钩子**：执行到指定状态时立刻强杀自身进程（模拟断电/被杀）。
    /// 只有在环境变量 <c>DIRPORTER_ALLOW_KILL_HOOK=1</c> 时才生效，避免误伤真实用户。
    /// </summary>
    public string? KillAfterState { get; init; }

    /// <summary>
    /// **测试专用钩子**：在指定状态的正向动作之前强制抛异常，用于验证回滚路径。
    /// 不对 CLI 暴露；只影响单次迁移，失败即回滚，不会造成数据丢失。
    /// </summary>
    public string? FailBeforeState { get; init; }

    public static MigrationTaskOptions Default { get; } = new();
}

/// <summary>台账归档方式。</summary>
public enum LedgerArchiveMode
{
    /// <summary>默认：随备份保留位置（即 `.migbak\.ledger` 就地保留）。</summary>
    AlongsideBackup = 0,

    /// <summary>归档到目标侧。</summary>
    TargetSide = 1,

    /// <summary>归档到用户指定目录。</summary>
    UserSpecified = 2,
}

/// <summary>进度事件（UI 只消费这个，节流由引擎保证 ≤10 次/秒）。</summary>
public sealed record MigrationProgress(
    string EntryId,
    MigrationState State,
    string Message,
    double Percent,
    long BytesDone,
    long BytesTotal,
    double BytesPerSecond);

/// <summary>单条目结果。</summary>
public sealed record MigrationEntryResult(
    string EntryId,
    MigrationState FinalState,
    bool Success,
    string Message,
    string? Error,
    IReadOnlyList<string> RollbackSteps,
    IReadOnlyList<string> RollbackFailures,
    string? LedgerDirectory,
    string? BatchId)
{
    public bool RolledBack => RollbackSteps.Count > 0;
}

/// <summary>批次结果。</summary>
public sealed record MigrationBatchResult(
    string BatchId,
    PrecheckReport Precheck,
    IReadOnlyList<MigrationEntryResult> Entries,
    bool DryRun,
    string? LedgerDirectory,
    TimeSpan Elapsed)
{
    public bool Success => Entries.All(static e => e.Success);
    public bool AnyDiskChange => Entries.Any(static e => e.FinalState is MigrationState.Done or MigrationState.BackupCleanup);
}

/// <summary>回滚动作的可靠程度。</summary>
public enum RollbackReliability
{
    /// <summary>精确还原（同卷改名、删链接本体等）。</summary>
    Exact = 0,

    /// <summary>尽力而为，可能留下无害残留（例如空目录壳）。</summary>
    BestEffort = 1,
}

/// <summary>已登记的回滚动作。</summary>
public sealed record RollbackActionRecord(string Step, string Description, RollbackReliability Reliability);

/// <summary>回滚执行结果。</summary>
public sealed record RollbackOutcome(bool Success, IReadOnlyList<string> Steps, IReadOnlyList<string> Failures, FailedEntryError? Error);

/// <summary>回滚失败时的伴随异常信息（若有）。</summary>
public sealed record FailedEntryError(string Step, string Message, int? Win32Code);

/// <summary>
/// 回滚计划：**每个改变磁盘的步骤都必须先在此登记回滚动作**。
///
/// 设计意图（需求「迁移状态机」）：任一步失败都必须回到"与开始前完全一致"的状态，
/// 因此正向动作与回滚动作成对出现，且回滚按 LIFO 逆序执行
/// （后做的先撤销：先删链接 → 再把源改回来 → 再清目标残留）。
///
/// 回滚执行期间**忽略取消信号**：用户取消的是"继续前进"，不是"不要收拾现场"。
/// </summary>
public sealed class RollbackPlan
{
    private readonly List<(RollbackActionRecord Record, Func<CancellationToken, Task> Action)> _actions = new();

    public IReadOnlyList<RollbackActionRecord> Records => _actions.Select(static a => a.Record).ToList();

    public bool IsEmpty => _actions.Count == 0;

    /// <summary>登记一个回滚动作。**必须在正向动作成功之后立即调用**。</summary>
    public void Register(string step, string description, Func<CancellationToken, Task> action, RollbackReliability reliability = RollbackReliability.Exact)
        => _actions.Add((new RollbackActionRecord(step, description, reliability), action));

    public async Task<RollbackOutcome> ExecuteAsync(IProgress<string>? log = null)
    {
        var steps = new List<string>();
        var failures = new List<string>();
        FailedEntryError? firstError = null;

        // 逆序：后做的先撤销
        for (var i = _actions.Count - 1; i >= 0; i--)
        {
            var (record, action) = _actions[i];
            try
            {
                // ⚠️ 回滚不接受取消：CancellationToken.None
                await action(CancellationToken.None).ConfigureAwait(false);
                steps.Add(record.Description);
                log?.Report($"已回滚：{record.Description}");
            }
            catch (Exception ex)
            {
                var message = $"{record.Step} 的回滚失败：{ex.Message}";
                failures.Add(message);
                firstError ??= new FailedEntryError(record.Step, ex.Message, ex is DirPorterNativeException native ? native.Win32Code : null);
                log?.Report(message);
            }
        }

        return new RollbackOutcome(failures.Count == 0, steps, failures, firstError);
    }
}

/// <summary>单个条目的执行上下文（步骤之间共享状态）。</summary>
public sealed class MigrationContext
{
    public required ResolvedEntry Entry { get; init; }
    public required TreeProbeResult Probe { get; init; }
    public required MigrationTaskOptions Options { get; init; }

    /// <summary>本条目的实际传输方式是否走了"同卷直接改名"（由引擎在开始时判定）。</summary>
    public bool SameVolumeMove { get; set; }
    public required IMigrationLedger Ledger { get; init; }
    public required string VolumeSource { get; init; }
    public required string VolumeTarget { get; init; }

    public RollbackPlan Rollback { get; } = new();

    /// <summary>Journal 记录（每步更新；进程被杀后靠它恢复）。</summary>
    public MigrationRunRecord Journal { get; set; } = null!;

    public IProgress<MigrationProgress>? Progress { get; init; }

    public CopyResult? CopyResult { get; set; }
    public VerifyResult? VerifyResult { get; set; }
    public LinkInfo? LinkInfo { get; set; }
    public OccupancyReport Occupancy { get; set; } = OccupancyReport.Empty;

    /// <summary>停车路径（`.migbak\<name>_<suffix>`）。</summary>
    public string ParkingPath { get; set; } = string.Empty;

    /// <summary>台账归档后的实际位置（用于报告）。</summary>
    public string? ArchiveDirectory { get; set; }
}
