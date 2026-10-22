#requires -Version 7.2
[CmdletBinding()]
param([ValidatePattern('^[a-zA-Z0-9_-]{1,40}$')][string]$InstanceName = 'business')
. (Join-Path $PSScriptRoot 'Online.Common.ps1')
$layout = Get-OnlineLayout
$statePath = Join-Path $layout.Cache ('local-online/' + $InstanceName + '.json')
if (!(Test-Path -LiteralPath $statePath)) { Write-Host '没有该工具启动的实例记录。'; return }
$state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
$serviceProcess = Get-Process -Id $state.ProcessId -ErrorAction SilentlyContinue
if ($null -ne $serviceProcess) {
    # PID 会被系统复用；同时核对启动时间和可执行路径，避免误停其他 dotnet 项目。
    $sameStart = $serviceProcess.StartTime.ToUniversalTime().Ticks -eq [long]$state.StartTimeUtcTicks
    $samePath = [string]::Equals($serviceProcess.Path, $state.Executable, [StringComparison]::OrdinalIgnoreCase)
    if (!$sameStart -or !$samePath -or $state.Kind -ne 'Norn.Business') { throw '实例身份不匹配，保留记录并拒绝停止。' }
    Stop-Process -Id $serviceProcess.Id
    $serviceProcess.WaitForExit(5000) | Out-Null
}
Remove-Item -LiteralPath $statePath
Write-Host "已停止并清理本工具实例：$InstanceName；日志保留。"
