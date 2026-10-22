# GitHub 开源 + 用 Release 做自动更新（操作指引）

## 已经替你做完的部分

| 项 | 状态 |
|---|---|
| 本地 Git 仓库 | 已 `git init`，2 次提交（`f174d94` 初始、`948c7e8` 0.3.0） |
| `.gitignore` | 已排除 `dist/`、`bin/`、`obj/`、`logs/`、`installer/output/` |
| `LICENSE` | MIT |
| `README.md` | 已补「9.5 自动更新」与「15 许可」章节 |
| 代码 | `UpdateService` 已支持 `github:owner/repo`（读 Releases API） |
| 发布产物 | `dist/0.3.0/dirporter.exe`（67,827,071 B，SHA256 `a5741f2b…`） |

仓库内容：235 个文件、约 2.1 MB（不含 64 MB 的可执行文件——它走 Release 附件）。

---

## 第一步：用 GitHub Desktop 发布仓库

1. 打开 GitHub Desktop → 菜单 **File → Add local repository…**
2. 选择 `C:\Users\31125\WorkBuddy\工具下载使用\DirPorter`
3. 点 **Publish repository**
   - Name：`DirPorter`
   - 取消勾选 **Keep this code private**（公开才能让别人匿名检查更新；私有仓库需要 token）
   - 不要勾选 Git LFS
4. 发布完成后记住你的仓库地址：`https://github.com/<你的用户名>/DirPorter`

> 如果 Desktop 提示"这个目录已有提交"，那是正常的——两次提交已经在了。

---

## 第二步：建第一个 Release（这一步才是"可更新"的关键）

GitHub Desktop **不能**建 Release，要去网页：

1. 打开 `https://github.com/<用户名>/DirPorter/releases/new`
2. **Choose a tag**：输入 `v0.3.0` → 点「Create new tag on publish」
3. **Release title**：`0.3.0`
4. **Describe this release**：粘贴更新说明（会显示在客户端的更新提示里）
5. **Attach binaries**：把 `dist\0.3.0\dirporter.exe` 拖进去
   - ⚠️ 文件名必须保持 `dirporter.exe`（程序按这个名字挑附件）
6. 点 **Publish release**

---

## 第三步：让程序指向 GitHub（两种方式，二选一）

### 方式 A：设置页填一次（立刻生效，不用重新编译）

打开 DirPorter → **设置 → 一键更新 → 高级：自定义更新源**，填：

```
github:你的用户名/DirPorter
```

然后点「检查并更新」。以后所有用户只要在设置里填这一行（或由你把默认值编译进去）。

### 方式 B：编译进默认值（推荐，最终用户零配置）

改 `src/DirPorter.App/DirPorter.App.csproj` 里这一行：

```xml
<UpdateFeedUrl>https://junmo.de5.net/dirporter/latest.json</UpdateFeedUrl>
```

改成：

```xml
<UpdateFeedUrl>github:你的用户名/DirPorter</UpdateFeedUrl>
```

然后重新 `.\publish.ps1` 并重新发 Release。

---

## 以后每次发版的固定流程（4 步）

```powershell
cd "C:\Users\31125\WorkBuddy\工具下载使用\DirPorter"

# 1) 改版本号（唯一改动点）
#    Directory.Build.props 里 <Version>0.3.1</Version>

# 2) 发布
.\publish.ps1

# 3) 提交并推送（GitHub Desktop 里点 Commit → Push 也行）
git add -A; git commit -m "版本 0.3.1"; git push

# 4) 网页建 Release：tag = v0.3.1，上传 dist\0.3.1\dirporter.exe
```

客户端点「检查并更新」就会：读 `api.github.com/.../releases/latest` → 比版本 →
下载 exe → 校验（GitHub 提供 digest 就校验 SHA256）→ 替换 → 重启。

---

## 几个必须知道的点

1. **仓库必须公开**，或者给程序配 token。私有仓库的 Release 匿名读不到。
2. **tag 用 `v<版本>`**（如 `v0.3.0`）。程序会去掉 `v` 再比版本，`0.3.0` 这种没 `v` 的也能识别。
3. **附件名保持 `dirporter.exe`**；上传多个 exe 时程序优先选这个名字。
4. **GitHub API 有匿名限流**（每小时 60 次/IP）。个人使用完全够；如果给很多人用，之后可以换成
   `https://github.com/<用户>/<仓库>/releases/latest/download/latest.json` 这类静态地址。
5. **国内网络**：程序用系统代理（Clash 等）。如果 `api.github.com` 打不开，检查代理是否覆盖了它。
6. **签名**：按你的决定不做了。没有证书的自签名反而会让人误以为"已签名但不受信任"。
   GitHub Release 页面会显示文件大小与 SHA256，够用作校验。

---

## 关于 `.migbak` 那 5.99 GB

`dirporter sweep --days 1 --dry-run` 的演练结果：**38 个停车目录全部到期，可回收 5.99 GB**。

0.3.0 起：
- **每次启动**、**每批迁移结束后**，自动按「设置 → 保留天数」清理到期备份；
- 也可以点 **设置 → 迁移备份区 → 清理到期备份** 立刻执行。

`.ledger` 台账永远保留，不受清理影响。
