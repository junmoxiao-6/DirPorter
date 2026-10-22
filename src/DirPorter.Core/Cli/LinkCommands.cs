using System.Text.Json;
using DirPorter.Core.Config;
using DirPorter.Core.Diagnostics;
using DirPorter.Core.FileSystem;
using DirPorter.Core.Journal;
using DirPorter.Core.Ledger;
using DirPorter.Core.Links;
using DirPorter.Core.Privilege;
using DirPorter.Core.Scan;

namespace DirPorter.Core.Cli;

/// <summary>
/// 链接类命令：<c>link</c> / <c>unlink</c> / <c>linkdir</c>，以及 <c>verify</c> 与 <c>selfcheck</c>。
///
/// 与 CLI 主机分开成独立文件的原因：这几条命令的逻辑量已经不小，
/// 且它们的共同点是"**只经 <see cref="ILinkProvider"/> 操作链接**"——
/// 集中在一处便于审阅"没有任何一条路径会绕过 provider 去 shell 拼 mklink"。
/// </summary>
internal static class LinkCommands
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ==================================================================
    //  link：建立单个链接（四种 mklink 模式 + auto）
    // ==================================================================

    public static int RunLink(CliHost.Args options, TextWriter stdout, TextWriter stderr)
    {
        if (!options.TryGet("source", out var source) || !options.TryGet("target", out var target))
        {
            stderr.WriteLine("link 需要 --source <要创建链接的路径> 与 --target <链接指向的位置>。");
            return ExitCodes.InvalidArguments;
        }

        if (!TryParseKind(options.Get("type", "auto"), out var kind, out var typeError))
        {
            stderr.WriteLine(typeError);
            return ExitCodes.InvalidArguments;
        }

        var relative = options.Has("relative");
        var request = new LinkRequest(source!, target!, kind, PreferRelativeTarget: relative);
        var service = LinkService.CreateDefault();

        var resolution = service.Resolve(request);
        var dryRun = options.Has("dry-run");

        stdout.WriteLine($"链接路径：{PathNormalizer.StripExtendedPrefix(PathNormalizer.NormalizeAbsolute(source!))}");
        stdout.WriteLine($"指向目标：{PathNormalizer.StripExtendedPrefix(PathNormalizer.NormalizeAbsolute(target!))}");
        stdout.WriteLine($"选用的链接类型：{DescribeKind(resolution.Kind)}"
                         + (resolution.WasAuto ? $"（auto 推荐：{resolution.Reason}）" : "（用户显式指定）"));

        if (!resolution.Capability.CanHandle)
        {
            stderr.WriteLine($"能力校验不通过：[{resolution.Capability.Code}] {resolution.Capability.Reason}");
            if (!string.IsNullOrWhiteSpace(resolution.Capability.Suggestion))
                stderr.WriteLine($"  建议：{resolution.Capability.Suggestion}");

            // 权限类的拒绝给出专门的退出码，便于脚本区分"需要提权"与"这条路本身走不通"。
            return resolution.Capability.Code == LinkCapabilityCode.PrivilegeNeeded
                ? ExitCodes.NeedsAdministrator
                : ExitCodes.PrecheckFailed;
        }

        stdout.WriteLine($"能力校验：通过。{resolution.Capability.Reason}");

        if (dryRun || !options.Has("yes"))
        {
            stdout.WriteLine();
            stdout.WriteLine("将执行的操作：");
            stdout.WriteLine($"  1) 在 {source} 创建 {DescribeKind(resolution.Kind)}，指向 {target}");

            if (resolution.Kind is LinkKind.Junction)
                stdout.WriteLine("     说明：目录联接会把相对/UNC 目标转为绝对本地路径（与 mklink /J 一致）。");
            if (resolution.Kind is LinkKind.HardLink)
                stdout.WriteLine("     说明：硬链接不占额外空间，但必须与目标在同一卷，且删除链接不影响数据。");
            if (relative && resolution.Kind is LinkKind.Junction or LinkKind.HardLink)
                stdout.WriteLine("     注意：该类型不支持相对目标，--relative 将被忽略。");

            stdout.WriteLine();
            stdout.WriteLine("其余三种类型的可用性：");
            foreach (var candidate in service.EvaluateAll(request).Where(c => c.Kind != resolution.Kind))
            {
                stdout.WriteLine($"  · {DescribeKind(candidate.Kind)}："
                                 + (candidate.Capability.CanHandle ? "可用" : $"不可用（{candidate.Capability.Reason}）"));
            }

            if (!dryRun) stdout.WriteLine("确认无误后加 --yes 实际执行。");
            return dryRun ? ExitCodes.Success : ExitCodes.UserCanceled;
        }

        try
        {
            service.Create(request, new LinkCreateOptions
            {
                Relative = relative,
                ReplaceExisting = options.Has("force"),
                DryRun = false,
            });
        }
        catch (LinkPrivilegeRequiredException ex)
        {
            stderr.WriteLine(ex.Message);
            return ExitCodes.NeedsAdministrator;
        }
        catch (InvalidOperationException ex)
        {
            stderr.WriteLine($"创建失败（能力校验未通过）：{ex.Message}");
            return ExitCodes.PrecheckFailed;
        }
        catch (DirPorterNativeException ex)
        {
            stderr.WriteLine($"{ex.Message}（Win32 {ex.Win32Code}）");
            return ExitCodes.InternalError;
        }

        var info = service.Inspect(source!);
        stdout.WriteLine();
        stdout.WriteLine("创建成功：" + info.Describe());

        if (options.Has("json")) stdout.WriteLine(JsonSerializer.Serialize(BuildLinkJson(resolution, info), JsonOptions));

        return ExitCodes.Success;
    }

    // ==================================================================
    //  unlink：解除链接（只删链接本体）
    // ==================================================================

    public static int RunUnlink(CliHost.Args options, TextWriter stdout, TextWriter stderr)
    {
        if (!options.TryGet("path", out var path))
        {
            stderr.WriteLine("unlink 需要 --path <链接路径>。");
            return ExitCodes.InvalidArguments;
        }

        var service = LinkService.CreateDefault();
        var info = service.Inspect(path!);

        if (!info.PathExists)
        {
            // 幂等：目标已经不存在，视为已达成目的（不判错，脚本可以重复调用）。
            stdout.WriteLine($"路径不存在，无需解除链接：{info.Path}");
            return ExitCodes.Success;
        }

        if (info.Kind is null)
        {
            // ⚠️ 安全红线：不是链接就绝不删除 —— 删掉它会丢真实数据。
            stderr.WriteLine(info.IsReparsePoint
                ? $"拒绝解除链接：{info.Path} 是重解析点但不是本工具支持的类型（标签 0x{info.ReparseTag:X8}）。"
                : $"拒绝解除链接：{info.Path} 不是链接（是普通文件或目录），删除它会丢失真实数据。");
            return ExitCodes.PrecheckFailed;
        }

        stdout.WriteLine($"链接路径：{info.Path}");
        stdout.WriteLine($"链接类型：{DescribeKind(info.Kind.Value)}");
        stdout.WriteLine($"指向目标：{info.TargetAbsolute ?? "(无法解析)"}"
                         + (info.IsRelative ? "（相对链接）" : string.Empty)
                         + (info.IsDangling ? "  ⚠️ 目标已丢失（悬空链接）" : string.Empty));

        if (options.Has("dry-run") || !options.Has("yes"))
        {
            stdout.WriteLine();
            stdout.WriteLine("将执行的操作：删除该链接本体（不会触碰目标数据）。");
            if (!options.Has("dry-run")) stdout.WriteLine("确认无误后加 --yes 实际执行。");
            return options.Has("dry-run") ? ExitCodes.Success : ExitCodes.UserCanceled;
        }

        var targetBefore = info.TargetAbsolute;
        int? sentinelBefore = targetBefore is not null && Directory.Exists(targetBefore)
            ? SafeProbe(targetBefore)
            : null;

        try
        {
            var deletedKind = service.Delete(path!);
            stdout.WriteLine();
            stdout.WriteLine($"已解除链接：{info.Path}（{DescribeKind(deletedKind)}）");
            stdout.WriteLine("链接本体已删除，目标数据未被触碰。");

            // 事后自证：目标目录仍然可枚举（悬空链接没有可验证的目标，跳过）。
            if (sentinelBefore is not null && Directory.Exists(targetBefore!))
            {
                var after = SafeProbe(targetBefore!);
                stdout.WriteLine(after == sentinelBefore
                    ? $"目标目录完好（{after} 个条目）。"
                    : $"⚠️ 目标目录条目数发生变化：{sentinelBefore} → {after}，请人工确认。");
            }
        }
        catch (LinkDeleteRefusedException ex)
        {
            stderr.WriteLine(ex.Message);
            return ExitCodes.PrecheckFailed;
        }
        catch (DirPorterNativeException ex)
        {
            stderr.WriteLine($"{ex.Message}（Win32 {ex.Win32Code}）");
            return ExitCodes.InternalError;
        }

        return ExitCodes.Success;
    }

    // ==================================================================
    //  linkdir：批量文件级链接（保持相对子目录结构，支持增量）
    // ==================================================================

    public static int RunLinkDir(CliHost.Args options, TextWriter stdout, TextWriter stderr)
    {
        if (!options.TryGet("source", out var source) || !options.TryGet("target", out var target))
        {
            stderr.WriteLine("linkdir 需要 --source <源目录> 与 --target <目标目录>。");
            return ExitCodes.InvalidArguments;
        }

        if (!TryParseKind(options.Get("type", "auto"), out var kind, out var typeError))
        {
            stderr.WriteLine(typeError);
            return ExitCodes.InvalidArguments;
        }

        // linkdir 只支持文件级链接：目录不能做硬链接，符号链接在这里也没有意义。
        if (kind is LinkKind.Junction or LinkKind.DirectorySymlink)
        {
            stderr.WriteLine("linkdir 只支持 --type hardlink 或 --type symlink-file；目录级链接请用 link 命令。");
            return ExitCodes.InvalidArguments;
        }

        var sourceExtended = PathNormalizer.NormalizeAbsolute(source!);
        var targetExtended = PathNormalizer.NormalizeAbsolute(target!);

        if (!ReparsePointOps.Inspect(sourceExtended).PathExists)
        {
            stderr.WriteLine($"源目录不存在：{source}");
            return ExitCodes.PrecheckFailed;
        }

        var dryRun = options.Has("dry-run");
        var incremental = options.Has("incremental");

        // auto：同卷用硬链接（零额外空间），跨卷退化为文件符号链接。
        if (kind is null)
        {
            kind = SameVolume(sourceExtended, targetExtended) ? LinkKind.HardLink : LinkKind.FileSymlink;
            stdout.WriteLine($"auto 选择：{(kind == LinkKind.HardLink ? "硬链接（同卷，零额外空间）" : "文件符号链接（跨卷，硬链接不支持）")}");
        }

        if (kind == LinkKind.HardLink && !SameVolume(sourceExtended, targetExtended))
        {
            stderr.WriteLine("硬链接不支持跨卷：源与目标不在同一卷。请改用 --type symlink-file。");
            return ExitCodes.PrecheckFailed;
        }

        if (!options.Has("yes") && !dryRun)
        {
            stdout.WriteLine($"将把 {source} 下的每个文件在 {target} 下建立 {DescribeKind(kind.Value)}，保持相对子目录结构。");
            stdout.WriteLine("确认无误后加 --yes 实际执行。");
            return ExitCodes.UserCanceled;
        }

        var service = LinkService.CreateDefault();
        var created = 0;
        var skipped = 0;
        var failed = 0;
        var shown = 0;
        var failures = new List<string>();

        // 刻意单线程：链接创建是逐条系统调用，多线程不提升吞吐，却会让失败归因变复杂。
        // 用流式回调（而非先收集条目）保证内存 O(1)，10 万文件也不会把条目常驻内存。
        DirectoryScanner.Scan(
            sourceExtended,
            new ScanOptions { Threads = 1, DetectSpecialObjects = false, CollectEntries = false },
            progress: null,
            cancellationToken: CancellationToken.None,
            collector: entry =>
            {
                if (entry.Kind != ScanEntryKind.File) return;

                var linkPath = PathNormalizer.Combine(targetExtended, entry.RelativePath);
                var dataPath = PathNormalizer.Combine(sourceExtended, entry.RelativePath);

                var request = new LinkRequest(linkPath, dataPath, kind);

                // 增量：已存在且指向同一份数据就跳过。
                if (incremental && IsAlreadyLinked(service, request, kind.Value))
                {
                    skipped++;
                    return;
                }

                if (dryRun)
                {
                    if (shown < 20) { stdout.WriteLine($"  · {PathNormalizer.StripExtendedPrefix(linkPath)} → {PathNormalizer.StripExtendedPrefix(dataPath)}"); shown++; }
                    created++;
                    return;
                }

                try
                {
                    var parent = Path.GetDirectoryName(PathNormalizer.StripExtendedPrefix(linkPath));
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                    service.Create(request, new LinkCreateOptions { AllowUnprivilegedSymlink = true });
                    created++;
                }
                catch (Exception ex)
                {
                    failed++;
                    if (failures.Count < 20)
                        failures.Add($"{PathNormalizer.StripExtendedPrefix(dataPath)}：{ex.Message}");
                }
            });

        stdout.WriteLine();
        stdout.WriteLine(dryRun
            ? $"演练：将创建 {created} 个链接（保持相对子目录结构），未产生任何写入。"
            : $"完成：创建 {created} 个链接，跳过 {skipped} 个，失败 {failed} 个。");

        if (incremental && skipped > 0) stdout.WriteLine($"  增量模式：{skipped} 个已存在且指向一致的链接被跳过。");
        foreach (var failure in failures) stdout.WriteLine($"  ✗ {failure}");

        if (failed > 0)
            stdout.WriteLine("注意：linkdir 是**追加式**操作（只新建链接、不动数据），因此不会自动回滚已建链接；请按上面的失败清单处理。");

        return failed > 0 ? ExitCodes.PartialFailure : ExitCodes.Success;
    }

    // ==================================================================
    //  verify：校验迁移记录的链接健康度
    // ==================================================================

    public static int RunVerify(CliHost.Args options, TextWriter stdout, TextWriter stderr)
    {
        var index = new MigrationIndexStore();
        var all = index.LoadAll();

        if (all.Count == 0)
        {
            stdout.WriteLine("没有迁移记录可校验。");
            return ExitCodes.Success;
        }

        IReadOnlyList<MigrationRecord> targets;

        if (options.TryGet("id", out var id))
        {
            targets = all.Where(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase)).ToList();
            if (targets.Count == 0)
            {
                stderr.WriteLine($"找不到记录：{id}");
                return ExitCodes.InvalidArguments;
            }
        }
        else if (options.TryGet("batch", out var batch))
        {
            targets = all.Where(r => string.Equals(r.BatchId, batch, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        else
        {
            targets = all;
        }

        var service = LinkService.CreateDefault();
        var results = new List<object>();
        var unhealthy = 0;

        foreach (var record in targets)
        {
            var info = service.Inspect(record.SourcePath);

            var status = "健康";
            var detail = string.Empty;

            if (!info.PathExists)
            {
                status = "源路径缺失";
                detail = "源路径既不是链接也不存在：可能被手工删除或移动。";
            }
            else if (info.Kind is null)
            {
                status = "源已不是链接";
                detail = "源路径是真实目录/文件：迁移可能已被还原，或链接被替换。";
            }
            else if (info.IsDangling)
            {
                status = "悬空链接";
                detail = $"链接指向 {info.TargetAbsolute ?? "(无法解析)"}，但该目标当前不可访问。";
            }
            else
            {
                detail = $"→ {info.TargetAbsolute}";
            }

            if (status != "健康") unhealthy++;

            stdout.WriteLine($"[{status}] {record.Id}  {record.SourcePath}  {detail}");
            results.Add(new
            {
                id = record.Id,
                batchId = record.BatchId,
                sourcePath = record.SourcePath,
                targetPath = record.TargetPath,
                status,
                detail,
                linkKind = info.Kind?.ToString(),
                linkTarget = info.TargetAbsolute,
                isDangling = info.IsDangling,
            });
        }

        if (options.Has("json")) stdout.WriteLine(JsonSerializer.Serialize(results, JsonOptions));

        stdout.WriteLine();
        stdout.WriteLine($"共校验 {targets.Count} 条：健康 {targets.Count - unhealthy}，异常 {unhealthy}。");

        return unhealthy > 0 ? ExitCodes.PartialFailure : ExitCodes.Success;
    }

    // ==================================================================
    //  selfcheck：发布后自检（尤其是单文件发布里原生依赖是否可用）
    // ==================================================================

    public static int RunSelfCheck(CliHost.Args options, TextWriter stdout, TextWriter stderr)
    {
        var failures = 0;
        var results = new List<object>();

        void Check(string name, bool ok, string detail)
        {
            if (!ok) failures++;
            stdout.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name} — {detail}");
            results.Add(new { name, ok, detail });
        }

        var assembly = typeof(LinkCommands).Assembly;

        stdout.WriteLine("=== DirPorter 自检 ===");
        stdout.WriteLine($"  版本：{assembly.GetName().Version}");
        stdout.WriteLine($"  运行时：{Environment.Version}  架构：{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        stdout.WriteLine($"  操作系统：{Environment.OSVersion.VersionString}（{(Environment.Is64BitProcess ? "64" : "32")} 位进程）");
        stdout.WriteLine();

        // 1) 权限环境（不判失败：普通权限是合法运行方式，只如实报告）
        var privileges = Win32PrivilegeProbe.Current;
        stdout.WriteLine($"  权限环境：{privileges.Describe()}");
        Check("权限探测可用", true, privileges.CanCreateSymbolicLink ? "可创建符号链接" : "符号链接需管理员或开发者模式（功能降级，不算故障）");

        // 2) robocopy（引擎 A 依赖）
        var robocopy = Path.Combine(PathNormalizer.StripExtendedPrefix(Environment.SystemDirectory), "Robocopy.exe");
        Check("robocopy 可用（复制引擎 A）", File.Exists(robocopy), robocopy);

        // 3) 数据根目录
        var dataRoot = DirPorterPaths.DataRoot;
        Check("数据根目录可写", !DirPorterPaths.IsFallback, dataRoot + (DirPorterPaths.IsFallback ? $"（已降级：{DirPorterPaths.FallbackReason}）" : string.Empty));

        // 4) 两种台账后端往返（SQLite 这条专门验证单文件发布里原生 e_sqlite3 已内置）
        var scratch = Path.Combine(Path.GetTempPath(), "dirporter-selfcheck-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var jsonlDir = Path.Combine(scratch, "jsonl");
            var jsonl = MigrationLedgerFactory.Create(jsonlDir, entryCountHint: 1, batchId: "mig-selfcheck-jsonl");
            jsonl.AppendEvent("e-01", "SELFCHECK", "ok", "往返测试");
            jsonl.WriteEntry(BuildProbeEntry("mig-selfcheck-jsonl"));
            var jsonlBack = jsonl.Read();
            Check("台账 JSONL 后端往返", jsonlBack.Entries.Count == 1, $"条目 {jsonlBack.Entries.Count} 条，文件 {Path.GetFileName(jsonl.PrimaryFilePath)}");

            var sqliteDir = Path.Combine(scratch, "sqlite");
            var sqlite = MigrationLedgerFactory.Create(sqliteDir, entryCountHint: LedgerSchema.SqliteThreshold + 1, batchId: "mig-selfcheck-sqlite");
            Check("台账已按阈值切到 SQLite", sqlite.StorageKind == LedgerStorageKind.Sqlite, sqlite.StorageKind.ToString());
            sqlite.AppendEvent("e-01", "SELFCHECK", "ok", "往返测试");
            sqlite.WriteEntry(BuildProbeEntry("mig-selfcheck-sqlite"));
            var sqliteBack = sqlite.Read();
            Check("台账 SQLite 后端往返（原生 e_sqlite3 已内置）", sqliteBack.Entries.Count == 1,
                $"条目 {sqliteBack.Entries.Count} 条，文件 {Path.GetFileName(sqlite.PrimaryFilePath)}");
        }
        catch (Exception ex)
        {
            Check("台账后端往返", false, ex.GetType().Name + "：" + ex.Message);
        }
        finally
        {
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); } catch { /* 自检清理失败不影响结论 */ }
        }

        // 5) 四种链接类型的能力矩阵（只报告，不写入）
        try
        {
            var probeTarget = Path.Combine(Path.GetTempPath(), "dirporter-selfcheck-target");
            Directory.CreateDirectory(probeTarget);
            try
            {
                var service = LinkService.CreateDefault();
                var request = new LinkRequest(Path.Combine(Path.GetTempPath(), "dirporter-selfcheck-link"), probeTarget);
                var matrix = service.EvaluateAll(request)
                    .Select(c => $"{DescribeKind(c.Kind)}={(c.Capability.CanHandle ? "可用" : c.Capability.Code.ToString())}");

                stdout.WriteLine();
                stdout.WriteLine("  四种链接类型能力矩阵：" + string.Join("，", matrix));
                Check("能力矩阵可枚举", true, string.Join("，", matrix));
            }
            finally
            {
                try { Directory.Delete(probeTarget, recursive: false); } catch { /* 已存在则忽略 */ }
            }
        }
        catch (Exception ex)
        {
            Check("能力矩阵可枚举", false, ex.Message);
        }

        if (options.Has("json")) stdout.WriteLine(JsonSerializer.Serialize(new { failures, checks = results }, JsonOptions));

        stdout.WriteLine();
        stdout.WriteLine(failures == 0 ? "自检通过。" : $"自检发现 {failures} 项问题。");

        return failures == 0 ? ExitCodes.Success : ExitCodes.InternalError;
    }

    private static LedgerEntry BuildProbeEntry(string batchId) => new()
    {
        BatchId = batchId,
        EntryId = "e-01",
        SourcePath = @"C:\selfcheck\source",
        ParkingPath = @"C:\selfcheck\.migbak\source_x",
        TargetPath = @"D:\selfcheck\target",
        LinkPath = @"C:\selfcheck\source",
        LinkType = nameof(LinkKind.Junction),
        VolumeSource = "C:",
        VolumeTarget = "D:",
        Operations = new List<LedgerOperation>
        {
            new() { Step = "SELFCHECK", At = DateTimeOffset.Now, Status = "ok" },
        },
        Checksum = "selfcheck",
    };

    // ==================================================================
    //  辅助
    // ==================================================================

    internal static bool TryParseKind(string? value, out LinkKind? kind, out string error)
    {
        // 解析规则统一收敛在 LinkKinds（UI 设置、历史记录回读用的是同一份），
        // 避免"CLI 认 dirsymlink 而设置只认 symlink-dir"这类静默分叉。
        if (LinkKinds.TryParse(value, out kind)) { error = string.Empty; return true; }

        error = $"无法识别的链接类型：{value}。可用值：auto | junction | symlink-dir | symlink-file | hardlink。";
        return false;
    }

    internal static string DescribeKind(LinkKind kind) => kind switch
    {
        LinkKind.Junction => "目录联接 Junction（mklink /J）",
        LinkKind.DirectorySymlink => "目录符号链接（mklink /D）",
        LinkKind.FileSymlink => "文件符号链接（mklink）",
        LinkKind.HardLink => "硬链接（mklink /H）",
        _ => kind.ToString(),
    };

    private static bool SameVolume(string sourceExtended, string targetExtended)
    {
        var resolver = new Win32VolumeResolver();
        if (!resolver.TryGetVolumeInfo(sourceExtended, out var source)) return false;
        if (!resolver.TryGetVolumeInfo(targetExtended, out var target)) return false;

        return string.Equals(source.RootPath, target.RootPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>增量判定：已存在同名链接且指向同一份数据。</summary>
    private static bool IsAlreadyLinked(LinkService service, LinkRequest request, LinkKind kind)
    {
        var info = service.Inspect(request.LinkPath);
        if (!info.PathExists || info.Kind != kind) return false;

        if (kind == LinkKind.HardLink)
        {
            // 硬链接不是重解析点：只能靠"链接数 ≥ 2 且文件索引一致"判断。
            // ⚠️ 本机实测：删除多硬链接文件会走回收站，NumberOfLinks 不可靠（见 docs/02 口径修正），
            // 因此这里只做保守判断：链接数 ≥ 2 即视为已建立，避免重复建链。
            return info.NumberOfLinks >= 2;
        }

        return string.Equals(
            PathNormalizer.StripExtendedPrefix(PathNormalizer.NormalizeAbsolute(info.TargetAbsolute ?? string.Empty)),
            PathNormalizer.StripExtendedPrefix(PathNormalizer.NormalizeAbsolute(request.TargetPath)),
            StringComparison.OrdinalIgnoreCase);
    }

    private static int SafeProbe(string directory)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directory).Count();
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static object BuildLinkJson(LinkResolution resolution, LinkInfo info) => new
    {
        kind = resolution.Kind.ToString(),
        wasAuto = resolution.WasAuto,
        reason = resolution.Reason,
        path = info.Path,
        target = info.TargetAbsolute,
        isRelative = info.IsRelative,
        isDangling = info.IsDangling,
        reparseTag = $"0x{info.ReparseTag:X8}",
    };
}
