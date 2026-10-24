# DirPorter

> **C 盘爆满，但不想重装软件？** 把占地的大目录搬到别的盘，原路径留一个链接——
> 软件照旧按老路径读写，注册表不用动，也不用重装。

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011%20x64-0078D4.svg)](#系统要求)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Version](https://img.shields.io/badge/version-0.3.3-brightgreen.svg)](../../releases/latest)

---

## 你在哪种情况下需要它

- C 盘只剩几个 G，红条天天报警，但**不想重装系统**、也不想挨个卸载重装软件；
- 某些工具/游戏只认安装时那个路径，**改了路径就要重配环境、丢配置**；
- `AppData` 里某个缓存目录涨到几十 G（Electron 应用、IDE、AI 客户端最典型），删了怕出问题；
- 想手动搬，但**搬完软件找不到数据**——因为路径变了。

DirPorter 解决的就是最后这一步：**搬走数据，但让旧路径依然有效**。

## 它怎么做到

```
迁移前                          迁移后
C:\Users\me\.zcode\cli    →    C:\Users\me\.zcode\cli   （目录联接）
  └─ 72.5 MB 文件                 └─ 指向 →  F:\jun\.zcode\cli
                                                └─ 72.5 MB 文件（真正的位置）
```

软件读 `C:\Users\me\.zcode\cli` 时，Windows 会自动重定向到 F 盘。
**对程序完全透明**：路径没变、配置没变、注册表没动，只是数据换了个地方存。

## 特性

- **四步迁移，任一步失败完整回滚**：复制 → 逐文件校验 → 源目录改名进停车区 → 在原位建链接。
  不会出现"搬了一半、两边都不完整"。
- **字节级校验**：文件数 + 总字节 + 每个文件的 size/mtime（可选内容哈希）。
- **四种链接类型全覆盖**：目录联接、目录符号链接、文件符号链接、硬链接，能力矩阵会告诉你当前环境哪种可用。
- **占用预检**：迁移前检查源目录是否被程序占用，避免"复制完才发现改不了名"。
- **可暂停/继续/取消**：复制过程带实时速度与剩余时间；取消走完整回滚。
- **备份保留策略**：停车目录可设保留天数，到期自动清理（台账永久保留）。
- **链接总览**：列出全盘链接（含格式/目标/是否悬空），可把目标数据还原回链接位置。
- **自动更新**：一次点击完成检查、下载、校验、替换、重启。

## ⚠️ 安全与风险（使用前必读）

这是一个**会改变磁盘结构**的工具。它做了很多防护，但风险必须由你知情后决定。

### 数据风险

| 风险 | 说明与防护 |
|---|---|
| **中途断电/强制结束** | 可能停在"已复制但未改名"等中间态。此时源目录数据完整，用 `dirporter.exe recover` 可识别并给出修复建议。 |
| **停车目录不是备份** | `.migbak` 里的副本只是**回滚兜底**，默认保留 7 天就会清理。**它不是备份方案**，重要数据请另做备份。 |
| **清理后无法回滚** | 停车目录被清理后，`restore` 就失去了数据来源，只能手工把文件从新位置搬回。 |
| **校验档位选择** | 默认 `metadata`（size/mtime）；对数据准确性要求极高时用 `--verify content`（更慢）。 |

### 链接带来的副作用

- **少数程序不跟随链接**：有些安装器/更新器会解析真实路径或拒绝写入重解析点，表现为"更新失败"或"数据写到别处"。
- **备份/同步软件可能重复计算**：云盘客户端可能把链接目标当成新数据再备份一份，占用反而翻倍。
- **宿主软件卸载/重装**：重装时可能覆盖或删除原路径上的链接，导致它指回一个空目录。重装后需要重新建链。
- **目标盘不可用时链接失效**：目标在移动硬盘/网络盘上，拔盘后原路径就变成"悬空链接"，程序会读不到数据。
  （因此工具**只允许目标为本地固定卷，且必须是 NTFS/ReFS**。）

### 系统层面的风险

- **迁移系统关键目录会导致 Windows 更新/驱动安装/服务启动失败**，且往往重启后才暴露。
  工具对这类目录**硬拒绝**（见下节），请不要用绕过参数强行迁移。
- **杀毒/安全软件可能拦截**：创建重解析点、替换自身 exe（自动更新）都可能被杀软误判，需自行放行。
- **无代码签名**：本项目没有购买代码签名证书，Windows SmartScreen 可能提示"未知发布者"。
  请以 Release 附件里 `latest.json` 的 `sha256` 自行核对文件完整性。
- **需要管理员权限的部分**：目录符号链接、复制所有者/审计信息、访问受保护目录。
  目录联接与硬链接**不需要**管理员权限。程序启动时会请求提权，拒绝也能降级运行。

### 隐私

- 程序**不联网上传任何数据**。唯一的网络行为是"检查更新"（访问 GitHub 的 Release 页面），
  且只在你点击更新、或启动时静默检查时发生。
- 迁移记录、日志、崩溃报告都保存在本地 `%ProgramData%\DirPorter\`，可随时删除。

## 系统要求

- Windows 10 1809+ / Windows 11，**x64**
- 目标卷为本地固定磁盘，文件系统 **NTFS 或 ReFS**（FAT32/exFAT 不支持重解析点）
- 目标卷剩余空间 ≥ 源实际占用 × 1.05
- 无需预装 .NET（单文件 exe 自包含运行时）

## 快速开始

### 下载

从 [Releases](../../releases/latest) 下载 `dirporter.exe`（约 65 MB，免安装）。

> 首次启动会把原生库解压到临时目录，慢几秒属正常。

### 三步上手

1. **扫描**：打开程序 → 「扫描」页 → 勾选要清理的盘/目录 → 按体积排序，找出真正的大户；
2. **迁移建链**：勾上目标目录 → 选目标盘 → 点「一键迁移建链」；
3. **验证**：回到原路径打开文件，能正常读写就成功了。

怕出错就先干跑：迁移页的**干跑预览**会列出将要执行的操作序列、风险项与被跳过的对象，**零磁盘写入**。

## 使用

### 图形界面（七个页面）

| 页面 | 作用 |
|---|---|
| **一键迁移建链** | 多选源目录 + 目标根，一次完成"迁移 + 建链" |
| **扫描** | 分析各目录占用、按体积排序、勾选后加入迁移队列 |
| **迁移** | 纯迁移（不建链）；完成后可「去建链」并自动预填 |
| **链接** | mklink 工具箱：四种类型 + 能力矩阵 + 干跑 + 检视 + 解链 |
| **链接总览** | 列出全盘链接（格式 / 目标 / 是否悬空），支持把目标数据还原回链接位置 |
| **记录** | 迁移台账：批次、状态、链接健康度、还原、重建链接、导出 CSV/JSON |
| **设置** | 语言、主题、线程数、默认链接类型、备份保留天数、清理到期备份、一键更新 |

### 命令行（同一个 exe）

```text
dirporter.exe --help      # 全部命令
dirporter.exe selfcheck   # 环境自检
```

| 命令 | 用途 |
|---|---|
| `move --source <目录> --target <目录>` | 迁移：复制 → 校验 → 改名 → 建链，任一步失败完整回滚 |
| `move ... --dry-run` | 只做预检与预览，零磁盘写入 |
| `scan --root <目录> [--min-size 100MB]` | 扫描目录树，列出超阈值条目 |
| `link --source <旧路径> --target <新位置> [--type auto]` | 建立单个链接（`auto` 会说明推荐理由） |
| `linkdir --source <源目录> --target <目标目录> [--incremental]` | 批量文件级链接，保持相对子目录结构 |
| `unlink --path <链接路径>` | 解除链接（只删链接本体，不碰目标数据） |
| `inspect --path <任意路径> [--json]` | 识别链接类型、重解析标签、目标、是否悬空 |
| `verify [--id <id>] [--batch <批次>]` | 校验迁移记录的链接健康度 |
| `list [--json]` | 列出迁移记录 |
| `restore --batch <批次ID>` 或 `restore --id <记录ID>` | 还原到迁移前状态 |
| `recover` | 识别被强杀留下的中间态并给出修复建议 |
| `cleanresidue --target <目录> --yes` | 清理半成品残留（遇链接只删链接本体） |
| `sweepbackups [--days 7] [--yes]` | 按保留策略清理到期的停车目录（台账永不清理） |
| `selfcheck` | 权限、robocopy、数据目录、台账后端、能力矩阵自检 |

常用选项：`--threads 8`、`--verify metadata|content`、`--delete-backup`、`--keep-backup-days 7`、
`--keep-backup-forever`、`--kill-locking`、`--json`。

退出码：`0` 成功 / `1` 部分失败 / `2` 参数错误 / `3` 预检不通过 / `4` 用户取消 / `5` 需管理员 / `6` 内部错误。

## 迁移做了什么（状态机）

```text
PLANNED → PRECHECK       预检（黑名单/白名单/空间/权限/占用），零磁盘写入
        → OCCUPANCY_SCAN 检测占用进程
        → TARGET_PREPARE 建目标父目录、空间复检
        → COPYING        复制（字节级进度，可暂停/继续/取消）
        → VERIFYING      文件数 + 总字节 + 逐文件 size/mtime 比对
        → PARKING        源目录【同卷重命名】进 .migbak\<名字>_<时间戳>
        → LINKING        在原路径创建链接
        → LINK_VERIFY    解析链接目标、枚举子项、试读文件
        → LEDGER_COMMIT  写台账（回滚的唯一权威依据）
        → BACKUP_CLEANUP 按保留策略处理停车目录
        → DONE
```

两个关键设计：

1. **先改名再建链，绝不先删源**。`PARKING` 是同卷重命名（毫秒级、不搬数据），
   所以建链失败只要把名字改回去——**校验不通过时源目录一个字节都没少**。
2. **台账（`.migbak\.ledger`）长期保留、永不自动清理**，与"停车目录到期清理"是两件事。

## 这些目录**绝对不要**迁移

工具会**硬拒绝**（预检直接拦下，零磁盘写入）：

- 任一卷的根目录（如 `C:\`）
- `C:\Windows` 及所有子目录、`WinSxS`
- `System Volume Information`、`$Recycle.Bin`
- `C:\Users`、`C:\Users\<用户>`（用户目录根）、`C:\Users\Public`
- `C:\ProgramData`（根目录本身）
- `AppData\Local\Microsoft\`、`AppData\Local\Packages\`、`AppData\Roaming\Microsoft\`

会**先警告、需显式确认**：`C:\Program Files`、`C:\Program Files (x86)`、`ProgramData` 下非 Microsoft 子目录、
杀毒/驱动/Office 相关目录、被服务或开机启动项引用的目录。

> 这不是"小心点就行"，而是"迁了就是自找麻烦"：系统目录被重定向后，
> Windows 更新、驱动安装、服务启动都可能失败，而且往往重启后才暴露。

## 备份、回滚与卸载

```text
# 还原到迁移前（优先用台账记录，最可靠）
dirporter.exe restore --batch <batchId>

# 只清理到期的停车目录（先演练，再实际执行）
dirporter.exe sweepbackups --days 7
dirporter.exe sweepbackups --days 7 --yes

# 想保留更久 / 永久保留
dirporter.exe move ... --keep-backup-days 30
dirporter.exe move ... --keep-backup-forever
```

图形界面里：**设置 → 迁移备份区** 可以查看每个停车区的大小、打开、删除、以及「清理到期备份」。
每次启动和每批迁移结束后，也会按你设置的保留天数自动清理。

## 自动更新

设置页「一键更新 → 检查并更新」一次点击完成：检查 → 下载 → 校验 SHA256 → 替换 → 重启。

- 更新源已内置（`github:junmoxiao-6/DirPorter`），**用户不需要填任何地址**；
- 优先读 `releases/latest/download/latest.json`（固定地址，不经过 GitHub API、无限流），缺失才回落 API；
- 替换由程序**自身**完成：先写 `dirporter.exe.new` 并校验大小，再原子改名覆盖，
  **失败不会损坏现有程序**；日志在 `%ProgramData%\DirPorter\logs\update.log`。

## 从源码构建

前置：[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（Windows）。

```text
dotnet build DirPorter.sln -c Debug
dotnet test  src\DirPorter.Tests\DirPorter.Tests.csproj
.\publish.ps1                              # 产出 dist\<版本>\dirporter.exe（自包含单文件）
.\tools\make-release.ps1 -Notes "..."      # 产出 dist\<版本>\latest.json（更新清单）
```

界面回归（可选）：`tools\DirPorter.UiProbe`（真实窗口渲染 + 列宽/留白断言）、
`tools\ColumnProbe`（列边界拖拽联动）。

## 项目结构

```text
src/DirPorter.Core/     内核（无 UI 依赖）
  Migration/            四步迁移状态机、回滚、台账
  Precheck/             预检规则（黑/白名单、空间、权限、占用）
  Links/                建链/检链/还原/扫描
  Backup/               停车区保留策略
  FileSystem/           路径规范化、SafeTreeDeleter、重解析点
  Update/               自更新执行器
src/DirPorter.App/      WPF 界面（MVVM，七个页面）
src/DirPorter.Tests/    xUnit（500+ 用例，含 XAML 契约静态扫描）
docs/                   设计文档、事故复盘、发行说明
tools/                  UiProbe / ColumnProbe / 发布与签名脚本
installer/              Inno Setup 安装包脚本（可选）
```

## 常见问题

**迁完软件打不开/找不到数据？**
检查链接是否有效：`dirporter.exe inspect --path <原路径>`。悬空说明目标盘没挂载或被移走了。

**能不能迁到移动硬盘/网络盘？**
不能，工具会拒绝。拔盘后链接就失效，程序会读不到数据。

**占空间的是整个 `AppData`，能整体迁吗？**
不要。`AppData\Local\Microsoft`、`Packages`、`Roaming\Microsoft` 被硬拒绝。
正确做法是挑其中真正的大户（某个应用自己的目录）单独迁。

**迁移时提示"源目录被占用"？**
先完全退出相关程序（含托盘图标）。必要时勾选「结束占用进程」，但请自行确认不会丢数据。

**卸载软件会怎样？**
原路径上是链接，卸载器可能删掉链接本身（目标数据通常还在）。重新装好后需要重新建链。

## 许可证

[MIT](LICENSE) —— 允许商用、修改、再分发，**但必须保留版权声明与许可声明**（即保留原作者信息）。

---

**免责声明**：本工具按"尽力而为"提供，不附带任何明示或暗示担保。
它会对磁盘结构做实质修改（重命名目录、创建重解析点、删除停车目录）。
尽管实现了逐步回滚与校验，在断电、硬件故障、第三方软件并发改写等极端情况下仍可能造成数据损坏。
**请始终对重要数据保留独立备份，风险由使用者自行承担。**
