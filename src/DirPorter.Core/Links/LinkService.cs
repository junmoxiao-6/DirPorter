using DirPorter.Core.FileSystem;
using DirPorter.Core.Privilege;

namespace DirPorter.Core.Links;

/// <summary>自动模式的推荐结果。</summary>
public sealed record LinkRecommendation(LinkKind Kind, string Reason);

/// <summary>解析结果：最终选定/请求的类型 + 对应的能力校验。</summary>
public sealed record LinkResolution(LinkKind Kind, bool WasAuto, string Reason, LinkCapability Capability);

/// <summary>
/// 四种链接提供器的统一门面 + 智能推荐 + auto 解析。
/// 迁移状态机的 LINKING / LINK_VERIFY / restore 全部经由本类执行，
/// 因此三种目录级链接（Junction、目录符号链接）都可用于迁移场景；
/// 硬链接不支持目录，<see cref="SupportsMigration"/> 会明确返回 false，不得出现在目录迁移候选中。
/// </summary>
public sealed class LinkService
{
    private readonly Dictionary<LinkKind, ILinkProvider> _providers;

    public LinkService(IVolumeResolver volumes, IPrivilegeProbe privileges)
    {
        Volumes = volumes ?? throw new ArgumentNullException(nameof(volumes));
        Privileges = privileges ?? throw new ArgumentNullException(nameof(privileges));

        _providers = new Dictionary<LinkKind, ILinkProvider>
        {
            [LinkKind.Junction] = new JunctionLinkProvider(volumes),
            [LinkKind.DirectorySymlink] = new DirSymlinkLinkProvider(volumes, privileges),
            [LinkKind.FileSymlink] = new FileSymlinkLinkProvider(volumes, privileges),
            [LinkKind.HardLink] = new HardLinkProvider(volumes),
        };
    }

    public IVolumeResolver Volumes { get; }
    public IPrivilegeProbe Privileges { get; }

    /// <summary>按默认（真实 Win32）实现构造。</summary>
    public static LinkService CreateDefault() => new(new Win32VolumeResolver(), Win32PrivilegeProbe.Current);

    public IReadOnlyDictionary<LinkKind, ILinkProvider> Providers => _providers;

    public ILinkProvider GetProvider(LinkKind kind) => _providers[kind];

    /// <summary>硬链接不支持目录，因此不能用于目录迁移场景。</summary>
    public static bool SupportsMigration(LinkKind kind) => kind is LinkKind.Junction or LinkKind.DirectorySymlink;

    /// <summary>迁移场景下可选的链接类型。</summary>
    public static IReadOnlyList<LinkKind> MigrationLinkKinds { get; } = new[] { LinkKind.Junction, LinkKind.DirectorySymlink };

    /// <summary>
    /// 智能推荐（需求「链接类型支持」第 3 条）：
    /// 目录 + 本地卷 → Junction；目录 + 需要 UNC 或相对路径 → 目录符号链接；
    /// 文件 + 同卷 → 硬链接；文件 + 跨卷 → 文件符号链接。
    /// </summary>
    public LinkRecommendation Recommend(LinkRequest req)
    {
        var attrs = NativeLinkHelper.TryGetAttributes(PathNormalizer.NormalizeAbsolute(req.TargetPath));
        var targetIsUnc = PathNormalizer.IsUnc(req.TargetPath);

        // UNC 目标：Junction 与硬链接都不支持网络位置，只剩符号链接。
        // 目标不可达时无法区分文件/目录，按目录符号链接给出默认推荐并说明原因。
        if (targetIsUnc)
        {
            var isFile = attrs is not null && !NativeLinkHelper.IsDirectory(attrs.Value);
            return isFile
                ? new LinkRecommendation(LinkKind.FileSymlink, "目标是 UNC / 网络文件：硬链接不支持网络路径，故推荐文件符号链接。")
                : new LinkRecommendation(LinkKind.DirectorySymlink, "目标是 UNC / 网络路径：目录联接与硬链接都不支持网络位置，故推荐目录符号链接。");
        }

        if (attrs is null)
            return new LinkRecommendation(LinkKind.Junction, "目标不存在（无法判断类型），默认按目录联接给出推荐；目录联接兼容性最好且普通权限即可。");

        var targetIsDir = NativeLinkHelper.IsDirectory(attrs.Value);

        if (targetIsDir)
        {
            if (req.PreferRelativeTarget)
                return new LinkRecommendation(LinkKind.DirectorySymlink, "需要相对路径目标，目录联接不支持相对路径，故推荐目录符号链接。");
            return new LinkRecommendation(LinkKind.Junction, "目标为本地目录：目录联接兼容性最好，普通权限即可，不消耗额外空间。");
        }

        // 文件
        if (req.PreferRelativeTarget)
            return new LinkRecommendation(LinkKind.FileSymlink, "需要相对路径目标，故推荐文件符号链接。");

        if (Volumes.TryGetVolumeInfo(req.TargetPath, out var targetVolume)
            && Volumes.TryGetVolumeInfo(req.LinkPath, out var linkVolume))
        {
            if (targetVolume.SerialNumber == linkVolume.SerialNumber)
                return new LinkRecommendation(LinkKind.HardLink, "目标文件与链接落点在同一卷：硬链接零额外空间、普通权限即可。");
            return new LinkRecommendation(LinkKind.FileSymlink, "目标文件与链接落点跨卷：硬链接不支持跨卷，故推荐文件符号链接。");
        }

        return new LinkRecommendation(LinkKind.FileSymlink, "无法判定卷信息，保守推荐文件符号链接。");
    }

    /// <summary>解析请求（auto 时按推荐规则选定），并给出选定类型的能力校验。</summary>
    public LinkResolution Resolve(LinkRequest req)
    {
        if (req.RequestedKind is { } explicitKind)
        {
            var capability = _providers[explicitKind].Evaluate(req);
            return new LinkResolution(explicitKind, false, "用户显式指定。", capability);
        }

        var recommendation = Recommend(req);
        var recommended = _providers[recommendation.Kind].Evaluate(req);

        // 推荐项不可用但存在其它可行项时，给出可操作的提示（不自动改选，避免静默偏离用户预期）。
        var alternatives = _providers
            .Where(p => p.Key != recommendation.Kind)
            .Select(p => (p.Key, Capability: p.Value.Evaluate(req)))
            .Where(x => x.Capability.CanHandle)
            .Select(x => x.Key.ToString())
            .ToList();

        var suggestion = recommended.CanHandle || alternatives.Count == 0
            ? recommended.Suggestion
            : $"{recommended.Suggestion ?? string.Empty} 当前环境下可用的替代类型：{string.Join("、", alternatives)}。";

        return new LinkResolution(
            recommendation.Kind,
            true,
            recommendation.Reason,
            recommended with { Suggestion = suggestion });
    }

    /// <summary>遍历四种类型的能力，供 UI 置灰与 CLI --json 输出。</summary>
    public IReadOnlyList<LinkResolution> EvaluateAll(LinkRequest req)
        => _providers.Keys.Select(kind => new LinkResolution(kind, false, string.Empty, _providers[kind].Evaluate(req))).ToList();

    /// <summary>
    /// 创建链接（auto 时先解析）。失败抛出：
    /// <see cref="LinkPrivilegeRequiredException"/>（需提权，CLI 返回 5）、
    /// <see cref="InvalidOperationException"/>（能力校验不通过，CLI 返回 3）、
    /// <see cref="DirPorterNativeException"/>（原生调用失败，CLI 返回 6）。
    /// </summary>
    public LinkResolution Create(LinkRequest req, LinkCreateOptions opt)
    {
        var resolution = Resolve(req);
        var provider = _providers[resolution.Kind];
        provider.Create(req, opt);
        return resolution;
    }

    public bool Exists(string path)
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

    /// <summary>
    /// 检视任意路径：区分四种链接类型、输出重解析标签、解析后的目标绝对路径、是否相对、是否悬空。
    /// 硬链接不是重解析点，走 NumberOfLinks 分支判定。
    /// </summary>
    public LinkInfo Inspect(string path)
    {
        var extended = PathNormalizer.NormalizeAbsolute(path);
        var attrs = NativeLinkHelper.TryGetAttributes(extended);

        if (attrs is null) return new LinkInfo(
            PathNormalizer.StripExtendedPrefix(extended), false, false, null, 0,
            null, null, null, false, false, 0, false, null);

        if (NativeLinkHelper.IsReparsePoint(attrs.Value))
        {
            var raw = NativeLinkHelper.ReadReparseData(extended);
            if (raw is not null && ReparseData.TryParse(raw, out var parsed))
            {
                if (parsed.Tag == ReparseData.TagMountPoint)
                    return _providers[LinkKind.Junction].Inspect(extended);
                if (parsed.Tag == ReparseData.TagSymlink)
                    return _providers[NativeLinkHelper.IsDirectory(attrs.Value) ? LinkKind.DirectorySymlink : LinkKind.FileSymlink].Inspect(extended);
            }

            // 其它重解析点（OneDrive 占位符等）：如实报告标签，不猜测类型。
            var tag = raw is not null && raw.Length >= 4 ? BitConverter.ToUInt32(raw.AsSpan(0, 4)) : 0u;
            return new LinkInfo(
                PathNormalizer.StripExtendedPrefix(extended), true, true, null, tag,
                null, null, null, false, false, 0, NativeLinkHelper.IsDirectory(attrs.Value),
                NativeLinkHelper.HexPreview(raw));
        }

        return _providers[LinkKind.HardLink].Inspect(extended);
    }

    /// <summary>
    /// 解除链接：只删除链接本体。分派依据是 <see cref="Inspect"/> 的实际判定结果，
    /// 因此即使调用方传错类型也不会误删。
    /// </summary>
    public LinkKind Delete(string linkPath)
    {
        var info = Inspect(linkPath);

        if (!info.PathExists)
            throw new InvalidOperationException($"路径不存在，无需解除链接：{info.Path}");

        if (info.Kind is null)
            throw new LinkDeleteRefusedException(
                info.IsReparsePoint
                    ? $"拒绝解除链接：{info.Path} 是重解析点但不是本工具支持的类型（标签 0x{info.ReparseTag:X8}）。"
                    : $"拒绝解除链接：{info.Path} 不是链接，删除它会丢失真实数据。");

        _providers[info.Kind.Value].Delete(linkPath);
        return info.Kind.Value;
    }
}
