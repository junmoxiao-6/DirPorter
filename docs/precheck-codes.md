# DirPorter 预检拒绝码对照表

> ⚠️ 本文件由代码自动生成，请勿手工编辑。
> 来源：`DirPorter.Core.Precheck.PrecheckCodes.All`（生成测试：`R2_CodeTableGenerationTests`）。
> 生成时间：2026-10-24 23:32:36

## 严重级别语义

| 级别 | 含义 | 程序行为 |
|---|---|---|
| Info | 提示信息 | 不打断，仅展示 |
| Warning | 警告 | 展示并计入风险摘要，可直接继续 |
| RequireAcknowledge | 需显式确认 | 必须勾选「我理解风险」+ 二次确认（`--yes` 可代答） |
| Blocking | 硬性阻止 | 拒绝执行，且保证磁盘零写入（`--force` 也不能绕过） |

## 建议动作枚举（GUI 引导按钮 / CLI 提示的契约）

| 动作 | 用户应做的事 |
|---|---|
| `None` | 无需动作 |
| `ChooseAnotherSource` | 换一个源目录 |
| `ChooseAnotherTarget` | 换一个目标位置或目标卷 |
| `FreeUpSpace` | 释放目标卷空间或改迁到更大的卷 |
| `RunAsAdministrator` | 以管理员身份重新运行（或开启开发者模式） |
| `AcknowledgeRisk` | 勾选「我理解风险」并二次确认 |
| `ReviewSpecialObjects` | 在预览中逐项查看并确认特殊对象 |
| `ReuseOrRenameTarget` | 复用空目录或给目标改名 |
| `RemoveNestedEntry` | 无需动作（子条目已被自动跳过） |
| `ResolveTargetConflict` | 为冲突条目指定不同的目标目录 |
| `OpenExistingTarget` | 打开既有目标位置或走还原入口 |
| `EnableLongPathSupport` | 必要时开启系统长路径支持 |

## 拒绝码对照表

| 拒绝码 | 级别 | 标题 | 建议动作 | 用户应如何处理 |
|---|---|---|---|---|
| `SRC_NOT_FOUND` | Blocking | 源路径不存在 | `ChooseAnotherSource` | 确认路径拼写；若目录已被移动，请改用「仅建链」模式，或在迁移记录页查看历史任务。 |
| `SRC_NOT_DIRECTORY` | Blocking | 源不是目录 | `ChooseAnotherSource` | 本功能用于迁移目录；文件请改用 link 的硬链接 / 文件符号链接模式。 |
| `SRC_IS_LINK` | Blocking | 源目录已经是链接（可能已迁移过） | `OpenExistingTarget` | 不要再迁移一次。可点「打开目标位置」查看现有数据，或在迁移记录页对该条目执行还原。 |
| `SRC_BLACKLISTED` | Blocking | 源目录属于系统关键目录（硬拒绝） | `ChooseAnotherSource` | 这是硬性限制，不可绕过。请选择具体应用/数据子目录，而不是系统目录本身。 |
| `SRC_WARNING_LIST` | RequireAcknowledge | 源目录在警告名单内（需确认风险） | `AcknowledgeRisk` | 确认你了解该目录可能被程序/服务按固定路径引用；勾选「我理解风险」并二次确认后可继续。建议先看 Dry-run 预览。 |
| `SRC_SERVICE_REFERENCED` | RequireAcknowledge | 源目录被系统服务或开机启动项引用 | `AcknowledgeRisk` | 迁移后对应服务可能无法启动。建议先停用相关服务，或放弃迁移该目录；如坚持迁移，请准备还原。 |
| `SRC_SPECIAL_OBJECTS` | Warning | 目录树内含特殊对象（已逐项标注） | `ReviewSpecialObjects` | 请在预览中逐项查看：嵌套链接不会被递归复制、硬链接会变成独立文件、云占位文件会触发下载、EFS 加密文件行为未实测。确认可接受后再继续。 |
| `SRC_SPECIAL_OBJECTS_ACK` | RequireAcknowledge | 目录树内含会产生额外成本的特殊对象（需确认） | `ReviewSpecialObjects` | 含云占位文件时迁移会触发从云端下载，可能耗时数十分钟并产生大量流量；含 EFS 加密文件时跨卷行为尚未实测。请确认后再继续；建议先做 Dry-run 并选低峰时段执行。 |
| `SRC_PERMISSION_DENIED` | Blocking | 对源目录权限不足（需要读 + 删 + 改父目录） | `RunAsAdministrator` | 以管理员身份重新运行；若仍失败，说明该目录被 ACL 或安全软件保护，请改选其它目录。 |
| `SRC_ENUMERATION_FAILED` | Blocking | 无法枚举源目录内容 | `RunAsAdministrator` | 以管理员身份重试；若目录内有正在被独占的子系统（如正在运行的服务数据目录），请先停用相关服务。 |
| `TGT_VOLUME_UNAVAILABLE` | Blocking | 无法获取目标所在卷的信息 | `ChooseAnotherTarget` | 确认目标盘符存在且已挂载；网络位置与未挂载的分区不能作为迁移目标。 |
| `TGT_NOT_FIXED_VOLUME` | Blocking | 目标不是本地固定卷（可移动盘 / U 盘 / 网络位置） | `ChooseAnotherTarget` | 请选择本地固定磁盘。U 盘拔盘后链接立即失效，网络位置不被 Junction/硬链接支持。 |
| `TGT_FS_UNSUPPORTED` | Blocking | 目标卷文件系统不支持重解析点（FAT32 / exFAT 等） | `ChooseAnotherTarget` | 请选择 NTFS 或 ReFS 卷。FAT32/exFAT 无法承载 Junction 与符号链接。 |
| `TGT_PATH_OVERLAP` | Blocking | 源与目标路径重叠（相同 / 互为父子） | `ChooseAnotherTarget` | 目标不能是源本身、源的子目录，也不能是源的上级目录，否则会形成自我嵌套或环。请换一个目标位置。 |
| `TGT_SPACE_INSUFFICIENT` | Blocking | 目标卷剩余空间不足（要求 ≥ 源实际占用 × 1.05） | `FreeUpSpace` | 释放目标卷空间，或改迁到空间更大的卷。注意：占用按文件实际磁盘占用统计，不是名义大小。 |
| `TGT_EXISTS_NONEMPTY` | Blocking | 目标位置已存在同名且非空的目录 | `ReuseOrRenameTarget` | 给目标改名，或先自行处理该目录内容。工具不会覆盖或合并已存在的非空目录。 |
| `TGT_EXISTS_EMPTY` | RequireAcknowledge | 目标位置已存在同名空目录 | `ReuseOrRenameTarget` | 可直接复用该空目录（勾选确认），或给目标改名。 |
| `TGT_PARENT_NOT_WRITABLE` | Blocking | 目标父目录不可写 | `RunAsAdministrator` | 以管理员身份重新运行；若目标位于受保护位置（如 Program Files），请改选可写目录。 |
| `TGT_NAME_INVALID` | Blocking | 目标目录名非法或包含系统保留名 | `ChooseAnotherTarget` | 避免使用 CON / PRN / AUX / NUL / COM1-9 / LPT1-9 等保留名，以及 : * ? " < > | 等非法字符。 |
| `BATCH_NESTED_SOURCE` | Info | 批次内存在嵌套条目，已自动跳过子条目 | `RemoveNestedEntry` | 无需处理：父目录已包含子目录，迁移父目录即可。此处仅告知你子条目被跳过的原因。 |
| `BATCH_TARGET_CONFLICT` | Blocking | 批次内多个条目映射到同一个目标路径 | `ResolveTargetConflict` | 为每个条目指定不同的目标目录（否则后写入的会覆盖先写入的）。 |
| `BATCH_SPACE_TOTAL` | Blocking | 批次总占用超出目标卷剩余空间 | `FreeUpSpace` | 分批迁移，或先释放目标卷空间。可按大小排序，先迁移最关键的部分。 |
| `INFO_ASSESSMENT` | Info | 预检评估摘要 | `None` | 无需处理。此处的文件数与占用用于预估传输量与耗时（Dry-run 依据）。 |
| `INFO_LONG_PATH` | Info | 树内存在超过 260 字符的路径 | `EnableLongPathSupport` | 本工具用 \\?\ 前缀可正常迁移；若迁移后某些老程序访问失败，可在系统「长路径支持」策略中开启。 |

## 按严重级别分组

### Blocking

- `SRC_NOT_FOUND` — 源路径不存在
- `SRC_NOT_DIRECTORY` — 源不是目录
- `SRC_IS_LINK` — 源目录已经是链接（可能已迁移过）
- `SRC_BLACKLISTED` — 源目录属于系统关键目录（硬拒绝）
- `SRC_PERMISSION_DENIED` — 对源目录权限不足（需要读 + 删 + 改父目录）
- `SRC_ENUMERATION_FAILED` — 无法枚举源目录内容
- `TGT_VOLUME_UNAVAILABLE` — 无法获取目标所在卷的信息
- `TGT_NOT_FIXED_VOLUME` — 目标不是本地固定卷（可移动盘 / U 盘 / 网络位置）
- `TGT_FS_UNSUPPORTED` — 目标卷文件系统不支持重解析点（FAT32 / exFAT 等）
- `TGT_PATH_OVERLAP` — 源与目标路径重叠（相同 / 互为父子）
- `TGT_SPACE_INSUFFICIENT` — 目标卷剩余空间不足（要求 ≥ 源实际占用 × 1.05）
- `TGT_EXISTS_NONEMPTY` — 目标位置已存在同名且非空的目录
- `TGT_PARENT_NOT_WRITABLE` — 目标父目录不可写
- `TGT_NAME_INVALID` — 目标目录名非法或包含系统保留名
- `BATCH_TARGET_CONFLICT` — 批次内多个条目映射到同一个目标路径
- `BATCH_SPACE_TOTAL` — 批次总占用超出目标卷剩余空间

### RequireAcknowledge

- `SRC_WARNING_LIST` — 源目录在警告名单内（需确认风险）
- `SRC_SERVICE_REFERENCED` — 源目录被系统服务或开机启动项引用
- `SRC_SPECIAL_OBJECTS_ACK` — 目录树内含会产生额外成本的特殊对象（需确认）
- `TGT_EXISTS_EMPTY` — 目标位置已存在同名空目录

### Warning

- `SRC_SPECIAL_OBJECTS` — 目录树内含特殊对象（已逐项标注）

### Info

- `BATCH_NESTED_SOURCE` — 批次内存在嵌套条目，已自动跳过子条目
- `INFO_ASSESSMENT` — 预检评估摘要
- `INFO_LONG_PATH` — 树内存在超过 260 字符的路径

