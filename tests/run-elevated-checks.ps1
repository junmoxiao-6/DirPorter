# DirPorter R4 提权校验（必须"以管理员身份运行"）
#
# 为什么需要它：以下能力在**非提权**环境里物理上无法验证，只能如实降级：
#   1. robocopy /COPYALL 里的 U（审计 SACL）需要 SeSecurityPrivilege；
#   2. 复制"所有者/组"需要 SeRestorePrivilege **且该特权已启用**；
#   3. 符号链接（非开发者模式）需要 SeCreateSymbolicLinkPrivilege；
#   4. EFS 加密文件的复制/解密行为。
# 非提权时这些路径只能测到"降级分支"，测不到"成功分支"。
#
# 用法（在**管理员** PowerShell 中执行）：
#     cd <仓库根>
#     .\tests\run-elevated-checks.ps1
#
# 退出码：0=全部通过；5=未提权（未执行任何检查）；>0=失败项数。
# 输出（除屏幕外）会同时写入 tests\elevated-report-<时间戳>.txt，可直接回传。

param(
    [switch]$SkipEfs,
    [switch]$KeepArtifacts
)

$ErrorActionPreference = "Stop"

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "src\DirPorter.Cli\bin\Debug\net8.0-windows\dirporter.exe"
$reportPath = Join-Path $PSScriptRoot ("elevated-report-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".txt")

$script:pass = 0
$script:fail = 0
$script:log = New-Object System.Collections.Generic.List[string]

function Write-Line([string]$text, [string]$color = "Gray") {
    Write-Host $text -ForegroundColor $color
    $script:log.Add($text)
}
function Write-Step([string]$t) { Write-Line "    · $t" "DarkGray" }
function Write-Pass([string]$t) { $script:pass++; Write-Line "  [PASS] $t" "Green" }
function Write-Fail([string]$t) { $script:fail++; Write-Line "  [FAIL] $t" "Red" }
function Write-Info([string]$t) { Write-Line "  [INFO] $t" "Cyan" }
function Write-Check([string]$t) { Write-Host ""; Write-Host $t -ForegroundColor White; $script:log.Add(""); $script:log.Add($t) }

# ---------------------------------------------------------------- 提权判定
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
$isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

Write-Line ("=== DirPorter R4 提权校验 ===" ) "White"
Write-Line ("用户: " + $identity.Name + "    Elevated: " + $isAdmin)

if (-not $isAdmin) {
    Write-Line ""
    Write-Line "当前不是管理员，无法执行任何一项校验。" "Yellow"
    Write-Line "请用管理员身份重新运行：" "Yellow"
    Write-Line "    开始菜单 → 搜索 PowerShell → 右键 → 以管理员身份运行" "Yellow"
    Write-Line ("    cd '" + $root + "'") "Yellow"
    Write-Line "    .\tests\run-elevated-checks.ps1" "Yellow"
    $script:log | Set-Content -Path $reportPath -Encoding UTF8
    exit 5
}

# ---------------------------------------------------------------- 准备
if (-not (Test-Path $exe)) {
    Write-Line "未找到 CLI，正在编译…" "Yellow"
    & dotnet build (Join-Path $root "DirPorter.sln") --nologo -v q | Out-Null
}
if (-not (Test-Path $exe)) { throw "编译后仍未找到 $exe" }

$scratch = Join-Path $env:TEMP ("dirporter-elev-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
$src = Join-Path $scratch "src"
$dstB = Join-Path $scratch "dst-native"
$dstA = Join-Path $scratch "dst-robocopy"
$dstEfs = Join-Path $scratch "dst-efs"
New-Item -ItemType Directory -Force -Path $src | Out-Null

function Invoke-Cli([string[]]$Arguments) {
    # 局部把 ErrorActionPreference 降为 Continue：PowerShell 5.1 下原生命令写 stderr
    # 会被 Stop 策略转成终止性 RemoteException（Message 为空），导致脚本无故中断。
    $lines = & { $ErrorActionPreference = 'Continue'; & $exe @Arguments 2>&1 }
    return @{ Code = $LASTEXITCODE; Text = (($lines | ForEach-Object { $_.ToString() }) -join "`n") }
}

function Get-ExplicitAcl([string]$path) {
    # 只比较**非继承**的 ACE：目标侧一定还会带上来自目标父目录的继承 ACE，
    # 那部分是 Windows 继承机制的正常结果，不是复制保真度问题。
    $acl = Get-Acl -LiteralPath $path
    return ($acl.Access | Where-Object { -not $_.IsInherited } |
        ForEach-Object { "$($_.IdentityReference)|$($_.FileSystemRights)|$($_.AccessControlType)" } |
        Sort-Object) -join "; "
}

function Get-Owner([string]$path) { return (Get-Acl -LiteralPath $path).Owner }

# ================================================================ 1. 权限探测
Write-Check "[1/7] 权限探测（SeSecurity / SeRestore / 符号链接）"
$probe = Invoke-Cli @("copy", "--source", $src, "--target", (Join-Path $scratch "probe-dst"), "--engine", "native")
$capabilityLine = ($probe.Text -split "`n" | Where-Object { $_ -match "安全能力" })
if ($capabilityLine) { Write-Step $capabilityLine.Trim() }

if ($capabilityLine -match "所有者/组=可复制" -and $capabilityLine -match "审计SACL=可复制") {
    Write-Pass "SeRestorePrivilege 与 SeSecurityPrivilege 均已启用，安全信息可完整搬运"
} else {
    Write-Fail ("安全信息仍处降级状态：" + $capabilityLine)
}

# ================================================================ 2. robocopy /COPYALL
Write-Check "[2/7] robocopy /COPYALL（含审计 SACL）是否真正可用"
$rcSrc = Join-Path $scratch "rc-src"; $rcDst = Join-Path $scratch "rc-dst"
New-Item -ItemType Directory -Force -Path $rcSrc | Out-Null
Set-Content -Path (Join-Path $rcSrc "a.txt") -Value "alpha" -Encoding UTF8
$rcLog = Join-Path $scratch "rc.log"

$rcArgs = @($rcSrc, $rcDst, "/E", "/COPYALL", "/DCOPY:DAT", "/R:1", "/W:1", "/BYTES", "/FP", "/NC", "/NDL", "/NP", "/XJ", ("/LOG:" + $rcLog))
$rcProcess = Start-Process -FilePath "C:\Windows\System32\Robocopy.exe" -ArgumentList $rcArgs -Wait -PassThru -NoNewWindow
Write-Step ("robocopy /COPYALL 退出码 = " + $rcProcess.ExitCode)

if ($rcProcess.ExitCode -lt 8 -and (Test-Path (Join-Path $rcDst "a.txt"))) {
    Write-Pass "提权后 /COPYALL 可用（退出码 < 8 且文件已复制）"
} else {
    Write-Fail ("提权后 /COPYALL 仍不可用：退出码 " + $rcProcess.ExitCode + "（非提权时本项会返回 16）")
}

# ================================================================ 3. 引擎 A 计数
Write-Check "[3/7] 引擎 A（robocopy）报告的复制文件数是否真实"
$rcCount = Invoke-Cli @("copy", "--source", $rcSrc, "--target", (Join-Path $scratch "rc-dst2"), "--engine", "robocopy")
$line = ($rcCount.Text -split "`n" | Where-Object { $_ -match "已复制" })
Write-Step ($line -join " | ")
if ($line -match "已复制 1 个文件") { Write-Pass "引擎 A 计数正确（修复了 ANSI 日志误读导致的恒 0）" }
else { Write-Fail ("引擎 A 计数异常：" + ($line -join " | ")) }

# ================================================================ 4. 引擎 B 安全信息完整搬运
Write-Check "[4/7] 引擎 B 搬运 DACL / 所有者 / 审计 SACL（提权专属）"
$secSrc = Join-Path $src "secure.txt"
Set-Content -Path $secSrc -Value "protected-content" -Encoding UTF8

# 4a. 显式 DACL：给 Everyone 只读，并去掉继承（使显式 ACE 可被独立比较）
$acl = Get-Acl -LiteralPath $secSrc
$acl.SetAccessRuleProtection($true, $true)   # 断开继承、保留已继承的 ACE 为显式
$everyone = New-Object System.Security.Principal.SecurityIdentifier("S-1-1-0")
$rule = New-Object System.Security.AccessControl.FileSystemAccessRule($everyone, "Read", "Allow")
$acl.AddAccessRule($rule) | Out-Null
Set-Acl -LiteralPath $secSrc -AclObject $acl
Write-Step "已为源文件设置显式 ACE：Everyone:(R)"

# 4b. 所有者改成 Administrators（需要 SeRestorePrivilege，本身就是提权证据）
try {
    $ownerAcl = Get-Acl -LiteralPath $secSrc
    $ownerAcl.SetOwner((New-Object System.Security.Principal.NTAccount("BUILTIN\Administrators")))
    Set-Acl -LiteralPath $secSrc -AclObject $ownerAcl
    Write-Step "已把源文件所有者改为 BUILTIN\Administrators（成功 ⇒ SeRestorePrivilege 生效）"
} catch {
    Write-Step ("设置所有者失败：" + $_.Exception.Message)
}

# 4c. 审计 SACL：对 Everyone 的读取成功进行审计（需要 SeSecurityPrivilege）
try {
    $auditAcl = Get-Acl -LiteralPath $secSrc -Audit
    $auditRule = New-Object System.Security.AccessControl.FileSystemAuditRule($everyone, "ReadData", "Success")
    $auditAcl.AddAuditRule($auditRule) | Out-Null
    Set-Acl -LiteralPath $secSrc -AclObject $auditAcl
    Write-Step "已为源文件添加审计规则：Everyone / ReadData / Success"
} catch {
    Write-Step ("设置审计规则失败：" + $_.Exception.Message)
}

$copySec = Invoke-Cli @("copy", "--source", $src, "--target", $dstB, "--engine", "native")
($copySec.Text -split "`n") | Where-Object { $_ -match "安全|已复制|引擎" } | ForEach-Object { Write-Step $_.Trim() }

$targetSec = Join-Path $dstB "secure.txt"
if (Test-Path $targetSec) {
    $aclMatch = (Get-ExplicitAcl $secSrc) -eq (Get-ExplicitAcl $targetSec)
    if ($aclMatch) { Write-Pass "显式 DACL 完整搬运（源与目标一致）" }
    else {
        Write-Fail "显式 DACL 不一致"
        Write-Step ("源  : " + (Get-ExplicitAcl $secSrc))
        Write-Step ("目标: " + (Get-ExplicitAcl $targetSec))
    }

    $ownerMatch = (Get-Owner $secSrc) -eq (Get-Owner $targetSec)
    if ($ownerMatch) { Write-Pass ("所有者完整搬运：" + (Get-Owner $targetSec)) }
    else { Write-Fail ("所有者不一致：源 " + (Get-Owner $secSrc) + " / 目标 " + (Get-Owner $targetSec)) }

    try {
        $srcAudit = (Get-Acl -LiteralPath $secSrc -Audit).Audit.Count
        $tgtAudit = (Get-Acl -LiteralPath $targetSec -Audit).Audit.Count
        if ($tgtAudit -ge $srcAudit -and $srcAudit -gt 0) { Write-Pass ("审计 SACL 完整搬运（源 $srcAudit 条 / 目标 $tgtAudit 条）") }
        else { Write-Fail ("审计 SACL 未完整搬运：源 $srcAudit 条 / 目标 $tgtAudit 条") }
    } catch {
        Write-Fail ("读取审计 SACL 失败：" + $_.Exception.Message)
    }
} else {
    Write-Fail "目标侧没有 secure.txt，安全信息校验无法进行"
}

# ================================================================ 5. 符号链接
Write-Check "[5/7] 符号链接创建（SeCreateSymbolicLinkPrivilege）"
$linkTarget = Join-Path $scratch "link-target"
$linkPath = Join-Path $scratch "dir-symlink"
New-Item -ItemType Directory -Force -Path $linkTarget | Out-Null
Set-Content -Path (Join-Path $linkTarget "payload.txt") -Value "linked" -Encoding UTF8

try {
    New-Item -ItemType SymbolicLink -Path $linkPath -Target $linkTarget -ErrorAction Stop | Out-Null
    $item = Get-Item $linkPath -Force
    if ($item.LinkType) {
        Write-Pass ("目录符号链接创建成功（LinkType=" + $item.LinkType + "）")
        $inspect = Invoke-Cli @("inspect", "--path", $linkPath, "--json")
        if ($inspect.Text -match "DirectorySymlink") { Write-Pass "inspect 正确识别为目录符号链接" }
        else { Write-Step "inspect 输出：" + ($inspect.Text -replace "`n", " ") }
    } else {
        Write-Fail "创建后未呈现链接类型"
    }
} catch {
    Write-Fail ("符号链接创建失败：" + $_.Exception.Message)
}

# ================================================================ 6. Junction 仍正常
Write-Check "[6/7] 目录联接（Junction）—— 非提权也应可用，此处做交叉确认"
$junction = Join-Path $scratch "dir-junction"
$mk = & cmd.exe /c "mklink /J `"$junction`" `"$linkTarget`"" 2>&1
if (Test-Path $junction) {
    Write-Pass "mklink /J 创建成功并可用"
} else {
    Write-Fail ("mklink /J 创建失败：" + ($mk -join " "))
}

# ================================================================ 7. EFS
if (-not $SkipEfs) {
    Write-Check "[7/7] EFS 加密文件行为（记录型检查）"
    $efsDir = Join-Path $scratch "efs-src"
    New-Item -ItemType Directory -Force -Path $efsDir | Out-Null
    $efsFile = Join-Path $efsDir "secret.dat"
    Set-Content -Path $efsFile -Value ("top-secret-" + ("x" * 500)) -Encoding UTF8

    $cipherOut = & cipher.exe /e $efsFile 2>&1
    $isEncrypted = ((Get-Item $efsFile -Force).Attributes -band [IO.FileAttributes]::Encrypted) -ne 0

    if (-not $isEncrypted) {
        Write-Info ("无法在本机建立 EFS 加密文件（可能未启用 EFS），跳过。cipher: " + ($cipherOut -join " "))
    } else {
        Write-Step "已用 cipher /e 加密源文件"

        $deny = Invoke-Cli @("copy", "--source", $efsDir, "--target", $dstEfs, "--engine", "native")
        $efsTarget = Join-Path $dstEfs "secret.dat"
        if ($deny.Text -match "EFS") {
            Write-Pass "默认拒绝解密复制，并给出明确原因（不静默降级安全性）"
        } else {
            Write-Info "默认行为的输出：" + (($deny.Text -split "`n" | Where-Object { $_ -match "已复制|EFS|失败" }) -join " | ")
        }
    }
    & cipher.exe /d $efsFile 2>&1 | Out-Null
} else {
    Write-Check "[7/7] EFS 检查（已按要求跳过）"
}

# ================================================================ 收尾
Write-Host ""
Write-Host ("=" * 60) -ForegroundColor White
Write-Host ("结果：PASS " + $script:pass + " / FAIL " + $script:fail) -ForegroundColor $(if ($script:fail -eq 0) { "Green" } else { "Red" })
Write-Line ("结果：PASS " + $script:pass + " / FAIL " + $script:fail)

if (-not $KeepArtifacts) {
    Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
} else {
    Write-Host ("现场已保留：" + $scratch) -ForegroundColor DarkGray
}

Write-Host ("报告已写入：" + $reportPath) -ForegroundColor DarkGray
$script:log | Set-Content -Path $reportPath -Encoding UTF8

exit $script:fail
