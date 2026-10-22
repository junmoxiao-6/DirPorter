using DirPorter.Core.Links;

namespace DirPorter.Core.Migration;

/// <summary>
/// 迁移计划中的一个条目：一个源目录 → 一个目标目录 + 链接类型。
///
/// <para>R12.4：<see cref="CreateLink"/> = false 表示"纯迁移"（只搬数据、不建链）——
/// 引擎会跳过 LINKING / LINK_VERIFY，状态机为 COPY_VERIFY → PARKING → DONE。
/// 纯迁移时 <see cref="LinkKind"/> 仅作占位（预检按"目录联接"的能力判定，保证无符号链接权限的环境不被误阻断）。</para>
/// </summary>
public sealed record MigrationPlanEntry(
    string SourcePath,
    string TargetPath,
    LinkKind LinkKind,
    string? EntryId = null,
    bool CreateLink = true)
{
    /// <summary>未显式指定时按顺序生成稳定的条目 ID（e-01、e-02 …）。</summary>
    public string ResolveEntryId(int index) => EntryId ?? $"e-{index + 1:00}";
}

/// <summary>一次迁移任务的所有条目。</summary>
public sealed record MigrationPlan(IReadOnlyList<MigrationPlanEntry> Entries, PrecheckOptions Options)
{
    public static MigrationPlan Single(string sourcePath, string targetPath, LinkKind kind = LinkKind.Junction, PrecheckOptions? options = null)
        => new(new[] { new MigrationPlanEntry(sourcePath, targetPath, kind) }, options ?? new PrecheckOptions());
}

/// <summary>预检选项（用户可确认项集中在此处）。</summary>
public sealed record PrecheckOptions
{
    /// <summary>用户已勾选"我理解风险"，接受警告名单与需确认项。</summary>
    public bool AcknowledgeWarnings { get; init; }

    /// <summary>目标同名空目录：允许复用（否则提示改名）。</summary>
    public bool ReuseEmptyTargetDirectory { get; init; }

    /// <summary>昂贵探测（硬链接数、ADS）的文件数上限；超过后如实降级并标注。</summary>
    public int DeepProbeFileLimit { get; init; } = 20_000;

    /// <summary>是否探测备用数据流（ADS）。</summary>
    public bool DetectAlternateDataStreams { get; init; } = true;

    /// <summary>是否探测服务/开机启动项引用（只读注册表，失败静默降级）。</summary>
    public bool ProbeStartupReferences { get; init; } = true;

    /// <summary>空间余量系数（默认 1.05，需求第 10 条）。</summary>
    public double SpaceHeadroomFactor { get; init; } = 1.05;

    public static PrecheckOptions Default { get; } = new();
}
