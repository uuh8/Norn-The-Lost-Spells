param([ValidateSet('red','green','regression')][string]$Mode='red')
$ErrorActionPreference='Stop';$u='F:\Develop Project\RPG\RPG';$e=Join-Path $u 'docs/superpowers/evidence/multiplayer/n5/native-player-reconnect-2026-10-31/resume-2026-11-01'
function Read($p){if(Test-Path $p){Get-Content -LiteralPath $p -Raw|ConvertFrom-Json}}
function Utc($v){if($v -is [DateTime]){return $v.ToUniversalTime()};[DateTime]::Parse($v,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()}
function Status($s){@{State=$s;Mode=$Mode;RecordedUtc=[DateTime]::UtcNow.ToString('O')}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $e ($Mode+'-progress.json')) -Encoding utf8}
function Request($p,$body){if(Test-Path $p){throw 'Existing request; inspect'};New-Item -ItemType Directory -Path (Split-Path $p) -Force|Out-Null;$stream=[IO.File]::Open($p,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None);try{$b=[Text.Encoding]::UTF8.GetBytes($body);$stream.Write($b,0,$b.Length)}finally{$stream.Dispose()}}
Status 'RefreshSource';$after=[DateTime]::UtcNow;Request (Join-Path $u 'Temp/OnlineLab/command.request') 'refresh-source';$end=$after.AddMinutes(5)
do{$v=Read (Join-Path $u 'Temp/OnlineLab/command-result.json');if($v -and $v.Command -eq 'refresh-source' -and (Utc $v.RecordedAtUtc) -gt $after){if(!$v.Passed){throw $v.Error};break};if([DateTime]::UtcNow -gt $end){throw 'Refresh result uncertain'};Start-Sleep -Milliseconds 750}while($true)
function RunTest($kind,$names,$label){
 Status ('Running-'+$label);$after=[DateTime]::UtcNow;Request (Join-Path $u ('Temp/SinglePlayer/run-'+$kind+'.request.json')) (@{TestNames=@($names)}|ConvertTo-Json);$end=$after.AddMinutes(10)
 do{$v=Read (Join-Path $u ('Temp/SinglePlayer/'+$kind+'-report.json'));if($v -and (Utc $v.UpdatedUtc) -gt $after -and $v.State -in @('Finished','Error','Blocked')){break};if([DateTime]::UtcNow -gt $end){throw 'Test wait uncertain; preserve request'};Start-Sleep -Milliseconds 750}while($true)
 foreach($ext in @('report.json','results.xml')){Copy-Item -LiteralPath (Join-Path $u ('Temp/SinglePlayer/'+$kind+'-'+$ext)) -Destination (Join-Path $e ($label+'-'+$ext))}
 Write-Host ($label+' '+$v.Passed+'/'+$v.Failed+' '+$v.UpdatedUtc);return $v
}
if($Mode -eq 'red'){$v=RunTest 'playmode' @('Game.Run.Tests.FormalDungeonWorldPlayModeTests.ExplicitReplicaSamplerSurvivesItsStartupSceneUnload') 'sampler-red';if($v.Passed -ne 0 -or $v.Failed -ne 1){throw 'Expected one actual lifecycle RED'}}
if($Mode -eq 'green'){$v=RunTest 'playmode' @('Game.Run.Tests.FormalDungeonWorldPlayModeTests') 'formal-and-sampler-green';if($v.Passed -ne 5 -or $v.Failed -ne 0){throw 'Expected unchanged P7/P8 plus three sampler cases'}}
if($Mode -eq 'regression'){
 $names=(Read (Join-Path $u 'docs/superpowers/evidence/multiplayer/n5/native-recovery-gates-2026-10-31/full-request.json')).TestNames;$v=RunTest 'editmode' $names 'n5-regression';if($v.Failed -ne 0 -or $v.Passed -ne 427){throw 'N5 regression failed'}
 $v=RunTest 'editmode' @('Game.Character.Tests.OnlineCastDriverTests') 'character-regression';if($v.Failed -ne 0 -or $v.Passed -ne 10){throw 'Character regression failed'}
}
Status 'Finished'
