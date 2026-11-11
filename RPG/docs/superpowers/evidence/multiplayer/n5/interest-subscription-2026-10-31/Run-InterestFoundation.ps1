$ErrorActionPreference='Stop'
$unity='F:\Develop Project\RPG\RPG'
$e=Join-Path $unity 'docs/superpowers/evidence/multiplayer/n5/interest-subscription-2026-10-31'
function Status($s,$d){@{State=$s;Detail=$d;RecordedUtc=[DateTime]::UtcNow.ToString('O')}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $e 'foundation-progress.json') -Encoding utf8}
$manifest=Get-Content -LiteralPath (Join-Path $unity 'Assets/_Project/Resources/Online/ContentManifest.json') -Raw|ConvertFrom-Json
if($manifest.ProtocolVersion -ne 4){throw 'ContentManifest not exported to protocol4; no service started'}
if(Test-Path -LiteralPath (Join-Path $unity '.tools/local-mysql/state.json')){throw 'Existing private database state; inspect ownership first'}
Status 'StartingOwnedDatabase' 'Existing private loopback MySQL, isolated test schema'
& (Join-Path $unity 'Tools/Networking/Start-LocalDatabase.ps1')
Copy-Item -LiteralPath (Join-Path $unity '.tools/local-mysql/state.json') -Destination (Join-Path $e 'owned-database.json')
$admin=Join-Path $unity '.tools/mysql/mysql-8.4.13-winx64/bin/mysqladmin.exe'
$end=[DateTime]::UtcNow.AddSeconds(25)
do{
    & $admin ('--defaults-extra-file='+(Join-Path $unity '.tools/local-mysql/control.private.ini')) ping --silent 2>$null|Out-Null
    if($LASTEXITCODE -eq 0){break};if([DateTime]::UtcNow -gt $end){throw 'Owned DB readiness failed'};Start-Sleep -Milliseconds 500
}while($true)
Status 'RunningFoundation' 'Pinned protocol check, Release business tests and real HTTP v4 acceptance/v1-v3 rejection'
& (Join-Path $unity 'Tools/Networking/Test-OnlineFoundation.ps1') *> (Join-Path $e 'foundation.log')
if($LASTEXITCODE -ne 0){throw 'Foundation command failed; inspect archived log'}
$report=Join-Path $unity 'Tools/Networking/.artifacts/foundation-check.json'
if(Test-Path $report){Copy-Item -LiteralPath $report -Destination (Join-Path $e 'foundation-check.json')}
Status 'Finished' 'Foundation completed; own database retained for same-stage Native, not shared with unrelated work'
