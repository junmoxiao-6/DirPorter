namespace DirPorter.Core.Precheck;

/// <summary>预检结论的严重级别。</summary>
public enum CheckSeverity
{
    /// <summary>提示信息，不影响执行。</summary>
    Info = 0,

    /// <summary>警告：可以继续，但用户应知道风险。</summary>
    Warning = 1,

    /// <summary>需要显式确认（勾选"我理解风险" / 二次确认弹窗）。</summary>
    RequireAcknowledge = 2,

    /// <summary>硬性阻止：不通过就不允许执行（且此时磁盘零写入）。</summary>
    Blocking = 3,
}

/// <summary>
/// 建议动作：把"拒绝原因"直接映射到"用户该怎么办"。
/// 该枚举是用户文档与 GUI 引导按钮的契约。
/// </summary>
public enum SuggestedAction
{
    /// <summary>无需动作（纯信息）。</summary>
    None = 0,

    /// <summary>换一个源目录（该目录不能迁移）。</summary>
    ChooseAnotherSource = 1,

    /// <summary>换一个目标位置或目标卷。</summary>
    ChooseAnotherTarget = 2,

    /// <summary>释放目标卷空间，或改迁到空间更大的卷。</summary>
    FreeUpSpace = 3,

    /// <summary>以管理员身份重新运行（或开启开发者模式）。</summary>
    RunAsAdministrator = 4,

    /// <summary>需要勾选"我理解风险"并二次确认后才能继续。</summary>
    AcknowledgeRisk = 5,

    /// <summary>查看并逐项处理预览中列出的特殊对象。</summary>
    ReviewSpecialObjects = 6,

    /// <summary>目标同名目录已存在：复用空目录，或给目标改名。</summary>
    ReuseOrRenameTarget = 7,

    /// <summary>批次内存在嵌套条目，已被自动跳过（无需动作，但须知悉）。</summary>
    RemoveNestedEntry = 8,

    /// <summary>批次内多个条目指向同一目标，需要先消解冲突。</summary>
    ResolveTargetConflict = 9,

    /// <summary>打开已存在的迁移目标 / 走还原入口（源已是链接时的引导）。</summary>
    OpenExistingTarget = 10,

    /// <summary>存在超长路径，若目标程序访问失败可开启系统长路径支持。</summary>
    EnableLongPathSupport = 11,
}

/// <summary>
/// 一条预检结果的静态定义（拒绝码元数据）。
/// 出用户文档时直接遍历 <see cref="PrecheckCodes.All"/> 即可生成"拒绝码 ↔ 用户处理建议"对照表。
/// </summary>
public sealed record PrecheckCodeInfo(
    string Code,
    CheckSeverity Severity,
    SuggestedAction Action,
    string Title,
    string UserGuide);

/// <summary>一条预检结果。</summary>
public sealed record CheckResult(
    string Code,
    CheckSeverity Severity,
    string Title,
    string Detail,
    SuggestedAction Action,
    string? UserGuide,
    string? RelatedPath = null,
    string? Win32 = null,
    string? EntryId = null)
{
    public bool IsBlocking => Severity == CheckSeverity.Blocking;
    public bool RequiresAcknowledge => Severity == CheckSeverity.RequireAcknowledge;
}

/// <summary>被自动跳过的条目（批次内嵌套关系）。</summary>
public sealed record SkippedEntry(string SourcePath, string TargetPath, string Reason);

/// <summary>预检报告。</summary>
public sealed record PrecheckReport(
    IReadOnlyList<CheckResult> Results,
    IReadOnlyList<ResolvedEntry> EffectiveEntries,
    IReadOnlyList<SkippedEntry> SkippedEntries,
    IReadOnlyList<FileSystem.SpaceRequirement> SpaceChecks,
    IReadOnlyDictionary<string, FileSystem.TreeProbeResult> Probes)
{
    /// <summary>存在硬性阻止项 → 不允许执行（且保证零磁盘写入）。</summary>
    public bool IsBlocked => Results.Any(static r => r.IsBlocking);

    /// <summary>存在待确认项（用户勾选"我理解风险"后可继续）。</summary>
    public bool RequiresAcknowledge => Results.Any(static r => r.RequiresAcknowledge);

    /// <summary>是否可以通过（不阻止即通过，是否确认由 UI/CLI 另行处理）。</summary>
    public bool CanProceed => !IsBlocked;

    public IReadOnlyList<CheckResult> BlockingResults => Results.Where(static r => r.IsBlocking).ToList();

    public IReadOnlyList<CheckResult> AcknowledgeResults => Results.Where(static r => r.RequiresAcknowledge).ToList();

    /// <summary>汇总源侧总占用（磁盘实际占用，用于 Dry-run 预估）。</summary>
    public long TotalSourceDiskBytes => Probes.Values.Sum(static p => p.DiskBytes);

    public long TotalSourceFileCount => Probes.Values.Sum(static p => p.FileCount);
}

/// <summary>预检解析后的有效条目（路径已规范化）。</summary>
public sealed record ResolvedEntry(
    string EntryId,
    string SourceExtendedPath,
    string TargetExtendedPath,
    string SourcePlainPath,
    string TargetPlainPath,
    Links.LinkKind LinkKind,
    bool CreateLink = true);
