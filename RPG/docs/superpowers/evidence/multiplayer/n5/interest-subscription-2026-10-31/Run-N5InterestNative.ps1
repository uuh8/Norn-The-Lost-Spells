$ErrorActionPreference='Stop'
$t='C:\Users\zhy\Documents\Codex\2026-10-31\task'
$u='F:\Develop Project\RPG\RPG'
$e=Join-Path $u 'docs/superpowers/evidence/multiplayer/n5/interest-subscription-2026-10-31'
$private=Join-Path $u '.tools/networking-checks/n5-interest-subscription-20261031'
$h=Join-Path $t 'Invoke-N5InterestNative.ps1'
function Read-Json($p){Get-Content -LiteralPath $p -Raw|ConvertFrom-Json}
function Status($s,$d){@{State=$s;Detail=$d;RecordedUtc=[DateTime]::UtcNow.ToString('O')}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $e 'native-progress.json') -Encoding utf8}
function Chunk-Text($v){(@($v.MaterialChunks|Sort-Object Domain,X,Y,Z|ForEach-Object {"$($_.Domain),$($_.X),$($_.Y),$($_.Z):$($_.WorldEpoch),$($_.Generation),$($_.Revision),$($_.Checksum),$($_.Words),$($_.Resident)"}) -join '|')}
function Command($text){$path=Join-Path $private 'interest-position.request';if(Test-Path $path){throw 'Previous pose request pending'};[IO.File]::WriteAllText($path,$text,[Text.UTF8Encoding]::new($false))}
Status 'WaitingMatchedBuilds' 'No old protocol3 executable or unverified business assembly'
$end=[DateTime]::UtcNow.AddMinutes(35)
while((!(Test-Path (Join-Path $e 'build-progress.json'))) -or (Read-Json (Join-Path $e 'build-progress.json')).State -ne 'Finished' -or (Read-Json (Join-Path $e 'foundation-progress.json')).State -ne 'Finished'){
 if([DateTime]::UtcNow -gt $end){throw 'Prerequisite not finished; no native started'};Start-Sleep -Milliseconds 750
}
$own=Read-Json (Join-Path $e 'owned-database.json');$state=Read-Json (Join-Path $u '.tools/local-mysql/state.json')
if($own.ProcessId -ne $state.ProcessId -or $own.StartTicks -ne $state.StartTicks -or $own.Executable -ne $state.Executable){throw 'Database ownership mismatch'}
Status 'StartingOwnBusiness' 'Same-stage isolated test schema; fresh real accounts/Ready/Ticket'
& $h -Action StartBusiness
& $h -Action CreateRoom -Dungeon p7
$instance=Read-Json (Join-Path $e 'p7-start.json');$dsPath=Join-Path $private ('instances/'+$instance.InstanceId+'/state.json')
$end=[DateTime]::UtcNow.AddSeconds(50)
do{if(Test-Path $dsPath){$v=Read-Json $dsPath;if($v.Error){throw $v.Error};if($v.Ready){break}};if([DateTime]::UtcNow -gt $end){throw 'DS Ready timed out'};Start-Sleep -Milliseconds 500}while($true)
& $h -Action Connect -Dungeon p7
$firstAck=0;$homeAck=0;$bInitialAck=0
foreach($phase in @(1,2,3,4,5)){
 if($phase -eq 4){
  # 必须先证实真实Actor已离开且客户端缓存已空，再允许世界变化，避免两个私有请求的轮询次序竞争。
  Status 'AwayBeforeWorldChange' 'Wait for actual Leave/cache eviction before second material write'
  $end=[DateTime]::UtcNow.AddSeconds(50)
  do{
   $clear=$false
   try{$ds=Read-Json $dsPath;$a=Read-Json (Join-Path $private 'p7-client0.report.json');$b=Read-Json (Join-Path $private 'p7-client1.report.json');$seed=Read-Json (Join-Path $private 'material-seed.json')
    $clear=$ds.InterestProbePose -eq 'away' -and $seed.Phase -eq 1 -and $null -ne $a.MaterialChunks -and $a.MaterialChunks.Count -eq 0 -and (Chunk-Text $ds) -eq (Chunk-Text $b)
   }catch{}
   if($clear){break};if([DateTime]::UtcNow -gt $end){throw 'Second departure not confirmed; no material write issued'};Start-Sleep -Milliseconds 500
  }while($true)
  Copy-Item -LiteralPath $dsPath -Destination (Join-Path $e 'away-before-write-ds.json')
  Copy-Item -LiteralPath (Join-Path $private 'p7-client0.report.json') -Destination (Join-Path $e 'away-before-write-client0.json')
  Copy-Item -LiteralPath (Join-Path $private 'material-seed.json') -Destination (Join-Path $e 'away-before-write-seed.json')
  $request=Join-Path $private 'material-seed.json.delta.request';$stream=[IO.File]::Open($request,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None);$stream.Dispose()
 }
 Status ('Checkpoint'+$phase) 'Real authenticated DS Actor pose, formal subscription channel; diagnostic pose is not keyboard player path'
 $end=[DateTime]::UtcNow.AddSeconds(50)
 do{
  $ready=$false
  try{
   $ds=Read-Json $dsPath;$seed=Read-Json (Join-Path $private 'material-seed.json')
   $a=Read-Json (Join-Path $private 'p7-client0.report.json');$b=Read-Json (Join-Path $private 'p7-client1.report.json')
   if($ds.MaterialFaulted -or $a.MaterialFaulted -or $b.MaterialFaulted -or $ds.InterestProbeError){throw 'Diagnostic or replication fault: preserve native evidence'}
   $source=Chunk-Text $ds
   $common=$ds.Ready -and $ds.WorldReady -and $a.Authenticated -and $b.Authenticated -and $a.WorldReady -and $b.WorldReady -and $ds.MaterialChunks.Count -gt 0 -and $source -eq (Chunk-Text $b)
   if($phase -in @(1,3,5)){
    $ready=$common -and $ds.InterestProbePose -eq 'home' -and $source -eq (Chunk-Text $a) -and $seed.PausedForComparison
    if($phase -eq 1){$ready=$ready -and $ds.MaterialPeerAcks[0] -ge $ds.MaterialChunks.Count -and $ds.MaterialPeerAcks[1] -ge $ds.MaterialChunks.Count}
    if($phase -eq 3){$ready=$ready -and $seed.Phase -eq 1 -and $ds.MaterialPeerAcks[0] -ge ($firstAck+$ds.MaterialChunks.Count) -and @($a.MaterialChunks|Where-Object SubscriptionEpoch -lt 2).Count -eq 0}
    if($phase -eq 5){$ready=$ready -and $seed.Phase -eq 2 -and $ds.MaterialPeerAcks[0] -ge ($homeAck+$ds.MaterialChunks.Count)}
   }else{
    $ready=$common -and $ds.InterestProbePose -eq 'away' -and $null -ne $a.MaterialChunks -and $a.MaterialChunks.Count -eq 0
    if($phase -eq 4){$ready=$ready -and $seed.Phase -eq 2 -and $seed.PausedForComparison -and $ds.MaterialPeerAcks[1] -gt $bInitialAck}
   }
  }catch{if($_.Exception.Message -like '*Diagnostic or replication fault*'){throw}}
  if($ready){break};if([DateTime]::UtcNow -gt $end){throw ('Native checkpoint '+$phase+' did not converge; keep processes/logs')};Start-Sleep -Milliseconds 500
 }while($true)
 Copy-Item -LiteralPath $dsPath -Destination (Join-Path $e ('checkpoint'+$phase+'-ds.json'))
 Copy-Item -LiteralPath (Join-Path $private 'material-seed.json') -Destination (Join-Path $e ('checkpoint'+$phase+'-seed.json'))
 foreach($i in @(0,1)){Copy-Item -LiteralPath (Join-Path $private ('p7-client'+$i+'.report.json')) -Destination (Join-Path $e ('checkpoint'+$phase+'-client'+$i+'.json'))}
 switch($phase){
  1{$firstAck=$ds.MaterialPeerAcks[0];$bInitialAck=$ds.MaterialPeerAcks[1];Command 'away'}
  2{Command 'home'}
  3{$homeAck=$ds.MaterialPeerAcks[0];Command 'away'}
  4{Command 'home'}
 }
}
& $h -Action Collect
& python (Join-Path $t 'check-interest-native.py')
if($LASTEXITCODE -ne 0){throw 'Native interest consistency failed; keep evidence and processes'}
Status 'Passed' 'Same-version reentry and changed-world reentry; A evicts, B/DS retain; per-peer ACKs verified; awaiting owned cleanup'
