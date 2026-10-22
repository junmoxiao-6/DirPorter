using DirPorter.Core.FileSystem;

namespace DirPorter.Core.Links;

/// <summary>
/// 四种链接提供器的公共骨架：路径规范化、结构校验、`Exists` 默认实现。
/// 每个 Provider 只负责自己特有的部分（权限、卷/文件系统限制、创建/检视/删除）。
/// </summary>
public abstract class LinkProviderBase : ILinkProvider
{
    protected IVolumeResolver Volumes { get; }

    protected LinkProviderBase(IVolumeResolver volumes)
    {
        Volumes = volumes ?? throw new ArgumentNullException(nameof(volumes));
    }

    public abstract LinkKind Kind { get; }
    public abstract bool SupportsDirectories { get; }
    public abstract bool SupportsFiles { get; }

    /// <summary>能力校验（富信息版本，供 UI 置灰与 CLI --json 使用）。</summary>
    public abstract LinkCapability Evaluate(LinkRequest req);

    public bool CanHandle(LinkRequest req, out string reason)
    {
        var capability = Evaluate(req);
        reason = capability.Suggestion is null ? capability.Reason : $"{capability.Reason} 建议：{capability.Suggestion}";
        return capability.CanHandle;
    }

    public abstract void Create(LinkRequest req, LinkCreateOptions opt);

    public abstract LinkInfo Inspect(string path);

    public abstract void Delete(string linkPath);

    public virtual bool Exists(string path)
    {
        try
        {
            return NativeLinkHelper.Exists(PathNormalizer.NormalizeAbsolute(path));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    // ==================== 公共校验片段 ====================

    protected static bool TryNormalize(string linkPath, string targetPath, out string link, out string target, out LinkCapability error)
    {
        link = string.Empty;
        target = string.Empty;
        error = default;

        try
        {
            link = PathNormalizer.NormalizeAbsolute(linkPath);
        }
        catch (ArgumentException ex)
        {
            error = LinkCapability.No(LinkCapabilityCode.InvalidPath, $"链接路径无效：{ex.Message}");
            return false;
        }

        try
        {
            target = PathNormalizer.NormalizeAbsolute(targetPath);
        }
        catch (ArgumentException ex)
        {
            error = LinkCapability.No(LinkCapabilityCode.InvalidPath, $"目标路径无效：{ex.Message}");
            return false;
        }

        if (link.Length > MaxPathLength || target.Length > MaxPathLength)
        {
            error = LinkCapability.No(LinkCapabilityCode.PathTooLong, $"路径长度超过 {MaxPathLength} 字符上限。");
            return false;
        }

        return true;
    }

    /// <summary>
    /// 结构层校验：链接父目录存在 → 不成环 → 目标存在且类型正确 → 链接位置可用。
    /// </summary>
    protected static LinkCapability? CheckStructural(string link, string target, bool targetMustBeDirectory)
    {
        var linkParent = Path.GetDirectoryName(PathNormalizer.TrimTrailingSeparator(link));
        if (string.IsNullOrEmpty(linkParent) || !NativeLinkHelper.Exists(PathNormalizer.NormalizeAbsolute(linkParent)))
        {
            return LinkCapability.No(LinkCapabilityCode.LinkParentMissing, $"链接路径的父目录不存在：{linkParent}");
        }

        if (PathNormalizer.IsSameOrUnder(target, link))
        {
            return LinkCapability.No(
                LinkCapabilityCode.PathSelfOrAncestor,
                "目标路径位于链接路径之内，会形成自我引用环，已拒绝。",
                "请把目标放到链接路径之外的其他位置。");
        }

        var targetAttrs = NativeLinkHelper.TryGetAttributes(target);
        if (targetAttrs is null)
        {
            return LinkCapability.No(LinkCapabilityCode.TargetMissing, $"目标路径不存在：{PathNormalizer.StripExtendedPrefix(target)}");
        }

        var targetIsDir = NativeLinkHelper.IsDirectory(targetAttrs.Value);
        if (targetMustBeDirectory && !targetIsDir)
        {
            return LinkCapability.No(LinkCapabilityCode.TargetWrongKind, "该链接类型要求目标是目录，但给定目标是文件。");
        }

        if (!targetMustBeDirectory && targetIsDir)
        {
            return LinkCapability.No(LinkCapabilityCode.TargetWrongKind, "该链接类型要求目标是文件，但给定目标是目录。", "硬链接与文件符号链接都不支持目录。");
        }

        return null;
    }

    /// <summary>
    /// 链接落点是否可用。返回 null 表示可用。
    /// 注意：本方法不做任何删除，<c>ReplaceExisting</c> 的处理在 <c>Create</c> 内显式进行。
    /// </summary>
    protected static LinkCapability? CheckLinkSlot(string link)
    {
        var attrs = NativeLinkHelper.TryGetAttributes(link);
        if (attrs is null) return null;

        var isReparse = NativeLinkHelper.IsReparsePoint(attrs.Value);
        var isDir = NativeLinkHelper.IsDirectory(attrs.Value);

        if (isReparse)
        {
            return LinkCapability.No(
                LinkCapabilityCode.LinkAlreadyExists,
                $"链接路径已存在一个链接：{PathNormalizer.StripExtendedPrefix(link)}",
                "如需替换，请在创建选项中开启 ReplaceExisting。");
        }

        if (isDir)
        {
            // 真实目录：只有当它是空目录时才允许被用作链接落点（Junction 的落点必须是已存在的目录项）。
            var entries = Directory.EnumerateFileSystemEntries(link).Take(1).ToList();
            if (entries.Count > 0)
            {
                return LinkCapability.No(
                    LinkCapabilityCode.LinkPathNotEmpty,
                    $"链接路径已存在一个非空目录：{PathNormalizer.StripExtendedPrefix(link)}",
                    "请换一个链接路径，或先清空该目录。");
            }

            return null;
        }

        return LinkCapability.No(
            LinkCapabilityCode.LinkAlreadyExists,
            $"链接路径已存在一个文件：{PathNormalizer.StripExtendedPrefix(link)}",
            "目录类链接不能落在文件上，请先处理该文件。");
    }

    /// <summary>取自身所在卷的重解析点支持情况。</summary>
    protected LinkCapability? CheckReparseSupportOn(string path, string whatIsIt)
    {
        if (!Volumes.TryGetVolumeInfo(path, out var volume))
        {
            return LinkCapability.No(LinkCapabilityCode.VolumeUnavailable, $"无法获取{whatIsIt}所在卷的信息：{PathNormalizer.StripExtendedPrefix(path)}");
        }

        if (!volume.SupportsReparsePoints)
        {
            return LinkCapability.No(
                LinkCapabilityCode.FileSystemNotSupported,
                $"{whatIsIt}所在卷的文件系统是 {volume.FileSystemName}，不支持重解析点。",
                "Junction 与符号链接只能建在 NTFS 或 ReFS 卷上。");
        }

        if (PathNormalizer.IsUnc(path))
        {
            return LinkCapability.No(
                LinkCapabilityCode.FileSystemNotSupported,
                $"{whatIsIt}位于网络路径（UNC），本地重解析点无法建立在网络位置上。");
        }

        return null;
    }

    /// <summary>构造 inspect 用的公共字段。</summary>
    protected static LinkInfo BuildInfo(
        string extendedPath,
        uint? attributes,
        LinkKind? kind,
        uint reparseTag,
        ReparseParseResult? parsed,
        string? targetAbsolute,
        bool isRelative,
        bool isDangling,
        uint numberOfLinks,
        byte[]? rawBuffer)
    {
        return new LinkInfo(
            Path: PathNormalizer.StripExtendedPrefix(extendedPath),
            PathExists: attributes is not null,
            IsReparsePoint: attributes is not null && NativeLinkHelper.IsReparsePoint(attributes.Value),
            Kind: kind,
            ReparseTag: reparseTag,
            SubstituteName: parsed?.SubstituteName,
            PrintName: parsed?.PrintName,
            TargetAbsolute: targetAbsolute,
            IsRelative: isRelative,
            IsDangling: isDangling,
            NumberOfLinks: numberOfLinks,
            IsDirectory: attributes is not null && NativeLinkHelper.IsDirectory(attributes.Value),
            RawHexPreview: NativeLinkHelper.HexPreview(rawBuffer));
    }

    /// <summary>判断解析出的目标是否已丢失。</summary>
    protected static bool IsDanglingTarget(string? targetAbsoluteExtended)
    {
        if (string.IsNullOrEmpty(targetAbsoluteExtended)) return true;
        try
        {
            return !NativeLinkHelper.Exists(PathNormalizer.NormalizeAbsolute(targetAbsoluteExtended));
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    /// <summary>Windows 单路径上限（\\?\ 前缀下为 32767）。</summary>
    protected const int MaxPathLength = 32767;
}
