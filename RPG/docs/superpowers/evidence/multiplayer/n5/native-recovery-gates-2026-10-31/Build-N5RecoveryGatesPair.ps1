$ErrorActionPreference='Stop'
$u='F:\Develop Project\RPG\RPG'
$e=Join-Path $u 'docs/superpowers/evidence/multiplayer/n5/native-recovery-gates-2026-10-31'
function Read-Json($p){Get-Content -LiteralPath $p -Raw|ConvertFrom-Json}
function Utc($v){if($v -is [DateTime]){return $v.ToUniversalTime()};[DateTime]::Parse($v,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()}
function Status($s,$d){@{State=$s;Detail=$d;RecordedUtc=[DateTime]::UtcNow.ToString('O')}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $e 'build-progress.json') -Encoding utf8}
function Request($command){
 $p=Join-Path $u 'Temp/OnlineLab/command.request';if(Test-Path $p){throw 'Another Editor request exists'}
 $now=[DateTime]::UtcNow;$stream=[IO.File]::Open($p,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
 try{$b=[Text.Encoding]::UTF8.GetBytes($command);$stream.Write($b,0,$b.Length)}finally{$stream.Dispose()};return $now
}
function Wait-Command($command,$after){
 $end=[DateTime]::UtcNow.AddMinutes(20)
 do{$v=Read-Json (Join-Path $u 'Temp/OnlineLab/command-result.json');if($v.Command -eq $command -and (Utc $v.RecordedAtUtc) -gt $after){if(!$v.Passed){throw $v.Error};return $v}
 if([DateTime]::UtcNow -gt $end){throw 'Editor wait timeout; do not rerun uncertain operation'};Start-Sleep -Milliseconds 750}while($true)
}
Status 'WaitingRegression' 'No new build while official tests run'
$end=[DateTime]::UtcNow.AddMinutes(15)
while((Read-Json (Join-Path $e 'regression-progress.json')).State -ne 'Finished'){if([DateTime]::UtcNow -gt $end){throw 'Regression not finished'};Start-Sleep -Milliseconds 750}
Status 'HashRefresh' 'Profile and source/schema included; no World/NPC export'
$start=Request 'refresh-online-hashes';$v=Wait-Command 'refresh-online-hashes' $start
Copy-Item -LiteralPath (Join-Path $u 'Temp/OnlineLab/command-result.json') -Destination (Join-Path $e 'hash-refresh.json')
foreach($kind in @('client','server')){
 Status 'Building' ('build-room-'+$kind);$start=Request ('build-room-'+$kind);$v=Wait-Command ('build-room-'+$kind) $start
 Copy-Item -LiteralPath (Join-Path $u ('Tools/Networking/.artifacts/room-'+$kind+'-build.json')) -Destination (Join-Path $e ($kind+'-build.json'))
 Copy-Item -LiteralPath (Join-Path $u 'Temp/OnlineLab/command-result.json') -Destination (Join-Path $e ($kind+'-command.json'))
 Write-Output ($kind+' protocol4 Native build completed '+$v.RecordedAtUtc)
}
Status 'Finished' 'Matched protocol4 pair, no old executable reuse'
