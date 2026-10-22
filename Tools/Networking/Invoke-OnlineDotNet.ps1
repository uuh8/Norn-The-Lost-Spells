#requires -Version 7.2
[CmdletBinding()]
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$DotNetArguments)
. (Join-Path $PSScriptRoot 'Online.Common.ps1')
$layout = Get-OnlineLayout
$dotNet = Get-OnlineDotNet $layout
# global.json 从当前目录向上查找，因此必须在 Server 中调用 dotnet。
Push-Location -LiteralPath $layout.Server
try {
    & $dotNet @DotNetArguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet 执行失败，退出码 $LASTEXITCODE" }
} finally { Pop-Location }
