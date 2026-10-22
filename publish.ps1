# DirPorter 一键发布单 exe（GUI + CLI 同一 exe）
#
# 用法：
#   .\publish.ps1                                  # 默认：自包含单文件（无运行时依赖）
#   .\publish.ps1 -Mode framework-dependent        # 依赖已安装的 .NET 8 Desktop Runtime（体积小）
#   .\publish.ps1 -RequireAdmin                    # 清单换成 requireAdministrator（每次启动弹 UAC）
#   .\publish.ps1 -SkipSelfCheck                   # 跳过发布后自检（不推荐）
#
# 产出：dist\<version>\dirporter.exe（单文件）
#
# R7 变更：发布目标由 DirPorter.Cli 改为 **DirPorter.App**。
#   · 单 exe 现在同时是 GUI 与 CLI：无参数启动界面，带参数走命令行（AttachConsole 接回父控制台）。
#   · DirPorter.Cli 保留为开发期控制台调试宿主（程序集名 dirporter-cli）。
#
# 设计取舍（写在这里，避免以后有人"顺手"改掉）：
#   · IncludeNativeLibrariesForSelfExtract=true —— Microsoft.Data.Sqlite 的原生 e_sqlite3
#     必须能被单文件模式找到，否则"条目 >1000 自动切 SQLite"这条路径会在运行时炸掉。
#   · **不做裁剪** —— 提示词要求的 TrimMode=partial 对 WPF 不成立：WPF 官方不支持裁剪
#     （XAML 反射与 BAML 解析无法静态分析），开启后发布直接失败。这是对需求原文的
#     显式偏离，理由与实测证据见 docs/07-R6-完整CLI与单文件发布.md §5 与 docs/08-R7*。
#   · 发布后**必须**跑 selfcheck：它覆盖两种台账后端往返（含 SQLite 原生库）。
#   · ⚠️ 本 exe 是 GUI 子系统（WinExe）：PowerShell 里直接 `& $exe args` **不会等待**、
#     也拿不到 $LASTEXITCODE。自检必须用 Start-Process -Wait 并重定向输出。

param(
    [ValidateSet("self-contained", "framework-dependent")]
    [string]$Mode = "self-contained",
    [switch]$RequireAdmin,
    [switch]$Trim,
    [switch]$SkipSelfCheck,
    [switch]$NoCompression
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

$root = $PSScriptRoot
$project = Join-Path $root "src\DirPorter.App\DirPorter.App.csproj"
$adminManifest = Join-Path $root "src\DirPorter.App\app.admin.manifest"
$version = (& dotnet msbuild $project -nologo -getProperty:Version).Trim()
$outRoot = Join-Path $root "dist"
$outDir = Join-Path $outRoot $version

if ($Trim) {
    # 用单引号数组拼接（不用 here-string：结束符必须顶格，容易写错）
    $trimMessage = @(
        '-Trim 对 WPF 不可用（显式偏离需求原文，理由如下）：',
        '  WPF 官方不支持裁剪 —— XAML/BAML 依赖运行期反射解析类型与属性，静态裁剪无法证明其安全性。',
        '  开启后发布阶段就会直接失败。若确实要压体积，请改用 -Mode framework-dependent',
        '  （依赖已安装的 .NET 8 Desktop Runtime，体积可显著下降且不需要裁剪）。',
        '  详见 docs/07-R6-完整CLI与单文件发布.md 第 5 节。'
    ) -join [Environment]::NewLine

    throw $trimMessage
}

Write-Host "=== DirPorter 单文件发布（GUI + CLI） ===" -ForegroundColor White
Write-Host "  版本        ：$version"
Write-Host "  模式        ：$Mode"
Write-Host "  权限清单    ：$(if ($RequireAdmin) { 'requireAdministrator（每次启动弹 UAC）' } else { 'asInvoker（按需提权）' })"
Write-Host "  裁剪        ：关闭（WPF 不支持裁剪）"
Write-Host "  输出目录    ：$outDir"

# 清理旧产物：用 .NET 递归删除（比 Remove-Item -Recurse 快得多，且十万小文件场景下不会卡死）。
# 清理失败**不阻断发布**（MSBuild 会覆盖同名文件），但必须把原因打出来。
if (Test-Path $outDir) {
    try {
        [System.IO.Directory]::Delete($outDir, $true)
    } catch {
        Write-Host ("  清理旧产物失败（将继续发布）：" + $_.Exception.Message) -ForegroundColor Yellow
    }
}

$arguments = @(
    "publish", $project,
    "-c", "Release",
    "-r", "win-x64",
    "-o", $outDir,
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:DebugType=none",
    "-p:GenerateDocumentationFile=false",
    "-p:SatelliteResourceLanguages=zh-Hans"
)

if ($Mode -eq "self-contained") { $arguments += "-p:SelfContained=true" } else { $arguments += "-p:SelfContained=false" }
if (-not $NoCompression) { $arguments += "-p:EnableCompressionInSingleFile=true" }
if ($RequireAdmin) { $arguments += "-p:ApplicationManifest=$adminManifest" }

Write-Host ""
Write-Host "正在发布（首次可能需数十秒）…" -ForegroundColor DarkGray
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "发布失败，退出码 $LASTEXITCODE" }

$exe = Join-Path $outDir "dirporter.exe"
if (-not (Test-Path $exe)) { throw "发布后未找到 $exe" }

$files = @(Get-ChildItem -Path $outDir -File -Recurse)
$exeSize = (Get-Item $exe).Length
$totalSize = ($files | Measure-Object -Property Length -Sum).Sum

Write-Host ""
Write-Host "发布结果：" -ForegroundColor White
Write-Host ("  exe 体积    ：{0:N1} MB" -f ($exeSize / 1MB))
Write-Host ("  目录总大小  ：{0:N1} MB，共 {1} 个文件" -f ($totalSize / 1MB), $files.Count)

if ($files.Count -gt 1) {
    Write-Host "  其余文件（单文件模式下可能仍有少量附属文件）：" -ForegroundColor DarkGray
    $files | Where-Object { $_.FullName -ne $exe } | ForEach-Object {
        Write-Host ("    · {0}  ({1:N0} KB)" -f $_.Name, ($_.Length / 1KB)) -ForegroundColor DarkGray
    }
}

if (-not $SkipSelfCheck) {
    Write-Host ""
    Write-Host "发布后自检（验证运行时依赖与两种台账后端）：" -ForegroundColor White

    # ⚠️ WinExe 不能用 `& $exe` 调用：PowerShell 不会等待也不会设置 $LASTEXITCODE。
    #    这里走 `cmd /c` 同步执行，能拿到真实的退出码；避免 Start-Process 的
    #    -RedirectStandardOutput 在部分环境里因 Path/PATH 键大小写冲突抛 ArgumentException。
    $selfCheckCode = 0
    $output = cmd /c "`"$exe`" selfcheck" 2>&1
    if ($LASTEXITCODE -ne 0) { $selfCheckCode = $LASTEXITCODE }
    $output | ForEach-Object { Write-Host $_ }

    if ($selfCheckCode -ne 0) {
        Write-Host ""
        Write-Host "自检未通过（退出码 $selfCheckCode）。" -ForegroundColor Red
        exit $selfCheckCode
    }

    Write-Host ""
    Write-Host "自检通过。" -ForegroundColor Green
}

Write-Host ""
Write-Host ("完成：{0}" -f $exe) -ForegroundColor Green
Write-Host "用法：双击启动界面；或在命令行带参数使用（dirporter.exe --help）。" -ForegroundColor DarkGray
Write-Host "提示：单文件 exe 首次运行时会把原生库解压到临时目录，启动略慢属正常现象。" -ForegroundColor DarkGray
