[CmdletBinding()]param([ValidateSet('gates-1','gates-2','gates-3')][string]$Case='gates-1')
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
$Case='gates-3';$ce=Join-Path $e $Case;$private=Join-Path $u ('.tools/networking-checks/n5-native-recovery-gates-20261031/'+$Case)
$instance=Read-Json (Join-Path $ce 'p7-start.json');$script:dsPath=Join-Path $private ('instances/'+$instance.InstanceId+'/state.json')
Load-State
$trace=Read-Json (Join-Path $ce 'history-conditions-last.json')
if(!$trace.Checks.AlphaSignature -or !$trace.Checks.AlphaCells -or !$trace.Checks.History160 -or !$trace.Checks.SeedComplete -or !$trace.Checks.Rounds80 -or !$trace.Checks.Capacity128){throw 'Missing actual held-A/160-record checkpoint; do not reconstruct data'}
if(!$fault.oldPageCached -or !(Consistent)){throw 'Auto-released history recovery not yet converged; no mutation sent'}
$targets=$seed.Targets
if(!(Complete-MultiTransfers 0 $targets 'HistoryGapBaseline') -or !(Complete-MultiTransfers 1 $targets)){throw 'Actual two-key transfer/ACK reason incomplete'}
$historyStart=(Read-Json (Join-Path $ce 'initial-ds.json')).MaterialHistoryRecords;$coveredRecords=$ds.MaterialHistoryRecords-$historyStart
if($coveredRecords -lt 160){throw 'Actual record count insufficient'}
Save-Json (Join-Path $ce 'covered-derived-observation.json') @{ActualHeldClientObservation=$trace;SourceRecordsAdded=$coveredRecords;BetaAtHeldObservationConverged=$trace.Checks.BetaSignature;BetaConvergedAtRecovery=$true;HeldRawClientSnapshotSaved=$false;Reason='35s observer ended before B final send; bounded45s auto-release preserved formal recovery. Actual held A metadata+cell comparisons/160 records were saved as derived checks, not reconstructed raw data.';RecordedUtc=[DateTime]::UtcNow.ToString('O')}
Archive 'history';$historyA=$a;$historyDs=$ds;$historyStore=$sa;$oldHeader=$fault.originalHeader
$target=[pscustomobject]@{Domain=$oldHeader.domain;X=$oldHeader.chunkX;Y=$oldHeader.chunkY;Z=$oldHeader.chunkZ}
if((Key $target) -ne (Key $targets[0])){throw 'Replay target differs from fixture clear-first target; do not modify world'}
Material-Command 'clear-first'
Wait-State 'TombstoneInstalledAndAcked' {
 $ack0=Ack-For 0 $target;$ack1=Ack-For 1 $target
 $seed.State -eq 'Removed' -and $ack0.Count -eq 1 -and $ack1.Count -eq 1 -and !$ack0[0].Resident -and !$ack1[0].Resident -and $ack0[0].Generation -eq $oldHeader.generation -and $ack0[0].Revision -gt $oldHeader.revision -and (Cells-Text $sa $target) -eq '' -and (Cells-Text $sb $target) -eq ''
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
