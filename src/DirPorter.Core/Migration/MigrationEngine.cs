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

/// <summary>
/// 迁移状态机引擎。
///
/// 执行顺序（与需求严格一致）：
/// PRECHECK →(DRY_RUN 出口)→ OCCUPANCY_SCAN → TARGET_PREPARE → COPYING → VERIFYING
/// → PARKING → LINKING → LINK_VERIFY →（LEDGER_COMMIT）→ BACKUP_CLEANUP → DONE
///
/// 编码顺序遵循"先回滚动作、再正向动作"：每个改变磁盘的步骤都**先登记回滚**，
/// 再执行正向动作。这样即使正向动作执行到一半被杀，回滚动作也已经登记在案。
///
/// ⚠️ 磁盘写入只发生在 TARGET_PREPARE 及之后。PRECHECK 与 DRY_RUN 保证**零写入**。
/// </summary>
public sealed class MigrationEngine
{
    private readonly PrecheckService _precheck;
    private readonly IFileCopier _copier;
    private readonly IFileLockService _lockService;
    private readonly IJournalStore _journal;
    private readonly MigrationIndexStore _index;
    private readonly LinkService _links;
    private readonly IVolumeResolver _volumes;

    /// <summary>引擎 B（自研）实例：按需创建，因为它的能力探测会触碰进程令牌。</summary>
    private CopyFileExCopier? _nativeCopier;

    /// <summary>
    /// 当前可用的暂停闸门（仅当使用引擎 B 时非 null）。
    /// UI/CLI 通过它实现"暂停/继续"；引擎 A 不支持暂停（只能终止并重启），故此处可能为 null。
    /// </summary>
    public PauseGate? PauseGate => _nativeCopier?.Pause;

    /// <summary>按选项解析实际使用的复制引擎。</summary>
    private IFileCopier ResolveCopier(CopyEngineKind kind)
        => kind switch
        {
            CopyEngineKind.Native => _nativeCopier ??= new CopyFileExCopier(),
            _ => _copier,
        };

    public MigrationEngine(
        PrecheckService? precheck = null,
        IFileCopier? copier = null,
        IFileLockService? lockService = null,
        IJournalStore? journal = null,
        MigrationIndexStore? index = null)
    {
        _volumes = new Win32VolumeResolver();
        _precheck = precheck ?? new PrecheckService(_volumes);
        _copier = copier ?? new RobocopyCopier();
        _lockService = lockService ?? new RestartManagerLockService();
        _journal = journal ?? new JsonJournalStore();
        _index = index ?? new MigrationIndexStore();
        _links = new LinkService(_volumes, Privilege.Win32PrivilegeProbe.Current);
    }

    public async Task<MigrationBatchResult> RunBatchAsync(
        MigrationPlan plan,
        MigrationTaskOptions? options = null,
        IProgress<MigrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= MigrationTaskOptions.Default;
        var started = System.Diagnostics.Stopwatch.StartNew();
        var batchId = MigrationLedgerFactory.CreateBatchId();

        // ---------- 阶段 1：PRECHECK（零磁盘写入）----------
        var precheckOptions = new PrecheckOptions
        {
            AcknowledgeWarnings = options.AcknowledgeWarnings,
            ReuseEmptyTargetDirectory = options.ReuseEmptyTargetDirectory,
            DeepProbeFileLimit = options.DeepProbeFileLimit,
        };

        var report = await _precheck
            .RunAsync(plan with { Options = precheckOptions }, null, cancellationToken)
            .ConfigureAwait(false);

        if (report.IsBlocked)
        {
            // 硬拒绝：到此为止，磁盘一个字节都没动。
            return new MigrationBatchResult(batchId, report, Array.Empty<MigrationEntryResult>(), false, null, started.Elapsed);
        }

        // ---------- 阶段 2：DRY_RUN 出口（零磁盘写入）----------
        if (options.DryRun)
        {
            return new MigrationBatchResult(batchId, report, Array.Empty<MigrationEntryResult>(), true, null, started.Elapsed);
        }

        if (report.EffectiveEntries.Count == 0)
        {
            return new MigrationBatchResult(batchId, report, Array.Empty<MigrationEntryResult>(), false, null, started.Elapsed);
        }

        // ---------- 阶段 3：台账（第一个有效条目的 `.migbak\.ledger`）----------
        var ledgerDirectory = MigrationLedgerFactory.ResolveLedgerDirectory(report.EffectiveEntries[0].SourceExtendedPath);
        var ledger = MigrationLedgerFactory.Create(ledgerDirectory, report.EffectiveEntries.Count, batchId);

        var results = new List<MigrationEntryResult>();

        foreach (var entry in report.EffectiveEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var probe = report.Probes.TryGetValue(entry.SourcePlainPath, out var probed)
                ? probed
                : TreeProbe.Probe(entry.SourceExtendedPath, new TreeProbeOptions { DeepProbeFileLimit = options.DeepProbeFileLimit }, null, cancellationToken);

            var result = await RunEntryAsync(entry, probe, report, ledger, options, progress, cancellationToken).ConfigureAwait(false);
            results.Add(result);
        }

        // ---------- 阶段 4：批次收尾（嵌套快照 + Markdown 摘要 + 归档）----------
        try
        {
            ledger.CompleteBatch(ResolveArchiveDirectory(options, ledgerDirectory, report, batchId));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DirPorter] 台账收尾失败（不影响已完成的数据迁移）：{ex.Message}");
        }

        return new MigrationBatchResult(batchId, report, results, false, ledgerDirectory, started.Elapsed);
    }

    /// <summary>执行单个条目的完整状态机。</summary>
    public async Task<MigrationEntryResult> RunEntryAsync(
        ResolvedEntry entry,
        TreeProbeResult probe,
        PrecheckReport report,
        IMigrationLedger ledger,
        MigrationTaskOptions options,
        IProgress<MigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sourcePlain = entry.SourcePlainPath;
        var targetPlain = entry.TargetPlainPath;

        var context = new MigrationContext
        {
            Entry = entry,
            Probe = probe,
            Options = options,
            Ledger = ledger,
            VolumeSource = PathNormalizer.TryGetVolumeRoot(sourcePlain) ?? string.Empty,
            VolumeTarget = PathNormalizer.TryGetVolumeRoot(targetPlain) ?? string.Empty,
            Progress = progress,
            ParkingPath = MigrationLedgerFactory.ResolveParkingPath(entry.SourceExtendedPath, ledger.BatchId),
        };

        // ---------- 传输方式判定（必须在步骤表之前定下来）----------
        // 三个条件同时成立才走"改名"：① 选项允许；② 源与目标同卷；③ 目标**不存在**。
        // 第 ③ 条很关键：改名要求目标路径不存在；若目标是一个已存在的空目录，
        // 我们宁可退回复制路径，也不去"先删空目录再改名"——少一个边界就少一类数据事故。
        context.SameVolumeMove = options.SameVolumeMove
                                 && IsSameVolume(entry.SourcePlainPath, entry.TargetPlainPath)
                                 && !ReparsePointOps.Inspect(entry.TargetExtendedPath).PathExists;

        context.Journal = new MigrationRunRecord
        {
            TaskId = $"{ledger.BatchId}-{entry.EntryId}",
            BatchId = ledger.BatchId,
            EntryId = entry.EntryId,
            SourcePath = sourcePlain,
            TargetPath = targetPlain,
            LinkPath = sourcePlain,
            LinkKind = entry.LinkKind.ToString(),
            ParkingPath = context.ParkingPath,
            State = MigrationState.Planned,
            StartedUtc = DateTimeOffset.UtcNow,
            AppVersion = typeof(MigrationEngine).Assembly.GetName().Version?.ToString(),
        };
        SaveJournal(context);

        // 状态序列（PRECHECK 已在批次级完成，此处从 OCCUPANCY_SCAN 开始）
        var steps = new List<(MigrationState State, string Name, Func<MigrationContext, CancellationToken, Task> Action)>
        {
            (MigrationState.OccupancyScan, "OCCUPANCY_SCAN", RunOccupancyScanAsync),
            (MigrationState.TargetPrepare, "TARGET_PREPARE", RunTargetPrepareAsync),
        };

        if (context.SameVolumeMove)
        {
            // 同卷：改名到目标。没有 COPYING、没有 PARKING、没有 BACKUP_CLEANUP
            // （没有第二份数据，也就没有"停车目录"和"备份清理"这两件事）。
            steps.Add((MigrationState.MovingToTarget, "MOVE_TO_TARGET", RunSameVolumeMoveAsync));
        }
        else
        {
            steps.Add((MigrationState.Copying, "COPYING", RunCopyAsync));
        }

        steps.Add((MigrationState.Verifying, "VERIFYING", RunVerifyAsync));

        if (!context.SameVolumeMove)
        {
            steps.Add((MigrationState.Parking, "PARKING", RunParkAsync));
        }

        // R12.4：纯迁移（CreateLink=false）跳过建链与链接校验 —— 状态机：… → VERIFYING → DONE。
        if (context.Entry.CreateLink)
        {
            steps.Add((MigrationState.Linking, "LINKING", RunLinkAsync));
            steps.Add((MigrationState.LinkVerify, "LINK_VERIFY", RunLinkVerifyAsync));
        }

        steps.Add((MigrationState.LedgerCommit, "LEDGER_COMMIT", RunLedgerCommitAsync));

        if (!context.SameVolumeMove)
        {
            steps.Add((MigrationState.BackupCleanup, "BACKUP_CLEANUP", RunBackupCleanupAsync));
        }

        try
        {
            foreach (var (state, name, action) in steps)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 先记录状态：进程若被杀，Journal 会停在这个中间态（恢复逻辑据此判定磁盘现状）
                UpdateState(context, state);
                ApplyKillHook(options.KillAfterState, state);
                ApplyFailureHook(options.FailBeforeState, state);

                Report(progress, context, state, $"{state.Describe()}…", 0);

                await action(context, cancellationToken).ConfigureAwait(false);

                context.Journal = context.Journal with
                {
                    LastCompletedStep = name,
                    CompletedSteps = context.Journal.CompletedSteps.Concat(new[] { name }).ToList(),
                };
                SaveJournal(context);
                ledger.AppendEvent(entry.EntryId, name, "ok");
            }

            UpdateState(context, MigrationState.Done);
            SaveJournal(context);
            WriteIndexRecord(context, report, "done", null, ledger);

            return new MigrationEntryResult(
                entry.EntryId, MigrationState.Done, true,
                context.SameVolumeMove
                    ? $"迁移完成（同卷直接改名，未产生备份）：{sourcePlain} → {targetPlain}{(entry.CreateLink ? $"（链接 {entry.LinkKind}）" : "（未建立链接）")}"
                    : $"迁移完成：{sourcePlain} → {targetPlain}{(entry.CreateLink ? $"（链接 {entry.LinkKind}）" : "（未建立链接）")}",
                null, Array.Empty<string>(), Array.Empty<string>(), ledger.LedgerDirectory, ledger.BatchId);
        }
        catch (OperationCanceledException)
        {
            // 取消 = 走完整回滚（需求：取消必须走完整回滚）
            var outcome = await RollbackAsync(context, ledger, "用户取消", progress).ConfigureAwait(false);
            WriteIndexRecord(context, report, "rolled_back", "用户取消", ledger);

            return new MigrationEntryResult(
                entry.EntryId, outcome.Success ? MigrationState.RolledBack : MigrationState.Failed,
                false, "已取消并回滚", "用户取消",
                outcome.Steps, outcome.Failures, ledger.LedgerDirectory, ledger.BatchId);
        }
        catch (Exception ex)
        {
            var outcome = await RollbackAsync(context, ledger, ex.Message, progress).ConfigureAwait(false);
            WriteIndexRecord(context, report, outcome.Success ? "rolled_back" : "failed", ex.Message, ledger);

            var message = outcome.Success
                ? $"执行失败，已完整回滚：{ex.Message}"
                : $"执行失败，回滚未完全成功（需人工检查）：{ex.Message}";

            return new MigrationEntryResult(
                entry.EntryId,
                outcome.Success ? MigrationState.RolledBack : MigrationState.Failed,
                false, message, ex.Message, outcome.Steps, outcome.Failures,
                ledger.LedgerDirectory, ledger.BatchId);
        }
    }

    // ==================================================================
    //  步骤实现：每个步骤的注释都写明「正向动作」与「失败时的回滚动作」
    // ==================================================================

    /// <summary>
    /// OCCUPANCY_SCAN：枚举占用源目录内文件的进程。
    /// 正向动作：只读查询（Restart Manager）。
    /// 失败时的回滚动作：**无**（本步不改变磁盘，因此不需要回滚）。
    ///
    /// R12.4 变更：检出占用且未启用「结束占用进程」时**立即失败**（SourceOccupiedException）。
    /// 旧行为是"记录在案、交由 VERIFYING 判定"，但实测 COPYING/PARKING 会先炸（Win32 32/5），
    /// 用户只拿到错误码、没有任何行动指引；提前拦截既省时间又给出"关掉哪个程序"的明确信息。
    /// </summary>
    private async Task RunOccupancyScanAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        await Task.Yield();

        var service = context.Options.DeepOccupancyScan
            ? new RestartManagerLockService { RegisterEveryFile = true }
            : _lockService;

        context.Occupancy = service.FindLockingProcesses(new[] { context.Entry.SourcePlainPath });

        if (!context.Occupancy.HasOccupancy) return;

        context.Ledger.AppendEvent(context.Entry.EntryId, "OCCUPANCY_SCAN", "occupied", context.Occupancy.Describe());

        if (!context.Options.KillLockingProcesses)
        {
            // 默认不结束进程 → 直接拦截（可读错误 + 可执行指引）。
            throw new SourceOccupiedException(context.Entry.SourcePlainPath, context.Occupancy);
        }

        foreach (var process in context.Occupancy.Processes)
        {
            // 护栏先行：关键系统进程 / 自身 / 资源管理器 / 服务 / PID 复用一律拒绝，
            // 并把拒绝原因如实记入台账（用户需要知道"哪些没能结束、为什么"）。
            var guard = _lockService.CanKill(process);
            if (!guard.Allowed)
            {
                context.Ledger.AppendEvent(context.Entry.EntryId, "OCCUPANCY_SCAN", "kill-refused",
                    $"{process.Describe()}：[{guard.Reason}] {guard.Message}");
                continue;
            }

            if (_lockService.TryKill(process, out var error))
                context.Ledger.AppendEvent(context.Entry.EntryId, "OCCUPANCY_SCAN", "killed", $"已结束 {process.Describe()}");
            else
                context.Ledger.AppendEvent(context.Entry.EntryId, "OCCUPANCY_SCAN", "kill-failed", $"{process.Describe()}：{error}");
        }

        // 结束后复检：仍被占用 → 同样拦截，把"没能结束"的真实状况带出来。
        var remaining = service.FindLockingProcesses(new[] { context.Entry.SourcePlainPath });
        if (remaining.HasOccupancy)
        {
            context.Occupancy = remaining;
            throw new SourceOccupiedException(context.Entry.SourcePlainPath, remaining, "已尝试结束占用进程，但源目录仍被占用");
        }
    }

    /// <summary>
    /// TARGET_PREPARE：创建缺失的目标父目录链 + 空间复检。
    /// 正向动作：只创建**缺失**的目录，逐个记录进 Journal。
    /// 失败时的回滚动作：删除本步新建的目录（只用非递归 RemoveDirectory，删不掉就如实报告）。
    /// </summary>
    private async Task RunTargetPrepareAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        await Task.Yield();

        var target = context.Entry.TargetExtendedPath;
        var created = new List<string>();

        // 先登记回滚（LIFO 下最后执行：此时目标已清空，删除空目录即可）
        context.Rollback.Register(
            "TARGET_PREPARE",
            $"删除本任务新建的目标目录（仅删除空目录，绝不递归）",
            _ =>
            {
                foreach (var directory in Enumerable.Reverse(created))
                {
                    if (!Kernel32.RemoveDirectory(directory))
                    {
                        var code = Win32Error.Last;
                        if (code is Kernel32.ErrorFileNotFound or Kernel32.ErrorPathNotFound) continue;
                        // 非空 / 被占用：保留并留痕，绝不递归删除
                        System.Diagnostics.Debug.WriteLine($"[DirPorter] 回滚删除空目录失败（已保留）：{directory} {Win32Error.Describe(code)}");
                    }
                }

                return Task.CompletedTask;
            },
            RollbackReliability.BestEffort);

        var existingBefore = ReparsePointOps.Inspect(target).PathExists;
        context.Journal = context.Journal with { TargetDirectoryExistedBefore = existingBefore };

        // 逐级创建父目录链，记录新建了哪些
        var parent = Path.GetDirectoryName(PathNormalizer.TrimTrailingSeparator(target));
        if (!string.IsNullOrEmpty(parent))
        {
            var chain = new List<string>();
            var current = PathNormalizer.TrimTrailingSeparator(parent);
            while (current is not null && !ReparsePointOps.Inspect(current).PathExists)
            {
                chain.Add(current);
                var next = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(next) || string.Equals(next, current, StringComparison.OrdinalIgnoreCase)) break;
                current = PathNormalizer.TrimTrailingSeparator(next);
            }

            chain.Reverse();
            foreach (var directory in chain)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!Kernel32.CreateDirectory(directory, IntPtr.Zero))
                {
                    var code = Win32Error.Last;
                    if (code != Kernel32.ErrorAlreadyExists)
                        throw new DirPorterNativeException("创建目标父目录", code, $"创建 {PathNormalizer.StripExtendedPrefix(directory)} 失败：{Win32Error.Describe(code)}");
                }

                created.Add(directory);
            }
        }

        // 目标目录本身。三条互斥分支，顺序不能乱：
        //   A) 同卷快速移动：**绝不创建**目标 —— 改名的前提就是目标不存在；
        //      也**绝不能**去枚举它（原本这里会因为目标不存在而抛 DirectoryNotFoundException，
        //      这正是同卷快路径第一次落地时的真实崩溃点）。
        //   B) 目标原本不存在：创建它（复制路径）。
        //   C) 目标原本已存在：只做"非空"防御性复查。
        if (context.SameVolumeMove)
        {
            if (ReparsePointOps.Inspect(target).PathExists)
            {
                throw new InvalidOperationException(
                    $"同卷快速移动要求目标路径不存在，但它已存在：{PathNormalizer.StripExtendedPrefix(target)}。"
                    + "请先清空或改名该目录；也可以用 --no-same-volume 改用复制路径。");
            }
        }
        else if (!existingBefore)
        {
            if (!Kernel32.CreateDirectory(target, IntPtr.Zero))
            {
                var code = Win32Error.Last;
                if (code != Kernel32.ErrorAlreadyExists)
                    throw new DirPorterNativeException("创建目标目录", code, $"创建 {PathNormalizer.StripExtendedPrefix(target)} 失败：{Win32Error.Describe(code)}");
            }

            created.Add(target);
        }
        else if (Directory.EnumerateFileSystemEntries(target).Any())
        {
            // PRECHECK 应已拦住；防御性再查一次（预检与执行之间用户可能放入了文件）
            throw new InvalidOperationException(
                $"目标目录已存在且非空，拒绝继续：{PathNormalizer.StripExtendedPrefix(target)}（工具不会覆盖或合并已存在的非空目录）");
        }

        if (created.Count > 0)
        {
            context.Journal = context.Journal with
            {
                CreatedTargetDirectories = context.Journal.CreatedTargetDirectories.Concat(created).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            };
            SaveJournal(context);
        }

        // 空间复检（预检到执行之间可能被别的进程占走空间）
        var requirement = SpaceCalculator.Evaluate(context.Entry.TargetPlainPath, context.Probe.DiskBytes, _volumes, 1.05);
        if (!requirement.IsSatisfied)
            throw new IOException($"目标卷空间不足，已中止（未开始复制）：{requirement.Describe()}");
    }

    /// <summary>
    /// COPYING：调用复制引擎。
    /// 正向动作：把源目录内容复制到目标。
    /// 失败时的回滚动作：**清空目标目录内容**（SafeTreeDeleter，遇重解析点不穿越）。
    /// 源目录此时一个字节都没动。
    /// </summary>
    private async Task RunCopyAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        // 先登记回滚
        context.Rollback.Register(
            "COPYING",
            $"清空目标目录内容（{context.Entry.TargetPlainPath}）",
            _ =>
            {
                var result = SafeTreeDeleter.ClearDirectoryContents(context.Entry.TargetExtendedPath, CancellationToken.None);
                if (result.AnyFailure)
                {
                    var first = result.Failures[0];
                    throw new IOException($"清理目标残留时 {result.Failures.Count} 项失败，例：{first.Path}（{first.Message}）");
                }

                return Task.CompletedTask;
            });

        var progressAdapter = context.Progress is null
            ? null
            : new Progress<CopyProgress>(p =>
            {
                var percent = context.Probe.DiskBytes > 0 ? p.BytesDone * 100.0 / context.Probe.DiskBytes : 0;
                context.Progress!.Report(new MigrationProgress(
                    context.Entry.EntryId, MigrationState.Copying, p.CurrentItem ?? "复制中…",
                    percent, p.BytesDone, context.Probe.DiskBytes, p.BytesPerSecond));
            });

        var request = new CopyRequest(context.Entry.SourceExtendedPath, context.Entry.TargetExtendedPath, new CopyOptions
        {
            Threads = context.Options.CopyThreads,
            SourceTotalBytes = context.Probe.DiskBytes,
            SourceFileCount = context.Probe.FileCount,
            SkipReparsePoints = true,
            CopySecurity = context.Options.CopySecurity,
            ReplicateSparse = context.Options.ReplicateSparse,
            AllowDecryptEncryptedFiles = context.Options.AllowDecryptEncryptedFiles,
        });

        var copier = ResolveCopier(context.Options.CopyEngine);
        var result = await copier.CopyAsync(request, progressAdapter, cancellationToken).ConfigureAwait(false);
        context.CopyResult = result;

        // 如实记录"复制引擎实际做了什么"（含安全信息降级说明）——这是可观测性要求的一部分。
        if (!string.IsNullOrWhiteSpace(result.SecurityNote))
        {
            context.Ledger.AppendEvent(context.Entry.EntryId, "COPYING", "note",
                $"[{copier.Name}] {result.SecurityNote}");
        }

        if (result.Outcome == CopyOutcome.Canceled)
            throw new OperationCanceledException("复制被取消。");

        if (result.Outcome != CopyOutcome.Success)
        {
            var detail = result.Failures.Count > 0
                ? $"首个失败：{result.Failures[0].Path}（{result.Failures[0].Reason}）"
                : $"退出码 {result.ExitCode}";
            throw new IOException($"复制未成功（{result.Outcome}）：{detail}。{result.Describe()}");
        }
    }

    /// <summary>
    /// 源与目标是否在同一卷。
    ///
    /// 判据用**卷根字符串**相等（如 <c>C:\</c>）：它不需要目标路径已存在（改名正需要这一点），
    /// 且对"盘符不同"这种绝大多数情况完全准确。
    ///
    /// 已知边界（如实写明）：把另一个卷挂载到某个文件夹（volume mount point）时，
    /// 卷根字符串会相同而实际不同卷 —— 此时改名会以 ERROR_NOT_SAME_DEVICE 失败，
    /// 但那一次失败**不会移动任何数据**（改名是原子的：要么成功要么什么都没发生），
    /// 回滚只做无风险清理，用户可用 <c>--no-same-volume</c> 强制走复制路径。
    /// </summary>
    private static bool IsSameVolume(string sourcePath, string targetPath)
    {
        var sourceRoot = PathNormalizer.TryGetVolumeRoot(sourcePath);
        var targetRoot = PathNormalizer.TryGetVolumeRoot(targetPath);

        return !string.IsNullOrEmpty(sourceRoot)
               && !string.IsNullOrEmpty(targetRoot)
               && string.Equals(sourceRoot, targetRoot, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 说明每个条目会走哪条传输路径（同卷改名 / 跨卷复制）。干跑预览必须写清楚 ——
    /// 用户据此判断"这一次到底会不会真的搬数据"。
    /// </summary>
    public IReadOnlyList<string> DescribeTransferModes(MigrationPlan plan, MigrationTaskOptions? options = null)
    {
        options ??= MigrationTaskOptions.Default;
        var lines = new List<string>();

        foreach (var entry in plan.Entries)
        {
            var sameVolume = options.SameVolumeMove
                             && IsSameVolume(entry.SourcePath, entry.TargetPath)
                             && !ReparsePointOps.Inspect(PathNormalizer.NormalizeAbsolute(entry.TargetPath)).PathExists;

            lines.Add(sameVolume
                ? $"· {entry.SourcePath} → {entry.TargetPath}：**同卷**，直接改名到目标（瞬时、原子、不复制数据、不产生备份）"
                : $"· {entry.SourcePath} → {entry.TargetPath}：跨卷（或目标已存在），复制 + 校验，并保留停车目录备份");
        }

        return lines;
    }

    /// <summary>
    /// MOVE_TO_TARGET：同卷直接改名（资源管理器"剪切"语义）。
    ///
    /// 正向动作：<c>MoveFileEx(source → target)</c>（同卷、原子、瞬时；目标必须不存在）。
    /// 失败时的回滚动作：**先登记**"把目标改名回源路径"，再执行改名。
    /// 为什么敢先登记：改名要么完全成功要么完全没发生，所以登记在前不会有"部分改名"需要处理；
    /// 若改名失败，回滚发现目标不存在 → 直接跳过，磁盘保持原样。
    ///
    /// 数据安全说明：这一步**不复制、不删除任何数据**，只是换一个目录项。
    /// 因此 ACL / 所有者 / ADS / 稀疏 / EFS / 时间戳全部天然保留，比复制路径更保真。
    /// </summary>
    private async Task RunSameVolumeMoveAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        await Task.Yield();

        var source = context.Entry.SourceExtendedPath;
        var target = context.Entry.TargetExtendedPath;

        context.Rollback.Register(
            "MOVE_TO_TARGET",
            $"把目标目录改名回原始路径（{context.Entry.SourcePlainPath}）—— 同卷原子改名",
            _ =>
            {
                if (!ReparsePointOps.Inspect(target).PathExists) return Task.CompletedTask;

                if (TryMoveWithoutReplace(target, source)) return Task.CompletedTask;

                var code = Win32Error.Last;
                throw new DirPorterNativeException("回滚同卷改名", code,
                    $"把 {context.Entry.TargetPlainPath} 改回 {context.Entry.SourcePlainPath} 失败：{Win32Error.Describe(code)}");
            });

        cancellationToken.ThrowIfCancellationRequested();

        if (ReparsePointOps.Inspect(target).PathExists)
            throw new IOException($"目标路径已存在，拒绝覆盖：{context.Entry.TargetPlainPath}");

        if (!TryMoveWithoutReplace(source, target))
        {
            var code = Win32Error.Last;
            throw new DirPorterNativeException("同卷改名", code,
                $"把 {context.Entry.SourcePlainPath} 改名为 {context.Entry.TargetPlainPath} 失败：{Win32Error.Describe(code)}"
                + "（若目标是挂载到文件夹的另一个卷，请加 --no-same-volume 改用复制路径）");
        }

        // 台账语义：同卷模式下"数据停放的位置"就是目标本身。
        // 这样还原逻辑（把 parkingPath 改名回 sourcePath）无需任何特殊分支即可正确工作，
        // 也避免给台账/SQLite 表结构加字段。
        context.ParkingPath = context.Entry.TargetPlainPath;
        context.Journal = context.Journal with { ParkingPath = context.Entry.TargetPlainPath };
        SaveJournal(context);

        context.Ledger.AppendEvent(context.Entry.EntryId, "MOVE_TO_TARGET", "ok",
            $"同卷直接改名：{context.Entry.SourcePlainPath} → {context.Entry.TargetPlainPath}（零数据拷贝）");
    }

    /// <summary>
    /// VERIFYING：文件数 + 总字节 + 逐文件 size/mtime（默认档位）。
    /// 正向动作：只读比对。
    /// 失败时的回滚动作：复用 COPYING 已登记的清空动作（本步只读，不新增回滚项）。
    /// 校验不一致**必须判失败并回滚** —— 不接受"少复制了几个文件也算成功"。
    /// </summary>
    private async Task RunVerifyAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        await Task.Yield();

        if (context.SameVolumeMove)
        {
            // 同卷改名后源路径已经不存在，无法与源做树比对。
            // 但"改名不可能损坏数据"不等于"不需要校验"：仍要确认目标可访问、且规模与预检一致。
            var targetExtended = context.Entry.TargetExtendedPath;

            var actual = FileSystem.TreeProbe.Probe(targetExtended, new FileSystem.TreeProbeOptions
            {
                DeepProbeFileLimit = context.Options.DeepProbeFileLimit,
            }, null, cancellationToken);

            var expected = context.Probe;
            var consistent = actual.FileCount == expected.FileCount && actual.DiskBytes == expected.DiskBytes;

            var fingerprint = Ledger.MetadataFingerprint.Compute(
                PathNormalizer.StripExtendedPrefix(targetExtended), out _, out _);

            context.VerifyResult = new VerifyResult(
                consistent,
                expected.FileCount, actual.FileCount,
                expected.DiskBytes, actual.DiskBytes,
                Array.Empty<VerifyMismatch>(),
                actual.Errors,
                fingerprint, fingerprint,
                actual.Elapsed);

            if (!consistent)
            {
                throw new IOException(
                    "同卷改名后校验不一致，判定失败："
                    + $"预期 {expected.FileCount} 个文件 / {expected.DiskBytes} 字节，"
                    + $"实际 {actual.FileCount} 个文件 / {actual.DiskBytes} 字节。");
            }

            return;
        }

        var options = new VerifyOptions { Mode = context.Options.Verify };
        var result = TreeComparer.Compare(context.Entry.SourceExtendedPath, context.Entry.TargetExtendedPath, options, cancellationToken);
        context.VerifyResult = result;

        if (!result.IsConsistent)
            throw new IOException($"数据校验不一致，判定失败：{Environment.NewLine}{result.Describe()}");
    }

    /// <summary>
    /// PARKING：把源目录**同卷改名**到 `.migbak\&lt;name&gt;_&lt;suffix&gt;`。
    /// 正向动作：RENAME（同卷、原子、瞬时）。
    /// 失败时的回滚动作：把名字改回去（仍为同卷 RENAME，毫秒级、零数据风险）。
    /// 设计意图：先改名再建链，绝不先删源 —— 这样 LINKING 失败时回滚只是"改个名字回去"。
    /// </summary>
    private async Task RunParkAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        await Task.Yield();

        // 先登记回滚（改名失败时改回来；此时可能已经改了一半，登记在前更安全）
        context.Rollback.Register(
            "PARKING",
            $"把停车目录改回原始路径（{PathNormalizer.StripExtendedPrefix(context.Entry.SourceExtendedPath)}）",
            _ =>
            {
                var parkingExtended = PathNormalizer.NormalizeAbsolute(context.ParkingPath);
                if (!ReparsePointOps.Inspect(parkingExtended).PathExists) return Task.CompletedTask;

                if (TryMoveWithoutReplace(parkingExtended, context.Entry.SourceExtendedPath))
                    return Task.CompletedTask;

                var code = Win32Error.Last;
                throw new DirPorterNativeException("回滚停车改名", code,
                    $"把 {context.ParkingPath} 改回 {context.Entry.SourceExtendedPath} 失败：{Win32Error.Describe(code)}");
            });

        cancellationToken.ThrowIfCancellationRequested();

        var parkingDirectory = MigrationLedgerFactory.ResolveParkingDirectory(context.Entry.SourceExtendedPath);
        var parkingDirectoryExtended = PathNormalizer.NormalizeAbsolute(parkingDirectory);
        if (!ReparsePointOps.Inspect(parkingDirectoryExtended).PathExists)
        {
            if (!Kernel32.CreateDirectory(parkingDirectoryExtended, IntPtr.Zero))
            {
                var code = Win32Error.Last;
                if (code != Kernel32.ErrorAlreadyExists)
                    throw new DirPorterNativeException("创建停车目录", code, $"创建 {parkingDirectory} 失败：{Win32Error.Describe(code)}");
            }
        }

        var parkingExtended2 = PathNormalizer.NormalizeAbsolute(context.ParkingPath);
        if (ReparsePointOps.Inspect(parkingExtended2).PathExists)
            throw new IOException($"停车路径已存在，拒绝覆盖：{context.ParkingPath}");

        // 同卷 RENAME（不带 REPLACE_EXISTING：目标必须不存在）
        if (!TryMoveWithoutReplace(context.Entry.SourceExtendedPath, parkingExtended2))
        {
            var code = Win32Error.Last;

            // R12.4：失败后复检占用 —— 把"拒绝访问(5)"翻译成"谁还在用 + 怎么办"。
            var occupancy = _lockService.FindLockingProcesses(new[] { context.Entry.SourcePlainPath });
            var hint = occupancy.HasOccupancy
                ? $"（源目录仍在使用中：{occupancy.Describe()} ｜ 请关闭相关程序后重试，或勾选「结束占用进程」。）"
                : "（请确认源目录未被任何程序打开、当前账户有修改权限。）";

            throw new DirPorterNativeException("停车改名", code,
                $"把源目录改名为 {context.ParkingPath} 失败：{Win32Error.Describe(code)}{hint}");
        }

        context.Journal = context.Journal with { ParkingPath = context.ParkingPath };
        SaveJournal(context);

        await Task.CompletedTask;
    }

    /// <summary>
    /// LINKING：在原路径创建链接。
    /// 正向动作：通过 <see cref="LinkService"/> 选定/创建链接。
    /// 失败时的回滚动作：**先登记**删除链接本体（只删链接、绝不递归），再把源改回来（由 PARKING 的回滚完成）。
    /// </summary>
    private async Task RunLinkAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        await Task.Yield();

        // 先登记回滚：即使建链只完成了一半，也能收拾干净
        context.Rollback.Register(
            "LINKING",
            $"删除原路径上的链接本体（{context.Entry.SourcePlainPath}）",
            _ =>
            {
                var info = ReparsePointOps.Inspect(context.Entry.SourceExtendedPath);
                if (!info.PathExists) return Task.CompletedTask;

                if (info.IsReparsePoint)
                {
                    ReparsePointOps.RemoveLinkBody(context.Entry.SourceExtendedPath, info.IsDirectory, info.Tag, "回滚删除链接本体");
                    return Task.CompletedTask;
                }

                // 落点目录已建但重解析点未写入：只删空目录（非递归）
                if (info.IsDirectory && !Kernel32.RemoveDirectory(context.Entry.SourceExtendedPath))
                {
                    var code = Win32Error.Last;
                    if (code is not (Kernel32.ErrorFileNotFound or Kernel32.ErrorPathNotFound))
                    {
                        throw new DirPorterNativeException("回滚删除链接落点", code,
                            $"回滚时无法删除链接落点目录：{Win32Error.Describe(code)}（可能在回滚前已被重建）");
                    }
                }

                return Task.CompletedTask;
            });

        cancellationToken.ThrowIfCancellationRequested();

        var request = new LinkRequest(
            context.Entry.SourcePlainPath,
            context.Entry.TargetPlainPath,
            context.Entry.LinkKind);

        var resolution = _links.Create(request, LinkCreateOptions.Default);

        context.LinkInfo = _links.Inspect(context.Entry.SourcePlainPath);

        context.Ledger.AppendEvent(context.Entry.EntryId, "LINKING", "ok",
            $"{resolution.Kind}（{(resolution.WasAuto ? "auto" : "显式指定")}）：{resolution.Reason}");
    }

    /// <summary>
    /// LINK_VERIFY：解析链接目标、枚举其子项、试读一个文件。
    /// 正向动作：只读校验。
    /// 失败时的回滚动作：复用 LINKING 的删链接 + PARKING 的改名回退。
    /// </summary>
    private async Task RunLinkVerifyAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        await Task.Yield();

        var linkPath = context.Entry.SourcePlainPath;
        var info = _links.Inspect(linkPath);
        context.LinkInfo = info;

        if (!info.PathExists)
            throw new IOException($"链接校验失败：原始路径不存在：{linkPath}");

        if (info.Kind != context.Entry.LinkKind)
            throw new IOException($"链接校验失败：期望 {context.Entry.LinkKind}，实际 {info.Kind?.ToString() ?? "非链接"}");

        if (info.IsDangling)
            throw new IOException($"链接校验失败：目标已丢失（悬空链接）。解析到的目标：{info.TargetAbsolute}");

        // 枚举子项：源目录里有多少文件，通过链接就应能看到多少
        cancellationToken.ThrowIfCancellationRequested();

        var visible = 0;
        try
        {
            visible = Directory.EnumerateFileSystemEntries(linkPath).Take(1).Count();
        }
        catch (Exception ex)
        {
            throw new IOException($"链接校验失败：无法枚举链接内容：{ex.Message}");
        }

        if (context.Probe.FileCount > 0 && visible == 0)
            throw new IOException("链接校验失败：通过链接枚举到的内容为空，但源目录里存在文件。");

        // 试读一个文件（证明读写真的落到新位置）
        if (context.Probe.FileCount > 0)
        {
            var sample = Directory.EnumerateFiles(linkPath, "*", SearchOption.AllDirectories).Take(1).FirstOrDefault();
            if (sample is not null)
            {
                try
                {
                    using var stream = new FileStream(sample, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var buffer = new byte[Math.Min(4096, stream.Length)];
                    _ = stream.Read(buffer, 0, buffer.Length);
                }
                catch (Exception ex)
                {
                    throw new IOException($"链接校验失败：无法通过链接读取文件 {sample}：{ex.Message}");
                }
            }
        }

        context.Ledger.AppendEvent(context.Entry.EntryId, "LINK_VERIFY", "ok",
            $"链接目标 {info.TargetAbsolute}，子项可见 {visible} 项，试读通过。");
    }

    /// <summary>
    /// LEDGER_COMMIT：把权威台账条目落盘。
    /// 正向动作：写台账（**LINK_VERIFY 成功后立即执行**，需求约束 4）。
    /// 失败时的回滚动作：无（台账是记录而非数据变更；写失败会向上抛出并触发回滚，其本身无需撤销）。
    /// </summary>
    private async Task RunLedgerCommitAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        var verify = context.VerifyResult;
        var checksum = verify?.SourceFingerprint ?? string.Empty;

        // 操作序列如实反映"实际发生了什么"：
        // 同卷模式没有 PARK/COPY，只有一个 MOVE（这正是资源管理器剪切的语义）。
        var operations = context.SameVolumeMove
            ? new List<LedgerOperation>
            {
                new() { Step = "MOVE", At = DateTimeOffset.Now, Status = "ok",
                        Detail = $"同卷直接改名：{context.Entry.SourcePlainPath} → {context.Entry.TargetPlainPath}（零数据拷贝，无备份）" },
                new() { Step = "LINK", At = DateTimeOffset.Now, Status = "ok", Detail = context.Entry.LinkKind.ToString() },
                new() { Step = "VERIFY", At = DateTimeOffset.Now, Status = verify?.IsConsistent == true ? "ok" : "failed", Detail = verify?.Describe() },
            }
            : new List<LedgerOperation>
            {
                new() { Step = "PARK", At = DateTimeOffset.Now, Status = "ok", Detail = PathNormalizer.StripExtendedPrefix(context.ParkingPath) },
                new() { Step = "COPY", At = DateTimeOffset.Now, Status = "ok", Detail = context.CopyResult?.Describe() },
                new() { Step = "LINK", At = DateTimeOffset.Now, Status = "ok", Detail = context.Entry.LinkKind.ToString() },
                new() { Step = "VERIFY", At = DateTimeOffset.Now, Status = verify?.IsConsistent == true ? "ok" : "failed", Detail = verify?.Describe() },
            };

        var entry = new LedgerEntry
        {
            BatchId = context.Ledger.BatchId,
            EntryId = context.Entry.EntryId,
            SourcePath = context.Entry.SourcePlainPath,
            ParkingPath = PathNormalizer.StripExtendedPrefix(context.ParkingPath),
            TargetPath = context.Entry.TargetPlainPath,
            LinkPath = context.Entry.SourcePlainPath,
            LinkType = context.Entry.CreateLink ? context.Entry.LinkKind.ToString() : "None",
            VolumeSource = context.VolumeSource,
            VolumeTarget = context.VolumeTarget,
            Operations = operations,
            ChecksumMethod = MetadataFingerprint.MethodName,
            Checksum = checksum,
            FileCount = context.Probe.FileCount,
            TotalBytes = context.Probe.DiskBytes,
            LinkTarget = context.LinkInfo?.TargetAbsolute,
            Result = "done",
        };

        context.Ledger.WriteEntry(entry);
    }

    /// <summary>
    /// BACKUP_CLEANUP：按保留策略处理停车目录。
    /// 正向动作：默认只登记不删除（保留 N 天）；只有 Immediate 策略才真正删除。
    /// 失败时的回滚动作：**无** —— 这是全流程唯一不可逆点，因此放在最后一步，
    /// 且只有在前序步骤全部成功后才可能到达。删除失败不判任务失败，标记为警告。
    /// </summary>
    private async Task RunBackupCleanupAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        await Task.Yield();

        if (context.Options.BackupPolicy != BackupCleanupPolicy.Immediate)
        {
            var until = context.Options.BackupPolicy == BackupCleanupPolicy.KeepDays
                ? $"保留 {context.Options.BackupRetentionDays} 天（到期由备份保留策略清理）"
                : "永久保留（不自动清理）";

            context.Ledger.AppendEvent(context.Entry.EntryId, "BACKUP_CLEANUP", "kept",
                $"停车目录 {PathNormalizer.StripExtendedPrefix(context.ParkingPath)}：{until}");
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var result = SafeTreeDeleter.DeleteTree(PathNormalizer.NormalizeAbsolute(context.ParkingPath), cancellationToken);
        var status = result.AnyFailure ? "partial" : "ok";
        context.Ledger.AppendEvent(context.Entry.EntryId, "BACKUP_CLEANUP", status,
            $"删除停车目录：{result.Describe()}");
    }

    // ==================================================================
    //  回滚与恢复
    // ==================================================================

    private async Task<RollbackOutcome> RollbackAsync(
        MigrationContext context, IMigrationLedger ledger, string reason, IProgress<MigrationProgress>? progress)
    {
        UpdateState(context, MigrationState.RollingBack);
        ledger.AppendEvent(context.Entry.EntryId, "ROLLBACK", "begin", reason);

        var log = progress is null
            ? null
            : new Progress<string>(message => Report(progress, context, MigrationState.RollingBack, message, 0));

        var outcome = await context.Rollback.ExecuteAsync(log).ConfigureAwait(false);

        context.Journal = context.Journal with
        {
            State = outcome.Success ? MigrationState.RolledBack : MigrationState.Failed,
            Error = reason,
            RollbackFailures = outcome.Failures.ToList(),
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
        SaveJournal(context);

        ledger.AppendEvent(context.Entry.EntryId, "ROLLBACK",
            outcome.Success ? "ok" : "failed",
            outcome.Success
                ? $"已回滚 {outcome.Steps.Count} 步"
                : string.Join("；", outcome.Failures));
        ledger.WriteEntry(BuildLedgerEntry(context, outcome.Success ? "rolled_back" : "rollback-failed", reason));

        return outcome;
    }

    private LedgerEntry BuildLedgerEntry(MigrationContext context, string result, string? error)
        => new()
        {
            BatchId = context.Ledger.BatchId,
            EntryId = context.Entry.EntryId,
            SourcePath = context.Entry.SourcePlainPath,
            ParkingPath = PathNormalizer.StripExtendedPrefix(context.ParkingPath),
            TargetPath = context.Entry.TargetPlainPath,
            LinkPath = context.Entry.SourcePlainPath,
            LinkType = context.Entry.CreateLink ? context.Entry.LinkKind.ToString() : "None",
            VolumeSource = context.VolumeSource,
            VolumeTarget = context.VolumeTarget,
            Operations = context.Rollback.Records
                .Select(r => new LedgerOperation { Step = "ROLLBACK:" + r.Step, At = DateTimeOffset.Now, Status = "ok", Detail = r.Description })
                .ToList(),
            ChecksumMethod = MetadataFingerprint.MethodName,
            Checksum = context.VerifyResult?.SourceFingerprint ?? string.Empty,
            FileCount = context.Probe.FileCount,
            TotalBytes = context.Probe.DiskBytes,
            Result = result,
            Error = error,
        };

    /// <summary>同卷改名（不带 REPLACE_EXISTING：目标必须不存在，避免覆盖用户数据）。</summary>
    private static bool TryMoveWithoutReplace(string from, string to)
        => Kernel32.MoveFileEx(from, to, 0);

    private void UpdateState(MigrationContext context, MigrationState state)
    {
        context.Journal = context.Journal with { State = state, UpdatedUtc = DateTimeOffset.UtcNow };
        SaveJournal(context);
    }

    private void SaveJournal(MigrationContext context)
    {
        try
        {
            _journal.Save(context.Journal);
        }
        catch (Exception ex)
        {
            // Journal 写失败不能中断迁移（数据安全不依赖 Journal），但必须留痕
            System.Diagnostics.Debug.WriteLine($"[DirPorter] Journal 写入失败：{ex.Message}");
        }
    }

    private void Report(IProgress<MigrationProgress>? progress, MigrationContext context, MigrationState state, string message, double percent)
        => progress?.Report(new MigrationProgress(
            context.Entry.EntryId, state, message, percent,
            context.CopyResult?.BytesCopied ?? 0, context.Probe.DiskBytes, 0));

    /// <summary>
    /// 测试钩子：模拟"进程被强杀"（不执行 finally、不执行回滚）。
    /// 必须显式设置环境变量 <c>DIRPORTER_ALLOW_KILL_HOOK=1</c> 才生效。
    /// </summary>
    private static void ApplyKillHook(string? killAfterState, MigrationState state)
    {
        if (string.IsNullOrWhiteSpace(killAfterState)) return;
        if (!string.Equals(Environment.GetEnvironmentVariable("DIRPORTER_ALLOW_KILL_HOOK"), "1", StringComparison.Ordinal)) return;
        if (!string.Equals(killAfterState, state.ToString(), StringComparison.OrdinalIgnoreCase)) return;

        Console.Error.WriteLine($"[DirPorter][KILL-HOOK] 模拟强杀于状态 {state}（磁盘停在上一状态）");
        Console.Error.Flush();
        System.Diagnostics.Process.GetCurrentProcess().Kill();
    }

    /// <summary>
    /// 测试钩子：在指定状态的正向动作之前强制失败，用于验证"任一步失败都能完整回滚"。
    /// 不影响真实用户（CLI 未暴露该选项）。
    /// </summary>
    private static void ApplyFailureHook(string? failBeforeState, MigrationState state)
    {
        if (string.IsNullOrWhiteSpace(failBeforeState)) return;
        if (!string.Equals(failBeforeState, state.ToString(), StringComparison.OrdinalIgnoreCase)) return;

        throw new InvalidOperationException($"[测试注入] 在 {state} 之前强制失败，用于验证回滚路径。");
    }

    private void WriteIndexRecord(MigrationContext context, PrecheckReport report, string status, string? error, IMigrationLedger ledger)
    {
        try
        {
            _index.Upsert(new MigrationRecord
            {
                Id = context.Journal.TaskId,
                BatchId = ledger.BatchId,
                EntryId = context.Entry.EntryId,
                StartedUtc = context.Journal.StartedUtc,
                FinishedUtc = DateTimeOffset.UtcNow,
                SourcePath = context.Entry.SourcePlainPath,
                TargetPath = context.Entry.TargetPlainPath,
                // 同卷快速移动没有"停车目录/备份"这回事（数据从未被复制）→ 备份路径留空。
                // 这不只是措辞问题：BackupPath 为空能让备份保留清理（sweepbackups）彻底看不见它，
                // 避免把"目标目录"（= 用户的真实数据）当成过期备份删掉。
                BackupPath = context.SameVolumeMove ? null : PathNormalizer.StripExtendedPrefix(context.ParkingPath),
                LedgerPath = ledger.PrimaryFilePath,
                Status = status,
                LinkType = context.Entry.CreateLink ? context.Entry.LinkKind.ToString() : "None",
                FileCount = context.Probe.FileCount,
                TotalBytes = context.Probe.DiskBytes,
                ChecksumMethod = MetadataFingerprint.MethodName,
                Checksum = context.VerifyResult?.SourceFingerprint,
                Warnings = report.Results.Where(static r => r.Severity == CheckSeverity.Warning).Select(static r => r.Detail).ToList(),
                AppVersion = context.Journal.AppVersion,
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DirPorter] 写迁移记录索引失败：{ex.Message}");
        }
    }

    /// <summary>把台账归档到其它源父目录（批次的源可能分布在多个父目录下）。</summary>
    private static string? ResolveArchiveDirectory(MigrationTaskOptions options, string ledgerDirectory, PrecheckReport report, string batchId)
    {
        var perParent = report.EffectiveEntries
            .Select(e => MigrationLedgerFactory.ResolveLedgerDirectory(e.SourceExtendedPath))
            .Where(d => !string.Equals(d, ledgerDirectory, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        switch (options.LedgerArchive)
        {
            case LedgerArchiveMode.UserSpecified when !string.IsNullOrWhiteSpace(options.LedgerArchivePath):
                return options.LedgerArchivePath;

            case LedgerArchiveMode.TargetSide:
                var firstTarget = report.EffectiveEntries.FirstOrDefault();
                if (firstTarget is null) return null;
                var targetParent = Path.GetDirectoryName(PathNormalizer.TrimTrailingSeparator(firstTarget.TargetExtendedPath));
                return string.IsNullOrEmpty(targetParent) ? null : Path.Combine(targetParent, MigrationLedgerFactory.MigBakFolderName);

            default:
                // AlongsideBackup：就地保留；但若本批次的源跨多个父目录，额外归档到其余父目录，保证"PARKING 同级"都能找到台账
                if (perParent.Count == 1) return perParent[0];
                return perParent.Count > 1 ? perParent[0] : null;
        }
    }
}
