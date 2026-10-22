<#
.SYNOPSIS
    给 dirporter.exe（以及安装包）做 Authenticode 代码签名。

.DESCRIPTION
    两种模式：
      A) 用现成证书（推荐，能真正消除"未知发布者"）：
           $env:DIRPORTER_PFX = 'C:\path\codesign.pfx'
           $env:DIRPORTER_PFX_PASSWORD = '...'
           .\tools\sign.ps1

      B) 自签名（没证书时的临时方案）：
           .\tools\sign.ps1 -SelfSigned
         会在"当前用户\个人"证书库创建一张代码签名证书并签名。
         ⚠️ 自签名证书只在本机受信任：别人机器上仍显示"未知发布者"，
            想彻底消除必须购买 OV/EV 代码签名证书（或用云签名服务）。

.PARAMETER SelfSigned
    创建/复用自签名证书并签名。

.PARAMETER Path
    要签名的文件，默认 dist\<版本>\dirporter.exe。
#>
[CmdletBinding()]
param(
    [switch]$SelfSigned,
    [string]$Path
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# ---- 定位 signtool ----
$signTool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe' -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName

if (-not $signTool) {
    throw '找不到 signtool.exe。请安装 Windows SDK（或 Visual Studio 的“Windows 10/11 SDK”组件）。'
}

# ---- 目标文件 ----
if (-not $Path) {
    $props = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
    if ($props -match '<Version>\s*([^<]+?)\s*</Version>') { $version = $Matches[1] } else { throw '无法读取版本号' }

    $Path = Join-Path $root "dist\$version\dirporter.exe"
}

if (-not (Test-Path $Path)) { throw "找不到要签名的文件：$Path" }

# ---- 证书 ----
if ($env:DIRPORTER_PFX) {
    if (-not (Test-Path $env:DIRPORTER_PFX)) { throw "DIRPORTER_PFX 指向的文件不存在：$($env:DIRPORTER_PFX)" }

    $args = @('sign', '/fd', 'SHA256', '/td', 'SHA256', '/tr', 'http://timestamp.digicert.com',
              '/f', $env:DIRPORTER_PFX)

    if ($env:DIRPORTER_PFX_PASSWORD) { $args += @('/p', $env:DIRPORTER_PFX_PASSWORD) }
    $args += $Path

    Write-Host "使用 PFX 证书签名…" -ForegroundColor Cyan
}
elseif ($SelfSigned) {
    $subject = 'CN=DirPorter Self-Signed'

    $cert = Get-ChildItem Cert:\CurrentUser\My |
            Where-Object { $_.Subject -eq $subject -and $_.HasPrivateKey } |
            Sort-Object NotAfter -Descending | Select-Object -First 1

    if (-not $cert) {
        Write-Host "创建自签名代码签名证书（$subject）…" -ForegroundColor Cyan
        $cert = New-SelfSignedCertificate `
            -Type CodeSigningCert `
            -Subject $subject `
            -CertStoreLocation 'Cert:\CurrentUser\My' `
            -KeyUsage DigitalSignature `
            -KeyLength 2048 `
            -NotAfter (Get-Date).AddYears(5)
    }

    Write-Host "使用自签名证书签名…" -ForegroundColor Cyan

    $args = @('sign', '/fd', 'SHA256', '/td', 'SHA256', '/tr', 'http://timestamp.digicert.com',
              '/sha1', $cert.Thumbprint, $Path)
}
else {
    Write-Host "未提供证书。用法：" -ForegroundColor Yellow
    Write-Host "  1) 有证书：`$env:DIRPORTER_PFX='codesign.pfx'; `$env:DIRPORTER_PFX_PASSWORD='...'; .\tools\sign.ps1"
    Write-Host "  2) 无证书：.\tools\sign.ps1 -SelfSigned"
    exit 1
}

& $signTool @args
if ($LASTEXITCODE -ne 0) { throw "签名失败（signtool 退出码 $LASTEXITCODE）" }

Write-Host ""
Write-Host "=== 签名结果 ===" -ForegroundColor Cyan
& $signTool verify /pa /v $Path
Write-Host ""
if ($SelfSigned) {
    Write-Host "注意：自签名证书只在本机受信任；其他机器仍会提示“未知发布者”。" -ForegroundColor Yellow
}
