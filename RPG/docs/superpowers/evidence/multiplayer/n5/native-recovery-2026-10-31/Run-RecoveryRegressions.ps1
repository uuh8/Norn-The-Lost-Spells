$ErrorActionPreference='Stop'
$u='F:\Develop Project\RPG\RPG'
$e=Join-Path $u 'docs/superpowers/evidence/multiplayer/n5/native-recovery-2026-10-31'
function Read-Json($p){Get-Content -LiteralPath $p -Raw|ConvertFrom-Json}
function Utc($x){if($x -is [DateTime]){return $x.ToUniversalTime()};[DateTime]::Parse($x,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()}
function Status($s){@{State=$s;RecordedUtc=[DateTime]::UtcNow.ToString('O')}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $e 'regression-progress.json') -Encoding utf8}
function Wait-Report($mode,$after){
 $end=[DateTime]::UtcNow.AddMinutes(12)
 do{$v=Read-Json (Join-Path $u ('Temp/SinglePlayer/'+$mode+'-report.json'))
 if((Utc $v.UpdatedUtc) -gt $after -and $v.State -in @('Finished','Blocked','Error')){if($v.State -ne 'Finished' -or $v.Failed -ne 0){throw ('Test failed: '+($v|ConvertTo-Json -Compress))};return $v}
 if([DateTime]::UtcNow -gt $end){throw 'Test wait timeout; preserve ongoing operation'};Start-Sleep -Milliseconds 750}while($true)
}
function Submit-Test($mode,$names){
 $p=Join-Path $u ('Temp/SinglePlayer/run-'+$mode+'.request.json');if(Test-Path $p){throw 'Another test request exists'}
 $now=[DateTime]::UtcNow;@{TestNames=@($names)}|ConvertTo-Json|Set-Content -LiteralPath $p -Encoding utf8;return $now
}
function Archive($mode,$name){Copy-Item -LiteralPath (Join-Path $u ('Temp/SinglePlayer/'+$mode+'-report.json')) -Destination (Join-Path $e ($name+'-report.json'));Copy-Item -LiteralPath (Join-Path $u ('Temp/SinglePlayer/'+$mode+'-results.xml')) -Destination (Join-Path $e ($name+'-results.xml'))}
Status 'RefreshFinalTests'
$req=Join-Path $u 'Temp/OnlineLab/command.request';if(Test-Path $req){throw 'Existing Editor request'}
$now=[DateTime]::UtcNow;[IO.File]::WriteAllText($req,'refresh-source',[Text.UTF8Encoding]::new($false))
$end=[DateTime]::UtcNow.AddMinutes(3)
do{$v=Read-Json (Join-Path $u 'Temp/OnlineLab/command-result.json');if($v.Command -eq 'refresh-source' -and (Utc $v.RecordedAtUtc) -gt $now){if(!$v.Passed){throw $v.Error};break};if([DateTime]::UtcNow -gt $end){throw 'Refresh wait timed out'};Start-Sleep -Milliseconds 750}while($true)
Status 'SupplementaryCoordinatorCases'
$now=Submit-Test 'editmode' @('Game.Multiplayer.Tests.WorldReplicationCoordinatorTests');$v=Wait-Report 'editmode' $now
if($v.Passed -ne 31){throw 'Unexpected Coordinator case count'};Archive 'editmode' 'coordinator-final'
Status 'FinalFull27Fixtures'
$all=Read-Json (Join-Path $e 'full-request.json');$now=Submit-Test 'editmode' $all.TestNames;$v=Wait-Report 'editmode' $now
if($v.Passed -ne 423){throw 'Unexpected final full count'};Archive 'editmode' 'full'
Status 'FormalP7P8PlayMode'
$now=Submit-Test 'playmode' @('Game.Run.Tests.FormalDungeonWorldPlayModeTests');$v=Wait-Report 'playmode' $now
if($v.Passed -ne 2){throw 'Unexpected formal PlayMode count'};Archive 'playmode' 'playmode'
Status 'Finished'
Write-Output 'Coordinator31/0, final27fixtures423/0, formalP7P8PlayMode2/0.'
