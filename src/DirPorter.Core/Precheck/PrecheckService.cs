using DirPorter.Core.Config;
using DirPorter.Core.Diagnostics;
using DirPorter.Core.FileSystem;
using DirPorter.Core.Links;
using DirPorter.Core.Migration;

namespace DirPorter.Core.Precheck;

/// <summary>预检进度。</summary>
public sealed record PrecheckProgress(int CompletedEntries, int TotalEntries, string? CurrentSource, long ProbedFiles);

/// <summary>预检服务（PRECHECK，19 条逐条实现）。</summary>
public interface IPrecheckService
{
    Task<PrecheckReport> RunAsync(MigrationPlan plan, IProgress<PrecheckProgress>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// PRECHECK 实现。
///
/// ⚠️ 硬约束：**本模块全程零磁盘写入**。只做只读属性查询、句柄级权限探测（不写内容）、
/// 只读注册表读取、目录枚举。任何一步都不创建/修改/删除任何对象，因此本模块**没有回滚动作**。
/// </summary>
public sealed class PrecheckService : IPrecheckService
{
    private readonly IVolumeResolver _volumes;
    private readonly IBlacklistPolicy _blacklist;
    private readonly IWarningListPolicy _warnings;
    private readonly ITreeProbe _probe;
    private readonly LinkService _links;

    public PrecheckService(
        IVolumeResolver? volumes = null,
        IBlacklistPolicy? blacklist = null,
        IWarningListPolicy? warnings = null,
        ITreeProbe? probe = null)
    {
        _volumes = volumes ?? new Win32VolumeResolver();
        _blacklist = blacklist ?? new BlacklistPolicy();
        _probe = probe ?? new TreeProbe();

        // 警告名单需要启动项探测；用户可通过 options.ProbeStartupReferences 关闭（此处默认开启）。
        _warnings = warnings ?? new WarningListPolicy(new StartupReferenceProbe());
        _links = new LinkService(_volumes, Privilege.Win32PrivilegeProbe.Current);
    }

    public async Task<PrecheckReport> RunAsync(MigrationPlan plan, IProgress<PrecheckProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var results = new List<CheckResult>();
        var skipped = new List<SkippedEntry>();
        var probes = new Dictionary<string, TreeProbeResult>(StringComparer.OrdinalIgnoreCase);
        var spaceChecks = new List<SpaceRequirement>();
        var effective = new List<ResolvedEntry>();

        var options = plan.Options;

        // ==================== 阶段 0：规范化 + 批次嵌套去重（第 13 条）====================
        var normalized = new List<(MigrationPlanEntry Entry, string EntryId, string Source, string Target)>();
        for (var i = 0; i < plan.Entries.Count; i++)
        {
            var entry = plan.Entries[i];
            var entryId = entry.ResolveEntryId(i);

            string source, target;
            try
            {
                source = PathNormalizer.NormalizeAbsolute(entry.SourcePath);
            }
            catch (ArgumentException ex)
            {
                results.Add(PrecheckCodes.Create(PrecheckCodes.SrcNotFound, $"源路径无效：{ex.Message}", entry.SourcePath, entryId: entryId));
                continue;
            }

            try
            {
                target = PathNormalizer.NormalizeAbsolute(entry.TargetPath);
            }
            catch (ArgumentException ex)
            {
                results.Add(PrecheckCodes.Create(PrecheckCodes.TgtNameInvalid, $"目标路径无效：{ex.Message}", entry.TargetPath, entryId: entryId));
                continue;
            }

            normalized.Add((entry, entryId, source, target));
        }

        // 嵌套：若 B 在 A 之内，只保留 A，B 自动跳过并在报告中说明原因（第 13 条）
        normalized.Sort(static (a, b) => a.Source.Length.CompareTo(b.Source.Length));
        var kept = new List<(MigrationPlanEntry Entry, string EntryId, string Source, string Target)>();

        foreach (var candidate in normalized)
        {
            var ancestor = kept.FirstOrDefault(k => PathNormalizer.IsSameOrUnder(candidate.Source, k.Source));
            if (ancestor.Entry is not null)
            {
                skipped.Add(new SkippedEntry(
                    PathNormalizer.StripExtendedPrefix(candidate.Source),
                    PathNormalizer.StripExtendedPrefix(candidate.Target),
                    $"已被同批次的上级条目包含（{PathNormalizer.StripExtendedPrefix(ancestor.Source)}），只迁移上级目录即可。"));

                results.Add(PrecheckCodes.Create(
                    PrecheckCodes.BatchNestedSource,
                    $"「{PathNormalizer.StripExtendedPrefix(candidate.Source)}」位于同批次条目「{PathNormalizer.StripExtendedPrefix(ancestor.Source)}」之内，已自动跳过，不再单独迁移。",
                    PathNormalizer.StripExtendedPrefix(candidate.Source),
                    entryId: candidate.EntryId));
                continue;
            }

            kept.Add(candidate);
        }

        // ==================== 阶段 1：批次目标冲突（第 14 条）====================
        var targetOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var conflictedEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in kept)
        {
            if (targetOwners.TryGetValue(item.Target, out var owner))
            {
                results.Add(PrecheckCodes.Create(
                    PrecheckCodes.BatchTargetConflict,
                    $"「{PathNormalizer.StripExtendedPrefix(item.Source)}」与「{PathNormalizer.StripExtendedPrefix(owner)}」映射到同一个目标「{PathNormalizer.StripExtendedPrefix(item.Target)}」。",
                    PathNormalizer.StripExtendedPrefix(item.Target),
                    entryId: item.EntryId));

                conflictedEntries.Add(item.Source);
                continue;
            }

            targetOwners[item.Target] = item.Source;
        }

        // 交叉重叠：某个源落在另一个条目的目标之内（会把数据搬进已迁移区域）
        foreach (var item in kept)
        {
            foreach (var other in kept)
            {
                if (ReferenceEquals(item.Entry, other.Entry)) continue;
                if (!PathNormalizer.IsSameOrUnder(item.Source, other.Target)) continue;

                results.Add(PrecheckCodes.Create(
                    PrecheckCodes.BatchTargetConflict,
                    $"「{PathNormalizer.StripExtendedPrefix(item.Source)}」位于另一个条目的目标路径「{PathNormalizer.StripExtendedPrefix(other.Target)}」之内，同一个批次的源和目标不允许互相包含。",
                    PathNormalizer.StripExtendedPrefix(item.Target),
                    entryId: item.EntryId));

                conflictedEntries.Add(item.Source);
            }
        }

        // ==================== 阶段 2：逐条源侧 + 目标侧====================
        var completed = 0;
        foreach (var item in kept)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sourcePlain = PathNormalizer.StripExtendedPrefix(item.Source);
            var targetPlain = PathNormalizer.StripExtendedPrefix(item.Target);

            progress?.Report(new PrecheckProgress(completed, kept.Count, sourcePlain, 0));

            // ---------- 源侧第 1 条：存在且是目录 ----------
            var attributes = Kernel32.GetFileAttributes(item.Source);
            if (attributes == Kernel32.InvalidFileAttributes)
            {
                var code = Win32Error.Last;
                results.Add(PrecheckCodes.Create(PrecheckCodes.SrcNotFound,
                    $"源路径不存在或不可访问：{sourcePlain}（{Win32Error.Describe(code)}）",
                    sourcePlain, Win32Error.Describe(code), item.EntryId));
                completed++;
                continue;
            }

            if ((attributes & Kernel32.FileAttributeDirectory) == 0)
            {
                results.Add(PrecheckCodes.Create(PrecheckCodes.SrcNotDirectory,
                    $"源路径存在但不是目录：{sourcePlain}", sourcePlain, entryId: item.EntryId));
                completed++;
                continue;
            }

            // ---------- 源侧第 2 条：不是重解析点 ----------
            var linkInfo = _links.Inspect(item.Source);
            if (linkInfo.IsReparsePoint)
            {
                var targetOfLink = linkInfo.TargetAbsolute ?? "(无法解析)";
                results.Add(PrecheckCodes.Create(PrecheckCodes.SrcIsLink,
                    $"该目录已经是链接，可能已被迁移过：{sourcePlain} → {targetOfLink}。请勿重复迁移。",
                    targetOfLink, entryId: item.EntryId));
                completed++;
                continue;
            }

            // ---------- 源侧第 3 条：黑名单（硬拒绝）----------
            var blacklist = _blacklist.Evaluate(sourcePlain);
            if (blacklist.IsBlocked)
            {
                results.Add(PrecheckCodes.Create(PrecheckCodes.SrcBlacklisted,
                    $"源目录命中系统关键目录黑名单（规则 {blacklist.RuleId}）：{blacklist.Reason}",
                    sourcePlain, entryId: item.EntryId));
                completed++;
                continue;
            }

            // ---------- 源侧第 4 条：警告名单 ----------
            if (!options.AcknowledgeWarnings)
            {
                var warning = _warnings.Evaluate(sourcePlain);
                if (warning.RequiresAcknowledge)
                {
                    foreach (var hit in warning.Hits)
                    {
                        var info = hit.RuleId == "WN-SERVICE-REFERENCED" ? PrecheckCodes.SrcServiceReferenced : PrecheckCodes.SrcWarningList;
                        results.Add(PrecheckCodes.Create(info,
                            $"{hit.Reason}（规则 {hit.RuleId}）{(hit.Evidence is null ? string.Empty : $"，证据：{hit.Evidence}")}",
                            sourcePlain, entryId: item.EntryId));
                    }
                }
            }

            // ---------- 源侧第 5 条 + 第 10 条的数据来源：一次遍历同时得到 ----------
            TreeProbeResult probeResult;
            try
            {
                probeResult = await ProbeAsync(item.Source, options, progress, completed, kept.Count, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                results.Add(PrecheckCodes.Create(PrecheckCodes.SrcEnumerationFailed,
                    $"枚举源目录失败：{sourcePlain}（{ex.Message}）", sourcePlain, entryId: item.EntryId));
                completed++;
                continue;
            }

            probes[sourcePlain] = probeResult;

            if (probeResult.Errors.Count > 0)
            {
                var first = probeResult.Errors[0];
                results.Add(PrecheckCodes.Create(PrecheckCodes.SrcEnumerationFailed,
                    $"有 {probeResult.Errors.Count} 个子目录无法枚举，例如：{first.Path}（{first.Message}{Win32Error.Describe(first.Win32Code)}）。若这些目录属于正在运行的服务，请先停用相关服务。",
                    first.Path, Win32Error.Describe(first.Win32Code), item.EntryId));
            }

            if (probeResult.SpecialObjects.Count > 0)
            {
                var detail = string.Join("；", probeResult.SpecialObjects.Select(DescribeSpecial));
                // 含云占位文件时升级为"需确认"：迁移会触发下载，产生真实网络与时间成本。
                var requiresAck = probeResult.HasSpecialObject(SpecialObjectKind.CloudPlaceholder)
                                  || probeResult.HasSpecialObject(SpecialObjectKind.EncryptedFile);

                var info = requiresAck ? PrecheckCodes.SrcSpecialObjectsAcknowledge : PrecheckCodes.SrcSpecialObjects;
                results.Add(PrecheckCodes.Create(info,
                    $"目录树内含特殊对象（共 {probeResult.SpecialObjects.Sum(static s => s.Count)} 项）：{detail}",
                    sourcePlain, entryId: item.EntryId));
            }

            if (probeResult.HasSpecialObject(SpecialObjectKind.LongPath))
            {
                results.Add(PrecheckCodes.Create(PrecheckCodes.InfoLongPath,
                    $"树内有 {probeResult.CountOf(SpecialObjectKind.LongPath)} 条超过 260 字符的路径，例：{probeResult.SpecialObjects.First(static s => s.Kind == SpecialObjectKind.LongPath).Samples[0].Path}",
                    sourcePlain, entryId: item.EntryId));
            }

            results.Add(PrecheckCodes.Create(PrecheckCodes.InfoAssessment,
                $"{sourcePlain}：{probeResult.FileCount} 个文件 / {probeResult.DirectoryCount} 个子目录，"
                + $"逻辑大小 {SpaceRequirement.Format(probeResult.LogicalBytes)}，磁盘实际占用 {SpaceRequirement.Format(probeResult.DiskBytes)}，"
                + $"单文件最大 {SpaceRequirement.Format(probeResult.MaxFileBytes)}，遍历耗时 {probeResult.Elapsed.TotalMilliseconds:0} ms"
                + (probeResult.DeepProbeSkippedForScale ? "（文件数超过深入探测上限，硬链接/ADS 未逐文件检查）" : string.Empty),
                sourcePlain, entryId: item.EntryId));

            // ---------- 源侧第 6 条：权限（读 + 删 + 改父目录）----------
            var sourcePermission = PermissionProbe.ProbeSource(item.Source);
            if (!sourcePermission.AllGranted)
            {
                results.Add(PrecheckCodes.Create(PrecheckCodes.SrcPermissionDenied,
                    $"源目录权限不足（{sourcePermission.Detail}）",
                    sourcePlain,
                    sourcePermission.Win32Code is null ? null : Win32Error.Describe(sourcePermission.Win32Code.Value),
                    item.EntryId));
            }

            // ---------- 目标名称合法性（必须先于卷解析）----------
            // 实测发现：目标叶子名若为系统保留设备名（NUL / CON / COM1 …），
            // GetVolumePathNameW 等路径解析 API 会直接失败，从而被误报为"卷不可用"。
            // 因此这里把纯字符串校验前置，给出准确的原因。
            var leafName = Path.GetFileName(PathNormalizer.TrimTrailingSeparator(targetPlain));
            if (!IsValidLeafName(leafName))
            {
                results.Add(PrecheckCodes.Create(PrecheckCodes.TgtNameInvalid,
                    $"目标目录名非法或为系统保留名：{leafName}", targetPlain, entryId: item.EntryId));
                completed++;
                continue;
            }

            // ---------- 目标侧第 7 条：本地固定卷 ----------
            if (!_volumes.TryGetVolumeInfo(item.Target, out var targetVolume))
            {
                results.Add(PrecheckCodes.Create(PrecheckCodes.TgtVolumeUnavailable,
                    $"无法获取目标所在卷的信息：{targetPlain}", targetPlain, entryId: item.EntryId));
                completed++;
                continue;
            }

            if (!targetVolume.IsFixed)
            {
                results.Add(PrecheckCodes.Create(PrecheckCodes.TgtNotFixedVolume,
                    $"目标所在卷类型是 {targetVolume.DriveType}，不是本地固定卷：{targetPlain}（卷 {targetVolume.RootPath}）",
                    targetPlain, entryId: item.EntryId));
                completed++;
                continue;
            }

            // ---------- 目标侧第 8 条：NTFS / ReFS ----------
            if (!targetVolume.SupportsReparsePoints)
            {
                results.Add(PrecheckCodes.Create(PrecheckCodes.TgtFileSystemUnsupported,
                    $"目标卷 {targetVolume.RootPath} 的文件系统是 {targetVolume.FileSystemName}，不支持重解析点。",
                    targetPlain, entryId: item.EntryId));
                completed++;
                continue;
            }

            // ---------- 目标侧第 9 条：防环 / 防自我嵌套 ----------
            if (PathNormalizer.IsSameOrUnder(item.Target, item.Source) || PathNormalizer.IsSameOrUnder(item.Source, item.Target))
            {
                var reason = string.Equals(item.Source, item.Target, StringComparison.OrdinalIgnoreCase)
                    ? "源与目标完全相同"
                    : PathNormalizer.IsSameOrUnder(item.Target, item.Source)
                        ? "目标位于源目录之内"
                        : "源位于目标目录之内";
                results.Add(PrecheckCodes.Create(PrecheckCodes.TgtPathOverlap,
                    $"{reason}：源 {sourcePlain}，目标 {targetPlain}", targetPlain, entryId: item.EntryId));
                completed++;
                continue;
            }

            // ---------- 目标侧第 11 条：同名目录处理 ----------
            var targetAttributes = Kernel32.GetFileAttributes(item.Target);
            if (targetAttributes != Kernel32.InvalidFileAttributes)
            {
                var isReparse = (targetAttributes & Kernel32.FileAttributeReparsePoint) != 0;
                var isDirectory = (targetAttributes & Kernel32.FileAttributeDirectory) != 0;

                if (isReparse)
                {
                    results.Add(PrecheckCodes.Create(PrecheckCodes.TgtExistsNonEmpty,
                        $"目标路径已存在一个链接：{targetPlain}。",
                        targetPlain, entryId: item.EntryId,
                        userGuide: $"如果数据已经在 {targetPlain}，不需要迁移：到「链接」页把 {sourcePlain} 指向它即可；" +
                                   "否则请改用其它目标位置，或先解除该链接。"));
                    completed++;
                    continue;
                }

                if (!isDirectory)
                {
                    results.Add(PrecheckCodes.Create(PrecheckCodes.TgtExistsNonEmpty,
                        $"目标路径已存在一个同名文件：{targetPlain}。", targetPlain, entryId: item.EntryId));
                    completed++;
                    continue;
                }

                var entries = Directory.EnumerateFileSystemEntries(item.Target).Take(1).ToList();
                if (entries.Count > 0)
                {
                    results.Add(PrecheckCodes.Create(PrecheckCodes.TgtExistsNonEmpty,
                        $"目标位置已存在同名且非空的目录：{targetPlain}", targetPlain, entryId: item.EntryId,
                        userGuide: $"如果那是你之前搬过去的数据，**不需要再迁移**：到「链接」页把 {sourcePlain} 指向 {targetPlain} 即可；" +
                                   "否则请给目标改名，或先自行处理该目录内容（工具不会覆盖或合并非空目录）。"));
                    completed++;
                    continue;
                }

                results.Add(PrecheckCodes.Create(PrecheckCodes.TgtExistsEmpty,
                    $"目标位置已存在同名空目录：{targetPlain}（可复用）", targetPlain, entryId: item.EntryId));
            }

            // ---------- 目标侧第 12 条：父目录可写 ----------
            var targetParent = Path.GetDirectoryName(PathNormalizer.TrimTrailingSeparator(item.Target)) ?? item.Target;
            var targetPermission = PermissionProbe.ProbeTargetParent(PathNormalizer.NormalizeAbsolute(targetParent));
            if (!targetPermission.CanAddChild)
            {
                results.Add(PrecheckCodes.Create(PrecheckCodes.TgtParentNotWritable,
                    $"目标父目录不可写（{targetPermission.Detail}）",
                    targetPermission.Path,
                    targetPermission.Win32Code is null ? null : Win32Error.Describe(targetPermission.Win32Code.Value),
                    item.EntryId));
            }

            // ---------- 目标侧第 10 条：空间复检（单条）----------
            var requirement = SpaceCalculator.Evaluate(item.Target, probeResult.DiskBytes, _volumes, options.SpaceHeadroomFactor);
            spaceChecks.Add(requirement);
            if (!requirement.IsSatisfied)
            {
                results.Add(PrecheckCodes.Create(PrecheckCodes.TgtSpaceInsufficient,
                    $"「{sourcePlain}」{requirement.Describe()}", targetPlain, entryId: item.EntryId));
            }

            effective.Add(new ResolvedEntry(
                item.EntryId, item.Source, item.Target, sourcePlain, targetPlain, item.Entry.LinkKind, item.Entry.CreateLink));

            completed++;
        }

        // ==================== 阶段 3：批次总空间（第 15 条）====================
        // 语义分工：单条就已经放不下 → 报 TGT_SPACE_INSUFFICIENT（上面已报）；
        // 单条都能放下、但累计放不下 → 报 BATCH_SPACE_TOTAL（这里报），两者不重复。
        var singleBlockedVolumes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var requirement in spaceChecks.Where(static r => !r.IsSatisfied))
            singleBlockedVolumes.Add(requirement.VolumeRoot);

        var batchRequirements = SpaceCalculator.EvaluateBatch(
            effective.Select(e => (e.TargetExtendedPath, probes.TryGetValue(e.SourcePlainPath, out var p) ? p.DiskBytes : 0L)),
            _volumes,
            options.SpaceHeadroomFactor);

        foreach (var requirement in batchRequirements.Where(static r => !r.IsSatisfied))
        {
            if (singleBlockedVolumes.Contains(requirement.VolumeRoot)) continue;

            results.Add(PrecheckCodes.Create(PrecheckCodes.BatchSpaceInsufficient,
                $"批次累计{requirement.Describe()}（单个条目单独看都能放下，累计放不下）",
                requirement.VolumeRoot));
        }

        progress?.Report(new PrecheckProgress(kept.Count, kept.Count, null, 0));

        return new PrecheckReport(results, effective, skipped, spaceChecks, probes);
    }

    private async Task<TreeProbeResult> ProbeAsync(
        string sourceExtendedPath, PrecheckOptions options,
        IProgress<PrecheckProgress>? progress, int completed, int total, CancellationToken cancellationToken)
    {
        var probeOptions = new TreeProbeOptions
        {
            DeepProbeFileLimit = options.DeepProbeFileLimit,
            DetectAlternateDataStreams = options.DetectAlternateDataStreams,
        };

        var fileProgress = progress is null
            ? null
            : new Progress<long>(count => progress.Report(new PrecheckProgress(completed, total, null, count)));

        return await _probe.ProbeAsync(sourceExtendedPath, probeOptions, fileProgress, cancellationToken).ConfigureAwait(false);
    }

    private static string DescribeSpecial(SpecialObjectSummary summary)
    {
        var sample = summary.Samples.Count > 0 ? $"，例：{summary.Samples[0].Path}" : string.Empty;
        return $"{DescribeKind(summary.Kind)} {summary.Count} 项{sample}";
    }

    private static string DescribeKind(SpecialObjectKind kind) => kind switch
    {
        SpecialObjectKind.NestedDirectoryReparsePoint => "嵌套目录链接",
        SpecialObjectKind.NestedFileSymlink => "嵌套文件符号链接",
        SpecialObjectKind.HardLinkFile => "硬链接文件",
        SpecialObjectKind.SparseFile => "稀疏文件",
        SpecialObjectKind.AlternateDataStream => "含备用数据流(ADS)的文件",
        SpecialObjectKind.EncryptedFile => "EFS 加密文件",
        SpecialObjectKind.CloudPlaceholder => "云占位文件",
        SpecialObjectKind.LongPath => "超长路径",
        _ => kind.ToString(),
    };

    /// <summary>目录名合法性：非法字符 + 系统保留名 + 不以空格/点结尾。</summary>
    public static bool IsValidLeafName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Length > 255) return false;
        if (name.IndexOfAny(new[] { ':', '*', '?', '"', '<', '>', '|', '\\', '/' }) >= 0) return false;
        if (name.EndsWith(' ') || name.EndsWith('.')) return false;

        var stem = Path.GetFileNameWithoutExtension(name);
        string[] reserved = { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
        return !reserved.Any(r => string.Equals(r, stem, StringComparison.OrdinalIgnoreCase));
    }
}
