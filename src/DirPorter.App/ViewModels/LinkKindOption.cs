using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DirPorter.App.Services;
using DirPorter.Core.Links;

namespace DirPorter.App.ViewModels;

/// <summary>
/// 迁移场景的一个可选链接类型。
///
/// R12 起：选择器**展示 mklink 的全部四种类型**（用户要求"补充完善"）——
/// 目录级两种（目录联接 / 目录符号链接）可选；文件级两种（文件符号链接 / 硬链接）
/// 因目标必须是文件而**置灰**，悬停给出原因。这样用户能一眼看到工具支持的全集，
/// 而不是疑惑"为什么只有两种"。
/// </summary>
public sealed partial class LinkKindOption : ObservableObject
{
    public LinkKindOption(
        LinkKind kind,
        string nameKey,
        string hintKey,
        bool canUseForMigration = true,
        string? disabledHintKey = null)
    {
        Kind = kind;
        NameKey = nameKey;
        HintKey = hintKey;
        CanUseForMigration = canUseForMigration;
        DisabledHintKey = disabledHintKey;
        Refresh();
    }

    public LinkKind Kind { get; }

    public string NameKey { get; }

    public string HintKey { get; }

    /// <summary>是否可用于目录迁移（文件级类型为 false → 选择器里置灰）。</summary>
    public bool CanUseForMigration { get; }

    /// <summary>置灰原因（仅文件级类型有）；可用项为 null，悬停时不显示空提示。</summary>
    public string? DisabledHintKey { get; }

    [ObservableProperty] private string _display = string.Empty;
    [ObservableProperty] private string _hint = string.Empty;
    [ObservableProperty] private string? _disabledHint;

    public void Refresh()
    {
        Display = LocalizationService.Instance.Get(NameKey);
        Hint = LocalizationService.Instance.Get(HintKey);
        DisabledHint = DisabledHintKey is null ? null : LocalizationService.Instance.Get(DisabledHintKey);
    }

    /// <summary>
    /// 迁移场景的完整选项集（单一事实来源）：四种类型全展示，文件级两种置灰。
    /// 迁移页、一键迁移页、设置页共用，保证三处永不分叉。
    /// </summary>
    public static ObservableCollection<LinkKindOption> BuildMigrationSet() => new()
    {
        new(LinkKind.Junction, "link.type.junction", "mig.linkKind.hint.junction"),
        new(LinkKind.DirectorySymlink, "link.type.symlinkDir", "mig.linkKind.hint.symlinkDir"),
        new(LinkKind.FileSymlink, "link.type.symlinkFile", "mig.linkKind.fileOnly",
            canUseForMigration: false, disabledHintKey: "mig.linkKind.fileOnly"),
        new(LinkKind.HardLink, "link.type.hardlink", "mig.linkKind.fileOnly",
            canUseForMigration: false, disabledHintKey: "mig.linkKind.fileOnly"),
    };
}
