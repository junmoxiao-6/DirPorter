<#
.SYNOPSIS
    生成发布包与更新清单（latest.json）。

.DESCRIPTION
    在 publish.ps1 产出 dist\<版本>\dirporter.exe 之后运行，做三件事：
      1) 计算 exe 的 SHA256；
      2) 写 dist\<版本>\latest.json（version / url / sha256 / notes）；
      3) 打印需要上传的文件清单与目标路径。

    把 dist\<版本>\ 整个目录上传到更新源目录（默认 https://junmo.de5.net/dirporter/），
    使 <更新源根>/latest.json 与 <更新源根>/<版本>/dirporter.exe 都可匿名访问即可。

.PARAMETER BaseUrl
    更新源根地址。默认从 src\DirPorter.App\DirPorter.App.csproj 的 UpdateFeedUrl 推导。

.PARAMETER Notes
    更新说明（会出现在清单里）。

.EXAMPLE
    .\tools\make-release.ps1 -Notes "修复列宽拖动"
#>
[CmdletBinding()]
param(
    [string]$BaseUrl,
    [string]$Notes = ""
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# ---- 版本 ----
$props = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
if ($props -notmatch '<Version>\s*([^<]+?)\s*</Version>') { throw '无法从 Directory.Build.props 读取 <Version>' }
$version = $Matches[1]

$exe = Join-Path $root "dist\$version\dirporter.exe"
if (-not (Test-Path $exe)) {
    throw "找不到 $exe，请先运行 .\publish.ps1"
}

# ---- 更新源根地址 ----
if (-not $BaseUrl) {
    $csproj = Get-Content (Join-Path $root 'src\DirPorter.App\DirPorter.App.csproj') -Raw
    if ($csproj -match '<UpdateFeedUrl[^>]*>([^<]+)</UpdateFeedUrl>') {
        $BaseUrl = $Matches[1] -replace '/latest\.json$', ''
    }
}
if (-not $BaseUrl) { $BaseUrl = 'https://junmo.de5.net/dirporter' }
$BaseUrl = $BaseUrl.TrimEnd('/')

# ---- 摘要 ----
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item $exe).Length

# ---- GitHub 模式：不需要 latest.json（客户端直读 Releases API） ----
if ($BaseUrl -match '^github:') {
    $slug = $BaseUrl -replace '^github:', ''
    Write-Host ""
    Write-Host "=== 发布包就绪：$version （GitHub 模式）===" -ForegroundColor Cyan
    Write-Host ("exe       : {0}" -f $exe)
    Write-Host ("size      : {0:N0} 字节" -f $size)
    Write-Host ("sha256    : {0}" -f $hash)
    Write-Host ""
    Write-Host "下一步（网页操作，GitHub Desktop 不能建 Release）：" -ForegroundColor Yellow
    Write-Host "  1) 打开 https://github.com/$slug/releases/new"
    Write-Host "  2) tag 填 v$version ，标题填 $version"
    Write-Host "  3) 把上面的 exe 拖进 Attach binaries（文件名必须保持 dirporter.exe）"
    Write-Host "  4) Publish release"
    Write-Host ""
    Write-Host "客户端会读 api.github.com 的 releases/latest，不需要额外清单文件。" -ForegroundColor Gray
    return
}

$manifest = [ordered]@{
    version = $version
    url     = "$BaseUrl/$version/dirporter.exe"
    sha256  = $hash
    notes   = if ($Notes) { $Notes } else { "DirPorter $version" }
}

$jsonPath = Join-Path $root "dist\$version\latest.json"
$manifest | ConvertTo-Json -Depth 3 | Set-Content -Path $jsonPath -Encoding UTF8

# ---- 报告 ----
Write-Host ""
Write-Host "=== 发布包就绪：$version ===" -ForegroundColor Cyan
Write-Host ("exe       : {0}" -f $exe)
Write-Host ("size      : {0:N0} 字节" -f $size)
Write-Host ("sha256    : {0}" -f $hash)
Write-Host ("清单      : {0}" -f $jsonPath)
Write-Host ""
Write-Host "需要上传到 $BaseUrl 的内容：" -ForegroundColor Yellow
Write-Host ("  1) {0}  →  {1}/latest.json" -f $jsonPath, $BaseUrl)
Write-Host ("  2) {0}  →  {1}/{2}/dirporter.exe" -f $exe, $BaseUrl, $version)
Write-Host ""
Write-Host "上传后请用浏览器确认这两个地址能直接打开（无需登录）。" -ForegroundColor Gray
