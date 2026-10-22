using DirPorter.Core.Diagnostics;
using DirPorter.Core.FileSystem;

namespace DirPorter.Core.Links;

/// <summary>
/// 目录联接（Junction）提供器。
///
/// 实现约束（需求硬约束 1）：必须用原生 API，禁止 <c>cmd /c mklink</c>。
/// 流程：CreateDirectory → CreateFile(OPEN_REPARSE_POINT|BACKUP_SEMANTICS)
///      → DeviceIoControl(FSCTL_SET_REPARSE_POINT, IO_REPARSE_TAG_MOUNT_POINT)。
/// </summary>
public sealed class JunctionLinkProvider : LinkProviderBase
{
    public JunctionLinkProvider(IVolumeResolver volumes) : base(volumes) { }

    public override LinkKind Kind => LinkKind.Junction;
    public override bool SupportsDirectories => true;
    public override bool SupportsFiles => false;

    public override LinkCapability Evaluate(LinkRequest req)
    {
        if (!TryNormalize(req.LinkPath, req.TargetPath, out var link, out var target, out var error))
            return error;

        // Junction 的目标不支持 UNC（网络路径无法作为本地重解析点目标）。
        // 此判定必须在"目标是否存在"之前：UNC 不是"暂时不可达"，而是根本不被支持。
        if (PathNormalizer.IsUnc(target))
        {
            return LinkCapability.No(
                LinkCapabilityCode.UncNotSupported,
                "目录联接（Junction）不支持指向 UNC / 网络路径的目标。",
                "请改用「目录符号链接」，它支持 UNC 目标。");
        }

        var structural = CheckStructural(link, target, targetMustBeDirectory: true);
        if (structural is not null) return structural.Value;

        // 链接落点所在卷必须支持重解析点。
        var linkVolumeIssue = CheckReparseSupportOn(link, "链接路径");
        if (linkVolumeIssue is not null) return linkVolumeIssue.Value;

        // 目标卷必须是本地固定卷：可移动盘/U 盘拔盘后链接即失效，且可能写错位置。
        if (!Volumes.TryGetVolumeInfo(target, out var targetVolume))
        {
            return LinkCapability.No(LinkCapabilityCode.VolumeUnavailable, $"无法获取目标所在卷的信息：{PathNormalizer.StripExtendedPrefix(target)}");
        }

        if (!targetVolume.IsFixed)
        {
            return LinkCapability.No(
                LinkCapabilityCode.VolumeNotFixed,
                $"目标所在卷是 {targetVolume.DriveType}（非本地固定卷），Junction 只允许指向本地固定卷。",
                "可移动盘、U 盘、网络位置都存在失效风险，请换到本地固定磁盘。");
        }

        if (!targetVolume.SupportsReparsePoints)
        {
            return LinkCapability.No(
                LinkCapabilityCode.FileSystemNotSupported,
                $"目标所在卷的文件系统是 {targetVolume.FileSystemName}，不支持重解析点。",
                "Junction 只能在 NTFS / ReFS 卷之间建立。");
        }

        var slot = CheckLinkSlot(link);
        if (slot is not null) return slot.Value;

        // Junction 跨卷是支持的（这正是本工具的核心场景），此处显式记录，避免后来者误加"同卷"限制。
        var reason = targetVolume.FileSystemName.Equals("NTFS", StringComparison.OrdinalIgnoreCase)
            ? "可以创建：目录联接，普通权限即可，支持跨卷，不消耗额外空间。"
            : $"可以创建：目标卷为 {targetVolume.FileSystemName}，重解析点支持面依赖系统版本。";

        return targetVolume.FileSystemName.Equals("NTFS", StringComparison.OrdinalIgnoreCase)
            ? LinkCapability.Ok(reason)
            : LinkCapability.Risk(reason, "ReFS 上的重解析点行为建议先行干跑验证。");
    }

    public override void Create(LinkRequest req, LinkCreateOptions opt)
    {
        if (!TryNormalize(req.LinkPath, req.TargetPath, out var link, out var target, out var error))
            throw new InvalidOperationException(error.Reason);

        // 结构校验：失败时无任何磁盘改动，回滚动作 = 无。
        var structural = CheckStructural(link, target, targetMustBeDirectory: true);
        ThrowIfBlocked(structural);

        if (opt.DryRun) return; // 干跑：零磁盘写入。

        // 处理已存在的落点。此步失败时的回滚动作：不产生新状态（删除链接本身是幂等的）。
        PrepareSlot(link, opt);

        // 第 1 步：建立空的目录项（Junction 必须先有目录项才能挂重解析点）。
        // 此步失败时的回滚动作：无（目录未建成）。
        if (!Kernel32.CreateDirectory(link, IntPtr.Zero))
        {
            var code = Win32Error.Last;
            if (code != Kernel32.ErrorAlreadyExists)
                throw Diagnostic(code, "创建 Junction 落点目录");
        }

        // 第 2 步：打开目录句柄（不跟随重解析点）。
        // 此步失败时的回滚动作：删除第 1 步刚建的空目录（非递归，删不掉非空目录 → 天然安全）。
        var handle = NativeLinkHelper.OpenReparsePointHandle(link, Kernel32.GenericWrite);
        if (handle == Kernel32.InvalidHandleValue)
        {
            var code = Win32Error.Last;
            TryRemoveEmptyDirectoryRollback(link);
            throw Diagnostic(code, "打开 Junction 落点目录句柄");
        }

        try
        {
            // 第 3 步：写入重解析点。
            // 此步失败时的回滚动作：关闭句柄后删除刚建的空目录（同样非递归）。
            var plainTarget = PathNormalizer.TrimTrailingSeparator(PathNormalizer.StripExtendedPrefix(target));
            var substitute = ReparseData.ToSubstituteName(plainTarget);
            var buffer = ReparseData.BuildMountPoint(substitute, plainTarget);
            NativeLinkHelper.SetReparseData(handle, buffer, "写入 Junction 重解析点");
        }
        catch
        {
            Kernel32.CloseHandle(handle);
            handle = Kernel32.InvalidHandleValue;
            TryRemoveEmptyDirectoryRollback(link);
            throw;
        }
        finally
        {
            if (handle != Kernel32.InvalidHandleValue) Kernel32.CloseHandle(handle);
        }

        // 第 4 步：回读校验。
        // 此步失败时的回滚动作：删除刚创建的链接（只删链接本体，绝不递归）。
        var info = Inspect(link);
        if (info.Kind != LinkKind.Junction || info.IsDangling)
        {
            TryDeleteLinkRollback(link);
            throw new InvalidOperationException($"Junction 创建后校验失败（Kind={info.Kind}, Dangling={info.IsDangling}），已回滚。");
        }
    }

    public override LinkInfo Inspect(string path)
    {
        var extended = PathNormalizer.NormalizeAbsolute(path);
        var attrs = NativeLinkHelper.TryGetAttributes(extended);
        if (attrs is null)
            return BuildInfo(extended, null, null, 0, null, null, false, false, 0, null);

        if (!NativeLinkHelper.IsReparsePoint(attrs.Value))
            return BuildInfo(extended, attrs, null, 0, null, null, false, false, 0, null);

        var raw = NativeLinkHelper.ReadReparseData(extended);
        if (raw is null)
            return BuildInfo(extended, attrs, null, 0, null, null, false, false, 0, null);

        if (!ReparseData.TryParse(raw, out var parsed))
            return BuildInfo(extended, attrs, null, BitConverter.ToUInt32(raw.AsSpan(0, 4)), null, null, false, false, 0, raw);

        if (parsed.Tag != ReparseData.TagMountPoint)
        {
            // 是重解析点，但不是目录联接（例如符号链接，或 OneDrive 占位符）。
            return BuildInfo(extended, attrs, null, parsed.Tag, parsed, null, false, false, 0, raw);
        }

        var targetPlain = ReparseData.FromSubstituteName(parsed.SubstituteName);
        return BuildInfo(
            extended, attrs, LinkKind.Junction, parsed.Tag, parsed,
            targetAbsolute: targetPlain,
            isRelative: false,
            isDangling: IsDanglingTarget(targetPlain),
            numberOfLinks: 0,
            rawBuffer: raw);
    }

    /// <summary>
    /// 只删除链接本体。
    ///
    /// ⚠️ 安全红线：绝不走任何递归删除路径。<see cref="ReparsePointOps.RemoveLinkBody"/> 内部
    /// 以 FSCTL_DELETE_REPARSE_POINT 剥离标签后，再对"已变成空目录的壳"调用
    /// RemoveDirectory —— RemoveDirectory 对非空目录必然失败，因此物理上不可能误删数据。
    /// 此步失败时的回滚动作：若标签已剥离而目录壳未删掉，会残留一个空目录，由调用方记录并提示。
    /// </summary>
    public override void Delete(string linkPath)
    {
        var extended = PathNormalizer.NormalizeAbsolute(linkPath);
        var info = Inspect(extended);

        if (!info.PathExists)
            throw new InvalidOperationException($"路径不存在，无需删除：{info.Path}");

        if (!info.IsReparsePoint)
            throw new LinkDeleteRefusedException($"拒绝删除：{info.Path} 不是重解析点（不是链接），删除它会丢失真实数据。");

        if (info.Kind != LinkKind.Junction)
            throw new LinkDeleteRefusedException($"拒绝删除：{info.Path} 是重解析点但不是目录联接（实际：{info.Kind?.ToString() ?? "未知标签"}）。");

        NativeLinkHelper.RemoveLinkBody(extended, isDirectory: true, info.ReparseTag, "删除 Junction");
    }

    // ---- 内部辅助 ----

    private static void ThrowIfBlocked(LinkCapability? capability)
    {
        if (capability is null || capability.Value.CanHandle) return;
        var message = capability.Value.Suggestion is null
            ? capability.Value.Reason
            : $"{capability.Value.Reason} 建议：{capability.Value.Suggestion}";
        throw new InvalidOperationException(message);
    }

    private void PrepareSlot(string link, LinkCreateOptions opt)
    {
        var attrs = NativeLinkHelper.TryGetAttributes(link);
        if (attrs is null) return;

        if (NativeLinkHelper.IsReparsePoint(attrs.Value))
        {
            if (!opt.ReplaceExisting)
                throw new InvalidOperationException($"链接路径已存在链接，且未开启 ReplaceExisting：{PathNormalizer.StripExtendedPrefix(link)}");
            Delete(link);
            return;
        }

        if (NativeLinkHelper.IsDirectory(attrs.Value))
        {
            if (Directory.EnumerateFileSystemEntries(link).Any())
                throw new InvalidOperationException($"链接路径已存在非空目录，拒绝覆盖：{PathNormalizer.StripExtendedPrefix(link)}");
            return; // 空目录可以直接用作落点
        }

        throw new InvalidOperationException($"链接路径已存在文件，无法作为 Junction 落点：{PathNormalizer.StripExtendedPrefix(link)}");
    }

    private static void TryRemoveEmptyDirectoryRollback(string link)
    {
        try
        {
            if (Kernel32.RemoveDirectory(link))
            {
                return;
            }

            // 删不掉说明目录非空（可能是用户数据），此时必须保留 —— 这正是 RemoveDirectory 的安全价值。
            var code = Win32Error.Last;
            if (code != Kernel32.ErrorDirectoryNotEmpty && code != Kernel32.ErrorFileNotFound && code != Kernel32.ErrorPathNotFound)
            {
                System.Diagnostics.Debug.WriteLine($"[DirPorter] 回滚删除空目录失败：{Win32Error.Describe(code)}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DirPorter] 回滚删除空目录异常：{ex.Message}");
        }
    }

    private static void TryDeleteLinkRollback(string link)
    {
        try
        {
            NativeLinkHelper.RemoveLinkBody(link, isDirectory: true, ReparseData.TagMountPoint, "回滚删除 Junction");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DirPorter] 回滚删除 Junction 失败：{ex.Message}");
        }
    }

    private static DirPorterNativeException Diagnostic(int code, string operation)
        => new(operation, code, $"{operation} 失败：{Win32Error.Describe(code)}");
}
