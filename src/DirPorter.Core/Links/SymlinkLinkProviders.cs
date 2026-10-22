using DirPorter.Core.Diagnostics;
using DirPorter.Core.FileSystem;
using DirPorter.Core.Privilege;

namespace DirPorter.Core.Links;

/// <summary>
/// 符号链接提供器基类。目录符号链接与文件符号链接只差"目标必须是目录/文件"以及创建标志位。
/// 创建走 <c>CreateSymbolicLinkW</c>；检视走 FSCTL_GET_REPARSE_POINT 解析 IO_REPARSE_TAG_SYMLINK。
/// </summary>
public abstract class SymlinkLinkProviderBase : LinkProviderBase
{
    protected IPrivilegeProbe Privileges { get; }

    protected SymlinkLinkProviderBase(IVolumeResolver volumes, IPrivilegeProbe privileges) : base(volumes)
    {
        Privileges = privileges ?? throw new ArgumentNullException(nameof(privileges));
    }

    /// <summary>本提供器要求的目标类型。</summary>
    protected abstract bool TargetMustBeDirectory { get; }

    public override LinkCapability Evaluate(LinkRequest req)
    {
        if (!TryNormalize(req.LinkPath, req.TargetPath, out var link, out var target, out var error))
            return error;

        var structural = CheckStructural(link, target, TargetMustBeDirectory);
        if (structural is not null) return structural.Value;

        // 权限：管理员 或 开发者模式。UI 据此置灰，不允许点下去才报错（需求约束 2）。
        if (!Privileges.CanCreateSymbolicLink)
        {
            return LinkCapability.No(
                LinkCapabilityCode.PrivilegeNeeded,
                "创建符号链接需要管理员权限，或已开启系统「开发者模式」。",
                $"当前环境：{Privileges.Describe()}。请以管理员身份运行，或在「设置 → 隐私和安全性 → 开发者选项」中开启开发者模式。");
        }

        var linkVolumeIssue = CheckReparseSupportOn(link, "链接路径");
        if (linkVolumeIssue is not null) return linkVolumeIssue.Value;

        var slot = CheckLinkSlot(link);
        if (slot is not null) return slot.Value;

        var features = new List<string>();
        if (req.PreferRelativeTarget) features.Add("支持相对路径目标");
        if (PathNormalizer.IsUnc(req.TargetPath)) features.Add("支持 UNC / 网络目标");
        var extra = features.Count == 0 ? string.Empty : $"（{string.Join("、", features)}）";

        return LinkCapability.Ok($"可以创建：符号链接{extra}，支持跨卷，不消耗额外空间。");
    }

    public override void Create(LinkRequest req, LinkCreateOptions opt)
    {
        if (!TryNormalize(req.LinkPath, req.TargetPath, out var link, out var target, out var error))
            throw new InvalidOperationException(error.Reason);

        // 结构校验：失败时无任何磁盘改动，回滚动作 = 无。
        var structural = CheckStructural(link, target, TargetMustBeDirectory);
        ThrowIfBlocked(structural);

        if (!Privileges.CanCreateSymbolicLink)
            throw new LinkPrivilegeRequiredException(Kind, "创建符号链接需要管理员权限或已开启开发者模式。");

        if (opt.DryRun) return; // 干跑：零磁盘写入。

        // 处理已存在的落点。此步失败时的回滚动作：不产生新状态。
        // 注意：CreateSymbolicLinkW 不会覆盖已存在的文件，必须先删除。
        var attrs = NativeLinkHelper.TryGetAttributes(link);
        if (attrs is not null)
        {
            if (!opt.ReplaceExisting)
                throw new InvalidOperationException($"链接路径已存在，且未开启 ReplaceExisting：{PathNormalizer.StripExtendedPrefix(link)}");
            Delete(link);
        }

        var targetArgument = BuildTargetArgument(link, target, opt);
        var flags = (TargetMustBeDirectory ? Kernel32.SymbolicLinkFlagDirectory : 0u)
                    | (opt.AllowUnprivilegedSymlink ? Kernel32.SymbolicLinkFlagAllowUnprivilegedCreate : 0u);

        // 创建链接。此步失败时的回滚动作：无（未产生任何对象）。
        if (!Kernel32.CreateSymbolicLink(link, targetArgument, flags))
        {
            var code = Win32Error.Last;

            // 老系统不认识 ALLOW_UNPRIVILEGED_CREATE 标志，去掉后重试一次。
            if (code == Kernel32.ErrorInvalidParameter && opt.AllowUnprivilegedSymlink)
            {
                flags &= ~Kernel32.SymbolicLinkFlagAllowUnprivilegedCreate;
                if (Kernel32.CreateSymbolicLink(link, targetArgument, flags)) 
                {
                    VerifyAfterCreate(link);
                    return;
                }
                code = Win32Error.Last;
            }

            if (code == Kernel32.ErrorPrivilegeNotHeld)
            {
                throw new LinkPrivilegeRequiredException(
                    Kind,
                    $"创建符号链接被系统拒绝（{Win32Error.Describe(code)}）。请以管理员身份运行，或开启「开发者模式」。");
            }

            throw new DirPorterNativeException("创建符号链接", code, $"创建符号链接失败：{Win32Error.Describe(code)}");
        }

        VerifyAfterCreate(link);
    }

    public override LinkInfo Inspect(string path)
    {
        var extended = PathNormalizer.NormalizeAbsolute(path);
        var attrs = NativeLinkHelper.TryGetAttributes(extended);
        if (attrs is null) return BuildInfo(extended, null, null, 0, null, null, false, false, 0, null);
        if (!NativeLinkHelper.IsReparsePoint(attrs.Value)) return BuildInfo(extended, attrs, null, 0, null, null, false, false, 0, null);

        var raw = NativeLinkHelper.ReadReparseData(extended);
        if (raw is null) return BuildInfo(extended, attrs, null, 0, null, null, false, false, 0, null);
        if (!ReparseData.TryParse(raw, out var parsed))
            return BuildInfo(extended, attrs, null, BitConverter.ToUInt32(raw.AsSpan(0, 4)), null, null, false, false, 0, raw);
        if (parsed.Tag != ReparseData.TagSymlink)
            return BuildInfo(extended, attrs, null, parsed.Tag, parsed, null, false, false, 0, raw);

        var isDir = NativeLinkHelper.IsDirectory(attrs.Value);
        var kind = isDir ? LinkKind.DirectorySymlink : LinkKind.FileSymlink;
        var targetPlain = ResolveTarget(extended, parsed);

        return BuildInfo(extended, attrs, kind, parsed.Tag, parsed, targetPlain, parsed.IsRelative, IsDanglingTarget(targetPlain), 0, raw);
    }

    /// <summary>
    /// 只删除链接本体。⚠️ 安全红线见 <see cref="ReparsePointOps.RemoveLinkBody"/>。
    /// 此步失败时的回滚动作：若标签已剥离但空壳未删掉，会残留一个空目录/空文件，由调用方记录并提示。
    /// </summary>
    public override void Delete(string linkPath)
    {
        var extended = PathNormalizer.NormalizeAbsolute(linkPath);
        var info = Inspect(extended);

        if (!info.PathExists) throw new InvalidOperationException($"路径不存在，无需删除：{info.Path}");
        if (!info.IsReparsePoint)
            throw new LinkDeleteRefusedException($"拒绝删除：{info.Path} 不是重解析点（不是链接），删除它会丢失真实数据。");
        if (info.Kind is not (LinkKind.DirectorySymlink or LinkKind.FileSymlink))
            throw new LinkDeleteRefusedException($"拒绝删除：{info.Path} 是重解析点但不是符号链接（实际：{info.Kind?.ToString() ?? "未知标签"}）。");

        NativeLinkHelper.RemoveLinkBody(extended, info.IsDirectory, info.ReparseTag, "删除符号链接");
    }

    // ---- 内部辅助 ----

    private string BuildTargetArgument(string link, string targetExtended, LinkCreateOptions opt)
    {
        var targetPlain = PathNormalizer.TrimTrailingSeparator(PathNormalizer.StripExtendedPrefix(targetExtended));

        if (!opt.Relative) return targetPlain;

        if (PathNormalizer.IsUnc(targetPlain))
        {
            // UNC 目标无法用相对路径表达，退回绝对路径并记录（需求矩阵：Junction 不支持相对，symlink 支持）。
            System.Diagnostics.Debug.WriteLine("[DirPorter] 目标为 UNC，已忽略 --relative 请求，改用绝对路径。");
            return targetPlain;
        }

        var linkParent = Path.GetDirectoryName(PathNormalizer.TrimTrailingSeparator(PathNormalizer.StripExtendedPrefix(link)));
        if (string.IsNullOrEmpty(linkParent)) return targetPlain;

        return Path.GetRelativePath(linkParent, targetPlain);
    }

    /// <summary>把解析出的符号链接目标还原为绝对路径（相对链接以链接父目录为基准）。</summary>
    private static string ResolveTarget(string linkExtended, ReparseParseResult parsed)
    {
        if (!parsed.IsRelative)
        {
            var absolute = ReparseData.FromSubstituteName(parsed.SubstituteName);
            return string.IsNullOrEmpty(absolute) ? ReparseData.FromSubstituteName(parsed.PrintName) : absolute;
        }

        var relative = ReparseData.FromSubstituteName(parsed.SubstituteName);
        var linkParent = Path.GetDirectoryName(PathNormalizer.TrimTrailingSeparator(PathNormalizer.StripExtendedPrefix(linkExtended)));
        if (string.IsNullOrEmpty(linkParent)) return relative;

        try
        {
            return Path.GetFullPath(Path.Combine(linkParent, relative));
        }
        catch (Exception)
        {
            return relative;
        }
    }

    private void VerifyAfterCreate(string link)
    {
        var info = Inspect(link);
        if (info.Kind != Kind)
        {
            // 创建后校验失败 → 回滚：删除刚创建的链接（只删本体，绝不递归）。
            try
            {
                NativeLinkHelper.RemoveLinkBody(link, info.IsDirectory, info.ReparseTag, "回滚删除符号链接");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DirPorter] 回滚删除符号链接失败：{ex.Message}");
            }

            throw new InvalidOperationException($"符号链接创建后校验失败（期望 {Kind}，实际 {info.Kind}），已回滚。");
        }
    }

    private static void ThrowIfBlocked(LinkCapability? capability)
    {
        if (capability is null || capability.Value.CanHandle) return;
        var message = capability.Value.Suggestion is null
            ? capability.Value.Reason
            : $"{capability.Value.Reason} 建议：{capability.Value.Suggestion}";
        throw new InvalidOperationException(message);
    }
}

/// <summary>目录符号链接（mklink /D）。</summary>
public sealed class DirSymlinkLinkProvider : SymlinkLinkProviderBase
{
    public DirSymlinkLinkProvider(IVolumeResolver volumes, IPrivilegeProbe privileges) : base(volumes, privileges) { }

    public override LinkKind Kind => LinkKind.DirectorySymlink;
    public override bool SupportsDirectories => true;
    public override bool SupportsFiles => false;
    protected override bool TargetMustBeDirectory => true;
}

/// <summary>文件符号链接（mklink，无参数）。</summary>
public sealed class FileSymlinkLinkProvider : SymlinkLinkProviderBase
{
    public FileSymlinkLinkProvider(IVolumeResolver volumes, IPrivilegeProbe privileges) : base(volumes, privileges) { }

    public override LinkKind Kind => LinkKind.FileSymlink;
    public override bool SupportsDirectories => false;
    public override bool SupportsFiles => true;
    protected override bool TargetMustBeDirectory => false;
}
