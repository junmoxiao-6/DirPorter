# DirPorter R5 扫描性能基准（可复现）
#
# 目的：把"10 万文件扫描 ≤30s、峰值内存 <300MB"从口号变成**可复现的实测数字**。
#
# 用法：
#   .\tests\bench-scan.ps1                          # 默认 2 万文件（快速）
#   .\tests\bench-scan.ps1 -Files 100000 -Dirs 400   # 正式验收（生成文件较慢，请耐心等待）
#   .\tests\bench-scan.ps1 -Keep                     # 保留现场目录
#
# 说明：
# - 生成阶段本身可能耗时数分钟（每个文件的创建都有固定开销），脚本会分别报出"生成耗时"与"扫描耗时"；
# - 峰值内存通过轮询目标进程的 PeakWorkingSet64 采样，是"进程曾达到的最高值"，不是瞬时值；
# - 条目数用系统自带 dir 命令对账，**不靠自证**。

param(
    [int]$Files = 20000,
    [int]$Dirs = 200,
    [double]$MaxSeconds = 30,
    [double]$MaxPeakMb = 300,
    [switch]$Keep
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "src\DirPorter.Cli\bin\Debug\net8.0-windows\dirporter.exe"
$reportPath = Join-Path $PSScriptRoot ("bench-scan-$Files-$(Get-Date -Format 'yyyyMMdd-HHmmss').txt")

$script:log = New-Object System.Collections.Generic.List[string]
function Say([string]$t, [string]$c = "Gray") { Write-Host $t -ForegroundColor $c; $script:log.Add($t) }

if (-not (Test-Path $exe)) {
    Say "未找到 CLI，正在编译…" "Yellow"
    & dotnet build (Join-Path $root "DirPorter.sln") --nologo -v q | Out-Null
}
if (-not (Test-Path $exe)) { throw "编译后仍未找到 $exe" }

$scratch = Join-Path $env:TEMP ("dirporter-bench-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
$dataRoot = Join-Path $scratch "data"

Say ""
Say "=== DirPorter 扫描性能基准 ===" "White"
Say ("目标规模：{0} 个文件 / {1} 个子目录   阈值：≤{2}s、峰值 <{3}MB" -f $Files, $Dirs, $MaxSeconds, $MaxPeakMb)

# ---------------------------------------------------------------- 生成
Say ""
Say "正在生成测试目录树…" "DarkGray"

$tree = Join-Path $scratch "tree"
for ($d = 0; $d -lt $Dirs; $d++) {
    $sub = Join-Path $tree ("d{0:d4}" -f $d)
    New-Item -ItemType Directory -Force -Path $sub | Out-Null
}

$bytes = New-Object byte[] 128
for ($i = 0; $i -lt 128; $i++) { $bytes[$i] = 65 + ($i % 26) }

$genSw = [Diagnostics.Stopwatch]::StartNew()
for ($i = 0; $i -lt $Files; $i++) {
    $sub = Join-Path $tree ("d{0:d4}" -f ($i % $Dirs))
    [IO.File]::WriteAllBytes((Join-Path $sub ("f{0:d6}.bin" -f $i)), $bytes)
}
$genSw.Stop()

Say ("生成完成：{0} 个文件，耗时 {1:0.0}s（{2:0.00} ms/文件）" -f $Files, $genSw.Elapsed.TotalSeconds, ($genSw.Elapsed.TotalMilliseconds / [Math]::Max(1, $Files)))

# ---------------------------------------------------------------- 扫描（并行）
function Invoke-Scan([string[]]$ExtraArgs) {
    $jsonPath = Join-Path $scratch ("scan-" + [Guid]::NewGuid().ToString("N").Substring(0, 6) + ".json")
    $cliArgs = @("scan", "--path", $tree, "--json") + $ExtraArgs

    $sw = [Diagnostics.Stopwatch]::StartNew()
    $proc = Start-Process -FilePath $exe -ArgumentList $cliArgs -PassThru -NoNewWindow -RedirectStandardOutput $jsonPath
    $peak = 0L
    while (-not $proc.HasExited) {
        try {
            $sample = (Get-Process -Id $proc.Id -ErrorAction Stop).PeakWorkingSet64
            if ($sample -gt $peak) { $peak = $sample }
        } catch { }
        Start-Sleep -Milliseconds 20
    }
    $proc.WaitForExit()
    $sw.Stop()

    $json = Get-Content $jsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
    return [pscustomobject]@{
        Seconds   = $sw.Elapsed.TotalSeconds
        PeakMb    = [Math]::Round($peak / 1MB, 1)
        Json      = $json
        ExitCode  = $proc.ExitCode
    }
}

Say ""
Say "扫描中（并行，默认线程数）…" "DarkGray"
$parallel = Invoke-Scan @()

Say ("  扫描耗时 {0:0.00}s   峰值内存 {1} MB   退出码 {2}" -f $parallel.Seconds, $parallel.PeakMb, $parallel.ExitCode)
Say ("  内部计时 {0} ms（不含进程启动与 JSON 输出）" -f $parallel.Json.elapsedMs)
Say ("  文件 {0} / 子目录 {1} / 字节 {2}" -f $parallel.Json.fileCount, $parallel.Json.directoryCount, $parallel.Json.logicalBytes)

Say ""
Say "扫描中（单线程 --threads 1，用于对比）…" "DarkGray"
$single = Invoke-Scan @("--threads", "1")
Say ("  扫描耗时 {0:0.00}s   峰值内存 {1} MB" -f $single.Seconds, $single.PeakMb)

# ---------------------------------------------------------------- 对账
Say ""
Say "与系统 dir 命令对账…" "DarkGray"
$dirFiles = @(cmd /c "dir /s /b /a-d `"$tree`"").Count
$dirDirs = @(cmd /c "dir /s /b /ad `"$tree`"").Count

$filesMatch = ($dirFiles -eq $parallel.Json.fileCount)
$dirsMatch = ($dirDirs -eq $parallel.Json.directoryCount)

Say ("  dir 报告：{0} 文件 / {1} 子目录" -f $dirFiles, $dirDirs)
Say ("  DirPorter：{0} 文件 / {1} 子目录" -f $parallel.Json.fileCount, $parallel.Json.directoryCount)

# ---------------------------------------------------------------- 结论
$script:fail = 0
function Check([string]$name, [bool]$ok, [string]$detail) {
    if ($ok) { Say ("  [PASS] " + $name + " — " + $detail) "Green" }
    else { $script:fail++; Say ("  [FAIL] " + $name + " — " + $detail) "Red" }
}

Say ""
Say "=== 结论 ===" "White"
Check "条目数对账（文件）" $filesMatch ("dir={0} vs DirPorter={1}" -f $dirFiles, $parallel.Json.fileCount)
Check "条目数对账（子目录）" $dirsMatch ("dir={0} vs DirPorter={1}" -f $dirDirs, $parallel.Json.directoryCount)
Check "无遍历错误" ($parallel.Json.errors.Count -eq 0) ("errors=" + $parallel.Json.errors.Count)
Check "未截断" (-not $parallel.Json.truncated) ("truncated=" + $parallel.Json.truncated)
Check "扫描耗时" ($parallel.Seconds -le $MaxSeconds) ("{0:0.00}s ≤ {1}s" -f $parallel.Seconds, $MaxSeconds)
Check "峰值内存" ($parallel.PeakMb -lt $MaxPeakMb) ("{0} MB < {1} MB" -f $parallel.PeakMb, $MaxPeakMb)

Say ""
Say ("并行/单线程 加速比：{0:0.00}×（{1:0.00}s → {2:0.00}s）" -f ($single.Seconds / [Math]::Max(0.001, $parallel.Seconds)), $single.Seconds, $parallel.Seconds) "DarkGray"
Say ("结果：PASS {0} / FAIL {1}" -f (6 - $script:fail), $script:fail) $(if ($script:fail -eq 0) { "Green" } else { "Red" })

# ⚠️ 报告先写盘、再清理现场：
#   10 万文件的递归删除可能耗时一两分钟，若把写报告放在后面，
#   中途被打断（Ctrl+C / 关窗口）就会连结果一起丢掉。
$script:log | Set-Content -Path $reportPath -Encoding UTF8
Say ("报告已写入：" + $reportPath) "DarkGray"

if ($Keep) {
    Say ("现场已保留：" + $scratch) "DarkGray"
} else {
    Say "正在清理现场（大目录树删除可能耗时一两分钟）…" "DarkGray"
    Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
}

exit $script:fail
