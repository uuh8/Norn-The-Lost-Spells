$ErrorActionPreference='Stop'
$u='F:\Develop Project\RPG\RPG'
$e=Join-Path $u 'docs/superpowers/evidence/multiplayer/n5/native-recovery-gates-2026-10-31'
function Utc($x){if($x -is [DateTime]){return $x.ToUniversalTime()};[DateTime]::Parse($x,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()}
function Read-Json($p){Get-Content -LiteralPath $p -Raw|ConvertFrom-Json}
function Status($s){@{State=$s;RecordedUtc=[DateTime]::UtcNow.ToString('O')}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $e 'gate-progress.json') -Encoding utf8}
Status 'RefreshForRed'
$request=Join-Path $u 'Temp/OnlineLab/command.request'
if(Test-Path $request){throw 'An Editor request already exists'}
$after=[DateTime]::UtcNow;[IO.File]::WriteAllText($request,'refresh-source',[Text.UTF8Encoding]::new($false))
$end=[DateTime]::UtcNow.AddMinutes(4)
do{$v=Read-Json (Join-Path $u 'Temp/OnlineLab/command-result.json');if($v.Command -eq 'refresh-source' -and (Utc $v.RecordedAtUtc) -gt $after){if(!$v.Passed){throw $v.Error};break};if([DateTime]::UtcNow -gt $end){throw 'Refresh timeout; preserve ongoing operation'};Start-Sleep -Milliseconds 750}while($true)
Status 'RunningTwoRedCases'
$request=Join-Path $u 'Temp/SinglePlayer/run-editmode.request.json';if(Test-Path $request){throw 'A test request already exists'}
$after=[DateTime]::UtcNow
@{TestNames=@('Game.Multiplayer.Tests.WorldReplicationCoordinatorTests.RecoveryProbeAcknowledgesHigherGenerationRevisionOneWithoutRequiringOldRevision','Game.Multiplayer.Tests.WorldReplicationCoordinatorTests.CoveredHistoryNaturallyFreezesCurrentMultiPageBaselinesForTwoNonOriginKeys','Game.Multiplayer.Tests.WorldReplicationCoordinatorTests.TombstoneAcknowledgementPrecedesNewGenerationBaselineAndOldGenerationCannotRefillStore')}|ConvertTo-Json|Set-Content -LiteralPath $request -Encoding utf8
Copy-Item -LiteralPath $request -Destination (Join-Path $e 'green-request.json')
$end=[DateTime]::UtcNow.AddMinutes(8)
do{$v=Read-Json (Join-Path $u 'Temp/SinglePlayer/editmode-report.json');if((Utc $v.UpdatedUtc) -gt $after -and $v.State -in @('Finished','Blocked','Error')){break};if([DateTime]::UtcNow -gt $end){throw 'Test timeout; preserve ongoing operation'};Start-Sleep -Milliseconds 750}while($true)
Copy-Item -LiteralPath (Join-Path $u 'Temp/SinglePlayer/editmode-report.json') -Destination (Join-Path $e 'green-report.json')
Copy-Item -LiteralPath (Join-Path $u 'Temp/SinglePlayer/editmode-results.xml') -Destination (Join-Path $e 'green-results.xml')
if($v.State -ne 'Finished' -or $v.Failed -ne 0 -or $v.Passed -ne 4){throw ('Unexpected RED: '+($v|ConvertTo-Json -Compress))}
Status 'GreenConfirmed'
Write-Output 'Recovery GREEN: 4 passed / 0 failed; artifacts archived.'
