namespace DirPorter.Core.Precheck;

/// <summary>
/// 全部预检拒绝码的元数据表（严重级别 + 建议动作 + 用户处理建议）。
///
/// 用途：
/// <list type="bullet">
/// <item>GUI：按 <see cref="PrecheckCodeInfo.Severity"/> 决定颜色/图标，按 <see cref="PrecheckCodeInfo.Action"/> 决定引导按钮；</item>
/// <item>CLI：<c>--json</c> 输出直接带 code / action；</item>
/// <item>用户文档：遍历 <see cref="All"/> 即可生成"拒绝码 ↔ 用户该怎么办"对照表。</item>
/// </list>
/// 顺序与需求「PRECHECK」小节的 19 条编号一一对应，便于逐条审计。
/// </summary>
public static class PrecheckCodes
{
    // ==================== 源侧（需求第 1–6 条）====================

    /// <summary>第 1 条：源路径不存在。</summary>
    public static readonly PrecheckCodeInfo SrcNotFound = new(
        "SRC_NOT_FOUND", CheckSeverity.Blocking, SuggestedAction.ChooseAnotherSource,
        "源路径不存在",
        "确认路径拼写；若目录已被移动，请改用「仅建链」模式，或在迁移记录页查看历史任务。");

    /// <summary>第 1 条：源不是目录。</summary>
    public static readonly PrecheckCodeInfo SrcNotDirectory = new(
        "SRC_NOT_DIRECTORY", CheckSeverity.Blocking, SuggestedAction.ChooseAnotherSource,
        "源不是目录",
        "本功能用于迁移目录；文件请改用 link 的硬链接 / 文件符号链接模式。");

    /// <summary>第 2 条：源本身已经是重解析点。</summary>
    public static readonly PrecheckCodeInfo SrcIsLink = new(
        "SRC_IS_LINK", CheckSeverity.Blocking, SuggestedAction.OpenExistingTarget,
        "源目录已经是链接（可能已迁移过）",
        "不要再迁移一次。可点「打开目标位置」查看现有数据，或在迁移记录页对该条目执行还原。");

    /// <summary>第 3 条：命中系统关键目录黑名单。</summary>
    public static readonly PrecheckCodeInfo SrcBlacklisted = new(
        "SRC_BLACKLISTED", CheckSeverity.Blocking, SuggestedAction.ChooseAnotherSource,
        "源目录属于系统关键目录（硬拒绝）",
        "这是硬性限制，不可绕过。请选择具体应用/数据子目录，而不是系统目录本身。");

    /// <summary>第 4 条：命中警告名单。</summary>
    public static readonly PrecheckCodeInfo SrcWarningList = new(
        "SRC_WARNING_LIST", CheckSeverity.RequireAcknowledge, SuggestedAction.AcknowledgeRisk,
        "源目录在警告名单内（需确认风险）",
        "确认你了解该目录可能被程序/服务按固定路径引用；勾选「我理解风险」并二次确认后可继续。建议先看 Dry-run 预览。");

    /// <summary>第 4 条：被服务或开机启动项引用。</summary>
    public static readonly PrecheckCodeInfo SrcServiceReferenced = new(
        "SRC_SERVICE_REFERENCED", CheckSeverity.RequireAcknowledge, SuggestedAction.AcknowledgeRisk,
        "源目录被系统服务或开机启动项引用",
        "迁移后对应服务可能无法启动。建议先停用相关服务，或放弃迁移该目录；如坚持迁移，请准备还原。");

    /// <summary>第 5 条：目录树内含特殊对象。</summary>
    public static readonly PrecheckCodeInfo SrcSpecialObjects = new(
        "SRC_SPECIAL_OBJECTS", CheckSeverity.Warning, SuggestedAction.ReviewSpecialObjects,
        "目录树内含特殊对象（已逐项标注）",
        "请在预览中逐项查看：嵌套链接不会被递归复制、硬链接会变成独立文件、云占位文件会触发下载、EFS 加密文件行为未实测。确认可接受后再继续。");

    /// <summary>第 5 条：目录树内含特殊对象且带真实成本（云占位下载 / EFS）。</summary>
    public static readonly PrecheckCodeInfo SrcSpecialObjectsAcknowledge = new(
        "SRC_SPECIAL_OBJECTS_ACK", CheckSeverity.RequireAcknowledge, SuggestedAction.ReviewSpecialObjects,
        "目录树内含会产生额外成本的特殊对象（需确认）",
        "含云占位文件时迁移会触发从云端下载，可能耗时数十分钟并产生大量流量；含 EFS 加密文件时跨卷行为尚未实测。请确认后再继续；建议先做 Dry-run 并选低峰时段执行。");

    /// <summary>第 6 条：源权限不足。</summary>
    public static readonly PrecheckCodeInfo SrcPermissionDenied = new(
        "SRC_PERMISSION_DENIED", CheckSeverity.Blocking, SuggestedAction.RunAsAdministrator,
        "对源目录权限不足（需要读 + 删 + 改父目录）",
        "以管理员身份重新运行；若仍失败，说明该目录被 ACL 或安全软件保护，请改选其它目录。");

    /// <summary>第 6 条衍生：目录枚举失败。</summary>
    public static readonly PrecheckCodeInfo SrcEnumerationFailed = new(
        "SRC_ENUMERATION_FAILED", CheckSeverity.Blocking, SuggestedAction.RunAsAdministrator,
        "无法枚举源目录内容",
        "以管理员身份重试；若目录内有正在被独占的子系统（如正在运行的服务数据目录），请先停用相关服务。");

    // ==================== 目标侧（需求第 7–12 条）====================

    /// <summary>第 7 条衍生：无法获取目标卷信息。</summary>
    public static readonly PrecheckCodeInfo TgtVolumeUnavailable = new(
        "TGT_VOLUME_UNAVAILABLE", CheckSeverity.Blocking, SuggestedAction.ChooseAnotherTarget,
        "无法获取目标所在卷的信息",
        "确认目标盘符存在且已挂载；网络位置与未挂载的分区不能作为迁移目标。");

    /// <summary>第 7 条：目标不是本地固定卷。</summary>
    public static readonly PrecheckCodeInfo TgtNotFixedVolume = new(
        "TGT_NOT_FIXED_VOLUME", CheckSeverity.Blocking, SuggestedAction.ChooseAnotherTarget,
        "目标不是本地固定卷（可移动盘 / U 盘 / 网络位置）",
        "请选择本地固定磁盘。U 盘拔盘后链接立即失效，网络位置不被 Junction/硬链接支持。");

    /// <summary>第 8 条：目标文件系统不支持重解析点。</summary>
    public static readonly PrecheckCodeInfo TgtFileSystemUnsupported = new(
        "TGT_FS_UNSUPPORTED", CheckSeverity.Blocking, SuggestedAction.ChooseAnotherTarget,
        "目标卷文件系统不支持重解析点（FAT32 / exFAT 等）",
        "请选择 NTFS 或 ReFS 卷。FAT32/exFAT 无法承载 Junction 与符号链接。");

    /// <summary>第 9 条：源与目标路径重叠。</summary>
    public static readonly PrecheckCodeInfo TgtPathOverlap = new(
        "TGT_PATH_OVERLAP", CheckSeverity.Blocking, SuggestedAction.ChooseAnotherTarget,
        "源与目标路径重叠（相同 / 互为父子）",
        "目标不能是源本身、源的子目录，也不能是源的上级目录，否则会形成自我嵌套或环。请换一个目标位置。");

    /// <summary>第 10 条：目标卷空间不足。</summary>
    public static readonly PrecheckCodeInfo TgtSpaceInsufficient = new(
        "TGT_SPACE_INSUFFICIENT", CheckSeverity.Blocking, SuggestedAction.FreeUpSpace,
        "目标卷剩余空间不足（要求 ≥ 源实际占用 × 1.05）",
        "释放目标卷空间，或改迁到空间更大的卷。注意：占用按文件实际磁盘占用统计，不是名义大小。");

    /// <summary>第 11 条：目标同名目录非空。</summary>
    public static readonly PrecheckCodeInfo TgtExistsNonEmpty = new(
        "TGT_EXISTS_NONEMPTY", CheckSeverity.Blocking, SuggestedAction.ReuseOrRenameTarget,
        "目标位置已存在同名且非空的目录",
        "给目标改名，或先自行处理该目录内容。工具不会覆盖或合并已存在的非空目录。");

    /// <summary>第 11 条：目标同名空目录。</summary>
    public static readonly PrecheckCodeInfo TgtExistsEmpty = new(
        "TGT_EXISTS_EMPTY", CheckSeverity.RequireAcknowledge, SuggestedAction.ReuseOrRenameTarget,
        "目标位置已存在同名空目录",
        "可直接复用该空目录（勾选确认），或给目标改名。");

    /// <summary>第 12 条：目标父目录不可写。</summary>
    public static readonly PrecheckCodeInfo TgtParentNotWritable = new(
        "TGT_PARENT_NOT_WRITABLE", CheckSeverity.Blocking, SuggestedAction.RunAsAdministrator,
        "目标父目录不可写",
        "以管理员身份重新运行；若目标位于受保护位置（如 Program Files），请改选可写目录。");

    /// <summary>目标名称非法。</summary>
    public static readonly PrecheckCodeInfo TgtNameInvalid = new(
        "TGT_NAME_INVALID", CheckSeverity.Blocking, SuggestedAction.ChooseAnotherTarget,
        "目标目录名非法或包含系统保留名",
        "避免使用 CON / PRN / AUX / NUL / COM1-9 / LPT1-9 等保留名，以及 : * ? \" < > | 等非法字符。");

    // ==================== 批次侧（需求第 13–15 条）====================

    /// <summary>第 13 条：批次内嵌套源（自动跳过）。</summary>
    public static readonly PrecheckCodeInfo BatchNestedSource = new(
        "BATCH_NESTED_SOURCE", CheckSeverity.Info, SuggestedAction.RemoveNestedEntry,
        "批次内存在嵌套条目，已自动跳过子条目",
        "无需处理：父目录已包含子目录，迁移父目录即可。此处仅告知你子条目被跳过的原因。");

    /// <summary>第 14 条：批次内目标冲突。</summary>
    public static readonly PrecheckCodeInfo BatchTargetConflict = new(
        "BATCH_TARGET_CONFLICT", CheckSeverity.Blocking, SuggestedAction.ResolveTargetConflict,
        "批次内多个条目映射到同一个目标路径",
        "为每个条目指定不同的目标目录（否则后写入的会覆盖先写入的）。");

    /// <summary>第 15 条：批次总空间不足。</summary>
    public static readonly PrecheckCodeInfo BatchSpaceInsufficient = new(
        "BATCH_SPACE_TOTAL", CheckSeverity.Blocking, SuggestedAction.FreeUpSpace,
        "批次总占用超出目标卷剩余空间",
        "分批迁移，或先释放目标卷空间。可按大小排序，先迁移最关键的部分。");

    // ==================== 信息类 ====================

    /// <summary>评估摘要（信息）。</summary>
    public static readonly PrecheckCodeInfo InfoAssessment = new(
        "INFO_ASSESSMENT", CheckSeverity.Info, SuggestedAction.None,
        "预检评估摘要",
        "无需处理。此处的文件数与占用用于预估传输量与耗时（Dry-run 依据）。");

    /// <summary>存在超长路径（信息）。</summary>
    public static readonly PrecheckCodeInfo InfoLongPath = new(
        "INFO_LONG_PATH", CheckSeverity.Info, SuggestedAction.EnableLongPathSupport,
        "树内存在超过 260 字符的路径",
        "本工具用 \\\\?\\ 前缀可正常迁移；若迁移后某些老程序访问失败，可在系统「长路径支持」策略中开启。");

    /// <summary>全部拒绝码元数据（供用户文档生成）。</summary>
    public static IReadOnlyList<PrecheckCodeInfo> All { get; } = new[]
    {
        SrcNotFound, SrcNotDirectory, SrcIsLink, SrcBlacklisted, SrcWarningList, SrcServiceReferenced,
        SrcSpecialObjects, SrcSpecialObjectsAcknowledge, SrcPermissionDenied, SrcEnumerationFailed,
        TgtVolumeUnavailable, TgtNotFixedVolume, TgtFileSystemUnsupported, TgtPathOverlap,
        TgtSpaceInsufficient, TgtExistsNonEmpty, TgtExistsEmpty, TgtParentNotWritable, TgtNameInvalid,
        BatchNestedSource, BatchTargetConflict, BatchSpaceInsufficient,
        InfoAssessment, InfoLongPath,
    };

    /// <summary>按码取元数据（未知码返回 null）。</summary>
    public static PrecheckCodeInfo? Resolve(string code)
        => All.FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.Ordinal));

    /// <summary>按元数据构造一条结果，保证 code/title/action/guide 始终一致。</summary>
    public static CheckResult Create(
        PrecheckCodeInfo info,
        string detail,
        string? relatedPath = null,
        string? win32 = null,
        string? entryId = null,
        string? userGuide = null)
        => new(info.Code, info.Severity, info.Title, detail, info.Action, userGuide ?? info.UserGuide, relatedPath, win32, entryId);
}
