namespace DirPorter.Core.Links;

/// <summary>四种链接类型，与 mklink 的四种模式一一对应。</summary>
public enum LinkKind
{
    /// <summary>目录联接（mklink /J）。</summary>
    Junction = 0,

    /// <summary>目录符号链接（mklink /D）。</summary>
    DirectorySymlink = 1,

    /// <summary>文件符号链接（mklink，无参数）。</summary>
    FileSymlink = 2,

    /// <summary>硬链接（mklink /H）。</summary>
    HardLink = 3,
}

/// <summary>
/// 能力校验结果码。每一个拒绝码都必须有对应的单元测试用例（需求交付物 4）。
/// </summary>
public enum LinkCapabilityCode
{
    /// <summary>可以创建。</summary>
    Ok = 0,

    /// <summary>可以创建，但存在需要注意的风险（如 ReFS 上的重解析点支持面）。</summary>
    OkWithRisk = 1,

    // ---- 入参问题 ----
    InvalidPath = 10,
    SourceMissing = 11,
    TargetMissing = 12,
    TargetWrongKind = 13,
    LinkParentMissing = 14,

    // ---- 卷与文件系统 ----
    CrossVolumeNotSupported = 20,
    UncNotSupported = 21,
    FileSystemNotSupported = 22,
    VolumeNotFixed = 23,
    VolumeUnavailable = 24,

    // ---- 权限 ----
    PrivilegeNeeded = 30,

    // ---- 结构 ----
    PathSelfOrAncestor = 40,
    PathTooLong = 41,
    LinkAlreadyExists = 42,
    LinkPathNotEmpty = 43,
}

/// <summary>能力校验结果。<see cref="CanHandle"/> 为 false 时 <see cref="Reason"/> 必须可读且给出建议。</summary>
public readonly record struct LinkCapability(bool CanHandle, LinkCapabilityCode Code, string Reason, string? Suggestion = null)
{
    public static LinkCapability Ok(string reason = "可以创建。") => new(true, LinkCapabilityCode.Ok, reason);

    public static LinkCapability Risk(string reason, string? suggestion = null)
        => new(true, LinkCapabilityCode.OkWithRisk, reason, suggestion);

    public static LinkCapability No(LinkCapabilityCode code, string reason, string? suggestion = null)
        => new(false, code, reason, suggestion);
}

/// <summary>
/// 链接创建/校验请求。
/// <paramref name="RequestedKind"/> 为 null 时表示 auto，由 <see cref="LinkService"/> 按推荐规则选择。
/// </summary>
public sealed record LinkRequest(
    string LinkPath,
    string TargetPath,
    LinkKind? RequestedKind = null,
    bool PreferRelativeTarget = false)
{
    /// <summary>是否为自动模式。</summary>
    public bool IsAuto => RequestedKind is null;
}

/// <summary>链接创建选项。</summary>
public sealed record LinkCreateOptions
{
    /// <summary>已存在同名链接时先删除再创建。</summary>
    public bool ReplaceExisting { get; init; }

    /// <summary>创建相对路径符号链接（Junction 与 HardLink 不支持，该值为 true 时被忽略并记录警告）。</summary>
    public bool Relative { get; init; }

    /// <summary>允许尝试非特权创建符号链接（内部会附加 SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE）。</summary>
    public bool AllowUnprivilegedSymlink { get; init; } = true;

    /// <summary>干跑：只做能力校验与入参校验，不产生任何磁盘写入。</summary>
    public bool DryRun { get; init; }

    public static LinkCreateOptions Default { get; } = new();
}

/// <summary>
/// 链接检视结果。可用于 inspect 命令输出，也可用于判定"这是不是链接"。
/// </summary>
public sealed record LinkInfo(
    string Path,
    bool PathExists,
    bool IsReparsePoint,
    LinkKind? Kind,
    uint ReparseTag,
    string? SubstituteName,
    string? PrintName,
    string? TargetAbsolute,
    bool IsRelative,
    bool IsDangling,
    uint NumberOfLinks,
    bool IsDirectory,
    string? RawHexPreview)
{
    /// <summary>是否为受支持的链接（四种之一）。</summary>
    public bool IsKnownLink => Kind is not null;

    /// <summary>一句话描述，供 CLI 与 UI 直接展示。</summary>
    public string Describe()
    {
        if (!PathExists) return $"{Path}：路径不存在。";
        if (Kind is null)
            return IsReparsePoint
                ? $"{Path}：重解析点，标签 0x{ReparseTag:X8}（不受支持的类型）。"
                : NumberOfLinks > 1
                    ? $"{Path}：普通文件，硬链接数 {NumberOfLinks}（非本工具创建的链接无法与普通多链接文件区分，按普通文件处理）。"
                    : $"{Path}：普通{(IsDirectory ? "目录" : "文件")}。";

        var kindText = Kind switch
        {
            LinkKind.Junction => "目录联接 (Junction)",
            LinkKind.DirectorySymlink => "目录符号链接",
            LinkKind.FileSymlink => "文件符号链接",
            LinkKind.HardLink => "硬链接",
            _ => "未知",
        };

        var parts = new List<string> { $"{Path}：{kindText}" };
        if (Kind != LinkKind.HardLink)
        {
            parts.Add($"标签 0x{ReparseTag:X8}");
            parts.Add($"目标 {TargetAbsolute ?? "(无法解析)"}");
            if (IsRelative) parts.Add("相对路径");
            if (IsDangling) parts.Add("目标已丢失（悬空链接）");
        }
        else
        {
            parts.Add($"硬链接数 {NumberOfLinks}");
        }

        return string.Join("；", parts) + "。";
    }
}

/// <summary>
/// 链接创建失败但"需要更高权限"时抛出。
/// 上层据此返回退出码 5，而不是笼统的内部错误。
/// </summary>
public sealed class LinkPrivilegeRequiredException : Exception
{
    public LinkKind Kind { get; }

    public LinkPrivilegeRequiredException(LinkKind kind, string message) : base(message) => Kind = kind;
}

/// <summary>
/// 拒绝删除的链接（例如硬链接数已降回 1，删它等同于删普通文件）时抛出。
/// 这是刻意的保守设计：不确定就不删。
/// </summary>
public sealed class LinkDeleteRefusedException : Exception
{
    public LinkDeleteRefusedException(string message) : base(message) { }
}
