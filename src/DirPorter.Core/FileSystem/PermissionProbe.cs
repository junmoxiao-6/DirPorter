using DirPorter.Core.Diagnostics;

namespace DirPorter.Core.FileSystem;

/// <summary>一次权限探测的结果（用于给出精确的"为什么会被拒绝"原因）。</summary>
public sealed record PermissionProbeResult(
    string Path,
    bool CanList,
    bool CanDelete,
    bool CanAddChild,
    int? Win32Code,
    string Detail)
{
    public bool AllGranted => CanList && CanDelete && CanAddChild;
}

/// <summary>
/// 权限探测（PRECHECK 源侧第 6 条 / 目标侧第 12 条）。
///
/// 设计取舍：**不做 ACL 逐项解析**（`AccessCheck` + 通用映射代价高、易误判），
/// 而是用"以目标权限打开句柄、但不写任何内容"的方式逼近真实行为：
/// <list type="bullet">
/// <item>读：以 <c>FILE_LIST_DIRECTORY</c> 打开目录句柄；</item>
/// <item>删：以 <c>DELETE</c> 打开对象句柄（PARKING 改名需要 DELETE 权限）；</item>
/// <item>建子项：以 <c>FILE_ADD_FILE | FILE_ADD_SUBDIRECTORY</c> 打开父目录句柄（建链接/建目标目录需要）。</item>
/// </list>
/// 已知局限（必须在文档中写明）：句柄探测无法覆盖"稍后权限被撤销 / 文件被独占"的竞态，
/// 也无法覆盖 ACL 允许但策略（如受控文件夹访问）拦截的情况。
/// </summary>
public static class PermissionProbe
{
    /// <summary>能否枚举目录内容。</summary>
    public static bool CanListDirectory(string extendedPath)
        => TryOpen(extendedPath, Kernel32.FileListDirectory | Kernel32.FileReadAttributes, out _);

    /// <summary>能否删除/重命名该对象（需要对象自身的 DELETE 权限）。</summary>
    public static bool CanDeleteObject(string extendedPath)
        => TryOpen(extendedPath, Kernel32.Delete | Kernel32.FileReadAttributes, out _);

    /// <summary>能否在该目录下创建子项（文件或子目录）。</summary>
    public static bool CanAddChildTo(string extendedDirectoryPath)
        => TryOpen(extendedDirectoryPath, Kernel32.FileAddFile | Kernel32.FileAddSubdirectory | Kernel32.FileReadAttributes, out _);

    /// <summary>源目录组合探测：读 + 删 + （父目录）建子项。</summary>
    public static PermissionProbeResult ProbeSource(string sourceExtendedPath)
    {
        var canList = TryOpen(sourceExtendedPath, Kernel32.FileListDirectory | Kernel32.FileReadAttributes, out var listError);
        var canDelete = TryOpen(sourceExtendedPath, Kernel32.Delete | Kernel32.FileReadAttributes, out var deleteError);

        var parent = GetParent(sourceExtendedPath);
        var canAddChild = parent is not null && TryOpen(parent, Kernel32.FileAddFile | Kernel32.FileAddSubdirectory | Kernel32.FileReadAttributes, out _);

        var failureCode = listError != Kernel32.ErrorSuccess
            ? listError
            : deleteError != Kernel32.ErrorSuccess ? deleteError : Kernel32.ErrorSuccess;

        var detail = canList && canDelete && canAddChild
            ? "对源目录具备读、删、改父目录权限。"
            : BuildDetail(canList, canDelete, canAddChild);

        return new PermissionProbeResult(
            PathNormalizer.StripExtendedPrefix(sourceExtendedPath), canList, canDelete, canAddChild,
            failureCode == Kernel32.ErrorSuccess ? null : failureCode, detail);
    }

    /// <summary>目标父目录探测：能否在其中创建子项。</summary>
    public static PermissionProbeResult ProbeTargetParent(string targetParentExtendedPath)
    {
        // 目标父目录可能还不存在：向上找到最近的存在祖先，逐级判定其子项创建权限。
        var existing = FindNearestExistingAncestor(targetParentExtendedPath);
        if (existing is null)
        {
            return new PermissionProbeResult(
                PathNormalizer.StripExtendedPrefix(targetParentExtendedPath),
                false, false, false, Kernel32.ErrorPathNotFound,
                "目标路径所在的任何一层上级目录都不存在或不可访问。");
        }

        var canAdd = TryOpen(existing, Kernel32.FileAddFile | Kernel32.FileAddSubdirectory | Kernel32.FileReadAttributes, out var error);
        var sameAsRequested = string.Equals(existing, targetParentExtendedPath, StringComparison.OrdinalIgnoreCase);

        return new PermissionProbeResult(
            PathNormalizer.StripExtendedPrefix(existing),
            canAdd, canAdd, canAdd,
            canAdd ? null : error,
            canAdd
                ? (sameAsRequested ? "目标父目录可写。" : $"目标父目录不存在，但最近的上级目录可写（将自动创建缺失层级）：{PathNormalizer.StripExtendedPrefix(existing)}")
                : $"最近的上级目录不可写：{PathNormalizer.StripExtendedPrefix(existing)}");
    }

    /// <summary>向上找到最近的存在且为目录的祖先（含自身）。</summary>
    public static string? FindNearestExistingAncestor(string extendedPath)
    {
        var current = PathNormalizer.TrimTrailingSeparator(extendedPath);

        while (true)
        {
            var attributes = Kernel32.GetFileAttributes(current);
            if (attributes != Kernel32.InvalidFileAttributes && (attributes & Kernel32.FileAttributeDirectory) != 0)
                return current;

            var parent = GetParent(current);
            if (parent is null) return null;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) return null;
            current = parent;
        }
    }

    private static string? GetParent(string extendedPath)
    {
        var trimmed = PathNormalizer.TrimTrailingSeparator(extendedPath);
        var parent = Path.GetDirectoryName(trimmed);
        if (string.IsNullOrEmpty(parent)) return null;
        return PathNormalizer.TrimTrailingSeparator(parent);
    }

    private static bool TryOpen(string extendedPath, uint desiredAccess, out int win32Code)
    {
        // 打开句柄只探测权限，不做任何写入。目录必须带 FILE_FLAG_BACKUP_SEMANTICS。
        var handle = Kernel32.CreateFile(
            extendedPath,
            desiredAccess,
            Kernel32.FileShareRead | Kernel32.FileShareWrite | Kernel32.FileShareDelete,
            IntPtr.Zero,
            Kernel32.OpenExisting,
            Kernel32.FileFlagBackupSemantics,
            IntPtr.Zero);

        if (handle == Kernel32.InvalidHandleValue)
        {
            win32Code = Win32Error.Last;
            return false;
        }

        win32Code = Kernel32.ErrorSuccess;
        Kernel32.CloseHandle(handle);
        return true;
    }

    private static string BuildDetail(bool canList, bool canDelete, bool canAddChild)
    {
        var missing = new List<string>();
        if (!canList) missing.Add("读取目录内容");
        if (!canDelete) missing.Add("删除/重命名该目录");
        if (!canAddChild) missing.Add("在父目录中创建子项（建链接需要）");
        return "权限不足：" + string.Join("、", missing) + "。";
    }
}
