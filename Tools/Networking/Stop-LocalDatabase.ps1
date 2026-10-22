#requires -Version 7.2
$ErrorActionPreference='Stop'
$unityRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$directory=Join-Path $unityRoot '.tools/local-mysql'
$statePath=Join-Path $directory 'state.json'
if(!(Test-Path -LiteralPath $statePath)){throw '没有本工具的数据库进程记录，拒绝按名称查杀。'}
$record=Get-Content -LiteralPath $statePath -Raw|ConvertFrom-Json
$process=Get-Process -Id $record.ProcessId -ErrorAction SilentlyContinue
if(!$process){Remove-Item -LiteralPath $statePath;return}
if($process.StartTime.ToUniversalTime().Ticks -ne $record.StartTicks -or $process.MainModule.FileName -ne $record.Executable){throw 'PID 身份已变化，拒绝停止。'}
# 通过专用 SHUTDOWN 账号让 InnoDB 正常 Flush；密码在私有 option file 中，不进入命令行。
$admin=Join-Path $unityRoot '.tools/mysql/mysql-8.4.13-winx64/bin/mysqladmin.exe'
& $admin ('--defaults-extra-file='+(Join-Path $directory 'control.private.ini')) shutdown
if($LASTEXITCODE -ne 0){throw '认证 shutdown 失败，未强杀数据库，请查看私有实例日志。'}
if(!$process.WaitForExit(15000)){throw 'MySQL 尚未正常退出，保留实例记录供检查。'}
Remove-Item -LiteralPath $statePath
Write-Output 'Norn MySQL 已正常关闭。'
