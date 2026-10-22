using DirPorter.App.ViewModels;
using DirPorter.Core.Backup;
using DirPorter.Core.Copy;
using DirPorter.Core.FileSystem;
using DirPorter.Core.Journal;
using DirPorter.Core.Links;
using DirPorter.Core.Migration;
using DirPorter.Core.Privilege;

namespace DirPorter.App.Services;

/// <summary>
/// 组合根：把 Core 的服务与 ViewModel 装配起来。
///
/// 刻意不引入 DI 容器：依赖数量固定、生命周期与窗口一致，
/// 手写装配更直观，也让单元测试能按需替换任意一个组件。
/// </summary>
public sealed class AppHost
{
    public UiSettings Settings { get; }

    public LocalizationService Loc => LocalizationService.Instance;

    public IUserInteraction Interaction { get; }

    public IPrivilegeProbe Privileges { get; }

    public LinkService Links { get; }

    public MigrationIndexStore Index { get; }

    public RestoreService Restore { get; }

    public BackupRetentionService Backups { get; }

    /// <summary>迁移引擎：引擎固定为自研 CopyFileEx，才能提供真正的暂停/继续与字节级进度。</summary>
    public MigrationEngine Engine { get; }

    /// <summary>按需提权（"以管理员身份重启"）。</summary>
    public IElevationService Elevation { get; }

    public MainViewModel Main { get; }

    /// <summary>最近一次自动/手动清理的结果（供界面展示）。</summary>
    public DirPorter.Core.Backup.BackupSweepResult? LastSweep { get; private set; }

    /// <summary>
    /// 按设置里的保留天数清理到期停车备份。
    /// **必须在后台线程调用**（要递归删除大目录）。
    /// 保留天数 &lt; 0 表示"永不清理"，此时服务直接返回空结果。
    /// </summary>
    public DirPorter.Core.Backup.BackupSweepResult SweepExpiredBackups(int? retentionDays = null)
    {
        var options = new DirPorter.Core.Backup.BackupSweepOptions
        {
            RetentionDays = retentionDays ?? Settings.BackupRetentionDays,
        };

        var result = Backups.Sweep(options);
        LastSweep = result;
        return result;
    }

    public AppHost(IUserInteraction interaction, UiSettings settings)
    {
        Interaction = interaction;
        Settings = settings;
        Privileges = Win32PrivilegeProbe.Current;

        Links = new LinkService(new Win32VolumeResolver(), Privileges);
        Index = new MigrationIndexStore();
        Restore = new RestoreService(Links, new JsonJournalStore(), Index);
        Backups = new BackupRetentionService(Index);

        Elevation = ElevationService.Current;

        Engine = new MigrationEngine(
            copier: new CopyFileExCopier(Privileges),
            journal: new JsonJournalStore(),
            index: Index);

        Main = new MainViewModel(this);
    }
}
