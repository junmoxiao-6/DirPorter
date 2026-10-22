using System.Text;
using System.Text.Json;
using DirPorter.Core.Copy;
using DirPorter.Core.Config;
using DirPorter.Core.FileSystem;
using DirPorter.Core.Journal;
using DirPorter.Core.Ledger;
using DirPorter.Core.Links;
using DirPorter.Core.Migration;
using DirPorter.Core.Precheck;
using DirPorter.Core.Privilege;
using DirPorter.Core.RestartManager;
using DirPorter.Core.Scan;

namespace DirPorter.Core.Cli;

/// <summary>CLI 退出码（需求「命令行模式」）。</summary>
public static class ExitCodes
{
    public const int Success = 0;
    public const int PartialFailure = 1;
    public const int InvalidArguments = 2;
    public const int PrecheckFailed = 3;
    public const int UserCanceled = 4;
    public const int NeedsAdministrator = 5;
    public const int InternalError = 6;
}

/// <summary>
/// 命令行宿主。
///
/// 放在 Core 里（而不是独立 exe 里）是为了：① 可被单元测试直接调用；
/// ② R7 的 WPF 单 exe 可以直接复用同一个入口（`AttachConsole` 后调用 <see cref="Run"/>）。
/// 无参数时由宿主的 main 负责启动 GUI（R7）。
/// </summary>
public static class CliHost
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken = default)
    {
        try
        {
            if (args.Length == 0)
            {
                stderr.WriteLine("用法：dirporter <命令> [选项]，运行 dirporter help 查看全部命令。");
                return ExitCodes.InvalidArguments;
            }

            var command = args[0].ToLowerInvariant();
            var options = Args.Parse(args.Skip(1));

            var exitCode = command switch
            {
                "move" or "migrate" => RunMove(options, stdout, stderr, cancellationToken),
                "copy" => RunCopy(options, stdout, stderr, cancellationToken),
                "scan" => RunScan(options, stdout, stderr, cancellationToken),
                "link" => LinkCommands.RunLink(options, stdout, stderr),
                "unlink" => LinkCommands.RunUnlink(options, stdout, stderr),
                "linkdir" => LinkCommands.RunLinkDir(options, stdout, stderr),
                "verify" => LinkCommands.RunVerify(options, stdout, stderr),
                "selfcheck" or "doctor" => LinkCommands.RunSelfCheck(options, stdout, stderr),
                "recover" => RunRecover(options, stdout, cancellationToken),
                "list" or "history" => RunList(options, stdout, cancellationToken),
                "restore" => RunRestore(options, stdout, stderr, cancellationToken),
                "inspect" => RunInspect(options, stdout, stderr),
                "cleanresidue" => RunCleanResidue(options, stdout, stderr),
                "sweepbackups" or "sweep" => RunSweepBackups(options, stdout, stderr),
                "help" or "--help" or "-h" => RunHelp(stdout),
                _ => Unknown(command, stderr),
            };

            // 权限不足时统一给一条"怎么解决"，而不是让用户对着退出码 5 发呆。
            if (exitCode == ExitCodes.NeedsAdministrator && !options.Has("elevate"))
            {
                stderr.WriteLine("提示：该操作需要管理员权限。加 --elevate 会弹出 UAC 并以管理员身份重试同一命令；"
                                 + "或在「设置 → 系统 → 开发者选项」开启开发者模式，可免提权创建符号链接。");
            }

            return exitCode;
        }
        catch (OperationCanceledException)
        {
            stderr.WriteLine("已取消。");
            return ExitCodes.UserCanceled;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"内部错误：{ex}");
            return ExitCodes.InternalError;
        }
    }

    // ============================ 命令实现 ============================

    private static int RunMove(Args options, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        if (!options.TryGet("source", out var source) || !options.TryGet("target", out var target))
        {
            stderr.WriteLine("move 需要 --source 与 --target。");
            return ExitCodes.InvalidArguments;
        }

        var dryRun = options.Has("dry-run");
        var taskOptions = new MigrationTaskOptions
        {
            DryRun = dryRun,
            // --force 与 --yes 同义：需求命令行把"确认执行"写作 --force，交互式确认写作 --yes，两者都接受。
            AcknowledgeWarnings = options.Has("yes") || options.Has("force"),
            ReuseEmptyTargetDirectory = options.Has("yes") || options.Has("force"),
            CopyThreads = options.GetInt("threads", 8),
            KillLockingProcesses = options.Has("kill-locking"),
            // 同卷时默认走"直接改名"（与资源管理器剪切一致）；--no-same-volume 强制走复制路径（保留备份）。
            SameVolumeMove = !options.Has("no-same-volume"),
            DeepOccupancyScan = options.Has("deep-occupancy"),
            Verify = string.Equals(options.Get("verify", "metadata"), "content", StringComparison.OrdinalIgnoreCase)
                ? VerifyMode.Content
                : VerifyMode.Metadata,
            // 默认 KeepDays 7（D5 决策：回滚 SLA 优先）。--keep-backup 是显式表达"保留"的别名。
            BackupPolicy = options.Has("delete-backup")
                ? BackupCleanupPolicy.Immediate
                : options.Has("keep-backup-forever")
                    ? BackupCleanupPolicy.Never
                    : BackupCleanupPolicy.KeepDays,
            BackupRetentionDays = options.GetInt("keep-backup-days", 7),
            LedgerArchive = options.TryGet("archive-ledger", out var archive)
                ? LedgerArchiveMode.UserSpecified
                : LedgerArchiveMode.AlongsideBackup,
            LedgerArchivePath = archive,
            KillAfterState = options.Get("kill-after", null),
            DeepProbeFileLimit = options.GetInt("deep-probe-limit", 20_000),
            CopyEngine = ParseEngine(options.Get("engine", "auto")),
            CopySecurity = !options.Has("no-security"),
            ReplicateSparse = !options.Has("no-sparse"),
            AllowDecryptEncryptedFiles = options.Has("allow-decrypt"),
        };

        var plan = MigrationPlan.Single(source!, target!, LinkKind.Junction, new PrecheckOptions
        {
            AcknowledgeWarnings = taskOptions.AcknowledgeWarnings,
            ReuseEmptyTargetDirectory = taskOptions.ReuseEmptyTargetDirectory,
            DeepProbeFileLimit = taskOptions.DeepProbeFileLimit,
        });

        var engine = new MigrationEngine();

        var progress = new Progress<MigrationProgress>(p =>
        {
            if (!options.Has("quiet") && !options.Has("json"))
                stderr.Write($"\r[{p.EntryId}] {p.State.Describe()} {p.Percent:0.0}% {SpaceRequirement.Format((long)p.BytesDone)}   ");
        });

        var result = engine.RunBatchAsync(plan, taskOptions, progress, cancellationToken).GetAwaiter().GetResult();

        if (options.Has("json"))
        {
            stdout.WriteLine(JsonSerializer.Serialize(BuildMoveJson(result), JsonOptions));
        }
        else
        {
            WriteHumanReport(result, taskOptions, stdout);
        }

        if (result.Precheck.IsBlocked) return ExitCodes.PrecheckFailed;

        if (result.Precheck.RequiresAcknowledge && !taskOptions.AcknowledgeWarnings)
        {
            stderr.WriteLine("存在需要确认的风险项，请阅读上面的说明后重新执行并加 --yes。");
            return ExitCodes.UserCanceled;
        }

        if (result.Entries.Count == 0) return ExitCodes.Success;   // 干跑
        return result.Success ? ExitCodes.Success : ExitCodes.PartialFailure;
    }

    private static int RunRecover(Args options, TextWriter stdout, CancellationToken cancellationToken)
    {
        var analysis = JournalRecovery.Scan();

        if (options.Has("json"))
        {
            stdout.WriteLine(JsonSerializer.Serialize(new
            {
                needsAttention = analysis.NeedsAttentionCount,
                suggestions = analysis.Suggestions.Select(s => new
                {
                    taskId = s.TaskId,
                    entryId = s.EntryId,
                    observedState = s.ObservedState.ToString(),
                    action = s.Action.ToString(),
                    summary = s.Summary,
                    detail = s.Detail,
                    isDestructive = s.IsDestructive,
                    evidence = s.Evidence,
                }),
            }, JsonOptions));
            return ExitCodes.Success;
        }

        if (analysis.Suggestions.Count == 0)
        {
            stdout.WriteLine("未发现处于中间态的任务，无需恢复。");
            return ExitCodes.Success;
        }

        stdout.WriteLine($"发现 {analysis.Suggestions.Count} 个需要处理的任务：");
        foreach (var suggestion in analysis.Suggestions)
        {
            stdout.WriteLine();
            stdout.WriteLine($"  任务 {suggestion.TaskId}（条目 {suggestion.EntryId}，Journal 状态：{suggestion.ObservedState.Describe()}）");
            stdout.WriteLine($"  结论：{suggestion.Summary}");
            stdout.WriteLine($"  建议动作：{suggestion.Action}");
            stdout.WriteLine($"  说明：{suggestion.Detail}");
            stdout.WriteLine($"  磁盘证据：{string.Join("；", suggestion.Evidence)}");
            if (suggestion.IsDestructive) stdout.WriteLine("  ⚠️ 该动作会删除数据，请先备份。");
        }

        stdout.WriteLine();
        stdout.WriteLine("提示：工具**不会自动执行**修复动作。改回原名后可用 list 查看记录，或用 restore 走台账还原。");
        return ExitCodes.Success;
    }

    private static int RunList(Args options, TextWriter stdout, CancellationToken cancellationToken)
    {
        var records = new MigrationIndexStore().LoadAll();

        if (options.Has("json"))
        {
            stdout.WriteLine(JsonSerializer.Serialize(records, JsonOptions));
            return ExitCodes.Success;
        }

        if (records.Count == 0)
        {
            stdout.WriteLine("暂无迁移记录。");
            return ExitCodes.Success;
        }

        stdout.WriteLine($"{"id",-40} {"status",-14} {"源",-50} 目标");
        foreach (var record in records.OrderByDescending(static r => r.StartedUtc))
        {
            stdout.WriteLine($"{record.Id,-40} {record.Status,-14} {Truncate(record.SourcePath, 50),-50} {record.TargetPath}");
        }

        return ExitCodes.Success;
    }

    private static int RunRestore(Args options, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var batchId = options.Get("batch", null);
        var entryOverride = options.Get("entry", null);

        // 需求命令行写作 `restore --id <id>`；内部以 batchId + entryId 定位。
        // 这里把记录 id（形如 <batchId>-<entryId>）解析回两者，避免同一件事有两套命令形态。
        if (string.IsNullOrWhiteSpace(batchId) && options.TryGet("id", out var recordId) && !string.IsNullOrWhiteSpace(recordId))
        {
            var record = new MigrationIndexStore().LoadAll()
                .FirstOrDefault(r => string.Equals(r.Id, recordId, StringComparison.OrdinalIgnoreCase));

            if (record is null)
            {
                stderr.WriteLine($"找不到记录：{recordId}");
                return ExitCodes.InvalidArguments;
            }

            batchId = record.BatchId;
            entryOverride = record.EntryId;
        }

        if (string.IsNullOrWhiteSpace(batchId))
        {
            stderr.WriteLine("restore 需要 --batch <批次ID> [--entry <条目ID>]，或 --id <记录ID>。");
            return ExitCodes.InvalidArguments;
        }

        var service = new RestoreService();
        var ledgerDirectory = options.Get("ledger", null) ?? service.LocateLedgerDirectory(batchId!);

        if (ledgerDirectory is null)
        {
            stderr.WriteLine($"找不到批次 {batchId} 的台账目录，请用 --ledger 显式指定。");
            return ExitCodes.PrecheckFailed;
        }

        var entries = entryOverride is null ? null : new[] { entryOverride };
        var result = service.Restore(batchId, ledgerDirectory, entries, options.Has("clean-target"), cancellationToken);

        if (options.Has("json"))
        {
            stdout.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        }
        else
        {
            stdout.WriteLine($"批次 {result.BatchId}（台账目录 {ledgerDirectory}）");
            foreach (var entry in result.Entries)
            {
                stdout.WriteLine($"  [{entry.EntryId}] {(entry.Success ? "OK" : entry.Skipped ? "SKIP" : "FAIL")} {entry.Message}");
                foreach (var step in entry.Steps) stdout.WriteLine($"      · {step}");
                foreach (var failure in entry.Failures) stdout.WriteLine($"      ✗ {failure}");
            }

            stdout.WriteLine(result.Describe());
        }

        return result.Success ? ExitCodes.Success : ExitCodes.PartialFailure;
    }

    private static int RunInspect(Args options, TextWriter stdout, TextWriter stderr)
    {
        if (!options.TryGet("path", out var path))
        {
            stderr.WriteLine("inspect 需要 --path <任意文件或目录>。");
            return ExitCodes.InvalidArguments;
        }

        var service = LinkService.CreateDefault();
        var info = service.Inspect(path!);

        if (options.Has("json"))
        {
            stdout.WriteLine(JsonSerializer.Serialize(new
            {
                path = info.Path,
                pathExists = info.PathExists,
                isReparsePoint = info.IsReparsePoint,
                kind = info.Kind?.ToString(),
                reparseTag = $"0x{info.ReparseTag:X8}",
                substituteName = info.SubstituteName,
                printName = info.PrintName,
                targetAbsolute = info.TargetAbsolute,
                isRelative = info.IsRelative,
                isDangling = info.IsDangling,
                numberOfLinks = info.NumberOfLinks,
                isDirectory = info.IsDirectory,
                rawHexPreview = info.RawHexPreview,
            }, JsonOptions));
        }
        else
        {
            stdout.WriteLine(info.Describe());
            if (info.IsReparsePoint) stdout.WriteLine($"原始重解析缓冲区（前 64 字节）：{info.RawHexPreview}");
        }

        return ExitCodes.Success;
    }

    /// <summary>
    /// 清理目标侧半成品残留 / 已确认无用的停车目录。
    /// **走 SafeTreeDeleter**（遇重解析点只删链接本体，绝不穿透目标数据）。
    /// </summary>
    private static int RunCleanResidue(Args options, TextWriter stdout, TextWriter stderr)
    {
        if (!options.TryGet("target", out var target))
        {
            stderr.WriteLine("cleanresidue 需要 --target <目录>。");
            return ExitCodes.InvalidArguments;
        }

        if (!options.Has("yes"))
        {
            stderr.WriteLine("该操作会删除目录内容（或目录本身），请先确认后加 --yes 重新执行。");
            return ExitCodes.UserCanceled;
        }

        var extended = PathNormalizer.NormalizeAbsolute(target!);
        var info = ReparsePointOps.Inspect(extended);

        if (!info.PathExists)
        {
            stdout.WriteLine($"目标不存在，无需清理：{target}");
            return ExitCodes.Success;
        }

        if (info.IsReparsePoint)
        {
            stderr.WriteLine($"拒绝清理：{target} 是一个链接。清理它等于穿透到真实数据，请改用 unlink。");
            return ExitCodes.PrecheckFailed;
        }

        var includeRoot = options.Has("include-root");
        var result = includeRoot
            ? SafeTreeDeleter.DeleteTree(extended)
            : SafeTreeDeleter.ClearDirectoryContents(extended);

        stdout.WriteLine((includeRoot ? "已删除目录树：" : "已清空目录内容：") + result.Describe());
        foreach (var failure in result.Failures)
            stdout.WriteLine($"  ✗ {failure.Path}：{failure.Message}（{failure.Win32Code}）");

        return result.Success ? ExitCodes.Success : ExitCodes.PartialFailure;
    }

    private static int RunHelp(TextWriter stdout)
    {
        stdout.WriteLine("""
            DirPorter —— Windows 目录迁移与链接工具

            用法：dirporter <命令> [选项]

              move     --source <目录> --target <目录> [选项]
                       选项：--dry-run            只做预检与预览，零磁盘写入
                             --yes               接受全部需确认项（警告名单等）
                             --threads <n>       复制并发（默认 8）
                             --verify <档位>      metadata（默认）| content
                             --delete-backup     迁移后立即删除停车目录
                             --keep-backup-forever  永久保留停车目录
                             --keep-backup-days <n> 保留天数（默认 7）
                             --kill-locking      结束占用进程（默认否）
                             --deep-occupancy    逐文件占用检测（慢）
                             --archive-ledger <目录>  台账归档到指定位置
                             --json / --quiet
              recover            识别被强杀留下的中间态并给出修复建议
              list               列出迁移记录
              restore  --batch <batchId> [--entry <entryId>] [--ledger <目录>] [--clean-target]
              scan     --root <目录>（或 --path）[--min-size 100MB] [--threads n]
                       [--budget n] [--special] [--entries] [--locked] [--locked-deep]
                       [--estimate] [--no-cache] [--json]
                       目录树实时扫描（低内存并行）；--min-size 列出超阈值的顶层条目；
                       --estimate 走缓存估算（仅供预览，字节数可能过期）
              link     --source <旧路径> --target <新位置>
                       [--type auto|junction|symlink-dir|symlink-file|hardlink]
                       [--relative] [--force] [--dry-run] [--yes] [--json]
                       建立单个链接（四种 mklink 模式；auto 会说明推荐理由）
              unlink   --path <链接路径> [--dry-run] [--yes]
                       解除链接：只删链接本体，绝不触碰目标数据（非链接一律拒绝）
              linkdir  --source <源目录> --target <目标目录>
                       [--type auto|hardlink|symlink-file] [--incremental] [--dry-run] [--yes]
                       批量文件级链接：保持相对子目录结构；--incremental 跳过已存在的正确链接
              verify   [--id <记录ID>] [--batch <批次ID>] [--json]
                       校验迁移记录的链接健康度（源是否仍是链接、目标是否悬空）
              selfcheck [--json]
                       发布后自检：权限、robocopy、数据目录、两种台账后端往返、能力矩阵
              copy     --source <目录> --target <目录> [--engine native|robocopy]
                       [--no-security] [--no-sparse] [--allow-decrypt] [--verify metadata|content]
                       纯复制（不改动源、不建链接）。用于现场验证 / 提权后核对 ACL
              inspect  --path <任意路径> [--json]

              全局开关（可加在任意命令后）：
                --elevate            未提权时弹出 UAC，以管理员身份重启并执行同一命令
                                     （子进程输出会原样转发回来，退出码也一致）
                --elevate-relay <文件> 内部使用：提权子进程把输出写入该文件供父进程转发

              cleanresidue --target <目录> [--include-root] --yes
                         清理半成品残留 / 已确认无用的停车目录
                         （遇链接只删链接本体，绝不穿透目标数据）
              sweepbackups [--days 7] [--root <目录>] [--all] [--dry-run] [--yes]
                         按保留策略清理到期的 .migbak 停车目录
                         台账（.migbak\.ledger）永不清理
                         move 可加 --engine auto|robocopy|native 选择复制引擎
              help

            退出码：0 成功 / 1 部分失败 / 2 参数错误 / 3 前置校验不通过 /
                    4 用户取消 / 5 需要管理员权限 / 6 内部错误
            """);

        return ExitCodes.Success;
    }

    private static int Unknown(string command, TextWriter stderr)
    {
        stderr.WriteLine($"未知命令：{command}。运行 dirporter help 查看用法。");
        return ExitCodes.InvalidArguments;
    }

    // ============================ 输出 ============================

    private static void WriteHumanReport(MigrationBatchResult result, MigrationTaskOptions options, TextWriter stdout)
    {
        var report = result.Precheck;

        if (options.DryRun) stdout.WriteLine("=== 干跑预览（未产生任何磁盘写入）===");
        stdout.WriteLine($"批次：{result.BatchId}");

        if (report.IsBlocked)
        {
            stdout.WriteLine();
            stdout.WriteLine($"前置校验未通过（{report.BlockingResults.Count} 项阻止）：");
            foreach (var item in report.BlockingResults)
                stdout.WriteLine($"  ✗ [{item.Code}] {item.Title}{Environment.NewLine}      {item.Detail}{Environment.NewLine}      处理建议：{item.UserGuide}");
            return;
        }

        if (report.RequiresAcknowledge)
        {
            stdout.WriteLine();
            stdout.WriteLine($"需要确认的风险项（{report.AcknowledgeResults.Count} 项）：");
            foreach (var item in report.AcknowledgeResults)
                stdout.WriteLine($"  ! [{item.Code}] {item.Detail}{Environment.NewLine}      {item.UserGuide}");
        }

        stdout.WriteLine();
        stdout.WriteLine($"预检通过：{report.EffectiveEntries.Count} 个待迁移条目，共 {report.TotalSourceFileCount} 个文件、{SpaceRequirement.Format(report.TotalSourceDiskBytes)}。");

        foreach (var entry in report.EffectiveEntries)
        {
            stdout.WriteLine($"  · [{entry.EntryId}] {entry.SourcePlainPath} → {entry.TargetPlainPath}（链接 {entry.LinkKind}）");
            var probe = report.Probes.TryGetValue(entry.SourcePlainPath, out var p) ? p : null;
            if (probe is null) continue;

            foreach (var special in probe.SpecialObjects)
                stdout.WriteLine($"      标注：{special.Kind} × {special.Count}（例：{(special.Samples.Count > 0 ? special.Samples[0].Path : "-")}）");

            if (probe.DeepProbeSkippedForScale)
                stdout.WriteLine("      注意：文件数超过深入探测上限，硬链接/ADS 未逐文件检查。");
        }

        if (report.SkippedEntries.Count > 0)
        {
            stdout.WriteLine();
            stdout.WriteLine("已自动跳过的条目：");
            foreach (var skipped in report.SkippedEntries)
                stdout.WriteLine($"  ⊘ {skipped.SourcePath}：{skipped.Reason}");
        }

        if (options.DryRun)
        {
            stdout.WriteLine();

            // ⚠️ 这一段以前是**写死的步骤表**，在引入"同卷直接改名"之后就变成假话了
            // （明明不会复制、不会停车，预览却照样列 COPYING/PARKING/BACKUP_CLEANUP）。
            // 干跑的价值就是"所见即所得"，所以这里必须按**实际判定出的传输方式**输出。
            // 从预检结果重建计划：本方法只拿到报告，没有原始 plan。
            var dryRunPlan = new MigrationPlan(
                report.EffectiveEntries
                    .Select(static e => new MigrationPlanEntry(e.SourcePlainPath, e.TargetPlainPath, e.LinkKind, e.EntryId))
                    .ToList(),
                PrecheckOptions.Default);

            var transferModes = new MigrationEngine().DescribeTransferModes(dryRunPlan, options);
            var sameVolumeCount = transferModes.Count(static line => line.Contains("同卷", StringComparison.Ordinal));

            stdout.WriteLine($"传输方式（同卷 {sameVolumeCount} 项 / 跨卷 {transferModes.Count - sameVolumeCount} 项）：");
            foreach (var line in transferModes) stdout.WriteLine("  " + line);
            stdout.WriteLine();

            stdout.WriteLine(sameVolumeCount > 0
                ? "将执行的操作序列（同卷条目：直接改名，不复制、不停车、不产生备份）："
                : "将执行的操作序列（每条目）：");

            stdout.WriteLine("  1) OCCUPANCY_SCAN  占用扫描（只读）");
            stdout.WriteLine("  2) TARGET_PREPARE  创建缺失的目标父目录（同卷时不创建目标目录本身）");
            stdout.WriteLine($"  3) COPYING         复制（引擎：{DescribeEngine(options.CopyEngine)}）");
            if (options.CopyEngine == CopyEngineKind.Native)
            {
                var capability = FileSystem.SecurityInfoOps.Current;
                stdout.WriteLine($"                    CopyFileEx 多线程 + 字节级进度；安全信息：{capability.Describe}");
                stdout.WriteLine($"                    稀疏优化：{(options.ReplicateSparse ? "开启（按源分配范围，置零前先校验全零）" : "关闭")}"
                                 + $"，EFS 解密复制：{(options.AllowDecryptEncryptedFiles ? "允许（安全降级！）" : "拒绝")}");
            }
            else
            {
                stdout.WriteLine($"                    复制开关：{Copy.RobocopyCopier.DescribeCopySwitch(Privilege.Win32PrivilegeProbe.Current.IsElevated)}");
            }
            stdout.WriteLine("  4) VERIFYING       文件数 + 总字节 + 逐文件 size/mtime 比对");
            stdout.WriteLine("  5) PARKING         源目录同卷改名为 .migbak\\<name>_<ts>");
            stdout.WriteLine($"  6) LINKING         在原路径创建 {report.EffectiveEntries.FirstOrDefault()?.LinkKind.ToString() ?? "Junction"}");
            stdout.WriteLine("  7) LINK_VERIFY     解析目标 + 枚举子项 + 试读一个文件");
            stdout.WriteLine("  8) LEDGER_COMMIT   写入迁移台账（权威回滚依据）");
            stdout.WriteLine($"  9) BACKUP_CLEANUP  {DescribeBackupPolicy(options)}");

            stdout.WriteLine();
            stdout.WriteLine($"校验档位：--verify={options.Verify.ToString().ToLowerInvariant()}"
                             + (options.Verify == VerifyMode.Content
                                 ? "  ⚠️ 全文哈希会把 restore 校验从秒级拖到分钟级（2GB/5 万文件量级可能需数十分钟）"
                                 : "  元数据指纹（相对路径 + 长度 + mtime ticks），秒级完成"));
        }
        else
        {
            stdout.WriteLine();
            foreach (var entry in result.Entries)
            {
                stdout.WriteLine($"  [{(entry.Success ? "成功" : "失败")}] {entry.EntryId}：{entry.Message}");
                foreach (var step in entry.RollbackSteps) stdout.WriteLine($"      回滚：{step}");
                foreach (var failure in entry.RollbackFailures) stdout.WriteLine($"      ✗ 回滚问题：{failure}");
                if (entry.LedgerDirectory is not null) stdout.WriteLine($"      台账：{entry.LedgerDirectory}");
            }

            stdout.WriteLine(result.Success ? "全部完成。" : "存在失败条目，请查看上面的回滚结果。");
        }
    }

    /// <summary>解析人类可读的尺寸：100MB / 1.5GB / 1048576（纯字节）。</summary>
    internal static bool TryParseSize(string? text, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var value = text.Trim();
        var multiplier = 1L;

        var suffixes = new (string Suffix, long Factor)[]
        {
            ("TB", 1024L * 1024 * 1024 * 1024),
            ("GB", 1024L * 1024 * 1024),
            ("MB", 1024L * 1024),
            ("KB", 1024L),
            ("B", 1L),
        };

        foreach (var (suffix, factor) in suffixes)
        {
            if (!value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;

            multiplier = factor;
            value = value[..^suffix.Length].Trim();
            break;
        }

        if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var number) || number < 0)
        {
            return false;
        }

        bytes = (long)(number * multiplier);
        return true;
    }

    private static CopyEngineKind ParseEngine(string? value) => (value ?? "auto").Trim().ToLowerInvariant() switch
    {
        "native" or "b" or "copyfileex" => CopyEngineKind.Native,
        "robocopy" or "a" => CopyEngineKind.Robocopy,
        _ => CopyEngineKind.Auto,
    };

    private static string DescribeEngine(CopyEngineKind kind) => kind switch
    {
        CopyEngineKind.Native => "B / 自研 CopyFileEx（可暂停、字节级进度）",
        CopyEngineKind.Robocopy => "A / 系统 robocopy",
        _ => "A / 系统 robocopy（默认）",
    };

    /// <summary>
    /// 目录树扫描：看清规模、特殊对象与占用情况。
    ///
    /// 两种模式：
    /// <list type="bullet">
    /// <item><b>实时扫描（默认）</b>：低内存并行遍历，结果可作为迁移依据。</item>
    /// <item><b>估算（--estimate）</b>：按目录修改时间复用上次结果，秒回但**字节数可能过期**，
    /// 只用于预览；CLI 会强制打印有效性说明。</item>
    /// </list>
    /// </summary>
    private static int RunScan(Args options, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        // 需求命令行写作 --root（可多个扫描根），这里同时接受 --root 与 --path，避免脚本写法漂移。
        var path = options.Get("root", null) ?? options.Get("path", null);
        if (string.IsNullOrWhiteSpace(path))
        {
            stderr.WriteLine("scan 需要 --root <目录>（或 --path）。");
            return ExitCodes.InvalidArguments;
        }

        var minSize = 0L;
        if (options.TryGet("min-size", out var minSizeText) && !TryParseSize(minSizeText, out minSize))
        {
            stderr.WriteLine($"无法解析 --min-size：{minSizeText}。示例：100MB / 1.5GB / 1048576。");
            return ExitCodes.InvalidArguments;
        }

        if (options.Has("estimate"))
        {
            var cache = ScanEstimateCache.Open(options.Get("cache", null), enabled: !options.Has("no-cache"));
            var estimate = cache.EstimateTree(path!, cancellationToken);
            cache.Save();

            if (options.Has("json"))
            {
                stdout.WriteLine(JsonSerializer.Serialize(new
                {
                    mode = "estimate",
                    root = estimate.RootPath,
                    isEstimate = estimate.IsEstimate,
                    caveat = ScanEstimateResult.ValidityCaveat,
                    fileCount = estimate.FileCount,
                    directoryCount = estimate.DirectoryCount,
                    logicalBytes = estimate.LogicalBytes,
                    diskBytes = estimate.DiskBytes,
                    cacheHits = estimate.CacheHits,
                    cacheMisses = estimate.CacheMisses,
                    oldestCaptureUtc = estimate.OldestCaptureUtc,
                    elapsedMs = (long)estimate.Elapsed.TotalMilliseconds,
                }, JsonOptions));
            }
            else
            {
                stdout.WriteLine(estimate.Describe());
                stdout.WriteLine("  ⚠️ " + ScanEstimateResult.ValidityCaveat);
            }

            return ExitCodes.Success;
        }

        var scanOptions = new ScanOptions
        {
            Threads = options.GetInt("threads", 0),
            MaxEntries = options.GetInt("budget", 0),
            CollectEntries = options.Has("entries") || options.Has("locked-deep"),
            DetectSpecialObjects = !options.Has("no-special"),
            DeepProbeFileLimit = options.GetInt("deep-probe-limit", 20_000),
        };

        // --min-size：按顶层子项归集体积，用于"哪个目录最占地方"这类判断。
        // 只在需要时挂收集器，且字典大小 = 顶层子项数（不是文件总数），内存可控。
        Dictionary<string, (long Bytes, long Files, bool IsDirectory)>? buckets = null;
        Action<ScanEntry>? collector = null;

        if (minSize > 0)
        {
            var map = new Dictionary<string, (long Bytes, long Files, bool IsDirectory)>(StringComparer.OrdinalIgnoreCase);
            buckets = map;

            collector = entry =>
            {
                var separator = entry.RelativePath.IndexOf('\\');
                var top = separator < 0 ? entry.RelativePath : entry.RelativePath[..separator];
                var isTopDirectory = separator >= 0 || entry.Kind == ScanEntryKind.Directory;

                var current = map.TryGetValue(top, out var existing) ? existing : (Bytes: 0L, Files: 0L, IsDirectory: isTopDirectory);

                if (entry.Kind == ScanEntryKind.File)
                    current = (current.Bytes + entry.Size, current.Files + 1, current.IsDirectory);

                map[top] = current;
            };
        }

        var summary = DirectoryScanner.Scan(path!, scanOptions, null, cancellationToken, collector);

        if (options.Has("json"))
        {
            stdout.WriteLine(JsonSerializer.Serialize(new
            {
                mode = "scan",
                root = summary.RootPath,
                fileCount = summary.FileCount,
                directoryCount = summary.DirectoryCount,
                reparsePointCount = summary.ReparsePointCount,
                logicalBytes = summary.LogicalBytes,
                diskBytes = summary.DiskBytes,
                maxFileBytes = summary.MaxFileBytes,
                maxFilePath = summary.MaxFilePath,
                extensions = summary.Extensions,
                specialObjects = summary.SpecialObjects.Select(x => new { kind = x.Kind.ToString(), count = x.Count }),
                errors = summary.Errors.Select(e => new { path = e.Path, message = e.Message, win32 = e.Win32Code }),
                deepProbeApplied = summary.DeepProbeApplied,
                deepProbeSkippedForScale = summary.DeepProbeSkippedForScale,
                truncated = summary.Truncated,
                elapsedMs = (long)summary.Elapsed.TotalMilliseconds,
            }, JsonOptions));
        }
        else
        {
            stdout.WriteLine(summary.Describe());

            if (options.Has("special"))
            {
                foreach (var special in summary.SpecialObjects)
                {
                    stdout.WriteLine($"  {ScanSummary.Describe(special.Kind)} × {special.Count}");
                    foreach (var sample in special.Samples) stdout.WriteLine($"      {sample.Path} — {sample.Detail}");
                }
            }
        }

        if (buckets is not null)
        {
            var hits = buckets
                .Where(kv => kv.Value.Bytes >= minSize)
                .OrderByDescending(kv => kv.Value.Bytes)
                .ToList();

            stdout.WriteLine();
            stdout.WriteLine($"体积 ≥ {SpaceRequirement.Format(minSize)} 的顶层条目（{hits.Count} 项，按体积降序）：");

            foreach (var (name, value) in hits)
            {
                stdout.WriteLine($"  {SpaceRequirement.Format(value.Bytes),12}  {value.Files,7} 个文件   {name}"
                                 + (value.IsDirectory ? "\\" : string.Empty));
            }

            if (hits.Count == 0) stdout.WriteLine("  （没有达到阈值的条目）");
        }

        if (options.Has("locked"))
        {
            var locks = new RestartManagerLockService().FindLockingProcesses(new[] { path! });
            stdout.WriteLine("  " + locks.Describe());

            if (options.Has("locked-deep") && summary.Entries is not null)
            {
                var files = summary.Entries
                    .Where(static e => e.Kind == ScanEntryKind.File)
                    .Select(e => PathNormalizer.Combine(summary.RootPath, e.RelativePath))
                    .ToList();

                var attribution = new RestartManagerLockService().FindLockedResources(files);
                stdout.WriteLine("  " + attribution.Describe().Replace(Environment.NewLine, Environment.NewLine + "  "));
            }
        }

        // 遍历错误与截断必须以退出码体现，避免被脚本当成"扫完了"
        if (summary.Truncated) return ExitCodes.PartialFailure;
        return summary.Errors.Count > 0 ? ExitCodes.PartialFailure : ExitCodes.Success;
    }

    /// <summary>
    /// 纯复制（不做迁移、不建链接、不改动源）。
    /// 用途：① 现场验证两个引擎的产物是否一致；② 提权后验证 ACL/所有者/审计是否被完整搬运。
    /// </summary>
    private static int RunCopy(Args options, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        if (!options.TryGet("source", out var source) || !options.TryGet("target", out var target))
        {
            stderr.WriteLine("copy 需要 --source 与 --target。");
            return ExitCodes.InvalidArguments;
        }

        var engine = ParseEngine(options.Get("engine", "native"));
        IFileCopier copier = engine == CopyEngineKind.Robocopy ? new RobocopyCopier() : new CopyFileExCopier();

        var sourceExtended = PathNormalizer.NormalizeAbsolute(source!);
        var probe = TreeProbe.Probe(sourceExtended, new TreeProbeOptions(), null, cancellationToken);

        var copyOptions = new CopyOptions
        {
            Threads = options.GetInt("threads", 8),
            SourceTotalBytes = probe.DiskBytes,
            SourceFileCount = probe.FileCount,
            CopySecurity = !options.Has("no-security"),
            ReplicateSparse = !options.Has("no-sparse"),
            AllowDecryptEncryptedFiles = options.Has("allow-decrypt"),
        };

        var result = copier.CopyAsync(
            new CopyRequest(sourceExtended, PathNormalizer.NormalizeAbsolute(target!), copyOptions),
            null, cancellationToken).GetAwaiter().GetResult();

        stdout.WriteLine($"引擎：{copier.Name}");
        stdout.WriteLine(result.Describe());
        stdout.WriteLine($"已复制 {result.FilesCopied} 个文件 / {result.FilesSkipped} 个跳过 / {result.FailedCount} 项失败。");
        if (result.SecurityNote is not null) stdout.WriteLine($"安全信息：{result.SecurityNote}");
        if (copier is CopyFileExCopier native) stdout.WriteLine($"安全能力：{native.SecurityCapability.Describe}");

        foreach (var failure in result.Failures.Take(20))
            stdout.WriteLine($"  ✗ {failure.Path}：{failure.Reason}");

        if (options.Has("verify"))
        {
            var verify = TreeComparer.Compare(source!, target!, new VerifyOptions { Mode = options.Get("verify", "metadata") == "content" ? VerifyMode.Content : VerifyMode.Metadata });
            stdout.WriteLine(verify.Describe());
            if (!verify.IsConsistent) return ExitCodes.PartialFailure;
        }

        return result.Outcome == CopyOutcome.Success ? ExitCodes.Success : ExitCodes.PartialFailure;
    }

    /// <summary>
    /// 清理到期的停车目录（备份保留策略）。
    /// **只会删除 .migbak 之下的目录**；台账（.migbak\.ledger）永不清理。
    /// </summary>
    private static int RunSweepBackups(Args options, TextWriter stdout, TextWriter stderr)
    {
        var days = options.GetInt("days", 7);
        var dryRun = options.Has("dry-run") || !options.Has("yes");

        if (!options.Has("yes") && !options.Has("dry-run"))
        {
            stdout.WriteLine("这是删除操作，先做演练预览。确认无误后加 --yes 实际执行。");
        }

        var roots = options.TryGet("root", out var root) && !string.IsNullOrWhiteSpace(root)
            ? new[] { root! }
            : Array.Empty<string>();

        var sweepOptions = new Backup.BackupSweepOptions
        {
            RetentionDays = days,
            DryRun = dryRun,
            Force = options.Has("all"),
            ScanRoots = roots,
            ScanDepth = options.GetInt("depth", 3),
        };

        var result = new Backup.BackupRetentionService().Sweep(sweepOptions);

        stdout.WriteLine(result.Describe());
        foreach (var item in result.Items)
            stdout.WriteLine($"  [{item.Action}] {item.ParkingPath}：{item.Detail}");

        if (result.DryRun && result.Deleted > 0)
            stdout.WriteLine("提示：以上为演练结果；加 --yes 才会实际删除。");

        return result.Failed > 0 ? ExitCodes.PartialFailure : ExitCodes.Success;
    }

    private static string DescribeBackupPolicy(MigrationTaskOptions options) => options.BackupPolicy switch
    {
        BackupCleanupPolicy.Immediate => "迁移完成后立即删除停车目录（失去回滚兜底）",
        BackupCleanupPolicy.Never => "永久保留停车目录（不自动清理）",
        _ => $"保留停车目录 {options.BackupRetentionDays} 天（到期由备份保留策略清理）",
    };

    private static object BuildMoveJson(MigrationBatchResult result) => new
    {
        batchId = result.BatchId,
        dryRun = result.DryRun,
        blocked = result.Precheck.IsBlocked,
        requiresAcknowledge = result.Precheck.RequiresAcknowledge,
        totalFiles = result.Precheck.TotalSourceFileCount,
        totalBytes = result.Precheck.TotalSourceDiskBytes,
        ledgerDirectory = result.LedgerDirectory,
        elapsedMs = (long)result.Elapsed.TotalMilliseconds,
        results = result.Precheck.Results.Select(r => new
        {
            code = r.Code,
            severity = r.Severity.ToString(),
            title = r.Title,
            detail = r.Detail,
            action = r.Action.ToString(),
            userGuide = r.UserGuide,
            relatedPath = r.RelatedPath,
            win32 = r.Win32,
            entryId = r.EntryId,
        }),
        entries = result.Precheck.EffectiveEntries.Select(e => new
        {
            entryId = e.EntryId,
            sourcePath = e.SourcePlainPath,
            targetPath = e.TargetPlainPath,
            linkKind = e.LinkKind.ToString(),
        }),
        skipped = result.Precheck.SkippedEntries,
        executed = result.Entries.Select(e => new
        {
            entryId = e.EntryId,
            finalState = e.FinalState.ToString(),
            success = e.Success,
            message = e.Message,
            error = e.Error,
            rollbackSteps = e.RollbackSteps,
            rollbackFailures = e.RollbackFailures,
        }),
    };

    private static string Truncate(string value, int length) => value.Length <= length ? value : "…" + value[^Math.Max(0, length - 1)..];

    // ============================ 参数解析 ============================

    /// <summary>极简参数解析（内部可见：<see cref="LinkCommands"/> 复用同一套解析，避免两套规则漂移）。</summary>
    internal sealed class Args
    {
        private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

        public static Args Parse(IEnumerable<string> tokens)
        {
            var args = new Args();
            string? pending = null;

            foreach (var token in tokens)
            {
                if (token.StartsWith("--", StringComparison.Ordinal))
                {
                    if (pending is not null) args._values[pending] = null;
                    pending = token[2..];
                    continue;
                }

                if (pending is not null)
                {
                    args._values[pending] = token;
                    pending = null;
                }
            }

            if (pending is not null) args._values[pending] = null;
            return args;
        }

        public bool Has(string name) => _values.ContainsKey(name) && _values[name] is null or "true";

        public bool TryGet(string name, out string? value)
        {
            value = null;
            if (!_values.TryGetValue(name, out var stored) || stored is null) return false;
            value = stored;
            return true;
        }

        public string? Get(string name, string? fallback) => TryGet(name, out var value) ? value : fallback;

        public int GetInt(string name, int fallback)
            => TryGet(name, out var value) && int.TryParse(value, out var parsed) ? parsed : fallback;
    }
}
