[CmdletBinding()]param([ValidateSet('gates-1','gates-2','gates-3','gates-4')][string]$Case='gates-1')
$ErrorActionPreference='Stop'
$t='C:\Users\zhy\Documents\Codex\2026-10-31\task';$u='F:\Develop Project\RPG\RPG'
$e=Join-Path $u 'docs/superpowers/evidence/multiplayer/n5/native-recovery-gates-2026-10-31';$ce=Join-Path $e $Case
$private=Join-Path $u ('.tools/networking-checks/n5-native-recovery-gates-20261031/'+$Case)
$h=Join-Path $t 'Invoke-N5RecoveryGatesNative.ps1'
function Read-Json($p){Get-Content -LiteralPath $p -Raw|ConvertFrom-Json}
function Save-Json($p,$v){[IO.File]::WriteAllText($p,($v|ConvertTo-Json -Depth 40),[Text.UTF8Encoding]::new($false))}
function Utc($x){if($x -is [DateTime]){return $x.ToUniversalTime()};[DateTime]::Parse($x,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()}
function Status($state,$detail){Save-Json (Join-Path $e 'native-progress.json') @{State=$state;Detail=$detail;Case=$Case;RecordedUtc=[DateTime]::UtcNow.ToString('O')}}
function Key($x){"$($x.Domain),$($x.X),$($x.Y),$($x.Z)"}
function Wire-Key($x){"$($x.domain),$($x.chunkX),$($x.chunkY),$($x.chunkZ)"}
function Chunk-Text($v){(@($v.MaterialChunks|Where-Object Resident|Sort-Object Domain,X,Y,Z|ForEach-Object {"$(Key $_):$($_.WorldEpoch),$($_.Generation),$($_.Revision),$($_.Checksum),$($_.Words),$($_.Resident)"}) -join '|')}
function Stamp-Text($x){"$(Key $x):$($x.WorldEpoch),$($x.Generation),$($x.BaseRevision),$($x.Revision),$($x.SnapshotId),$($x.ServerTick),$($x.SubscriptionEpoch),$($x.Checksum),$($x.Words),$($x.Resident)"}
function Cells-Text($v,$target=$null){(@($v.Cells|Where-Object {$null -eq $target -or ($_.Domain -eq $target.Domain -and $_.ChunkX -eq $target.X -and $_.ChunkY -eq $target.Y -and $_.ChunkZ -eq $target.Z)}|Sort-Object Domain,X,Y,Z,Material|ForEach-Object {"$($_.Domain),$($_.Storage),$($_.X),$($_.Y),$($_.Z),$($_.Material):$($_.Quantity)"}) -join '|')}
function Ack-For($slot,$target){@($script:ds.('MaterialPeerAck'+$slot)|Where-Object {(Key $_) -eq (Key $target)})}
function Exact-Acks($client,$slot){foreach($chunk in $client.MaterialChunks){$ack=Ack-For $slot $chunk;if($ack.Count -ne 1 -or (Stamp-Text $ack[0]) -ne (Stamp-Text $chunk) -or $ack[0].ConnectionEpoch -ne $client.ConnectionEpoch){return $false}};return @($client.MaterialChunks).Count -gt 0}
function Transfer-For($slot,$target){@($script:fault.latestTransfers|Where-Object {$_.known -and $_.slot -eq $slot -and (Wire-Key $_.header) -eq (Key $target)})}
function Complete-MultiTransfers($slot,$targets,$kind=$null){
 foreach($target in $targets){$rows=Transfer-For $slot $target;if($rows.Count -ne 1){return $false};$x=$rows[0];if($x.header.pageCount -le 4 -or $x.uniquePages -ne $x.header.pageCount -or ($null -ne $kind -and $x.freezeKind -ne $kind)){return $false};$ack=Ack-For $slot $target;if($ack.Count -ne 1 -or $ack[0].SnapshotId -ne $x.header.snapshotId -or $ack[0].Revision -ne $x.header.revision -or $ack[0].Checksum -ne $x.header.checksum -or $ack[0].ConnectionId -ne $x.connectionId){return $false}}
 return $true
}
function Request($p,$command){$stream=[IO.File]::Open($p,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None);try{$bytes=[Text.Encoding]::UTF8.GetBytes($command);$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()}}
function Network-Command($x){Request (Join-Path $private 'recovery-fault.json.request') $x}
function Material-Command($x){Request (Join-Path $private 'material-seed.json.extended.request') $x}
function Load-State {
 $script:ds=Read-Json $script:dsPath;$script:a=Read-Json (Join-Path $private 'p7-client0.report.json');$script:b=Read-Json (Join-Path $private 'p7-client1.report.json')
 $script:sa=Read-Json (Join-Path $private 'p7-client0.store.json');$script:sb=Read-Json (Join-Path $private 'p7-client1.store.json');$script:seed=Read-Json (Join-Path $private 'material-seed.json');$script:fault=Read-Json (Join-Path $private 'recovery-fault.json')
 if($ds.MaterialFaulted -or $a.MaterialFaulted -or $b.MaterialFaulted -or $ds.InterestProbeError -or $fault.state -eq 'Error' -or $seed.State -eq 'Error'){throw ('FAIL: '+$fault.error+' | '+$seed.Error)}
}
function Wait-State($label,[scriptblock]$predicate,$seconds=60){Status $label $Case;$end=[DateTime]::UtcNow.AddSeconds($seconds);do{try{Load-State;if(& $predicate){return}}catch{Save-Json (Join-Path $ce 'wait-last-error.json') @{Label=$label;Exception=$_.Exception.Message;Type=$_.Exception.GetType().FullName;RecordedUtc=[DateTime]::UtcNow.ToString('O')};if($_.Exception.Message -like 'FAIL:*'){throw}};if([DateTime]::UtcNow -gt $end){throw ('Native deadline: '+$label+'; preserve case and owned process identities')};Start-Sleep -Milliseconds 100}while($true)}
function Archive($label){foreach($name in @('ds','a','b','sa','sb','seed','fault')){Save-Json (Join-Path $ce ($label+'-'+$name+'.json')) (Get-Variable -Name $name -Scope Script -ValueOnly)}}
function Consistent {return (Chunk-Text $ds) -eq (Chunk-Text $a) -and (Chunk-Text $ds) -eq (Chunk-Text $b) -and (Exact-Acks $a 0) -and (Exact-Acks $b 1) -and (Cells-Text $sa) -eq (Cells-Text $sb)}
Status 'WaitingPrerequisites' 'Frozen source regression and matching v4 builds; no repeated global expiry test'
$end=[DateTime]::UtcNow.AddMinutes(35)
do{$ready=$false;try{$ready=(Read-Json (Join-Path $e 'build-progress.json')).State -eq 'Finished' -and (Read-Json (Join-Path $e 'regression-progress.json')).State -eq 'Finished'}catch{};if($ready){break};if([DateTime]::UtcNow -gt $end){throw 'Prerequisite timeout; no native started'};Start-Sleep -Milliseconds 750}while($true)
$own=Read-Json (Join-Path $e 'owned-database.json');$db=Read-Json (Join-Path $u '.tools/local-mysql/state.json');if($own.ProcessId -ne $db.ProcessId -or $own.StartTicks -ne $db.StartTicks -or $own.Executable -ne $db.Executable){throw 'Database ownership mismatch'}
if(Test-Path (Join-Path $private 'session.private.json')){throw 'Case exists; inspect uncertain outcome before any retry'}
if(@([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveUdpListeners()|Where-Object {$_.Port -ge 18400 -and $_.Port -le 18403}).Count){throw 'UDP range in use'}
New-Item -ItemType Directory -Path $private -Force|Out-Null
$config=@{FirstOffset=@{x=5;y=1;z=5};SecondOffset=@{x=9;y=1;z=5};AmountPerTarget=8192;Radius=.9;HistoryRounds=80;HistoryInterval=.25}
if($Case -ne 'gates-1'){$config.FirstOffset=@{x=-5;y=1;z=-3};$config.SecondOffset=@{x=-1;y=1;z=-3}}
Save-Json (Join-Path $private 'extended-config.json') $config
Status 'StartingNative' $Case
& $h -Action StartBusiness -Case $Case
& $h -Action CreateRoom -Case $Case -Dungeon p7
$instance=Read-Json (Join-Path $ce 'p7-start.json');$script:dsPath=Join-Path $private ('instances/'+$instance.InstanceId+'/state.json')
$end=[DateTime]::UtcNow.AddSeconds(55)
do{if(Test-Path $dsPath){$v=Read-Json $dsPath;if($v.Error){throw $v.Error};if($v.Ready){break}};if([DateTime]::UtcNow -gt $end){throw 'DS Ready timeout'};Start-Sleep -Milliseconds 500}while($true)
& $h -Action Connect -Case $Case -Dungeon p7
Wait-State 'AuthenticatedEmptyBaseline' {$a.Authenticated -and $b.Authenticated -and $a.WorldReady -and $b.WorldReady -and $seed.State -eq 'WaitingForSeed' -and (Consistent)}
Archive 'empty';$emptyA=$a;$emptyStore=$sa
Network-Command 'extended-hold-initial'
Wait-State 'InitialHeld' {$fault.extendedStage -eq 'InitialHeld'} 10
Material-Command 'seed'
Wait-State 'RealTwoChunkSeeded' {$seed.State -eq 'Seeded' -and $seed.TargetsReady -and $seed.Pending -eq 0 -and @($seed.Targets|Where-Object Cells -ge 90).Count -eq 2 -and (Chunk-Text $ds) -eq (Chunk-Text $b) -and (Exact-Acks $b 1) -and (Complete-MultiTransfers 1 $seed.Targets)} 35
Archive 'seeded';$targets=$seed.Targets
Network-Command 'extended-release-initial'
Wait-State 'BeforeFinalPageActualStore' {
 $script:partialTarget=[pscustomobject]@{Domain=$fault.initialHeader.domain;X=$fault.initialHeader.chunkX;Y=$fault.initialHeader.chunkY;Z=$fault.initialHeader.chunkZ}
 $fault.extendedStage -eq 'InitialLastPageHeld' -and $fault.initialHeader.pageCount -gt 4 -and $fault.initialUniqueSentPages -eq ($fault.initialHeader.pageCount-1) -and (Utc $a.RecordedAtUtc) -ge (Utc $fault.lastPageHoldUtc) -and (Utc $sa.RecordedUtc) -ge (Utc $fault.lastPageHoldUtc) -and @($a.MaterialChunks|Where-Object {(Key $_) -eq (Key $partialTarget)}).Count -eq 0 -and (Cells-Text $sa $partialTarget) -eq (Cells-Text $emptyStore $partialTarget) -and @(Ack-For 0 $partialTarget).Count -eq 0
 } 20
Archive 'partial'
Wait-State 'BothPeersAllMultiPageBaselinesAcked' {$fault.initialAcknowledged -and (Consistent) -and (Complete-MultiTransfers 0 $targets) -and (Complete-MultiTransfers 1 $targets)}
Archive 'initial';$initialDs=$ds;$initialA=$a;$initialStore=$sa
Network-Command 'extended-hold-history'
Wait-State 'HistoryHeld' {$fault.extendedStage -eq 'HistoryHeld'} 10
$historyStart=$ds.MaterialHistoryRecords
Material-Command 'history'
Wait-State 'ActualHistoryRingCovered' {
 $checks=@{SeedComplete=($seed.State -eq 'HistoryComplete');Rounds80=($seed.HistoryStep -eq 80);Capacity128=($ds.MaterialHistoryCapacity -eq 128);
  History160=(($ds.MaterialHistoryRecords-$historyStart) -ge 160);BetaSignature=((Chunk-Text $ds) -eq (Chunk-Text $b));BetaAck=(Exact-Acks $b 1);
  AlphaSignature=((Chunk-Text $a) -eq (Chunk-Text $initialA));AlphaCells=((Cells-Text $sa) -eq (Cells-Text $initialStore))}
 Save-Json (Join-Path $ce 'history-conditions-last.json') @{Checks=$checks;HistoryStart=$historyStart;HistoryNow=$ds.MaterialHistoryRecords;RecordedUtc=[DateTime]::UtcNow.ToString('O')}
 # 与原八个AND完全相同，只把逐项值留档；不降低记录数/数量/页面/一致性门槛。
 $checks.Values -notcontains $false
 } 44
Archive 'covered';$coveredRecords=$ds.MaterialHistoryRecords-$historyStart
Network-Command 'extended-release-history'
Wait-State 'HistoryGapBaselineRecovery' {$fault.oldPageCached -and (Consistent) -and (Complete-MultiTransfers 0 $targets 'HistoryGapBaseline') -and (Complete-MultiTransfers 1 $targets)}
Archive 'history';$historyA=$a;$historyDs=$ds;$historyStore=$sa;$oldHeader=$fault.originalHeader
$target=[pscustomobject]@{Domain=$oldHeader.domain;X=$oldHeader.chunkX;Y=$oldHeader.chunkY;Z=$oldHeader.chunkZ}
if((Key $target) -ne (Key $targets[0])){throw 'Replay target differs from fixture clear-first target; do not modify world'}
Material-Command 'clear-first'
Wait-State 'TombstoneInstalledAndAcked' {
 $ack0=Ack-For 0 $target;$ack1=Ack-For 1 $target
 $seed.State -eq 'Removed' -and $ack0.Count -eq 1 -and $ack1.Count -eq 1 -and !$ack0[0].Resident -and !$ack1[0].Resident -and $ack0[0].Generation -eq $oldHeader.generation -and $ack0[0].Revision -gt $oldHeader.revision -and (Cells-Text $sa $target) -eq '' -and (Cells-Text $sb $target) -eq '' -and (Consistent)
 }
Archive 'removed'
Material-Command 'rebuild-first'
Wait-State 'HigherGenerationRecovered' {
 $current=@($a.MaterialChunks|Where-Object {(Key $_) -eq (Key $target)})
 $seed.State -eq 'Rebuilt' -and $fault.newGenerationAcknowledged -and $current.Count -eq 1 -and $current[0].Generation -gt $oldHeader.generation -and $current[0].Revision -lt $oldHeader.revision -and (Consistent) -and (Complete-MultiTransfers 0 $targets) -and (Complete-MultiTransfers 1 $targets)
 }
Archive 'rebuilt';$rebuiltA=$a;$rebuiltStore=$sa;$rebuiltDs=$ds
Network-Command 'extended-replay-old'
Wait-State 'OldGenerationRejected' {$fault.state -eq 'Replayed' -and $fault.replayCount -eq 1 -and $a.MaterialLastFailure -eq 'StaleVersion' -and $a.MaterialRejectedCandidate.Generation -eq $oldHeader.generation -and $a.MaterialRejected -gt $rebuiltA.MaterialRejected -and (Chunk-Text $a) -eq (Chunk-Text $rebuiltA) -and (Cells-Text $sa) -eq (Cells-Text $rebuiltStore) -and $ds.MaterialResyncAccepted -eq $rebuiltDs.MaterialResyncAccepted}
Start-Sleep -Milliseconds 1200;Load-State
if($ds.MaterialResyncAccepted -ne $rebuiltDs.MaterialResyncAccepted -or $ds.MaterialPeerAcks[0] -ne $rebuiltDs.MaterialPeerAcks[0] -or $ds.MaterialPeerAcks[1] -ne $rebuiltDs.MaterialPeerAcks[1]){throw 'Old generation changed accepted recovery/ACK state'}
Archive 'replayed'
Save-Json (Join-Path $ce 'result.json') @{Passed=$true;Targets=$targets;HistoryRecordsAdded=$coveredRecords;HistoryCapacity=128;BothPeersMultipleKeysMultiPage=$true;BeforeFinalPageFreshActualStore=$true;SameWorldConnectionSubscription=$true;HigherGenerationLowerRevision=$true;OldGenerationReplayRejected=$true;KeyboardPath=$false;PerformanceMeasured=$false;RecordedUtc=[DateTime]::UtcNow.ToString('O')}
& $h -Action Collect -Case $Case
& $h -Action Cleanup -Case $Case
Status 'Passed' 'Real two-Key multi-page baseline, >128 changed-record history fallback, tombstone and higher-generation recovery; owned Native/business cleaned'
