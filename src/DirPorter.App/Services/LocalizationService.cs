using System.ComponentModel;

namespace DirPorter.App.Services;

/// <summary>
/// 中英双语（需求「通用」：中英双语可切换）。
///
/// 设计取舍：**不做** .resx —— 中英文放在同一张表里逐条相邻，
/// 结构上就不可能漏译（漏一个连编译期都编不出这条键）。
/// 键缺失时回退显示键名本身，UI 上立刻能看出遗漏，而不是显示空白。
///
/// 绑定方式：<see cref="LocExtension"/> 把 <c>Loc Key</c> 变成对索引器的绑定；
/// 切换语言时抛 <c>Item[]</c> 变更通知，所有绑定一次性刷新。
/// </summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    /// <summary>全局实例（XAML 绑定用）。</summary>
    public static LocalizationService Instance { get; } = new();

    private string _language = Languages.Chinese;

    public static class Languages
    {
        public const string Chinese = "zh";
        public const string English = "en";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>当前语言：<c>zh</c> 或 <c>en</c>。未知值按 <c>zh</c> 处理。</summary>
    public string Language
    {
        get => _language;
        set
        {
            var normalized = value is Languages.English ? Languages.English : Languages.Chinese;
            if (string.Equals(_language, normalized, StringComparison.Ordinal)) return;

            _language = normalized;
            // 索引器绑定统一靠 "Item[]" 刷新（WPF 索引器绑定的约定名）。
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        }
    }

    /// <summary>索引器：XAML 里 <c>Loc Key</c> 即绑定到此处。</summary>
    public string this[string key] => Get(key);

    public string Get(string key)
        => Table.TryGetValue(key, out var entry)
            ? (_language == Languages.English ? entry.En : entry.Zh)
            : key;

    /// <summary>全部已定义的键（测试用于校验正反用例）。</summary>
    public static IReadOnlyCollection<string> Keys => Table.Keys;

    /// <summary>中文表（测试与导出用）。</summary>
    public static IReadOnlyDictionary<string, string> ChineseTable
        => Table.ToDictionary(static kv => kv.Key, static kv => kv.Value.Zh);

    /// <summary>英文表（测试用于校验"每条都有非空译文"）。</summary>
    public static IReadOnlyDictionary<string, string> EnglishTable
        => Table.ToDictionary(static kv => kv.Key, static kv => kv.Value.En);

    private static readonly Dictionary<string, (string Zh, string En)> Table = new(StringComparer.Ordinal)
    {
        // ---------------- 外壳 ----------------
        ["app.title"] = ("DirPorter — 目录迁移与联接工具", "DirPorter — Directory Migrator & Linker"),
        ["nav.quick"] = ("一键迁移建链", "One-click migrate & link"),
        ["nav.scan"] = ("扫描", "Scan"),
        ["nav.migrate"] = ("迁移", "Migrate"),
        ["nav.link"] = ("链接", "Links"),
        ["nav.history"] = ("记录", "History"),
        ["nav.linksOverview"] = ("链接总览", "Link overview"),
        ["lov.title"] = ("链接总览", "Link overview"),
        ["lov.roots"] = ("扫描范围", "Scan scope"),
        ["lov.hint"] = ("在下列范围内查找目录联接、目录符号链接与文件符号链接；勾选「包含硬链接」会逐个文件检查（较慢）。遇到链接不会递归进去，避免重复与环。",
                        "Finds junctions, directory symlinks and file symlinks under these roots. \"Include hard links\" checks every file (slower). Links are not recursed into."),
        ["lov.doneWithDangling"] = ("共 {0} 个链接，其中 {1} 个目标已丢失（悬空）—— 对应程序会读不到数据，请重新建链或还原。",
                                     "{0} links, {1} with missing targets (dangling) — the owning app can't read its data."),
        ["lov.includeHardLinks"] = ("包含硬链接（较慢）", "Include hard links (slow)"),
        ["lov.includeHardLinksHint"] = ("硬链接需要逐文件读取链接数，范围大时耗时明显。", "Hard links require per-file link-count checks."),
        ["lov.scan"] = ("扫描", "Scan"),
        ["lov.addRoot"] = ("添加范围", "Add scope"),
        ["lov.results"] = ("扫描结果", "Results"),
        ["lov.linkPath"] = ("链接所在路径", "Link path"),
        ["lov.kind"] = ("链接格式", "Link kind"),
        ["lov.target"] = ("目标（源目录）", "Target (source)"),
        ["lov.state"] = ("状态", "State"),
        ["lov.restore"] = ("还原", "Restore"),
        ["lov.restoreHint"] = ("把目标里的数据搬回链接所在位置，并移除链接本体。", "Copy data back from the target and remove the link."),
        ["lov.restoreConfirm"] = ("确定还原该链接？\n链接：{0}\n目标：{1}\n\n数据会复制回链接位置，随后删除链接本体。",
                                   "Restore this link?\nLink: {0}\nTarget: {1}\n\nData is copied back, then the link is removed."),
        ["lov.restoreUnsupported"] = ("仅目录联接 / 目录符号链接支持还原。", "Only junctions and directory symlinks can be restored."),
        ["lov.restoreNote"] = ("还原 = 复制目标内容回链接位置 + 删除链接本体（目标目录本身保留，不删除）。硬链接与文件符号链接暂不支持还原。",
                              "Restore = copy target content back + remove the link (target folder is kept). Hard links and file symlinks are not restorable yet."),
        ["lov.empty"] = ("尚未扫描，或扫描范围内没有链接。", "Nothing scanned yet, or no links found."),
        ["lov.idle"] = ("尚未扫描。", "Not scanned yet."),
        ["lov.scanning"] = ("正在扫描…", "Scanning…"),
        ["lov.done"] = ("共找到 {0} 个链接。", "Found {0} link(s)."),
        ["lov.failed"] = ("扫描失败：", "Scan failed: "),
        ["nav.settings"] = ("设置", "Settings"),

        // ---------------- 通用 ----------------
        ["common.browse"] = ("浏览…", "Browse…"),
        ["common.browseFile"] = ("选文件…", "Choose file…"),
        ["common.add"] = ("添加", "Add"),
        ["common.remove"] = ("移除", "Remove"),
        ["common.refresh"] = ("刷新", "Refresh"),
        ["common.cancel"] = ("取消", "Cancel"),
        ["common.close"] = ("关闭", "Close"),
        ["common.open"] = ("打开", "Open"),
        ["common.copy"] = ("复制", "Copy"),
        ["common.export"] = ("导出", "Export"),
        ["common.path"] = ("路径", "Path"),
        ["common.size"] = ("大小", "Size"),
        ["common.files"] = ("文件数", "Files"),
        ["common.status"] = ("状态", "Status"),
        ["common.type"] = ("类型", "Type"),
        ["common.actions"] = ("操作", "Actions"),
        ["common.search"] = ("搜索", "Search"),
        ["common.none"] = ("（无）", "(none)"),
        ["common.ready"] = ("就绪", "Ready"),
        ["common.running"] = ("执行中…", "Running…"),
        ["common.privileges"] = ("权限能力", "Privileges"),
        ["common.supported"] = ("可用", "Available"),
        ["common.unsupported"] = ("不可用", "Unavailable"),
        ["common.copyPath"] = ("复制路径", "Copy path"),
        ["common.openSource"] = ("打开源位置", "Open source"),
        ["common.openTarget"] = ("打开目标位置", "Open target"),
        ["common.dash"] = ("—", "—"),
        ["common.yes"] = ("是", "Yes"),
        ["common.no"] = ("否", "No"),
        ["common.copied"] = ("已复制路径：{0}", "Path copied: {0}"),
        ["common.clickToCopy"] = ("点击复制路径", "Click to copy the path"),
        ["common.openFolder"] = ("打开所在文件夹", "Open containing folder"),
        ["common.doubleClickHint"] = ("双击行可打开该位置", "Double-click a row to open that location"),

        // ---------------- 权限 / 环境 ----------------
        ["priv.elevated"] = ("管理员权限", "Elevated (admin)"),
        ["priv.devMode"] = ("开发者模式", "Developer mode"),
        ["priv.symlink"] = ("创建符号链接", "Create symbolic links"),
        ["priv.seSecurity"] = ("复制审计 (SACL)", "Copy auditing (SACL)"),
        ["priv.seRestore"] = ("复制所有者", "Copy owner/group"),
        ["priv.limited"] = ("当前以受限权限运行：符号链接不可用（已置灰）；目录联接与硬链接仍可正常使用。",
                            "Running with limited privileges: symbolic links are disabled (greyed out); junctions and hard links still work."),
        ["priv.full"] = ("当前具备完整权限，四种链接类型均可用。",
                         "Full privileges available: all four link types can be created."),
        ["priv.dataRoot"] = ("数据目录", "Data root"),
        ["priv.fallback"] = ("已降级", "fallback"),
        ["priv.fallbackReason"] = ("降级原因", "Reason"),

        // ---------------- 扫描页 ----------------
        ["scan.title"] = ("扫描磁盘占用", "Scan disk usage"),
        ["scan.roots"] = ("扫描根目录", "Scan roots"),
        ["scan.presets"] = ("预设", "Presets"),
        ["scan.clearRoots"] = ("清空", "Clear all"),
        ["scan.selectAll"] = ("全选", "Select all"),
        ["scan.clearSelection"] = ("清除选择", "Clear selection"),
        ["scan.start"] = ("开始扫描", "Start scan"),
        ["scan.startHint"] = ("先添加扫描根目录再开始；扫描只统计占用，不改动任何文件。",
                              "Add a scan root first. Scanning only measures usage; it never changes files."),
        ["scan.scanning"] = ("扫描中…", "Scanning…"),
        ["scan.results"] = ("扫描结果（按体积降序）", "Results (largest first)"),
        ["scan.minSize"] = ("最小体积", "Min size"),
        ["scan.searchHint"] = ("按名称过滤", "Filter by name"),
        ["scan.enqueue"] = ("加入迁移队列", "Enqueue for migration"),
        ["scan.summary"] = ("摘要", "Summary"),
        ["scan.special"] = ("特殊对象", "Special objects"),
        ["scan.elapsed"] = ("耗时", "Elapsed"),
        ["scan.risk"] = ("风险", "Risk"),
        ["scan.migrated"] = ("已迁移", "Migrated"),
        ["scan.clean"] = ("无风险", "clean"),
        ["scan.truncated"] = ("结果被截断（不完整）", "Truncated (incomplete)"),
        ["scan.preset.localAppData"] = ("%LOCALAPPDATA%", "%LOCALAPPDATA%"),
        ["scan.preset.roamingAppData"] = ("%APPDATA%", "%APPDATA%"),
        ["scan.preset.programFiles"] = ("Program Files", "Program Files"),
        ["scan.preset.programFilesX86"] = ("Program Files (x86)", "Program Files (x86)"),
        ["scan.preset.programData"] = ("ProgramData", "ProgramData"),
        ["scan.hintNoRoots"] = ("请先添加至少一个扫描根目录。", "Add at least one scan root first."),
        ["scan.hintEmpty"] = ("没有符合条件的条目。", "No matching entries."),
        ["scan.emptyState"] = ("尚无扫描结果：添加扫描根目录后点击「开始扫描」，结果会显示在这里。",
                               "No results yet: add a scan root and click Start scan. Results will show up here."),
        ["scan.failed"] = ("扫描失败", "Scan failed"),
        ["scan.done"] = ("扫描完成：{0} 个文件 / {1} 个子目录 / {2}，耗时 {3:0.00}s。",
                        "Scan done: {0} files / {1} dirs / {2}, {3:0.00}s."),
        ["scan.oneClickTitle"] = ("一键迁移并建链", "One-click migrate & link"),
        ["scan.oneClickHint"] = ("勾选条目 → 选目标根目录 → 一次点击完成「复制 → 校验 → 建链」（默认目录联接，可在右侧改选）。",
                                 "Tick entries → pick a target root → one click does copy → verify → link (junction by default, switchable)."),
        ["scan.oneClick"] = ("一键迁移并建链", "Migrate & link now"),
        ["scan.addToQueue"] = ("加入迁移队列（勾选）", "Add checked to migration queue"),
        ["scan.addToQueueHint"] = ("把勾选的行加入迁移队列（在「迁移」页执行）。", "Add checked rows to the migration queue (run on the Migrate page)."),
        ["scan.enqueueRow"] = ("加入队列", "Enqueue"),
        ["scan.enqueueRowHint"] = ("把这一行加入迁移队列（在「迁移」页执行）。", "Add this row to the migration queue (run it on the Migrate page)."),

        // ---------------- 一键迁移页（R12 独立主功能） ----------------
        ["quick.title"] = ("一键迁移建链", "One-click migrate & link"),
        ["quick.sources"] = ("源目录（可多个，一次全部迁移并建链）", "Source folders (multiple allowed; all handled in one go)"),
        ["quick.addSource"] = ("添加目录…", "Add folder…"),
        ["quick.add"] = ("添加", "Add"),
        ["quick.sourceHint"] = ("输入目录路径后点「添加」，或直接用「添加目录…」选择。",
                                "Type a folder path and click Add, or use Add folder… to browse."),
        ["quick.sourceMissing"] = ("目录不存在：请检查路径是否正确。", "Folder does not exist; check the path."),
        ["quick.needSource"] = ("请先添加至少一个源目录。", "Add at least one source folder first."),
        ["quick.resultIdle"] = ("选择源目录与目标根，一步完成「复制 → 校验 → 建链」。",
                                "Pick source folders and a target root; one click does copy → verify → link."),
        ["quick.runHint"] = ("立即执行：复制 → 校验 → 建立链接（用当前选中的建链类型）。",
                             "Runs copy → verify → link immediately with the selected link type."),
        ["scan.noSelection"] = ("请先勾选要迁移的条目（行首的复选框）。", "Tick the entries you want to migrate first."),
        ["scan.needTargetRoot"] = ("请先选择目标根目录。", "Choose a target root first."),
        ["scan.linking"] = ("正在迁移并建链：{0} 项…", "Migrating and linking {0} items…"),
        ["scan.doneAndLinked"] = ("完成：{0} 项已迁移并建立联接。", "Done: {0} migrated and linked."),
        ["scan.partial"] = ("部分失败：成功 {0} / 失败 {1}，详情见「迁移」页。",
                            "Partly failed: {0} ok / {1} failed — see the Migrate page."),

        // ---------------- 迁移页 ----------------
        ["mig.title"] = ("迁移", "Migrate"),
        ["mig.queue"] = ("迁移队列", "Migration queue"),
        ["mig.targetRoot"] = ("目标根目录", "Target root"),
        ["mig.targetHint"] = ("会在目标根下自动创建同名子目录", "A same-named subfolder is created under the target root"),
        ["mig.addManual"] = ("手动添加（源 → 目标）", "Add manually (source → target)"),
        ["mig.source"] = ("源目录", "Source"),
        ["mig.target"] = ("目标目录", "Target"),
        ["mig.dryRun"] = ("干跑预览（不写入磁盘）", "Dry-run preview (no disk writes)"),
        ["mig.run"] = ("开始迁移", "Start migration"),
        ["mig.removeRow"] = ("移除", "Remove"),
        ["mig.removeRowHint"] = ("从迁移队列中移除这一条（不迁移它了）。", "Remove this item from the queue."),
        ["set.updateTitle"] = ("一键更新", "One-click update"),
        ["set.updateNow"] = ("检查并更新", "Check and update"),
        ["set.updateNowHint"] = ("一次点击完成：检查新版本 → 下载 → 校验 SHA256 → 自动替换并重启。无需先检查再安装。",
                                  "One click does it all: check → download → verify SHA256 → replace and restart."),
        ["set.updateAdvanced"] = ("高级：自定义更新源", "Advanced: custom update source"),
        ["set.updateSource"] = ("更新源：{0}", "Update source: {0}"),
        ["set.updateFoundShort"] = ("发现新版本 {0}，正在下载…", "New version {0} found, downloading…"),
        ["set.updateVerifying"] = ("正在校验并准备替换…", "Verifying and preparing to replace…"),
        ["set.updateHint"] = ("填入发布清单地址（一个静态 JSON：version / url / sha256 / notes）。检查到新版本后可在此下载并自动替换重启。",
                              "Point to a release manifest (static JSON with version / url / sha256 / notes). When a newer version is found you can download and restart here."),
        ["set.updateFeed"] = ("更新清单地址", "Update feed URL"),
        ["set.updateFeedHint"] = ("例如 https://example.com/dirporter/latest.json；也可用环境变量 DIRPORTER_UPDATE_URL 指定。",
                                  "e.g. https://example.com/dirporter/latest.json; DIRPORTER_UPDATE_URL is also supported."),
        ["set.updateCheck"] = ("检查更新", "Check now"),
        ["set.updateApply"] = ("下载并安装", "Download & install"),
        ["set.updateApplyHint"] = ("下载到临时目录并校验 SHA256，然后退出本程序、替换 exe 并重启。",
                                   "Downloads to temp, verifies SHA256, then exits, replaces the exe and restarts."),
        ["set.updateIdle"] = ("尚未检查。", "Not checked yet."),
        ["set.updateChecking"] = ("正在检查…", "Checking…"),
        ["set.updateUpToDate"] = ("已是最新版本（{0}）。", "You are up to date ({0})."),
        ["set.updateFound"] = ("发现新版本 {0}（当前 {1}）。{2}", "New version {0} available (current {1}).{2}"),
        ["set.updateNoFeed"] = ("尚未配置更新清单地址。", "No update feed configured yet."),
        ["set.updateFailed"] = ("检查更新失败：", "Update check failed: "),
        ["set.updateDownloading"] = ("正在下载新版本…", "Downloading…"),
        ["set.updateProgress"] = ("正在下载新版本… {0:0}%", "Downloading… {0:0}%"),
        ["set.updateRestarting"] = ("更新已就绪，程序将退出并自动重启。", "Update ready; the app will exit and restart automatically."),
        ["set.updateApplyFailed"] = ("无法自动替换（可能需要管理员权限或安装目录不可写），请手动替换 exe。",
                                     "Automatic replacement failed (admin rights or read-only install folder). Please replace the exe manually."),
        ["set.parkingTitle"] = ("迁移备份区（.migbak）", "Migration parking folders (.migbak)"),
        ["set.parkingHint"] = ("迁移时源目录会被改名到「源目录父级下的 .migbak」作为回滚备份（默认按保留天数自动清理，.ledger 台账长期保留）。删除后仍可从目标目录使用数据，但无法再靠它回滚。",
                              "During migration the source folder is renamed into a .migbak folder next to it as a rollback backup (auto-cleaned by retention days; .ledger is kept). Deleting it keeps your data at the target, but removes the rollback copy."),
        ["set.parkingEmpty"] = ("未发现停车区（当前没有需要清理的迁移备份）。", "No parking folders found."),
        ["set.parkingSummary"] = ("共 {0} 处 · 合计 {1}", "{0} folder(s) · {1} total"),
        ["set.parkingPurge"] = ("清理到期备份", "Purge expired"),
        ["set.parkingPurgeHint"] = ("按「保留天数」删除已到期的停车副本（.ledger 台账始终保留）。",
                                     "Deletes parking copies older than the retention days (.ledger is always kept)."),
        ["set.parkingRefresh"] = ("刷新", "Refresh"),
        ["set.parkingOpen"] = ("打开", "Open"),
        ["set.parkingDelete"] = ("删除", "Delete"),
        ["set.parkingDeleteConfirm"] = ("确定删除该停车区的全部备份内容？\n{0}\n\n（.ledger 台账会保留；此操作不可撤销）",
                                        "Delete all backup content in this parking folder?\n{0}\n\n(.ledger is kept; this cannot be undone)"),
        ["set.parkingDeleteDone"] = ("已删除 {0} 个备份目录。", "Deleted {0} backup folder(s)."),
        ["set.parkingDeletePartial"] = ("已删除 {0} 个，{1} 个失败（可能被占用）。", "Deleted {0}, failed {1} (may be in use)."),
        ["mig.pause"] = ("暂停", "Pause"),
        ["mig.resume"] = ("继续", "Resume"),
        ["mig.cancel"] = ("取消（完整回滚）", "Cancel (full rollback)"),
        ["mig.threads"] = ("并发线程数", "Threads"),
        ["mig.keepBackup"] = ("保留备份（可回滚）", "Keep backup (rollback)"),
        ["mig.keepBackupHint"] = ("默认关闭：迁移完成后立即删除源目录的临时副本（剪切语义，不留第二份数据）。勾选后副本保留若干天，便于回滚，但会额外占用一份空间。",
                                  "Off by default: the temporary copy of the source is deleted right after a successful migration (cut semantics). Enable it to keep the copy for a few days for rollback, at the cost of extra disk space."),
        ["mig.retention"] = ("备份保留天数", "Backup retention (days)"),
        ["mig.killLocking"] = ("结束占用进程（慎用）", "Kill locking processes (risky)"),
        ["mig.killLockingHint"] = ("勾选后，源目录被程序占用时会尝试结束占用进程；关键系统进程 / 资源管理器一律拒绝。",
                                   "When enabled, DirPorter tries to end processes locking the source folder; critical system processes and Explorer are always refused."),
        ["mig.continueOnFailure"] = ("失败时继续后续任务", "Continue on failure"),
        ["mig.restorePoint"] = ("迁移前创建系统还原点", "Create restore point first"),
        ["mig.preview"] = ("预览结果", "Preview"),
        ["mig.blocked"] = ("存在硬拒绝项，无法继续。", "Blocked: hard rejections present."),
        ["mig.needsAck"] = ("存在需要确认的风险项，请勾选确认后再迁移。",
                            "Warnings require acknowledgement before migrating."),
        ["mig.acknowledge"] = ("我已理解上述风险并确认继续", "I understand the risks and want to continue"),
        ["mig.skippedCount"] = ("跳过 {0} 项", "{0} skipped"),
        ["mig.effective"] = ("实际迁移 {0} 项", "{0} effective entries"),
        ["mig.progress"] = ("总进度", "Overall progress"),
        ["mig.speed"] = ("速度", "Speed"),
        ["mig.eta"] = ("剩余", "ETA"),
        ["mig.hintNoQueue"] = ("队列为空：先在「扫描」页选中条目加入，或在下方手动添加。",
                               "Queue is empty: add entries from the Scan page or manually below."),
        ["mig.queueHint"] = ("勾选 = 要迁移的条目（默认全选）；双击行打开源路径。",
                             "Checked = items to migrate (all checked by default); double-click a row to open its source."),
        ["mig.noSelection"] = ("请先勾选要迁移的条目（队列默认全选）。",
                               "Tick at least one queue item to migrate (all are ticked by default)."),
        ["mig.removeSelectedHint"] = ("把勾选的条目从队列移除（不会执行迁移）。",
                                      "Remove ticked items from the queue (no migration is performed)."),
        ["mig.done"] = ("批次完成：成功 {0} / 失败 {1}。", "Batch finished: {0} ok / {1} failed."),
        ["mig.alreadyLink"] = ("该目录已是链接（指向 {0}），请用「记录」页的还原功能，而不是重复迁移。",
                               "Already a link (→ {0}); use Restore instead of migrating again."),
        ["mig.state.waiting"] = ("等待", "Waiting"),
        ["mig.state.copying"] = ("复制中", "Copying"),
        ["mig.state.verifying"] = ("校验中", "Verifying"),
        ["mig.state.linking"] = ("建链中", "Linking"),
        ["mig.state.done"] = ("成功", "Done"),
        ["mig.state.warning"] = ("完成（有警告）", "Done (warnings)"),
        ["mig.state.failed"] = ("失败", "Failed"),
        ["mig.state.rolledBack"] = ("已回滚", "Rolled back"),
        ["mig.state.skipped"] = ("已跳过", "Skipped"),

        // ---------------- 链接页 ----------------
        ["link.title"] = ("链接管理", "Link manager"),
        ["link.linkPath"] = ("链接路径（要创建的位置）", "Link path (where to create)"),
        ["link.targetPath"] = ("目标路径（真实数据位置）", "Target path (real data)"),
        ["link.type"] = ("链接类型", "Link type"),
        ["link.type.auto"] = ("自动推荐", "Auto (recommended)"),
        ["link.type.junction"] = ("目录联接 (Junction)", "Directory junction"),
        ["link.type.symlinkDir"] = ("目录符号链接", "Directory symlink"),
        ["link.type.symlinkFile"] = ("文件符号链接", "File symlink"),
        ["link.type.hardlink"] = ("硬链接", "Hard link"),
        ["link.relative"] = ("使用相对路径（仅符号链接）", "Relative target (symlinks only)"),
        ["link.invalidPath"] = ("路径无效：去掉引号/非法字符后重试。", "Invalid path: remove quotes or illegal characters and retry."),
        ["link.recommend"] = ("推荐：", "Recommendation: "),
        ["link.create"] = ("创建链接", "Create link"),
        ["link.dryRunCreate"] = ("干跑校验", "Dry-run check"),
        ["link.capability"] = ("能力矩阵", "Capability matrix"),
        ["link.reason"] = ("说明", "Reason"),
        ["link.existing"] = ("已存在的链接", "Existing links"),
        ["link.inspectPath"] = ("检视路径", "Inspect path"),
        ["link.inspect"] = ("检视", "Inspect"),
        ["link.unlink"] = ("删除链接", "Unlink"),
        ["link.unlinkConfirm"] = ("将删除链接本体（不会递归删除目标数据）。继续？",
                                 "This deletes the link itself (never recurses into the target). Continue?"),
        ["link.unlinkSafe"] = ("完成：目标数据未被触碰，链接已移除。", "Done: target data untouched, link removed."),
        ["link.hint"] = ("删除链接只删除链接本体；目标目录内的数据不受影响（有回归测试守护）。",
                         "Unlink only removes the link; target data is untouched (guarded by regression tests)."),
        ["link.emptyState"] = ("暂无链接：迁移或手动创建的链接会显示在这里。",
                               "No links yet: migrated or manually created links will show up here."),
        ["link.browseFileHint"] = ("选择已存在的文件作为目标（文件符号链接 / 硬链接）。",
                                   "Pick an existing file as the target (file symlink / hard link)."),
        ["link.created"] = ("链接创建成功：{0}", "Link created: {0}"),
        ["link.createFailed"] = ("链接创建失败：{0}", "Link creation failed: {0}"),
        ["link.usableKinds"] = ("当前输入下可用：{0}", "Available for your input: {0}"),
        ["link.useAutoHint"] = ("不想挑就把类型切回「自动推荐」，让程序替你选。", "Not sure? Switch the type back to 'Auto' and let the app decide."),
        ["link.needPrivilege"] = ("该操作需要管理员权限（或开启开发者模式）。", "This requires admin rights (or developer mode)."),

        // ---------------- 记录页 ----------------
        ["hist.title"] = ("迁移记录", "Migration history"),
        ["hist.records"] = ("记录", "Records"),
        ["hist.restore"] = ("还原", "Restore"),
        ["hist.health"] = ("检查链接健康度", "Check link health"),
        ["hist.exportJson"] = ("导出 JSON", "Export JSON"),
        ["hist.exportCsv"] = ("导出 CSV", "Export CSV"),
        ["hist.id"] = ("编号", "Id"),
        ["hist.batch"] = ("批次", "Batch"),
        ["hist.started"] = ("开始", "Started"),
        ["hist.finished"] = ("完成", "Finished"),
        ["hist.source"] = ("源", "Source"),
        ["hist.target"] = ("目标", "Target"),
        ["hist.backup"] = ("备份", "Backup"),
        ["hist.linkType"] = ("链接类型", "Link type"),
        ["hist.checksum"] = ("校验", "Checksum"),
        ["hist.skipped"] = ("跳过", "Skipped"),
        ["hist.warnings"] = ("警告", "Warnings"),
        ["hist.health.healthy"] = ("链接健康", "healthy"),
        ["hist.health.dangling"] = ("链接悬空（目标已丢失）", "dangling (target missing)"),
        ["hist.health.notLink"] = ("未建链接", "Not linked"),
        ["hist.health.missing"] = ("路径不存在", "path missing"),
        ["hist.restoreConfirm"] = ("还原会把目标数据搬回源路径并删除链接。继续？",
                                   "Restore moves the target data back to the source path and removes the link. Continue?"),
        ["hist.restoreDone"] = ("还原完成：{0}", "Restore finished: {0}"),
        ["hist.exported"] = ("已导出到 {0}", "Exported to {0}"),
        ["hist.hintEmpty"] = ("还没有迁移记录。", "No migration records yet."),
        ["hist.recordFile"] = ("记录文件已在数据目录（migrations.json）。", "Records file: migrations.json in the data root."),
        ["hist.relink"] = ("重建链接", "Re-link"),
        ["hist.relinkConfirm"] = ("将在源路径重建指向目标位置的链接。继续？",
                                  "This creates a link at the source path pointing to the target. Continue?"),
        ["hist.relinkDone"] = ("已重建链接：{0}", "Link re-created: {0}"),
        ["hist.relinkFailed"] = ("重建链接失败：{0}", "Re-link failed: {0}"),
        ["hist.openAll"] = ("打开目标位置", "Open target"),
        ["hist.locateOnDisk"] = ("在磁盘上定位", "Show on disk"),

        // ---------------- 设置页 ----------------
        ["set.title"] = ("设置", "Settings"),
        ["set.general"] = ("通用", "General"),
        ["set.language"] = ("语言", "Language"),
        ["set.lang.zh"] = ("简体中文", "Chinese (Simplified)"),
        ["set.lang.en"] = ("English", "English"),
        ["set.theme"] = ("主题", "Theme"),
        ["set.theme.light"] = ("浅色", "Light"),
        ["set.theme.dark"] = ("深色", "Dark"),
        ["set.threads"] = ("默认并发线程数", "Default threads"),
        ["set.backupRetention"] = ("备份默认保留天数", "Backup retention (days)"),
        ["set.defaultLinkKind"] = ("默认建链类型", "Default link type"),
        ["set.defaultLinkKindHint"] = ("迁移与「一键迁移并建链」默认使用该类型；默认目录联接（普通权限即可）。"
                                       + "每次操作前仍可在扫描页 / 迁移页临时改选。",
                                       "Default type for migrations and one-click migrate & link (junction needs no admin rights). "
                                       + "You can still override it per run on the Scan / Migrate page."),
        ["set.save"] = ("保存设置", "Save settings"),
        ["set.saved"] = ("设置已保存。", "Settings saved."),
        ["set.about"] = ("关于", "About"),
        ["set.version"] = ("版本", "Version"),
        ["set.env"] = ("运行环境", "Environment"),
        ["set.openDataRoot"] = ("打开数据目录", "Open data folder"),
        ["set.openCrashFolder"] = ("打开崩溃日志目录", "Open crash-log folder"),
        ["set.sweepBackups"] = ("清理到期备份（先预览）", "Sweep expired backups (preview first)"),
        ["set.riskyTitle"] = ("风险提示", "Risk notice"),
        ["set.riskyBody"] = ("本工具会移动真实数据并创建链接。请务必先干跑预览确认操作序列，"
                            + "并在确认新位置可正常使用之前保留备份。",
                             "This tool moves real data and creates links. Always review the dry-run preview "
                            + "and keep the backup until the new location is verified working."),

        // ---------------- 迁移页/设置页补充 ----------------
        ["mig.clearQueue"] = ("清空队列", "Clear queue"),
        ["mig.clearQueueHint"] = ("移除队列里的全部条目（不会删除任何文件）",
                                  "Removes every queued entry (deletes no files)"),
        ["mig.clearQueueConfirm"] = ("将清空迁移队列（磁盘上的文件不受影响）。继续？",
                                    "This clears the migration queue (no files on disk are touched). Continue?"),
        ["mig.sourceHint"] = ("要迁移走的目录（迁移后原路径会变成链接）",
                              "Directory to move away (its original path becomes a link)"),
        ["mig.targetHintShort"] = ("数据要搬到的位置", "Where the data should live"),
        ["mig.addToQueue"] = ("加入队列", "Add to queue"),
        ["mig.sameVolume"] = ("同卷时直接改名（不复制数据）", "Move within a volume by renaming (no copy)"),
        ["mig.sameVolumeHint"] = ("与资源管理器剪切同卷文件夹一致：瞬时、原子，且 ACL/属性/时间戳全部天然保留。\n代价：同卷项不产生备份（「保留备份」对它无效）；需要备份请取消勾选。",
                                  "Same as cutting a folder within one drive in Explorer: instant, atomic, and all ACLs/attributes/timestamps preserved.\nTrade-off: same-volume entries get no backup; uncheck to force the copy path if you need one."),
        ["mig.transferMode"] = ("传输方式", "Transfer mode"),
        ["mig.linkKind"] = ("建链类型", "Link type"),
        ["mig.linkKind.hint.junction"] = ("目录联接（mklink /J）：普通权限即可创建，兼容性最好；不支持相对路径目标。",
                                          "Directory junction (mklink /J): no admin rights needed, best compatibility; relative targets unsupported."),
        ["mig.linkKind.hint.symlinkDir"] = ("目录符号链接（mklink /D）：支持相对路径目标，部分程序对它的识别与联接不同。",
                                            "Directory symlink (mklink /D): supports relative targets; some programs treat it differently from a junction."),
        ["mig.linkKind.fileOnly"] = ("仅支持文件对象，不能用于目录迁移。", "File targets only; not usable for folder migration."),
        ["mig.linkKind.scopeHint"] = ("链接类型全览：目录联接与目录符号链接用于目录；硬链接与文件符号链接仅支持文件，可在「链接」页创建。",
                                      "All link kinds: junctions and directory symlinks work on folders; hard links and file symlinks need file targets — create those on the Link page."),

        // ---------------- 纯迁移（R12.4：迁移页只搬运数据、不建链） ----------------
        ["mig.pureHint"] = ("本操作只搬运数据、不建立链接——完成后原路径将不再存在。需要原路径继续可用，请使用「一键迁移建链」，或迁移后在「链接」页为这些目录建立链接。",
                            "This only moves data (no link): the original path will no longer exist afterwards. To keep it usable, use \"One-click migrate & link\" or create links on the Links page afterwards."),
        ["mig.noLink"] = ("不建链", "No link"),
        ["mig.goLink"] = ("去建链", "Go create links"),
        ["mig.goLinkHint"] = ("跳转到「链接」页并预填最后一次迁移的源/目标路径。", "Jump to the Links page with the last migrated source/target pre-filled."),
        ["mig.linkKindNeedPrivilege"] = ("⚠ 当前权限无法创建符号链接：请以管理员身份重启（启动时会自动请求），或开启系统「开发者模式」。",
                                         "⚠ Current rights cannot create symlinks: restart as administrator (requested automatically at startup) or enable Developer Mode."),

        // ---------------- 权限与提权 ----------------
        ["priv.restartElevated"] = ("以管理员身份重启", "Restart as administrator"),
        ["priv.restartElevatedHint"] = ("重启后可用：目录/文件符号链接、复制审计(SACL)与所有者、访问受保护目录。\n目录联接与硬链接不需要管理员权限，现在就能用。",
                                        "After restarting: directory/file symlinks, copying auditing (SACL) and owner, and access to protected folders.\nJunctions and hard links need no admin rights and already work."),
        ["priv.banner.body"] = ("当前以普通权限运行（启动时已自动请求管理员权限，但未获得）。目录联接与硬链接可正常使用；"
                                + "符号链接、复制所有者/审计、访问受保护目录需要管理员权限。",
                                "Running unelevated (admin rights were requested automatically at startup but not granted). "
                                + "Junctions and hard links work now; symlinks, copying owner/ACL, and protected folders need admin rights."),
        ["priv.needAdminTitle"] = ("需要管理员权限", "Administrator rights required"),
        ["priv.needAdminBody"] = ("该操作需要管理员权限（或开启开发者模式）。\n\n是否现在以管理员身份重启？界面会保留当前设置。",
                                  "This action needs administrator rights (or developer mode).\n\nRestart as administrator now? Your settings will be kept."),
        ["priv.elevationCancelled"] = ("未获得管理员权限（你取消或拒绝了 UAC 提示），操作已跳过。",
                                      "Administrator rights not granted (UAC canceled or declined); the action was skipped."),
        ["priv.elevationFailed"] = ("以管理员身份启动失败：{0}", "Could not start elevated: {0}"),
        ["priv.whyLimitedTitle"] = ("为什么当前用不了这些权限？", "Why are these capabilities unavailable?"),
        ["priv.whyLimitedBody"] = ("① 启动时会自动请求管理员权限——若你当时取消了 UAC，或启动失败，当前就是普通权限；\n② 当前进程令牌不是「提权管理员」令牌，而 SeCreateSymbolicLinkPrivilege（创建符号链接）只存在于提权令牌中；\n③ 开发者模式未开启 —— 它是免提权的替代路径。\n三者任一改变即可解锁；点上面的「以管理员身份重启」最直接。",
                                   "① Admin rights are requested automatically at startup — if you declined UAC or the launch failed, the process runs unelevated;\n② The current process token is not an elevated administrator token, and SeCreateSymbolicLinkPrivilege exists only in elevated tokens;\n③ Developer mode is off, which would otherwise be a no-elevation workaround.\nChanging any one of them unlocks this; the button above is the quickest path."),

        // ---------------- 对话框 ----------------
        ["msg.title"] = ("DirPorter", "DirPorter"),
        ["msg.crash"] = ("发生内部错误，已写入崩溃日志：{0}", "An internal error occurred; log written to: {0}"),
        ["msg.crashNoLog"] = ("发生内部错误，且崩溃日志写入失败。", "An internal error occurred and the crash log could not be written."),
    };
}
