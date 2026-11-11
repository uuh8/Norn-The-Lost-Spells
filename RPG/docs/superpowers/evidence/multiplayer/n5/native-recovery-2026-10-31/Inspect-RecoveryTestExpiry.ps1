$ErrorActionPreference='Stop'
$u='F:\Develop Project\RPG\RPG'
$cfg=Get-Content -LiteralPath (Join-Path $u '.tools/local-mysql/connections.private.json') -Raw|ConvertFrom-Json
$builder=[System.Data.Common.DbConnectionStringBuilder]::new();$builder.set_ConnectionString($cfg.Test)
function Field($pattern){$k=@($builder.get_Keys()|Where-Object {$_ -match $pattern});if($k.Count -ne 1){throw 'Unexpected test connection field shape'};[string]$builder[$k[0]]}
$server=Field '^(server|host)$';$port=Field '^port$';$schema=Field '^(database|initial catalog)$'
if($server -notin @('127.0.0.1','localhost') -or $port -ne '13384' -or $schema -ne 'norn_online_test'){throw 'Refuse non-task database'}
$user=Field '^(user ?id|uid|user ?name)$';$password=Field '^(password|pwd)$'
function Ini($value){if($value.Contains("`r") -or $value.Contains("`n")){throw 'Unexpected credential newline'};'"'+$value.Replace('\','\\').Replace('"','\"')+'"'}
$file=Join-Path ([IO.Path]::GetTempPath()) ('n5-recovery-read-'+[Guid]::NewGuid().ToString('N')+'.ini')
[IO.File]::WriteAllText($file,"[client]`nhost=127.0.0.1`nport=13384`nuser="+(Ini $user)+"`npassword="+(Ini $password)+"`ndatabase=norn_online_test`n",[Text.UTF8Encoding]::new($false))
try{
 $q="SELECT InstanceId,State,FailureCode,LeaseExpiresUtc FROM ServerInstances WHERE InstanceId='94a4ccb6c04740569ebcf5c3447b44dd'; SELECT COUNT(*) AS ActiveInstanceCount FROM ServerInstances WHERE ActivePort IS NOT NULL; SELECT InstanceId,State,FailureCode FROM ServerInstances WHERE RunId IN (SELECT RunId FROM RunLifecycleEvents WHERE EventCode='LeaseExpired' AND RecordedAtUtc BETWEEN '2026-10-31 14:47:00' AND '2026-10-31 14:51:00');"
 $v=& (Join-Path $u '.tools/mysql/mysql-8.4.13-winx64/bin/mysql.exe') ('--defaults-extra-file='+$file) '--batch' '--execute' $q
 if($LASTEXITCODE -ne 0){throw 'Read-only test-schema inspection failed'}
 $v|Set-Content -LiteralPath (Join-Path $u 'docs/superpowers/evidence/multiplayer/n5/native-recovery-2026-10-31/business-expiry-inspection.txt') -Encoding utf8
 $v
}finally{Remove-Item -LiteralPath $file}
