#requires -Version 7.2
[CmdletBinding()]
param(
    [ValidateRange(1024,65535)][int]$Port = 5080,
    [ValidatePattern('^[a-zA-Z0-9_-]{1,40}$')][string]$InstanceName = 'business',
    [switch]$NoBuild
)
. (Join-Path $PSScriptRoot 'Online.Common.ps1')
$layout = Get-OnlineLayout
$dotNet = Get-OnlineDotNet $layout
$stateRoot = Join-Path $layout.Cache 'local-online'
New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
$statePath = Join-Path $stateRoot ($InstanceName + '.json')
if (Test-Path -LiteralPath $statePath) { throw "实例记录已存在，请先核查或停止该实例：$statePath" }

# 先检查端口，不能为了启动 Demo 杀掉用户的其他服务。
$portProbe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
try { $portProbe.Start() } finally { $portProbe.Stop() }
if (!$NoBuild) {
    & (Join-Path $PSScriptRoot 'Invoke-OnlineDotNet.ps1') -DotNetArguments @('build', 'Norn.Online.sln', '--configuration', 'Release')
}
$application = Join-Path $layout.Server 'Norn.Business/bin/Release/net10.0/Norn.Business.dll'
if (!(Test-Path -LiteralPath $application)) { throw '缺少 Release 构建，请先构建解决方案。' }
$serviceUrl = "http://127.0.0.1:$Port"
$stdoutPath = Join-Path $stateRoot ($InstanceName + '.stdout.log')
$stderrPath = Join-Path $stateRoot ($InstanceName + '.stderr.log')
# 启动 dotnet + DLL 而非 dotnet run，避免额外父进程导致停止时遗漏真实服务。
$serviceProcess = Start-Process -FilePath $dotNet -ArgumentList @(('"' + $application + '"'), '--urls', $serviceUrl) -WorkingDirectory (Join-Path $layout.Server 'Norn.Business') -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
$state = [PSCustomObject]@{
    ProcessId = $serviceProcess.Id
    StartTimeUtcTicks = $serviceProcess.StartTime.ToUniversalTime().Ticks
    Executable = $dotNet
    Url = $serviceUrl
    Kind = 'Norn.Business'
}
$state | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding utf8
try {
    $ready = $false
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        $serviceProcess.Refresh()
        if ($serviceProcess.HasExited) { throw "业务服提前退出，请查看 $stderrPath" }
        try {
            $health = Invoke-WebRequest -Uri ($serviceUrl + '/health/live') -TimeoutSec 1
            if ($health.StatusCode -eq 200 -and $health.Content -eq 'alive') { $ready = $true; break }
        } catch { Start-Sleep -Milliseconds 100 }
    }
    if (!$ready) { throw '业务服未在启动窗口内完成 liveness 验证。' }
    Write-Host "业务服已启动：$serviceUrl；PID=$($serviceProcess.Id)；记录=$statePath"
} catch {
    # 此处句柄就是本次创建的子进程，失败时仅清理它及其记录。
    if (!$serviceProcess.HasExited) { Stop-Process -Id $serviceProcess.Id }
    Remove-Item -LiteralPath $statePath
    throw
}
