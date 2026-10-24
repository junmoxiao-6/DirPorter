# DirPorter

把 Windows 上**任意**目录无损迁移到新位置，并在原路径留下链接，让程序继续按旧路径访问。
目录范围不受限（不是只搬 AppData 的"瘦身器"），支持跨盘/跨卷，强调**字节级校验、任一步失败可完整回滚、全程可观测**。

> ⚠️ **这是会改磁盘结构的工具，请先读完本节再动手。**
> 迁移会重命名源目录并创建链接。虽然工具在每一步都实现了回滚，但**错误的目标选择或对系统关键目录的误操作仍可能造成不可逆后果**。
> **首次使用请务必先跑 `--dry-run`**，确认它列出的事项与你的预期一致后再实际执行。

---

## 1. 快速开始

### 单文件 exe（推荐）

```powershell
.\publish.ps1                 # 产出 dist\<版本>\dirporter.exe（自包含，无需安装 .NET）
```

单文件产物 **约 65 MB**，**同一个 exe 既是图形界面也是命令行**：

* **双击** → 打开图形界面（五个页面：扫描 / 迁移 / 链接 / 记录 / 设置）
* **命令行带参数** → 走命令行模式（`dirporter.exe --help` 看全部命令）

无外部运行时依赖（Windows 10 1809+ / Windows 11 x64）。

> 首次运行会把原生库解压到临时目录，启动略慢（数百毫秒级）属正常现象。

### 从源码构建

```powershell
dotnet build DirPorter.sln
.\tests\run-unit-tests.ps1     # 全量单元测试
```

---

## 2. 图形界面

**不需要记任何命令。** 双击 `dirporter.exe` 打开：

| 页面 | 做什么 |
|---|---|
| **扫描** | 加扫描根目录（含 `%LOCALAPPDATA%`、`Program Files` 等预设），并行扫描后按体积降序列出「根目录 + 一级子目录」，标注是否已迁移与风险项；支持体积阈值过滤、名称搜索、全选/清除选择；**一键迁移并建链横条可先选建链类型** |
| **迁移** | 选目标根目录（自动记忆）、**建链类型选择**（默认目录联接）、**干跑预览**（列出操作序列 / 风险项 / 将被跳过的对象，零磁盘写入）、队列执行、逐项状态、字节级总进度 + 实时速度 + ETA、**暂停 / 继续 / 取消**（取消走完整回滚） |
| **链接** | 四种链接类型（含"自动推荐"）的能力矩阵——**当前环境不可用的类型会置灰并写明原因**；推荐理由、干跑校验、检视、删除链接；列出记录里当前仍是链接的条目，可逐个解链 |
| **记录** | 全部迁移记录（序号 / 批次 / 源 / 目标 / 状态 / 链接健康度 / 体积 / 时间），支持**还原**、**重建链接**、打开源位置、打开目标位置、复制路径、校验链接健康度、导出 JSON / CSV |
| **设置** | 中英双语切换、深色/浅色主题、默认线程数、**默认建链类型**、备份保留天数、权限能力自检、数据目录与崩溃日志入口、清理到期备份 |

### 一键迁移并建链（推荐用法）

对齐 WindowsClear（愚公移盘）的直线动线，**全程停在一个页面**：

1. 在「扫描」页添加扫描根目录 → 点「开始扫描」；
2. 勾选要迁移的条目（可全选）；
3. 在「一键迁移并建链」横条里选**目标根目录**与**建链类型**（默认目录联接，可改选目录符号链接）；
4. 点「**一键迁移并建链**」——

复制 → 校验 → 停车 → **建立链接**（默认目录联接 `mklink /J`）→ 写台账 → 刷新记录，一次完成。
状态显示在页面底部，列表的「已迁移」列就地刷新。表格每行还有一个「一键迁移并建链」按钮，可单条执行。

> 若条目命中警告名单等**需确认**项，工具会停下并提示勾选「我理解风险」，
> **不会替你接受风险**。
>
> 想精细控制（干跑、暂停、结束占用进程）时用「仅加入队列（高级）」，会自动跳到「迁移」页。

### 建链类型（目录联接 / 目录符号链接）

迁移的「建链」一步支持目录级两种链接类型，**默认目录联接**（普通权限即可、兼容性最好）：

* 在**扫描页**一键横条或**迁移页**选项区临时改选（仅影响尚未执行的条目）；迁移执行中不可改（引擎已按条目快照）。
* 在**设置页**改「默认建链类型」——同时同步迁移页当前选择，并记入偏好，下次启动沿用。
* 目录符号链接需要管理员权限或开发者模式；未满足时给出**提前提示**（而不是跑到建链那一步才失败）。
* 硬链接与文件符号链接**不支持目录**，因此不会出现在迁移的建链候选里（链接管理页里四种类型全都有）。

### 路径的快捷操作（四张表通用）

* **单击路径文字** → 复制该路径（底部会显示「已复制路径：…」）
* **点路径右侧的「打开」** → 打开所在文件夹
* **双击整行** → 打开该行主路径所在文件夹

界面在**操作进行中不会冻结**（所有 IO 在后台线程，进度事件节流刷新）；
窗口关闭后再次启动会**把已有窗口带到前台**（单实例，不会开出第二个进程）。

如果当前不是管理员且未开启开发者模式，符号链接相关选项会被**置灰并写明原因**，
目录联接（Junction）与硬链接照常可用——不会让你点下去才报错。


---

## 3. 三种典型命令行用法

### 3.1 迁移目录（核心功能）

```powershell
# 先干跑：零磁盘写入，列出全部操作步骤、风险项与被跳过的对象
dirporter.exe move --source "C:\Users\me\Documents\ProjectA" --target "D:\Archive\ProjectA" --dry-run

# 确认后执行
dirporter.exe move --source "C:\Users\me\Documents\ProjectA" --target "D:\Archive\ProjectA" --yes

# 校验链接健康度
dirporter.exe verify
```

执行完后：源路径变成一个**目录联接（Junction）**，`dir /AL` 能看到 `<JUNCTION>`，
资源管理器里有快捷方式箭头，所有程序访问原路径都会被透明重定向到新位置 —— 软件无需重装、无需改配置。

### 3.2 只看不动：扫描与体检

```powershell
# 看哪个子目录最占地方（列出体积 ≥ 1GB 的顶层条目，按体积降序）
dirporter.exe scan --root "C:\Users\me\AppData\Local" --min-size 1GB

# 结构化输出，便于脚本消费
dirporter.exe scan --root "D:\Data" --json | ConvertFrom-Json

# 谁占用了这些文件
dirporter.exe scan --root "D:\Data" --locked
```

### 3.3 只建链、批量建链、解链

```powershell
# 你已经自己把目录搬走了，只想在旧路径补一个链接（跳过复制/校验/备份）
dirporter.exe link --source "C:\Old\Path" --target "D:\New\Path" --yes

# 让一份数据在两棵目录树里同时可见（不占双份空间），并保持子目录结构
dirporter.exe linkdir --source "D:\Shared" --target "C:\Project\vendor" --type hardlink --incremental --yes

# 解除链接（只删链接本体，绝不触碰目标数据）
dirporter.exe unlink --path "C:\Old\Path" --yes
```

---

## 4. 命令行参考

| 命令 | 用途 |
|---|---|
| `move --source <目录> --target <目录>` | 迁移：复制 → 校验 → 改名 → 建链，任一步失败完整回滚 |
| `scan --root <目录> [--min-size 100MB]` | 扫描目录树（低内存并行），可列出超阈值条目 |
| `link --source <旧路径> --target <新位置>` | 建立单个链接（四种类型，`--type auto` 会说明推荐理由） |
| `linkdir --source <源目录> --target <目标目录>` | 批量文件级链接，保持相对子目录结构，`--incremental` 增量 |
| `unlink --path <链接路径>` | 解除链接（不是链接一律拒绝） |
| `inspect --path <任意路径> [--json]` | 识别四种链接类型、重解析标签、目标绝对路径、是否悬空 |
| `verify [--id <id>] [--batch <批次>]` | 校验迁移记录的链接健康度 |
| `list [--json]` | 列出迁移记录 |
| `restore (--batch <批次ID> \| --id <记录ID>) [--entry <条目>] [--clean-target]` | 还原到迁移前状态 |
| `recover` | 识别被强杀留下的中间态并给出修复建议 |
| `cleanresidue --target <目录> [--include-root] --yes` | 清理半成品残留（遇链接只删链接本体） |
| `sweepbackups [--days 7] [--root <目录>] [--all] [--yes]` | 按保留策略清理到期的 `.migbak` 停车目录（台账永不清理） |
| `selfcheck` | 自检：权限、robocopy、数据目录、两种台账后端、能力矩阵 |
| `help` | 全部命令与选项 |

常用选项：

* `move`：`--dry-run`（零写入预览）、`--yes`/`--force`（确认执行）、`--threads 8`、
  `--engine auto|robocopy|native`、`--verify metadata|content`、
  `--keep-backup-days 7` / `--delete-backup` / `--keep-backup-forever`、
  `--kill-locking`（结束占用进程，默认不结束）、`--json`
* `link`：`--type auto|junction|symlink-dir|symlink-file|hardlink`、`--relative`、`--force`、`--dry-run`、`--yes`
* `linkdir`：`--type auto|hardlink|symlink-file`、`--incremental`、`--dry-run`、`--yes`
* `scan`：`--threads n`、`--budget n`、`--special`、`--entries`、`--locked`、`--locked-deep`、`--estimate`

**退出码**：`0` 成功 / `1` 部分失败 / `2` 参数错误 / `3` 前置校验不通过 / `4` 用户取消 / `5` 需要管理员权限 / `6` 内部错误。
所有命令都支持 `--json`，便于脚本集成。

---

## 5. 链接类型：完整覆盖 `mklink` 的四种模式

| 模式 | mklink 等价 | 对象 | 跨卷 | UNC/网络 | 相对路径 | 权限要求 | 额外空间 |
|---|---|---|---|---|---|---|---|
| 目录联接 | `/J` | 目录→目录 | 支持 | 不支持 | 不支持 | 普通用户 | 极少 |
| 目录符号链接 | `/D` | 目录→目录 | 支持 | 支持 | 支持 | 管理员 **或** 开发者模式 | 极少 |
| 文件符号链接 | （无参） | 文件→文件 | 支持 | 支持 | 支持 | 管理员 **或** 开发者模式 | 极少 |
| 硬链接 | `/H` | 文件→文件 | **不支持（同卷）** | 不支持 | 不支持 | 普通用户 | 0 |

`--type auto` 的推荐规则：目录 + 本地卷 → 目录联接；目录 + 需要 UNC 或相对路径 → 目录符号链接；
文件 + 同卷 → 硬链接；文件 + 跨卷 → 文件符号链接。

**当前环境不可用的模式会在 `link --dry-run` 里标出原因**，不会等你点下去才报错：

```
其余三种类型的可用性：
  · 目录符号链接（mklink /D）：不可用（需要管理员权限或开启开发者模式）
  · 文件符号链接（mklink）：不可用（目标不存在）
  · 硬链接（mklink /H）：不可用（目标不存在）
```

---

## 6. ⚠️ 哪些目录**绝对不要**迁移

工具会对下列路径**硬拒绝**（PRECHECK 直接拦下，零磁盘写入）：

* 任一卷的根目录（如 `C:\`）
* `C:\Windows` 及其**所有**子目录、`WinSxS`
* `System Volume Information`、`$Recycle.Bin`
* `C:\Users`、`C:\Users\<用户>`（用户 Profile 根）、`C:\Users\Public`
* `C:\ProgramData`（根目录本身）
* `AppData\Local\Microsoft\`、`AppData\Local\Packages\`、`AppData\Roaming\Microsoft\`

下列路径会**先警告、需要你显式确认**：`C:\Program Files`、`C:\Program Files (x86)`、
`ProgramData` 下的非 Microsoft 子目录、杀毒/驱动/Office 相关目录、被服务或开机启动项引用的目录。

**这些不是"小心点就行"的清单，而是"迁了就是自找麻烦"**：
系统目录被重定向后，Windows 更新、驱动安装、服务启动都可能失败，且往往在你重启之后才暴露。

另外，目标侧同样有硬约束：

* 目标必须是**本地固定卷**（U 盘/移动盘/网络路径一律拒绝 —— 拔盘后链接失效）
* 目标卷文件系统必须是 **NTFS 或 ReFS**（FAT32/exFAT 不支持重解析点）
* 目标路径不得是源的子路径或祖先路径（防环）
* 目标卷剩余空间 ≥ 源**实际占用** × 1.05

---

## 7. 迁移都做了什么（状态机）

```
PLANNED → PRECHECK →（dry-run 出口）
        → OCCUPANCY_SCAN   检测占用进程（Restart Manager）
        → TARGET_PREPARE   建目标父目录、空间复检
        → COPYING          复制（字节级进度，可暂停/继续/取消）
        → VERIFYING        文件数 + 总字节 + 逐文件 size/mtime 比对
        → PARKING          源目录【同卷重命名】为 .migbak\<name>_<时间戳>
        → LINKING          在原路径创建链接
        → LINK_VERIFY      解析链接目标、枚举子项、试读文件
        → LEDGER_COMMIT    写入迁移台账（回滚的唯一权威依据）
        → BACKUP_CLEANUP   按保留策略处理停车目录（默认保留 7 天）
        → DONE
```

两个关键设计：

1. **先改名再建链，绝不先删源**。`PARKING` 是**同卷重命名**（毫秒级、无数据风险），
   所以 `LINKING` 失败时只要把名字改回去即可 —— 校验不通过时源目录一个字节都没少。
2. **台账（`.migbak\.ledger`）长期保留、永不自动清理**。它是回滚的唯一权威来源，
   与"停车目录到期清理"是两件事，后者永远不会删到台账。

进程被强杀后，`dirporter.exe recover` 能识别中间态并给出可执行建议（例如残留 `.migbak` 或残留链接）。

---

## 8. 还原方法

```powershell
# 方式一：按迁移记录还原（推荐）
dirporter.exe list
dirporter.exe restore --id <记录ID> --yes

# 方式二：按批次还原（一个批次可能含多条）
dirporter.exe restore --batch <批次ID> --yes
```

还原流程：删除链接本体 → 把停车目录改回原路径 → 校验 → （可选 `--clean-target`）清理目标侧。

**如果链接已经被手工删掉、只剩真实目录**：那说明它当前不是链接，`restore` 会报"源已不是链接"，
此时不需要做任何事 —— 数据本来就在原位置。

**如果目标数据还在、但源路径是空目录**：用 `link --source <旧路径> --target <新位置>` 重新建链即可。

---

## 9. 备份与保留

迁移完成后，源目录会以"停车目录"形式留在 `<源父目录>\.migbak\<名字>_<时间戳>`，
默认**保留 7 天**作为回滚兜底，之后可由 `sweepbackups` 清理：

```powershell
dirporter.exe sweepbackups --days 7            # 演练：只列出将删除什么
dirporter.exe sweepbackups --days 7 --yes      # 实际执行
```

* 停车目录内部若含嵌套链接，清理时**只删链接本体，不穿透**（与 Unlink 同一条安全红线）。
* 台账 `.migbak\.ledger` **不参与清理**，永久保留。
* 想迁移完立刻释放空间：`move ... --delete-backup`（代价是失去回滚兜底）。
* 想永久保留：`move ... --keep-backup-forever` 或 `--keep-backup-days 3650`。

> **图形界面会自动执行保留策略**：每次启动、以及每批迁移结束后，按「设置 → 保留天数」清理已到期的
> 停车目录（也可以点「设置 → 迁移备份区 → 清理到期备份」立刻执行）。`.ledger` 台账始终保留。

---

## 9.5 自动更新

设置页「一键更新 → 检查并更新」一次点击完成：检查新版本 → 下载 → 校验 SHA256 → 替换 → 自动重启。

更新源优先级（高到低）：

1. 设置页「高级：自定义更新源」填写的地址；
2. 环境变量 `DIRPORTER_UPDATE_URL`；
3. 编译期常量（`src/DirPorter.App/DirPorter.App.csproj` 的 `UpdateFeedUrl`）；
4. exe 同目录下的 `latest.json`（离线/内网分发用）。

支持三种写法：

| 写法 | 说明 |
|---|---|
| `github:用户名/DirPorter` | 读 GitHub 最新 Release（推荐；发版在 Release 里上传 `dirporter.exe` + `latest.json`） |
| `https://github.com/用户名/DirPorter` | 同上 |
| `https://任意地址/latest.json` | 自建静态清单：`{"version","url","sha256","notes"}` |

GitHub 模式下版本取 Release 的 tag（如 `v0.3.0`），下载地址取名为 `dirporter.exe` 的附件，
摘要优先用 GitHub 提供的 `digest`（没有则不做摘要校验）。

发布新版本只需三步：推送代码 → 网页建 Release（tag 用 `v<版本>`）→ 上传 `dist\<版本>\dirporter.exe` **和** `dist\<版本>\latest.json`。

客户端优先读 `https://github.com/<owner>/<repo>/releases/latest/download/latest.json`
（固定重定向地址，不经过 API、无 60 次/小时限流）；该附件缺失时自动回落到 `api.github.com`。

更新由程序自身执行：主进程把副本放到临时目录，由它等主进程退出后**原子替换** exe 并重启，
不依赖任何 cmd/bat；失败不会破坏现有程序，日志在 `%ProgramData%\DirPorter\logs\update.log`。

---

## 10. 管理员权限

**图形界面启动时会自动请求管理员权限**（UAC 弹一次）：

* 同意 → 以管理员身份继续，四种链接、复制所有者/审计、访问受保护目录全部可用。
* 取消（或启动失败）→ **降级为普通权限继续运行**，界面顶部常驻一条权限横幅说明现状，
  随时可点「以管理员身份重启」；目录联接与硬链接在普通权限下照常工作，**不会出现"程序起不来"**。

实现说明：单文件 exe 的清单保持 `asInvoker`，自动提权在 GUI 入口用 `runas` 重启自身完成。
之所以不用 `requireAdministrator` 清单：同一个 exe 还承载 CLI，写死清单会让只读的
`scan` / `inspect` / 干跑以及脚本管道调用全部弹 UAC 甚至挂起，且无法在同一 exe 内切回普通权限。
**CLI 模式不自动提权**，保留 `--elevate` 手动开关（脚本需要提权时显式加）。

需要提权的场景：创建**符号链接**（未开启开发者模式时）、访问/删除受保护目录内容。

* 非管理员运行时工具会**明确告知能力受限**，并把不可用的链接类型在预览里标出原因，不会静默失败。
* 图形界面里同样如此：符号链接选项**置灰并写明原因**，Junction 与硬链接照常可用；
  未提权时顶部有权限横幅，「设置」页有权限能力自检，一眼看清哪些能力当前不可用。

同时提供**开发者模式**这条不需要管理员的路径（设置 → 系统 → 开发者选项）。

---

## 11. 已知限制

* **EFS 加密文件**默认拒绝复制（静默解密属于安全降级）；跨卷行为未实测。
* **云占位文件**（OneDrive 等）迁移会触发从云端下载，可能产生大量流量与等待，扫描时会标注。
* **硬链接的链接计数不可信**：本机实测删除多硬链接文件会被路由到回收站，`NumberOfLinks` 不下降；
  因此增量判定与解链以工具自己的台账为准，不依赖计数。
* **扫描估算缓存**（`scan --estimate`）按目录修改时间复用结果；
  文件内容变化**不会**让缓存失效，字节数可能过期，仅用于预览，不用于任何决策。
* **不能裁剪发布**：WPF 官方不支持裁剪（XAML/BAML 依赖运行期反射），`publish.ps1 -Trim` 会直接拒绝。
  要压体积请用 `publish.ps1 -Mode framework-dependent`（依赖已安装的 .NET 8 Desktop Runtime）。
* **图形界面扫描页只展开到一级子目录**（「根目录行 + 一级子目录行」）：更深层级需要逐目录单独遍历，
  大目录上是数量级的额外 IO。命令行的 `scan` 不受此限。
* **同一 exe 的交互式命令行提示符会提前返回**：本 exe 是 GUI 子系统，`cmd` 不会等待它，
  输出可能与提示符交错；脚本 / 管道场景（重定向）不受影响，输出完整、退出码正确。
* **图形界面扫描页只展开到一级子目录**（「根目录行 + 一级子目录行」）：更深层级需要逐目录单独遍历，
  大目录上是数量级的额外 IO。命令行的 `scan` 不受此限。
* **一键迁移遇到需确认项会停下**，需要手动勾选「我理解风险」后才继续（刻意保留的安全闸门）。
* **记录页「重建链接」一次一条**，且源路径上存在真实目录时硬拒绝，绝不覆盖。

---

## 12. 免责声明

本工具按"尽力而为"提供，**不附带任何明示或暗示的担保**。

* 它会对磁盘结构做实质修改（重命名目录、创建重解析点、删除停车目录）。
* 尽管实现了逐步回滚与字节级校验，**在极端情况下（断电、硬件故障、第三方软件并发改写、用户绕过确认）仍可能造成数据损坏或丢失**。
* **请始终对重要数据保留独立备份**。工具自带的停车目录只是过渡性兜底，不是备份方案。
* 在系统关键位置、生产环境、或你无法承担重装系统代价的机器上使用本工具，**风险由使用者自行承担**。

## 13. 验证命令

```powershell
.\tests\run-unit-tests.ps1               # 全量单元测试
.\tests\drill-interrupt-recovery.ps1     # 中断/恢复演练（强杀各中间态）
.\tests\bench-scan.ps1                   # 扫描性能基准（10 万文件量级）
.\tests\run-elevated-checks.ps1          # 提权能力校验（需管理员执行）
```

单跑某一轮：

```powershell
.\tests\run-unit-tests.ps1 -Filter R7_   # 界面契约（本地化键 / 资源键 / 五页面真实构造）
.\tests\run-unit-tests.ps1 -Filter R6_   # 命令行完整性
```

界面契约这一组值得单独说：它能查出**编译期完全不报错、点开那个页面才炸**的问题
（资源键写错、本地化键漏定义、XAML 注释里的非法双连字符）。

## 14. 项目结构

```
src/DirPorter.Core/   核心：Links（四种 provider）· FileSystem · Migration · Scan · RestartManager · Journal · Ledger · Copy · Backup · Cli
src/DirPorter.App/    WPF 桌面端（MVVM）：Views 五个页面 · ViewModels · Services · Themes 深浅主题 · Markup 本地化
                      → 产出正式单 exe「dirporter.exe」（GUI + CLI 同一 exe）
src/DirPorter.Cli/    命令行调试宿主（AssemblyName=dirporter-cli，供开发期使用）
src/DirPorter.Tests/  单元测试（含界面契约测试）
tests/                可复现的验证脚本（单测 / 演练 / 基准 / 提权校验）
docs/                 设计、各轮交付说明、验收记录
publish.ps1           一键发布单 exe（默认发布 DirPorter.App）
```

## 15. 许可

[MIT](LICENSE) —— 可自由使用、修改、再分发，请保留版权声明。
