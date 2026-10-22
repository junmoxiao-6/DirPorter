# R6 完整 CLI 与单文件发布

> 范围：补齐需求规定的全部命令行形态（`link` / `linkdir` / `unlink` / `verify` / `scan --root --min-size`）、
> 应用清单（longPathAware + PerMonitorV2 + 权限级别）、`publish.ps1` 一键单文件发布、README。
> 结论：**单元测试 376/376 全绿**；单文件 exe **34.7 MB / 仅 1 个文件**，发布后自检全部通过。
> 本轮又抓到 **3 个真实缺陷**，其中一个会让 exe **完全无法启动**。

---

## 1. 交付清单

| 文件 | 说明 |
|---|---|
| `Core/Cli/LinkCommands.cs` | `link` / `unlink` / `linkdir` / `verify` / `selfcheck` 五条命令 |
| `Core/Cli/CliHost.cs` | 路由与帮助；`scan` 增加 `--root` 别名与 `--min-size`；`move` 增加 `--force`/`--keep-backup`；`restore` 支持 `--id` |
| `src/DirPorter.Cli/app.manifest` | **asInvoker** + longPathAware + PerMonitorV2 |
| `src/DirPorter.Cli/app.admin.manifest` | requireAdministrator 变体（`publish.ps1 -RequireAdmin`） |
| `publish.ps1` | 一键发布单 exe；发布后**强制自检**，不通过即失败 |
| `README.md` | 功能说明、命令行参考、风险提示与免责声明、"绝对不要迁移"清单、还原方法 |
| `src/DirPorter.Tests/R6_CliTests.cs` | 31 个用例：**清单合法性**、参数解析、五条命令端到端、打包契约 |
| `dist/0.1.0/dirporter.exe` | 单文件产物（34.7 MB） |

---

## 2. 命令行覆盖情况（对齐需求 §147–165）

| 需求命令 | 状态 | 本次补齐的内容 |
|---|---|---|
| `scan --root <目录> [--min-size 100MB] [--json]` | ✅ | `--root` 别名 + `--min-size` 顶层条目过滤 |
| `move --source --target [--dry-run] [--force] [--keep-backup] [--threads] [--yes]` | ✅ | `--force`、`--keep-backup`（默认保留 7 天，见 §5 说明） |
| `list [--json]` | ✅ | 已有 |
| `verify --id <id>` | ✅ | **新增**：校验迁移记录的链接健康度 |
| `restore --id <id> [--yes]` | ✅ | `--id` 现在可直接使用（内部解析成 batch + entry） |
| `link --source --target [--type ...] [--relative] [--dry-run] [--yes]` | ✅ | **新增**：四种类型 + auto 推荐理由 |
| `linkdir --source --target [--type hardlink\|symlink-file] [--incremental]` | ✅ | **新增**：批量文件级链接 + 增量 |
| `unlink --path <链接路径> [--yes]` | ✅ | **新增**：只删链接本体 |
| `inspect --path [--json]` | ✅ | 已有 |
| `selfcheck` | ✅ | **新增**（需求未要求，但发布验证必需，见 §4） |

退出码沿用需求约定：`0/1/2/3/4/5/6`。`link` 在**能力校验因权限不通过**时返回 `5`（需要管理员），
其余校验不通过返回 `3`（前置校验），便于脚本区分"要提权"和"这条路本身走不通"。

---

## 3. 三条命令守住的安全边界

### 3.1 `unlink`：不是链接就拒绝

```
拒绝解除链接：D:\plain-directory 不是链接（是普通文件或目录），删除它会丢失真实数据。
```

需求的最高优先级红线在这里落地为三层：
① `Inspect` 先判定类型（重解析点类型 + 硬链接分支），②**类型为空一律拒绝**，
③ 删除走 `ILinkProvider.Delete`（内部仍是"剥离标签 + 删空壳"，绝不递归）。

`unlink` 还会在删除后**自证**：删除前记录目标目录的条目数，删除后再读一次并对比，
把"目标数据未被触碰"从口号变成一次真实检查：

```
已解除链接：C:\...\link-old（目录联接 Junction（mklink /J））
链接本体已删除，目标数据未被触碰。
目标目录完好（1 个条目）。
```

不存在的路径视为**幂等成功**（脚本可重复调用）；悬空链接同样能安全清除。

### 3.2 `linkdir`：追加式操作，因此不假装能回滚

`linkdir` 只新建链接、不动任何数据。这意味着它**没有**回滚概念——
中途失败时已创建的链接会保留。代码里如实写明并在失败时输出这条提示，而不是假装"原子"：

> 注意：linkdir 是**追加式**操作（只新建链接、不动数据），因此不会自动回滚已建链接；请按上面的失败清单处理。

实现上刻意**单线程 + 流式**（不先收集条目）：链接创建是逐条系统调用，多线程不提升吞吐却会让失败归因变复杂；
流式回调让内存为 O(1)，10 万文件也不会把条目常驻内存。

`--incremental` 的判定对硬链接做了保守处理：硬链接不是重解析点，且**本机实测删除多硬链接文件会走回收站**
（见 `docs/02` 的口径修正），因此只以"链接数 ≥ 2"作为"已建立"的保守判据，不依赖链接计数下降。

### 3.3 `link`：把"其余类型为什么不可用"也说清楚

`--dry-run` 除了列出将要执行的操作，还会列出**另外三种类型在当前环境下的可用性及原因**，
避免用户以为"工具不支持"而不是"当前环境不满足"：

```
其余三种类型的可用性：
  · 目录符号链接（mklink /D）：不可用（需要管理员权限或开启开发者模式）
  · 文件符号链接（mklink）：不可用（目标不存在）
  · 硬链接（mklink /H）：不可用（目标不存在）
```

---

## 4. 单文件发布

```powershell
.\publish.ps1                    # 自包含单文件（默认）
.\publish.ps1 -RequireAdmin      # 换用 requireAdministrator 清单
.\publish.ps1 -Mode framework-dependent
.\publish.ps1 -Trim              # 裁剪（需自检通过）
```

### 4.1 实测结果

| 项 | 值 |
|---|---|
| 产物 | `dist/0.1.0/dirporter.exe` |
| **体积** | **34.7 MB（34,629,164 字节）** |
| 目录内文件数 | **1**（真正的单文件） |
| 目标平台 | win-x64，自包含（无 .NET 运行时依赖） |
| 发布后自检 | **全部 PASS**（含台账 JSONL + **SQLite 后端往返**、能力矩阵） |

关键属性（写在 `publish.ps1` 里并有用例守护）：

* `PublishSingleFile=true`
* **`IncludeNativeLibrariesForSelfExtract=true`** —— `Microsoft.Data.Sqlite` 的原生 `e_sqlite3`
  必须能被单文件模式找到，否则"条目数 > 1000 自动切 SQLite"这条路径会在发布版里静默失效；
* 发布后**强制执行 `selfcheck`**，不通过就以非 0 退出（不给自己留"看起来发布了"的余地）。

### 4.2 为什么新增了 `selfcheck` 命令

需求没要求这条命令，但"发布成功"与"发布出去的能跑"是两件事。`selfcheck` 覆盖：

1. 版本 / 运行时 / 架构 / OS；
2. 权限环境（`SeCreateSymbolicLink` / `SeSecurity` / `SeRestore` / 开发者模式）；
3. robocopy 是否可用（复制引擎 A 的依赖）；
4. 数据根目录是否可写（含降级说明）；
5. **两种台账后端往返**（JSONL + SQLite —— 后者专门验证原生库已内置）；
6. 四种链接类型的能力矩阵。

它同时也是裁剪发布的验收闸门（见 §6）。

---

## 5. 权限模型：与需求原文的一处偏离（必须说明）

需求 §32 要求：清单默认 `requireAdministrator`，**同时**提供"以普通权限运行"的降级路径。

在**单 exe** 产品里这两件事无法同时成立：清单写了 `requireAdministrator` 之后，
每次调用（包括只读的 `inspect`、`scan`、干跑）都会弹 UAC，而且没有任何办法在同一 exe 内切回普通权限 ——
所谓"降级路径"实际上就消失了。

因此本项目采用：

* **默认 `asInvoker`**（按需提权）：只读、扫描、干跑全程无 UAC；
* 需要提权的操作由程序明确提示（`link` 因权限不通过返回退出码 5）；
* **同时提供 `app.admin.manifest`**：`publish.ps1 -RequireAdmin` 一键切换成"启动即提权"的部署形态。

这样两种运行方式都是真实存在的，而不是名义上存在。两份清单都有用例守护（见 §7）。

---

## 6. 裁剪实测结论：**当前不可用**（有证据，不是猜测）

需求 §19 要求 `PublishSingleFile + SelfContained + TrimMode=partial`。实测：

| 配置 | 体积 | 发布后自检 |
|---|---|---|
| 未裁剪（默认） | 34.7 MB | **全绿** |
| `-Trim`（TrimMode=partial） | **11.6 MB** | **失败** |

裁剪版自检失败原文：

```
[FAIL] 台账后端往返 — InvalidOperationException：
Reflection-based serialization has been disabled for this application.
Either use the source generator APIs or explicitly configure the 'JsonSerializerOptions.TypeInfoResolver' property.
```

根因：裁剪会关闭 `JsonSerializerIsReflectionEnabledByDefault`，而本项目所有 JSON（台账、Journal、
迁移记录索引、扫描缓存、所有 `--json` 输出）都走**反射式** `JsonSerializer`。
放开裁剪就必须把所有模型改成 `JsonSerializerContext` 源生成——那是一次独立的、会波及全部模型的改造。

**处理方式**：`-Trim` 开关保留，但**默认关闭**，且开启时以 `selfcheck` 作为硬闸门；
`publish.ps1` 在失败时直接提示"这通常意味着裁剪把 JSON 序列化需要的成员裁掉了"。
体积能省 23 MB，但不能用"应该没问题"来换。

---

## 7. 本轮抓到的 3 个真实缺陷

### 缺陷 1（致命）：应用清单里注释写了命令行参数 → exe 完全无法启动

* **现象**：加完 `app.manifest` 后，Debug 与发布版 exe **统统起不来**：
  命令行报 `Permission denied`，PowerShell 报
  `应用程序无法启动，因为应用程序的并行配置不正确`（SxS 无法生成激活上下文）。
* **根因**：XML 规范**禁止注释内出现两个连续的连字符**。而我在清单注释里写了
  `--dry-run`、`--json` 这类命令行参数 → 整个清单不是合法 XML → SxS 拒绝。
* **最坑的地方**：**编译完全不报错**，只有运行时才炸；而报错信息是"并行配置不正确"，
  与"注释里有双连字符"看起来毫无关系。
* **定位方法**：写一份最小清单（只有 `assemblyIdentity` + `trustInfo`）重建 → 能跑；
  于是确定是清单内容问题，再逐项排查到注释。
* **修复与防复发**：
  1. 两份清单重写，注释里不再出现双连字符，并**在注释里写明这条坑**；
  2. 新增用例 `应用清单_必须是合法XML_且注释里不能有双连字符`：用 `XDocument.Parse` 解析两份清单，
     并单独断言注释内容合法。这类"编译期无感、运行期致命"的问题必须由测试兜住。

### 缺陷 2：`publish.ps1` 的清理步骤失败被升级成终止错误

* **现象**：脚本打印完标题就退出（退出码 1），看起来像"发布失败"，其实什么都没做。
* **根因**：`$ErrorActionPreference = "Stop"` 下，`Remove-Item -Recurse` 的清理失败（
  在本机是被安全删除策略拦截）被升级为终止错误，脚本在"正在发布"之前就死了。
* **修复**：清理步骤 `try/catch` 包起来，**失败不阻断发布**（MSBuild 会覆盖同名文件），但把原因打印出来。

### 缺陷 3（脚本层，复现）：PowerShell 5.1 下原生命令写 stderr 会变成空消息终止异常

R5 已修过一次（`drill` / `elevated` / `bench` 三个脚本），本轮 `publish.ps1` 又踩到同一类问题的变体：
发布脚本调用 `& $exe selfcheck` 时，宿主抛 `ApplicationFailedException`。
这一条最终指向缺陷 1（exe 根本起不来），但也说明：**任何"调用自家 exe 并解析输出"的脚本都要把它当不信任输入处理**。

---

## 8. 验证方法（可复现）

```powershell
# 1) 全量单元测试（376 个）
.\tests\run-unit-tests.ps1

# 2) 只跑本轮新增的 CLI 与打包用例
.\tests\run-unit-tests.ps1 -Filter R6_

# 3) 一键发布 + 发布后自检
.\publish.ps1

# 4) 手工确认几种链接（与系统工具对账）
.\dist\0.1.0\dirporter.exe link --source "C:\Temp\old" --target "C:\Temp\new" --yes
dir /AL C:\Temp
fsutil reparsepoint query C:\Temp\old
.\dist\0.1.0\dirporter.exe unlink --path "C:\Temp\old" --yes
```

本机实测：`失败 0，通过 376`；`publish.ps1` 输出 `34.7 MB / 共 1 个文件` 且自检全绿。

---

## 9. 未覆盖项（不假装已测）

| 项 | 状态 |
|---|---|
| GUI（WPF，需求 §179 `App/`） | **未开始**，属 R7；单 exe 届时可直接复用现有 `CliHost` 与 `publish.ps1` |
| 目录符号链接的创建路径 | 未测（需管理员或开发者模式；提权脚本已就绪，待执行） |
| `linkdir` 的文件符号链接分支 | 逻辑与硬链接同源，但本机非提权，未实测成功路径 |
| UNC 目标（符号链接支持、Junction 拒绝） | 需网络环境，未测 |
| 相对路径符号链接父目录整体移动后仍可解析 | 需求显式要求，R1 已有用例（`相对路径符号链接_父目录改名后仍能解析`），但依赖提权环境，本机走的"拒绝理由"分支 |
| `subst` 虚拟盘模拟跨卷的集成脚本（需求 §183） | 未做（本机已有真实多卷可用，但脚本化验证仍未落地） |
| 裁剪发布 | 已实测判定不可用（§6） |

---

## 10. 下一轮（R7）：WPF GUI

R7 主体：`App/` WPF（MVVM）+ 五页 + 中英双语 + 深/浅主题 + 单实例 + 崩溃兜底 + `AttachConsole` 复用同一 CLI 入口。

验收口径提前声明：

1. 单 exe 同时承担 GUI 与 CLI：无参数启动 GUI，有参数执行 CLI 并把输出接回父控制台；
2. 五页（扫描 / 迁移 / 迁移记录 / 设置 / 关于）均可独立走查，UI 不得冻结（IO 全在后台线程，进度节流 ≤10 次/秒）；
3. 需要提权的链接类型在 UI 上**置灰并写明原因**，不允许点下去才报错；
4. 打包体积与启动时间如实报出（WPF 不支持 full trim，预期 150–180 MB，压缩后 60–75 MB）。
