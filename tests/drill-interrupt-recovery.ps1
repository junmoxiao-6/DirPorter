# DirPorter R3 中断 / 恢复演练（可复现）
#
# 目的：证明"进程被强杀在任何中间态"之后，工具都能识别磁盘现状并给出可执行建议，
#       且按建议修复后源数据零丢失。
#
# 用法：  .\tests\drill-interrupt-recovery.ps1
#         .\tests\drill-interrupt-recovery.ps1 -KeepArtifacts   # 保留现场便于人工查看
#
# 原理：CLI 内置测试钩子 --kill-after <State>，到达该状态时**立即强杀自身进程**
#       （不执行 finally、不执行回滚，等价于断电/任务管理器结束进程）。
#       钩子需要环境变量 DIRPORTER_ALLOW_KILL_HOOK=1 才生效，避免误伤真实使用。

param(
    [switch]$KeepArtifacts
)

$ErrorActionPreference = "Stop"

# CLI 以 UTF-8 输出；PowerShell 必须用同一编码解码，否则中文会被错误解码、进而破坏 JSON。
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "src\DirPorter.Cli\bin\Debug\net8.0-windows\dirporter.exe"

if (-not (Test-Path $exe)) {
    Write-Host "未找到 CLI 可执行文件，正在编译…" -ForegroundColor Yellow
    & dotnet build (Join-Path $root "DirPorter.sln") --nologo -v q | Out-Null
}

if (-not (Test-Path $exe)) { throw "编译后仍未找到 $exe" }

$scratch = Join-Path $env:TEMP ("dirporter-drill-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
$srcRoot = Join-Path $scratch "src"
$tgtRoot = Join-Path $scratch "tgt"
$source = Join-Path $srcRoot "ProjectA"
$target = Join-Path $tgtRoot "Target"
$parkingDir = Join-Path $srcRoot ".migbak"

$env:DIRPORTER_ALLOW_KILL_HOOK = "1"

# 把 Journal / 迁移记录 / 备份索引全部隔离到演练现场内：
# 保证每个场景的 recover 判定确定（只看得到本场景留下的中间态），也不污染真实数据。
$env:DIRPORTER_DATA_ROOT = Join-Path $scratch "data"

$script:pass = 0
$script:fail = 0

function Write-Step([string]$text) { Write-Host "  · $text" -ForegroundColor DarkGray }
function Write-Pass([string]$text) { $script:pass++; Write-Host "  [PASS] $text" -ForegroundColor Green }
function Write-Fail([string]$text) { $script:fail++; Write-Host "  [FAIL] $text" -ForegroundColor Red }

function Reset-Scratch {
    # ⚠️ 现场里可能含 Junction（迁移成功/半成功时），直接 Remove-Item -Recurse 会**穿越链接**删掉目标数据。
    #    因此先用工具自己的安全删除器清空子树（遇链接只删链接本体），最后再删空壳目录。
    if (Test-Path $srcRoot) { & $exe cleanresidue --target $srcRoot --include-root --yes | Out-Null }
    if (Test-Path $tgtRoot) { & $exe cleanresidue --target $tgtRoot --include-root --yes | Out-Null }
    Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
}

function Initialize-Scenario {
    Reset-Scratch
    New-Item -ItemType Directory -Force -Path (Join-Path $source "sub") | Out-Null
    New-Item -ItemType Directory -Force -Path $tgtRoot | Out-Null
    1..4 | ForEach-Object {
        Set-Content -Path (Join-Path $source "f$_.txt") -Value ("payload-" + ("x" * (100 * $_)))
    }
    1..2 | ForEach-Object {
        Set-Content -Path (Join-Path $source "sub\g$_.txt") -Value ("nested-" + $_)
    }
}

function Invoke-DirPorter {
    param([string[]]$Arguments, [switch]$AllowFailure)
    # 不用 Out-String：它会按控制台宽度做格式化，可能把 JSON 折行破坏掉。
    #
    # 已在脚本内局部把 ErrorActionPreference 降为 Continue：
    # PowerShell 5.1 在 $ErrorActionPreference='Stop' 下，原生命令写 stderr 会被转成
    # 终止性的 RemoteException（且 Message 为空），使脚本莫名其妙中断。
    # CLI 的进度条写在 stderr，因此这个坑必然会踩到。
    $lines = & { $ErrorActionPreference = 'Continue'; & $exe @Arguments 2>&1 }
    $code = $LASTEXITCODE
    $output = ($lines | ForEach-Object { $_.ToString() }) -join "`n"
    if (-not $AllowFailure -and $code -ne 0 -and $code -ne 1) {
        throw "dirporter $($Arguments -join ' ') 退出码 $code`n$output"
    }
    return @{ Output = $output; Code = $code }
}

function Get-SourceFileCount {
    if (-not (Test-Path $source)) { return 0 }
    return (Get-ChildItem -Path $source -Recurse -File -Force -ErrorAction SilentlyContinue).Count
}

function Get-ParkedDirectories {
    if (-not (Test-Path $parkingDir)) { return @() }
    return @(Get-ChildItem -Path $parkingDir -Directory -Force |
        Where-Object { $_.Name -ne ".ledger" })
}

function Invoke-RepairByAction {
    param([string]$Action, [string]$ParkingPath)

    switch ($Action) {
        "RenameParkingBack" {
            $parked = Get-ParkedDirectories | Select-Object -First 1
            if (-not $parked) { return "没有可改回原名的停车目录" }
            Move-Item -LiteralPath $parked.FullName -Destination $source
            return "已把停车目录改回原名"
        }
        "CleanTargetResidue" {
            $r = Invoke-DirPorter @("cleanresidue", "--target", $target, "--include-root", "--yes")
            return "已清理目标残留：" + ($r.Output.Trim())
        }
        "FinalizeByDeletingParking" {
            $parked = Get-ParkedDirectories | Select-Object -First 1
            if (-not $parked) { return "没有停车目录可清理" }
            $r = Invoke-DirPorter @("cleanresidue", "--target", $parked.FullName, "--include-root", "--yes")
            return "已清理停车目录：" + ($r.Output.Trim())
        }
        "FinalizeRecord" { return "无需触碰数据，补写记录即可" }
        default { return "无需修复" }
    }
}

Write-Host ""
Write-Host "DirPorter 中断 / 恢复演练" -ForegroundColor Cyan
Write-Host "现场目录：$scratch"
Write-Host ""

# ============================================================================
# 场景 1–4：在四个中间态强杀，验证 recover 的判定与修复
# ============================================================================
$cases = @(
    @{ State = "Copying"; Expected = "None"; Note = "停在复制中：目标侧空、源完好" },
    @{ State = "Parking"; Expected = "CleanTargetResidue"; Note = "停在停车前：目标已有半成品、源完好" },
    @{ State = "Linking"; Expected = "RenameParkingBack"; Note = "停在建链前：源已改名到 .migbak" },
    @{ State = "LinkVerify"; Expected = "FinalizeByDeletingParking"; Note = "停在链接校验前：链接已生效" }
)

foreach ($case in $cases) {
    Write-Host "场景：--kill-after $($case.State)  （$($case.Note)）" -ForegroundColor White
    Initialize-Scenario

    # 1) 带杀点运行：进程必须被强杀（退出码非 0）
    $kill = Invoke-DirPorter @("move", "--source", $source, "--target", $target, "--yes", "--no-same-volume", "--quiet", "--kill-after", $case.State) -AllowFailure
    if ($kill.Code -ne 0) { Write-Pass "进程在 $($case.State) 被强杀（退出码 $($kill.Code)）" }
    else { Write-Fail "预期被强杀，实际正常退出" }

    # 2) recover 必须识别中间态并给出正确动作
    $recover = Invoke-DirPorter @("recover", "--json")
    $json = $recover.Output | ConvertFrom-Json
    $suggestion = $json.suggestions | Select-Object -First 1

    if (-not $suggestion) {
        Write-Fail "recover 未识别出中间态任务"
        continue
    }

    Write-Step "recover 结论：$($suggestion.action) — $($suggestion.summary)"
    if ($suggestion.action -eq $case.Expected) { Write-Pass "恢复动作符合预期（$($case.Expected)）" }
    else { Write-Fail "恢复动作不符：期望 $($case.Expected)，实际 $($suggestion.action)" }

    # 3) 按建议修复
    $repair = Invoke-RepairByAction -Action $suggestion.action -ParkingPath $suggestion.parkingPath
    Write-Step $repair

    # 4) 修复后：源数据必须完好（要么是真实目录，要么是链接且能看到同样多的文件）
    $inspect = Invoke-DirPorter @("inspect", "--path", $source, "--json")
    $info = $inspect.Output | ConvertFrom-Json
    $visible = Get-SourceFileCount

    if ($visible -eq 6) { Write-Pass "源侧文件数完好（6 个）" }
    else { Write-Fail "源侧文件数异常：$visible（期望 6）" }

    if ($info.isReparsePoint) { Write-Step "源路径当前是链接（$($info.kind) → $($info.targetAbsolute)）" }
    else { Write-Step "源路径当前是真实目录" }

    Write-Host ""
}

# ============================================================================
# 场景 5：完整生命周期 —— 迁移 → 列出记录 → 还原 → 校验
# ============================================================================
Write-Host "场景：完整生命周期（迁移 → list → restore）" -ForegroundColor White
Initialize-Scenario

$move = Invoke-DirPorter @("move", "--source", $source, "--target", $target, "--yes", "--quiet")
if ($move.Code -eq 0) { Write-Pass "迁移成功（退出码 0）" } else { Write-Fail "迁移失败：$($move.Output)" }

$inspect = Invoke-DirPorter @("inspect", "--path", $source, "--json")
$info = $inspect.Output | ConvertFrom-Json
if ($info.kind -eq "Junction" -and -not $info.isDangling) { Write-Pass "源路径已成为 Junction 且目标可达" }
else { Write-Fail "源路径链接状态异常：$($info.kind)" }

$list = Invoke-DirPorter @("list", "--json")
$records = $list.Output | ConvertFrom-Json
$record = $records | Where-Object { $_.status -eq "done" } | Select-Object -First 1
if ($record) { Write-Pass "迁移记录已写入（batchId=$($record.batchId)）" } else { Write-Fail "迁移记录缺失" }

$recover = Invoke-DirPorter @("recover", "--json")
$afterMove = ($recover.Output | ConvertFrom-Json).needsAttention
if ($afterMove -eq 0) { Write-Pass "迁移完成后 recover 无待处理项（无中间态残留）" }
else { Write-Fail "迁移完成后仍有 $afterMove 个中间态" }

$restore = Invoke-DirPorter @("restore", "--batch", $record.batchId)
if ($restore.Code -eq 0) { Write-Pass "还原成功（退出码 0）" } else { Write-Fail "还原失败：$($restore.Output)" }

$inspect2 = Invoke-DirPorter @("inspect", "--path", $source, "--json")
$info2 = $inspect2.Output | ConvertFrom-Json
if (-not $info2.isReparsePoint -and $info2.pathExists -and (Get-SourceFileCount) -eq 6) {
    Write-Pass "还原后源路径是真实目录且文件完整（6 个）"
} else {
    Write-Fail "还原后状态异常：isReparsePoint=$($info2.isReparsePoint), files=$(Get-SourceFileCount)"
}

if (Test-Path $target) { Write-Pass "目标侧数据默认保留（未被清理）" } else { Write-Step "目标侧已被清理" }

Write-Host ""
Write-Host "演练结果：PASS=$script:pass  FAIL=$script:fail" -ForegroundColor $(if ($script:fail -eq 0) { "Green" } else { "Red" })

if (-not $KeepArtifacts) {
    # 只删演练自己的临时目录；其中可能含 Junction，故用工具自己的安全删除器
    & $exe cleanresidue --target $srcRoot --include-root --yes | Out-Null
    & $exe cleanresidue --target $tgtRoot --include-root --yes | Out-Null
    Write-Host "现场已清理（加 -KeepArtifacts 可保留）" -ForegroundColor DarkGray
} else {
    Write-Host "现场保留在：$scratch" -ForegroundColor Yellow
}

if ($script:fail -gt 0) { exit 1 }
exit 0
