# 运行 DirPorter 单元测试
# 用法：在 PowerShell 中执行  .\tests\run-unit-tests.ps1
#       只跑链接模块：           .\tests\run-unit-tests.ps1 -Filter Links
<#
   说明：本文件以 UTF-8 BOM 保存。Windows PowerShell 5.1 对无 BOM 的 .ps1 按系统 ANSI 解码，
   会把脚本里的中文解析成乱码（甚至产生语法错误），这是本仓库脚本一律带 BOM 的原因。
#>

param(
    [string]$Filter = "",
    [switch]$Detailed
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$testProject = Join-Path $root "src\DirPorter.Tests\DirPorter.Tests.csproj"

if (-not (Test-Path $testProject)) {
    throw "找不到测试项目：$testProject"
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

$arguments = @("test", $testProject, "--nologo", "-nodeReuse:false")

switch ($Filter) {
    ""      { }
    "Links" { $arguments += @("--filter", "FullyQualifiedName~LinkProviderTests|FullyQualifiedName~LinkServiceTests|FullyQualifiedName~ReparseDataTests|FullyQualifiedName~SymlinkLinkProviderTests|FullyQualifiedName~JunctionLinkProviderTests|FullyQualifiedName~HardLinkProviderTests|FullyQualifiedName~SystemToolParityTests") }
    default { $arguments += @("--filter", "FullyQualifiedName~$Filter") }
}

if ($Detailed) { $arguments += @("--logger", "console;verbosity=detailed") }

Write-Host "执行：dotnet $($arguments -join ' ')" -ForegroundColor Cyan
& dotnet @arguments
exit $LASTEXITCODE
