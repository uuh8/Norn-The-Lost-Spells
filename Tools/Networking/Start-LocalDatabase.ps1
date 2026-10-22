#requires -Version 7.2
$ErrorActionPreference='Stop'
$unityRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$directory=Join-Path $unityRoot '.tools/local-mysql'
$statePath=Join-Path $directory 'state.json'
$executable=Join-Path $unityRoot '.tools/mysql/mysql-8.4.13-winx64/bin/mysqld.exe'
if(Test-Path -LiteralPath $statePath){
    $record=Get-Content -LiteralPath $statePath -Raw|ConvertFrom-Json
    $existing=Get-Process -Id $record.ProcessId -ErrorAction SilentlyContinue
    if($existing){throw '实例记录指向仍存在的进程，请先检查/停止，不能重复启动。'}
}
if(@([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners()|Where-Object Port -eq 13384).Count -gt 0){throw '13384 仍有监听者，拒绝重复启动'}
$ini=Join-Path $directory 'my.ini'
if(!(Test-Path -LiteralPath $ini)){throw '缺少已初始化的 Norn 私有数据库，不能自动创建/覆盖既有数据目录。'}
# Windows 默认 fork 出 monitor；禁用它才能让记录的 PID 就是拥有数据目录的 Server。
$process=Start-Process -FilePath $executable -ArgumentList @('"--defaults-file='+$ini+'"','--no-monitor') -WindowStyle Hidden -PassThru
[pscustomobject]@{ProcessId=$process.Id;StartTicks=$process.StartTime.ToUniversalTime().Ticks;Executable=$executable}|ConvertTo-Json|Set-Content -LiteralPath $statePath
Write-Output '已请求启动 Norn MySQL；就绪状态需通过认证查询另行确认。'
