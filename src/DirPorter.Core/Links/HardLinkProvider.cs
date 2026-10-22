using DirPorter.Core.Diagnostics;
using DirPorter.Core.FileSystem;

namespace DirPorter.Core.Links;

/// <summary>
/// 硬链接提供器（mklink /H）。
///
/// 与另外三种的关键差异：硬链接**不是重解析点**，它是同一个 MFT 记录的多个目录项。
/// 因此：
///   - 无法与"恰好有多个链接的普通文件"区分，判定依据只能是 NumberOfLinks &gt; 1；
///   - 不支持跨卷（同一份数据必须在同一个卷上）；
///   - 删除单个硬链接 = 删除一个目录项，数据本身由其它链接保留 —— 但若 NumberOfLinks 已是 1，
///     删它等同于删普通文件（数据丢失），因此 Delete 必须拒绝，这是刻意的保守设计。
/// </summary>
public sealed class HardLinkProvider : LinkProviderBase
{
    public HardLinkProvider(IVolumeResolver volumes) : base(volumes) { }

    public override LinkKind Kind => LinkKind.HardLink;
    public override bool SupportsDirectories => false;
    public override bool SupportsFiles => true;

    public override LinkCapability Evaluate(LinkRequest req)
    {
        if (!TryNormalize(req.LinkPath, req.TargetPath, out var link, out var target, out var error))
            return error;

        // UNC 判定先于存在性判定：硬链接在物理上不可能跨到网络位置。
        if (PathNormalizer.IsUnc(target) || PathNormalizer.IsUnc(link))
        {
            return LinkCapability.No(LinkCapabilityCode.UncNotSupported, "硬链接不支持 UNC / 网络路径（必须是同一本地卷）。");
        }

        // 目标必须是已存在的文件（目录被 CheckStructural 拒绝）。
        var structural = CheckStructural(link, target, targetMustBeDirectory: false);
        if (structural is not null) return structural.Value;

        // 目标本身不能是链接（对符号链接建硬链接会指向链接本体，语义混乱）。
        var targetAttrs = NativeLinkHelper.TryGetAttributes(target)!.Value;
        if (NativeLinkHelper.IsReparsePoint(targetAttrs))
        {
            return LinkCapability.No(
                LinkCapabilityCode.TargetWrongKind,
                "目标是一个重解析点（链接），不能作为硬链接的目标。",
                "请改用真实文件路径。");
        }

        if (!Volumes.TryGetVolumeInfo(target, out var targetVolume))
        {
            return LinkCapability.No(LinkCapabilityCode.VolumeUnavailable, $"无法获取目标所在卷的信息：{PathNormalizer.StripExtendedPrefix(target)}");
        }

        if (!Volumes.TryGetVolumeInfo(link, out var linkVolume))
        {
            return LinkCapability.No(LinkCapabilityCode.VolumeUnavailable, $"无法获取链接路径所在卷的信息：{PathNormalizer.StripExtendedPrefix(link)}");
        }

        if (!targetVolume.SupportsHardLinks)
        {
            return LinkCapability.No(
                LinkCapabilityCode.FileSystemNotSupported,
                $"目标所在卷的文件系统是 {targetVolume.FileSystemName}，不支持硬链接。");
        }

        // 跨卷：硬链接在物理上不可能（同一份数据必须在同一卷上）。
        if (targetVolume.SerialNumber != linkVolume.SerialNumber)
        {
            return LinkCapability.No(
                LinkCapabilityCode.CrossVolumeNotSupported,
                "硬链接不支持跨卷：目标与链接落点必须在同一个卷上。",
                "跨卷场景请改用文件符号链接。");
        }

        var slot = CheckLinkSlot(link);
        if (slot is not null) return slot.Value;

        return LinkCapability.Ok("可以创建：硬链接，零额外空间，普通权限即可，仅限同一卷内。");
    }

    public override void Create(LinkRequest req, LinkCreateOptions opt)
    {
        if (!TryNormalize(req.LinkPath, req.TargetPath, out var link, out var target, out var error))
            throw new InvalidOperationException(error.Reason);

        // 结构校验：失败时无任何磁盘改动，回滚动作 = 无。
        ThrowIfBlocked(CheckStructural(link, target, targetMustBeDirectory: false));

        if (opt.DryRun) return; // 干跑：零磁盘写入。

        // 处理已存在的落点。此步失败时的回滚动作：不产生新状态。
        var attrs = NativeLinkHelper.TryGetAttributes(link);
        if (attrs is not null)
        {
            if (!opt.ReplaceExisting)
                throw new InvalidOperationException($"链接路径已存在，且未开启 ReplaceExisting：{PathNormalizer.StripExtendedPrefix(link)}");
            if (NativeLinkHelper.IsDirectory(attrs.Value))
                throw new InvalidOperationException($"链接路径已存在目录，无法作为硬链接落点：{PathNormalizer.StripExtendedPrefix(link)}");

            // 回滚动作：此步删除的是"落点上原有的文件"，属于 ReplaceExisting 的显式语义，不参与后续回滚。
            if (!Kernel32.DeleteFile(link))
            {
                var code = Win32Error.Last;
                throw new DirPorterNativeException("删除已存在的落点文件", code, $"删除已存在的落点文件失败：{Win32Error.Describe(code)}");
            }
        }

        // CreateHardLink 自带跨卷/文件系统校验；此步失败时的回滚动作：无（未产生新的目录项）。
        if (!Kernel32.CreateHardLink(link, target, IntPtr.Zero))
        {
            var code = Win32Error.Last;
            var hint = code switch
            {
                Kernel32.ErrorAlreadyExists => "落点已存在同名文件。",
                Kernel32.ErrorAccessDenied => "权限不足，或目标位于只读位置。",
                17 => "目标与落点不在同一个卷上（硬链接要求同卷）。",
                _ => null,
            };
            var message = $"创建硬链接失败：{Win32Error.Describe(code)}" + (hint is null ? string.Empty : $"（{hint}）");
            throw new DirPorterNativeException("创建硬链接", code, message);
        }

        // 创建后校验。此步失败时的回滚动作：删除刚落点上的目录项（不影响其它链接与数据本体）。
        var info = Inspect(link);
        if (info.Kind != LinkKind.HardLink)
        {
            try
            {
                Kernel32.DeleteFile(link);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DirPorter] 回滚删除硬链接失败：{ex.Message}");
            }

            throw new InvalidOperationException($"硬链接创建后校验失败（NumberOfLinks={info.NumberOfLinks}），已回滚。");
        }
    }

    public override LinkInfo Inspect(string path)
    {
        var extended = PathNormalizer.NormalizeAbsolute(path);
        var attrs = NativeLinkHelper.TryGetAttributes(extended);
        if (attrs is null) return BuildInfo(extended, null, null, 0, null, null, false, false, 0, null);

        // 硬链接不是重解析点；若该路径本身是重解析点，则不是硬链接。
        if (NativeLinkHelper.IsReparsePoint(attrs.Value) || NativeLinkHelper.IsDirectory(attrs.Value))
            return BuildInfo(extended, attrs, null, 0, null, null, false, false, 0, null);

        var info = NativeLinkHelper.QueryFileInformation(extended);
        var numberOfLinks = info?.nNumberOfLinks ?? 0;

        // 判据：文件的硬链接数 > 1。注意这无法区分"本工具创建的硬链接"与"原本就多链接的文件"。
        var kind = numberOfLinks > 1 ? LinkKind.HardLink : (LinkKind?)null;
        return BuildInfo(extended, attrs, kind, 0, null, null, false, false, numberOfLinks, null);
    }

    /// <summary>
    /// 只删除链接本体（一个目录项），不触碰数据。
    ///
    /// ⚠️ 保守拒绝策略：仅当硬链接数 &gt; 1 时才允许删。若已降回 1，该路径就是唯一的目录项，
    /// 删除它等同于删除文件数据，属于 unlink 语义之外的破坏性操作，必须拒绝并交由显式删除流程处理。
    ///
    /// ⚠️ 该守卫是必要但不充分的（实测发现，见设计与计划文档 §21）：
    /// 在把删除路由到回收站的系统上，删除一个硬链接只是把这个"名字"移入 $Recycle.Bin
    /// （同卷重命名，保留同一 MFT 记录），NumberOfLinks 不会下降。因此：
    ///   - 批量解链 / linkdir 增量判定 **不得** 只依赖 NumberOfLinks 判断"还剩几个名字"，
    ///     必须维护本工具自己创建过的链接台账（ledger），逐条核对后再删；
    ///   - 单条 unlink 场景下本守卫仍然有效：它挡住了"唯一目录项"这一确定性危险。
    ///
    /// 此步失败时的回滚动作：无（DeleteFile 要么成功要么无副作用）。
    /// </summary>
    public override void Delete(string linkPath)
    {
        var extended = PathNormalizer.NormalizeAbsolute(linkPath);
        var info = Inspect(extended);

        if (!info.PathExists) throw new InvalidOperationException($"路径不存在，无需删除：{info.Path}");

        if (info.Kind != LinkKind.HardLink)
        {
            throw new LinkDeleteRefusedException(
                $"拒绝删除：{info.Path} 不是硬链接（硬链接数={info.NumberOfLinks}）。"
                + "删除唯一的目录项会真正丢失数据，unlink 只允许删除链接本体。");
        }

        if (!Kernel32.DeleteFile(extended))
        {
            var code = Win32Error.Last;
            throw new DirPorterNativeException("删除硬链接", code, $"删除硬链接失败：{Win32Error.Describe(code)}");
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
