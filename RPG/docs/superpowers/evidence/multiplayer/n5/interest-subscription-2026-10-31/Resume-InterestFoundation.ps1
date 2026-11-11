$ErrorActionPreference='Stop'
$u='F:\Develop Project\RPG\RPG'
$e=Join-Path $u 'docs/superpowers/evidence/multiplayer/n5/interest-subscription-2026-10-31'
$own=Get-Content -LiteralPath (Join-Path $e 'owned-database.json') -Raw|ConvertFrom-Json
$state=Get-Content -LiteralPath (Join-Path $u '.tools/local-mysql/state.json') -Raw|ConvertFrom-Json
if($own.ProcessId -ne $state.ProcessId -or $own.StartTicks -ne $state.StartTicks -or $own.Executable -ne $state.Executable){throw 'Owned database changed; no test rerun'}
$env:NORN_N1_CONFIG=Join-Path $u '.tools/local-mysql/connections.private.json'
$env:NORN_CONTENT_MANIFEST=Join-Path $u 'Assets/_Project/Resources/Online/ContentManifest.json'
$cfg=Get-Content -LiteralPath $env:NORN_N1_CONFIG -Raw|ConvertFrom-Json
$env:ConnectionStrings__Online=$cfg.Test;$env:Online__MigrationConnectionString=$cfg.TestMigration
$env:Online__Enabled='true';$env:Online__AutoMigrate='true';$env:Online__ManifestPath=$env:NORN_CONTENT_MANIFEST
@{State='RunningWithExplicitPrivateConfig';RecordedUtc=[DateTime]::UtcNow.ToString('O')}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $e 'foundation-progress.json') -Encoding utf8
& (Join-Path $u 'Tools/Networking/Test-OnlineFoundation.ps1') *> (Join-Path $e 'foundation.log')
if($LASTEXITCODE -ne 0){throw 'Foundation failed; keep exact log'}
Copy-Item -LiteralPath (Join-Path $u 'Tools/Networking/.artifacts/foundation-report.json') -Destination (Join-Path $e 'foundation-report.json')
$v=Get-Content -LiteralPath (Join-Path $e 'foundation-report.json') -Raw|ConvertFrom-Json
if($v.Status -ne 'passed' -or $v.ProtocolVersion -ne 4){throw 'Unexpected HTTP foundation outcome'}
$trx=Get-ChildItem -LiteralPath 'F:\Develop Project\RPG\Server\TestResults' -Filter '*.trx' -File|Sort-Object LastWriteTimeUtc -Descending|Select-Object -First 1
Copy-Item -LiteralPath $trx.FullName -Destination (Join-Path $e 'business-final.trx')
@{State='Finished';ProtocolVersion=4;TestSchema='norn_online_test';RecordedUtc=[DateTime]::UtcNow.ToString('O')}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $e 'foundation-progress.json') -Encoding utf8
'Foundation and real HTTP version gates completed; own database retained for Native.'
