namespace DirPorter.Core.Migration;

/// <summary>
/// 迁移状态机的状态集合。
/// 顺序与需求「迁移状态机」严格一致，任何新增状态都必须同时在
/// <see cref="MigrationStateExtensions.IsIntermediate"/> 与恢复逻辑中登记。
/// </summary>
public enum MigrationState
{
    /// <summary>已规划，尚未开始。</summary>
    Planned = 0,

    /// <summary>前置校验（零磁盘写入）。</summary>
    Precheck = 1,

    /// <summary>干跑预览（零磁盘写入，执行完即出栈）。</summary>
    DryRun = 2,

    /// <summary>占用扫描（Restart Manager）。</summary>
    OccupancyScan = 3,

    /// <summary>目标准备（建父目录、空间复检）。</summary>
    TargetPrepare = 4,

    /// <summary>复制中（字节级进度）。</summary>
    Copying = 5,

    /// <summary>校验中（文件数 + 总字节 + 逐文件 size/mtime）。</summary>
    Verifying = 6,

    /// <summary>停车：把源目录同卷改名为 <c>&lt;name&gt;.migbak-&lt;ts&gt;</c>。</summary>
    Parking = 7,

    /// <summary>建链：在原路径创建链接指向新位置。</summary>
    Linking = 8,

    /// <summary>链接校验（解析目标、枚举子项、试读一个文件）。</summary>
    LinkVerify = 9,

    /// <summary>台账落盘（需求要求：LINK_VERIFY 成功后立即写入；不改变需求给的状态序列，作为子步骤存在）。</summary>
    LedgerCommit = 10,

    /// <summary>备份清理（唯一不可逆点；按保留策略决定是否立即删除）。</summary>
    BackupCleanup = 11,

    /// <summary>完成。</summary>
    Done = 12,

    /// <summary>回滚中。</summary>
    RollingBack = 13,

    /// <summary>已回滚（磁盘恢复到开始前的状态）。</summary>
    RolledBack = 14,

    /// <summary>失败且回滚未能完全成功（需要人工介入）。</summary>
    Failed = 15,

    /// <summary>进程被强杀遗留的中间态（重启后由恢复逻辑识别）。</summary>
    Interrupted = 16,

    /// <summary>
    /// 同卷快速移动：把源目录**直接改名**到目标位置（瞬时、原子，零数据拷贝）。
    /// 与资源管理器"剪切同卷文件"是同一个语义。
    /// </summary>
    MovingToTarget = 17,
}

public static class MigrationStateExtensions
{
    /// <summary>终态：不会再自动推进。</summary>
    public static bool IsTerminal(this MigrationState state)
        => state is MigrationState.Done or MigrationState.RolledBack or MigrationState.Failed;

    /// <summary>
    /// 中间态：进程被强杀后可能停在这些状态，重启时**必须**被恢复逻辑识别并给出修复建议。
    /// </summary>
    public static bool IsIntermediate(this MigrationState state)
        => state is MigrationState.OccupancyScan
            or MigrationState.TargetPrepare
            or MigrationState.Copying
            or MigrationState.MovingToTarget
            or MigrationState.Verifying
            or MigrationState.Parking
            or MigrationState.Linking
            or MigrationState.LinkVerify
            or MigrationState.LedgerCommit
            or MigrationState.BackupCleanup
            or MigrationState.RollingBack
            or MigrationState.Interrupted;

    /// <summary>是否已经改动了磁盘（PRECHECK / DRY_RUN 之前为 false）。</summary>
    public static bool MayHaveWrittenDisk(this MigrationState state)
        => state is >= MigrationState.TargetPrepare;

    public static string Describe(this MigrationState state) => state switch
    {
        MigrationState.Planned => "已规划",
        MigrationState.Precheck => "前置校验",
        MigrationState.DryRun => "干跑预览",
        MigrationState.OccupancyScan => "占用扫描",
        MigrationState.TargetPrepare => "目标准备",
        MigrationState.Copying => "复制中",
        MigrationState.MovingToTarget => "同卷改名（直接移动到目标）",
        MigrationState.Verifying => "校验中",
        MigrationState.Parking => "停车（源目录改名）",
        MigrationState.Linking => "创建链接",
        MigrationState.LinkVerify => "链接校验",
        MigrationState.LedgerCommit => "台账落盘",
        MigrationState.BackupCleanup => "备份清理",
        MigrationState.Done => "已完成",
        MigrationState.RollingBack => "回滚中",
        MigrationState.RolledBack => "已回滚",
        MigrationState.Failed => "失败（回滚未完全成功）",
        MigrationState.Interrupted => "中断（进程被杀）",
        _ => state.ToString(),
    };
}
