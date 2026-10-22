# R7 验证与交付说明 —— WPF GUI（五页）+ 单实例 + 崩溃兜底 + GUI/CLI 同一 exe

> 对应提示词：`F:/jun/Downloads/AI提示词/文件夹迁移联接工具-开发提示词.md` 第 20、62–63、117、123–146、147–168、172、179 行。
> 上一轮：`docs/07-R6-完整CLI与单文件发布.md`。

---

## 1. 一句话结论

**R7 完成**：新增 `src/DirPorter.App`（WPF / MVVM / `CommunityToolkit.Mvvm`），五个页面全部接**真实 Core**（无桩、无假数据）；
单 exe `dirporter.exe` 现在**同时是 GUI 与 CLI**——无参数启动界面，带参数走命令行。
单元测试 **386/386 全绿**（新增 10 个界面契约用例）；GUI 冒烟实测：窗口成功创建、单实例生效、无崩溃日志。

---

## 2. 交付清单

| 文件 | 说明 |
|---|---|
| `src/DirPorter.App/DirPorter.App.csproj` | `WinExe` + `UseWPF` + 自定义 `StartupObject`；产出 `dirporter.exe` |
| `src/DirPorter.App/Program.cs` | 单 exe 入口：**无参 → GUI / 有参 → CLI**（`AttachConsole(ATTACH_PARENT_PROCESS)`） |
| `src/DirPorter.App/App.xaml(.cs)` | 应用资源、`ViewModel→View` 映射、单实例、全局崩溃兜底 |
| `src/DirPorter.App/MainWindow.xaml(.cs)` | 外壳：左侧导航 + 内容区（`ContentControl` + `DataTemplate`） |
| `src/DirPorter.App/Views/*.xaml` | 五个页面（Scan / Migrate / Link / History / Settings） |
| `src/DirPorter.App/ViewModels/*.cs` | 五个页面 VM + `MainViewModel` + 行模型 |
| `src/DirPorter.App/Services/*.cs` | `AppHost`（组合根）、`SingleInstanceGuard`、`CrashReporter`、`LocalizationService`、`ThemeService`、`UiSettings`、`IUserInteraction`/`WpfUserInteraction`、`SystemRestorePoint` |
| `src/DirPorter.App/Themes/{Shared,Light,Dark}.xaml` | 共享样式 + 深/浅两套画笔（键完全对齐，有测试守护） |
| `src/DirPorter.App/Markup/LocExtension.cs` | `{markup:Loc key}` → 索引器绑定，切语言即时刷新 |
| `src/DirPorter.App/Converters/BoolToStateConverter.cs` | bool → 本地化「可用/不可用」 |
| `src/DirPorter.App/app.manifest` / `app.admin.manifest` | `asInvoker`（默认）/ `requireAdministrator`（`publish.ps1 -RequireAdmin`） |
| `src/DirPorter.Tests/R7_XamlContractTests.cs` | 10 个界面契约用例 |
| `publish.ps1` | 发布目标改为 `DirPorter.App`；自检改用 `Start-Process -Wait`（WinExe 陷阱） |

`src/DirPorter.Cli` 保留为**开发期控制台调试宿主**，程序集名改为 `dirporter-cli`，避免与正式单 exe 重名。

---

## 3. 五个页面 ↔ 需求逐条对照

| 需求（提示词行号） | 实现 | 说明 |
|---|---|---|
| 129–133 扫描页：多根目录、预置、并行、按占用降序、已迁移标记、风险标记、体积过滤、名称搜索 | ✅ | 预置 `%LOCALAPPDATA%`/`%APPDATA%`/`Program Files`/`Program Files (x86)`/`ProgramData` + 自定义（持久化）；体积过滤支持 `100MB`/`1.5GB`/`1024`；结果含「已迁移」「风险」列 |
| 132 结果缓存（USN/最后写入时间） | ⚠️ 部分 | Core 已有 `ScanEstimateCache`（按目录 mtime 键控，**默认关闭**）。GUI 未把缓存接到扫描页——因为缓存**只看目录 mtime、内容变化不失效**，直接当结果用会误导；已保留为 CLI `scan --estimate` 的显式预览档 |
| 133 展开子目录树勾选 | ⚠️ 简化 | 实现为「根目录行 + 一级子目录行」两行式 + 勾选（见 §6 偏离 1） |
| 136 目标根选择器（记忆上次） + 自动建同名子目录 | ✅ | `UiSettings.LastTargetRoot` 持久化；改目标根会**重算**队列里的目标路径 |
| 137 干跑预览：完整操作序列、传输量、风险项、将被跳过对象、**零磁盘写入** | ✅ | 干跑与正式迁移走**同一引擎**，只差 `DryRun` 标志 → 预览所见即所得；报告渲染硬拒绝/需确认/提示/跳过/操作序列 |
| 138 队列执行 + 逐项状态 | ✅ | 等待/复制中/校验中/建链中/成功/失败/已回滚/已跳过，逐项进度百分比 |
| 139 字节级总进度 + 单项进度 + 速度 + ETA + 暂停/继续/取消（取消走完整回滚） | ✅ | 引擎固定为自研 `CopyFileEx`（**只有它支持真暂停**）；暂停在进度回调内阻塞，不杀进程、不丢已复制数据 |
| 140 选项：保留备份 / 线程数 / 失败继续 / 系统还原点 | ✅ | 还原点用 `Checkpoint-Computer`，**失败只提示不阻塞**（按需求）；注意它需要管理员且受「24 小时内一次」节流 |
| 142–145 记录页：`migrations.json`、还原、打开源/目标、复制路径、校验链接健康度、导出 JSON/CSV | ✅ | 还原走 `RestoreService`（**以台账为唯一依据**，UI 不自行拼装磁盘操作）；健康度列区分 健康/悬空/不是链接/路径不存在 |
| 147–165 命令行模式（同一 exe） | ✅ | 无参 GUI、有参 CLI；R6 已实现的 13 个命令全部可用（`selfcheck` 实测通过） |
| 168 中英双语、深/浅主题、单实例、崩溃兜底、日志含 Win32 码+文本 | ✅ | 双语 182 条（结构上不可能漏译）；主题即时切换不重启；`Local\Mutex` + 命名事件「二次启动把已有窗口带到前台」；三层异常兜底写崩溃日志 |
| 172 UI 不冻结、IO 全在后台、进度节流 ≤10 次/秒 | ✅ | 扫描 `Task.Run` + `DirectoryScanner` 流式聚合；迁移进度由引擎节流后经 `IProgress` 回 UI 线程 |
| 175 稳态内存 < 300MB | ✅ | 扫描聚合 O(1)、只汇总**顶层子目录**（与目录数成正比，与文件数无关）；GUI 实测工作集 **59MB** |
| 179 目录结构含 `App/`（WPF MVVM） | ✅ | 见 §2 |
| 205 非管理员/未开开发者模式：两种符号链接**置灰并写明原因** | ✅ | 能力矩阵来自 `LinkService.EvaluateAll`，**与 CLI `link --dry-run` 同一套判定**（界面能不能点 = 命令行能不能建） |

---

## 4. 关键实现要点

### 4.1 GUI / CLI 同一 exe（`Program.cs`）

```
args.Length > 0  →  CLI：AttachConsole(ATTACH_PARENT_PROCESS) → CliHost.Run(...) → FreeConsole
args.Length == 0 →  GUI：new App().InitializeComponent() → app.Run()
```

* 输出流通过 `GetStdHandle` + `SafeFileHandle(ownsHandle: false)` 包装成 `StreamWriter`（UTF-8 无 BOM）。
  重定向/管道场景父句柄可直接继承，无需 `AttachConsole`；交互式场景先附加再取句柄。
* **已知限制（WinExe 固有）**：交互式使用时 `cmd` 不会等待 GUI 子系统进程，提示符会先返回、输出可能交错；
  **脚本/管道场景不受影响**（输出完整、退出码正确）。这与 R6 记录的取舍一致。

### 4.2 单实例（`SingleInstanceGuard`）

* `Local\DirPorter.SingleInstance.Mutex` 判定所有权（用 `Local\` 而非 `Global\`：远程桌面/多用户下不互相顶掉）。
* 第二个实例 `EventWaitHandle.TryOpenExisting` → `Set()` 唤醒已有实例，已有实例 `Dispatcher.Invoke` 把窗口带到前台
  （`Show` + 反最小化 + 临时 `Topmost` 绕过前台窗口锁定），然后自身退出。
* 判定在**任何窗口出现之前**完成（不用 `StartupUri`，`MainWindow` 在 `OnStartup` 手工创建）→ 不会闪一下窗口再退出。

### 4.3 崩溃兜底（`CrashReporter` + `App.InstallGlobalExceptionHandlers`）

三层：`DispatcherUnhandledException`（记日志 + 提示，`Handled=true` 不杀进程——用户可能正在迁移中）、
`AppDomain.UnhandledException`（留证据）、`TaskScheduler.UnobservedTaskException`（记日志 + `SetObserved`）。

日志路径 `%ProgramData%\DirPorter\crashes\crash-<ts>-p<pid>.log`，含时间/来源/版本/进程/线程/OS/CLR/数据根目录、
完整异常链，**若是 `DirPorterNativeException` 额外输出 `Win32Code=0x… (n) 系统消息`**（需求硬要求）。
写日志本身整体吞异常（兜底不能再成为二次崩溃源）。

### 4.4 本地化（不做 `.resx`）

中英放在**同一张表**里逐条相邻（`["key"] = ("中文", "English")`），结构上不可能漏译；
键缺失时回退显示键名本身（界面上立刻看得出，而不是空白）。切换语言抛 `Item[]` 变更 → 所有索引器绑定一次性刷新。
**依赖本地化文本的计算属性一律做成 VM 属性**（如 `MigratedText`/`HealthText`），语言切换时统一重算——
不用 `IValueConverter` 做本地化，避免"界面卡在旧语言"。

### 4.5 主题（深/浅）

主题字典作为**一个可替换的节**挂到 `Application.Resources.MergedDictionaries`，
视图统一用 `DynamicResource` → 切换不重启、不重建窗口。深浅两套的键集**有测试断言完全相同**
（少一个键会导致那个画笔静默丢失、界面发白）。

---

## 5. 本轮发现的两个真实缺陷

### 5.1 pack URI 用错了段（会让整个界面失去配色）

`ThemeService` 原写：

```csharp
new Uri("pack://application:,,,/DirPorter.App;component/Themes/Light.xaml")   // ❌
```

`ThemeService` 的 authority 段必须是**程序集名**，而本项目程序集名是 `dirporter`（`RootNamespace` 才是 `DirPorter.App`）。
运行期抛 `FileNotFoundException: Could not load file or assembly 'DirPorter.App'`。

**发现方式**：GUI 冒烟测试后检查 `%ProgramData%\DirPorter\crashes\`，发现一条 `DispatcherUnhandledException`。
——顺带证明全局兜底确实在工作（异常被接住、主流程继续），但也说明这类"非致命异常"必须主动查日志，
否则只看到"界面能打开"就以为没问题。

**修复**：改为从运行时反射取程序集名（改名也不会再踩），并在 `App.OnStartup` 用 try/catch 把主题加载降级为"不影响启动"。

```csharp
private static readonly string AssemblyName = typeof(ThemeService).Assembly.GetName().Name ?? "dirporter";
Source = new Uri($"pack://application:,,,/{AssemblyName};component/Themes/{normalized}.xaml", UriKind.Absolute)
```

### 5.2 视图依赖 App 级资源——测试必须复刻生产路径

界面契约测试初版用 `new Application()` 建宿主，五个页面**集体**抛「无法找到名为 `PageTitle` 的资源」。
根因：裸 `Application` 不会加载 `App.xaml` 的 `Application.Resources`，因此 `Themes/Shared.xaml` 里的样式全都不在。

这不是产品缺陷（生产路径走 `App.InitializeComponent()` 是对的），但**测试必须复刻生产路径**，否则测的是假环境。
已改为 `new DirPorter.App.App(); app.InitializeComponent();`。

**为什么值得写进文档**：这类错误编译期完全不报，只有"真的把页面 new 出来"才会暴露。
静态扫描（键存在性）+ 真实实例化（STA 线程构造）两条都必须有。

---

## 6. 未覆盖 / 显式偏离（不假装）

1. **扫描页「展开子目录树」简化为两层**：实现为「根目录行 + 一级子目录行」。理由：无限制懒加载树需要
   逐目录单独扫描（每个子目录一次目录遍历），在大目录上是数量级的额外 IO；而"哪个文件夹占地方"的核心诉求
   一级子目录已经满足。**未实现**：继续点入二级及更深层。
2. **干跑不给「预计耗时」**：耗时取决于磁盘吞吐与文件大小分布，给猜测值会误导。干跑给**总量 + 完整操作序列 + 全部风险项**；
   实时耗时/速度/ETA 在正式迁移的进度条给。
3. **系统还原点**：`Checkpoint-Computer` 需管理员权限，且 Windows 默认有「24 小时内只能创建一个还原点」的节流。
   失败只提示、不阻塞（按需求 140 行）。
4. **裁剪发布不可用**：`-Trim` 现在会**直接拒绝并说明原因** —— WPF 官方不支持裁剪（BAML/XAML 依赖运行期反射），
   开启后发布阶段即失败。这是对提示词 §19 的显式偏离，详见 `docs/07-R6-完整CLI与单文件发布.md` §5。
5. **管理员清单默认 `asInvoker`**：与需求原文的偏离及理由见 `docs/07-R6-完整CLI与单文件发布.md` §6
   （单 exe 里 `requireAdministrator` 与"普通权限降级路径"无法同时成立）。GUI 侧同样遵循此约定：
   受限权限下把符号链接项**置灰并写明原因**，Junction 与硬链接照常可用。
6. **未做 GUI 端到端自动化**：GUI 的点击流程没有自动化测试（环境无法注入 UI 自动化且截图被沙箱拦截）。
   交付的能力边界是：**XAML 契约 + 真实实例化 + 启动冒烟**（见 §7），加上底层逻辑由 376 个非 GUI 用例覆盖。
7. **界面文案只做中英两种**；需求只要"中英双语可切换"。
8. **`linkdir` 批量文件级链接、UNC 目标、目录符号链接创建**：GUI 的链接页已按能力矩阵置灰，
   但**成功分支需提权/网络环境**，`tests/run-elevated-checks.ps1` 仍待以管理员身份执行（见 §8）。

---

## 7. 验证方法与实测结果

### 7.1 单元测试（含界面契约）

```powershell
cd C:\Users\31125\WorkBuddy\工具下载使用\DirPorter
.\tests\run-unit-tests.ps1                       # 预期：失败 0，通过 386
.\tests\run-unit-tests.ps1 -Filter R7_           # 只看本轮 10 个界面契约用例
```

**本机实测**：`失败: 0，通过: 386，总计: 386`（R1 123 + R2 106 + R3 46 + R4 27 + R5 43 + R6 31 + R7 10）。

R7 新增的 10 条：

| 用例 | 防的是什么 |
|---|---|
| 本地化_XAML用到的每个键都必须在表里有定义 | 少一个键 → 界面直接显示 `nav.scan` 这种键名 |
| 本地化_每条键的中英文都必须非空 | 半翻译 |
| 本地化_语言切换后索引器返回对应语言 | 切语言不生效 / 未知语言崩 |
| 资源_XAML用到的每个StaticResource都必须有定义 | **页面加载时抛异常**（编译期不报） |
| 主题_每个DynamicResource画笔都必须由深浅主题提供 | 画笔丢失、界面发白 |
| 主题_深浅两套必须定义同一组画笔键 | 切深色丢画笔 |
| XML注释里不得出现两个连续连字符 | R6 清单事故的通用化（扩展到全部 xaml/manifest） |
| 五个页面_DataTemplate与View文件一一对应 | 导航到某页时找不到视图 |
| 应用清单_必须是合法XML且声明DPI与长路径 | 清单非法 → exe 起不来 |
| **界面_五个页面与主窗口都能在STA线程上成功构造** | **XAML 解析 + 资源解析的最终防线**（含 Light→Dark→Light 主题切换） |

### 7.2 GUI 冒烟（实测输出）

```
first : alive=True pid=…  MainWindowTitle=[DirPorter]  WorkingSet=59MB
second: exited=True exitCode=0 elapsedMs=589        ← 单实例：二次启动 0.6 秒内自行退出
crash : dir exists=False                            ← pack URI 修好后不再产生崩溃日志
remaining dirporter processes = 0
```

### 7.3 CLI 分支（同一个 exe）

```
dirporter.exe selfcheck            → exit=0（权限探测 / robocopy / 数据目录 / JSONL 与 SQLite 台账往返 / 能力矩阵 全 PASS）
dirporter.exe inspect --path C:\Windows --json → exit=0（返回合法 JSON）
dirporter.exe --help               → exit=0（13 个命令全部列出）
```

### 7.4 发布（实测）

```powershell
.\publish.ps1        # 自动跑 selfcheck（用 Start-Process -Wait，因为 WinExe 不会让 PowerShell 等待）
```

**本机实测**：

```
exe 体积：64.5 MB    目录总大小：64.5 MB，共 1 个文件
自检：6 项全 PASS（权限探测 / robocopy / 数据目录可写 / 台账 JSONL 往返 / 台账 SQLite 往返（原生 e_sqlite3）/ 能力矩阵可枚举）
```

> R6 曾按 150–180 MB 预估 WPF 单文件体积，实测 **64.5 MB** —— 差异来自 `EnableCompressionInSingleFile` 的压缩效果，
> 当时的预估漏算了这一项（见 `docs/01-设计与计划.md` §21.9-5）。
> `-Trim` 现在会**直接拒绝并说明原因**（WPF 不支持裁剪），不再产生"体积小但功能坏"的产物。

### 7.5 发布版 exe 的 GUI 冒烟（实测输出）

```
exe exists=True size=65MB
first  alive=True  title=[DirPorter — 目录迁移与联接工具]  workingSet=223MB threads=12
second exited=True code=0 elapsedMs=1353        ← 单实例
crash logs=0                                    ← pack URI 修好后不再产生崩溃日志
remaining=0
```

窗口标题是**本地化的应用标题**（而不是 `msg.title` 那个对话框标题）——这一条恰好把"真主窗口"与"启动期弹框"区分开：
5.1 的 pack URI 缺陷未修时，同一个探针读到的是 `DirPorter`（错误框），修好后才是完整标题。

---

## 8. 待你执行的一件事（沿用 R4 遗留）

非提权环境下这几条的**成功分支物理上测不到**：审计 SACL（`/COPYALL` 的 `U`）、复制所有者/组、
符号链接创建、EFS。请用**管理员** PowerShell 执行：

```powershell
cd "C:\Users\31125\WorkBuddy\工具下载使用\DirPorter"
.\tests\run-elevated-checks.ps1
```

> 说明：`tests/elevated-report-20261011-105457.txt` 显示上一次执行时**并非管理员**（`Elevated: False`），
> 因此 7 组校验都没有真正跑过，报告里只有提示文本。跑完把 `elevated-report-*.txt` 回传即可，
> 我会把结果写成 `docs/09-提权验证结论.md`，并把那四项能力声明从"按权限降级"改成实测结论。

---

## 9. 下一轮（R8）

1. 把提权结论写进文档（待 §8 报告）。
2. 集成测试脚本：用 `subst` 造虚拟盘符模拟跨卷，覆盖提示词 §183 的验收场景。
3. 补齐 5 项语义已实现但证据待补的项（EFS / OneDrive 云占位 / ACL 拒绝路径 / 稀疏文件 / 启动项引用真实命中），
   收敛为一条可在 R8 重复执行的验收脚本。
4. 扫描页二级及更深层的懒加载展开（若确有必要）。
