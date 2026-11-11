$ErrorActionPreference='Stop'
$t='C:\Users\zhy\Documents\Codex\2026-10-31\task'
$u='F:\Develop Project\RPG\RPG'
$e=Join-Path $u 'docs/superpowers/evidence/multiplayer/n5/native-recovery-2026-10-31'
$h=Join-Path $t 'Invoke-N5RecoveryNative.ps1'
function Read-Json($path){Get-Content -LiteralPath $path -Raw|ConvertFrom-Json}
function Save-Json($path,$v){[IO.File]::WriteAllText($path,($v|ConvertTo-Json -Depth 35),[Text.UTF8Encoding]::new($false))}
function Status($state,$detail){Save-Json (Join-Path $e 'native-progress.json') @{State=$state;Detail=$detail;RecordedUtc=[DateTime]::UtcNow.ToString('O')}}
function Key($x){"$($x.Domain),$($x.X),$($x.Y),$($x.Z)"}
function Chunk-Text($v){(@($v.MaterialChunks|Sort-Object Domain,X,Y,Z|ForEach-Object {"$(Key $_):$($_.WorldEpoch),$($_.Generation),$($_.Revision),$($_.Checksum),$($_.Words),$($_.Resident)"}) -join '|')}
function Stamp-Text($x){"$(Key $x):$($x.WorldEpoch),$($x.Generation),$($x.BaseRevision),$($x.Revision),$($x.SnapshotId),$($x.ServerTick),$($x.SubscriptionEpoch),$($x.Checksum),$($x.Words),$($x.Resident)"}
function Cells-Text($v,$target=$null){(@($v.Cells|Where-Object {$null -eq $target -or ($_.Domain -eq $target.Domain -and $_.ChunkX -eq $target.X -and $_.ChunkY -eq $target.Y -and $_.ChunkZ -eq $target.Z)}|Sort-Object Domain,X,Y,Z,Material|ForEach-Object {"$($_.Domain),$($_.Storage),$($_.X),$($_.Y),$($_.Z),$($_.Material):$($_.Quantity)"}) -join '|')}
function Exact-Acks($ds,$client,$slot){
 $rows=$ds.('MaterialPeerAck'+$slot)
 foreach($chunk in $client.MaterialChunks){$ack=@($rows|Where-Object {(Key $_) -eq (Key $chunk)});if($ack.Count -ne 1 -or (Stamp-Text $ack[0]) -ne (Stamp-Text $chunk) -or $ack[0].ConnectionEpoch -ne $client.ConnectionEpoch){return $false}}
 return @($client.MaterialChunks).Count -gt 0
}
function Request($path,$command){$stream=[IO.File]::Open($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None);try{$bytes=[Text.Encoding]::UTF8.GetBytes($command);$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()}}
function Load-State {
 $script:ds=Read-Json $script:dsPath;$script:a=Read-Json (Join-Path $script:private 'p7-client0.report.json');$script:b=Read-Json (Join-Path $script:private 'p7-client1.report.json')
 $script:sa=Read-Json (Join-Path $script:private 'p7-client0.store.json');$script:sb=Read-Json (Join-Path $script:private 'p7-client1.store.json');$script:seed=Read-Json (Join-Path $script:private 'material-seed.json');$script:fault=Read-Json (Join-Path $script:private 'recovery-fault.json')
 if($ds.MaterialFaulted -or $a.MaterialFaulted -or $b.MaterialFaulted -or $ds.InterestProbeError -or $fault.state -eq 'Error'){throw ('FAIL: replication/diagnostic error: '+$fault.error)}
}
function Wait-State($label,[scriptblock]$predicate,$seconds=60){
 Status $label $script:case
 $end=[DateTime]::UtcNow.AddSeconds($seconds)
 do{try{Load-State;if(& $predicate){return}}catch{if($_.Exception.Message -like 'FAIL:*'){throw}};if([DateTime]::UtcNow -gt $end){throw ('Native wait timed out: '+$label+'; preserve owned processes/evidence')};Start-Sleep -Milliseconds 100}while($true)
}
function Archive($label){foreach($name in @('ds','a','b','sa','sb','seed','fault')){Save-Json (Join-Path $script:caseEvidence ($label+'-'+$name+'.json')) (Get-Variable -Name $name -Scope Script -ValueOnly)}}
Status 'WaitingPrerequisites' 'Matched v4 Client/DS and full regression/foundation; never reuse old executable'
$end=[DateTime]::UtcNow.AddMinutes(35)
do{
 $ready=$false
 try{$ready=(Read-Json (Join-Path $e 'build-progress.json')).State -eq 'Finished' -and (Read-Json (Join-Path $e 'foundation-progress.json')).State -eq 'Finished' -and (Read-Json (Join-Path $e 'regression-progress.json')).State -eq 'Finished'}catch{}
 if($ready){break};if([DateTime]::UtcNow -gt $end){throw 'Prerequisite timeout; no native process started'};Start-Sleep -Milliseconds 750
}while($true)
$own=Read-Json (Join-Path $e 'owned-database.json');$db=Read-Json (Join-Path $u '.tools/local-mysql/state.json')
if($own.ProcessId -ne $db.ProcessId -or $own.StartTicks -ne $db.StartTicks -or $own.Executable -ne $db.Executable){throw 'Database ownership mismatch'}
foreach($script:case in @('checksum','missing-base')){
 $script:private=Join-Path $u ('.tools/networking-checks/n5-native-recovery-20261031/'+$case)
 $script:caseEvidence=Join-Path $e $case
 if(@([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveUdpListeners()|Where-Object {$_.Port -ge 18390 -and $_.Port -le 18393}).Count -gt 0){throw 'Diagnostic UDP range in use; do not affect existing listener'}
 Status 'StartFreshCase' $case
 & $h -Action StartBusiness -Case $case
 & $h -Action CreateRoom -Case $case -Dungeon p7
 $instance=Read-Json (Join-Path $caseEvidence 'p7-start.json');$script:dsPath=Join-Path $private ('instances/'+$instance.InstanceId+'/state.json')
 $end=[DateTime]::UtcNow.AddSeconds(55)
 do{if(Test-Path $dsPath){$state=Read-Json $dsPath;if($state.Error){throw $state.Error};if($state.Ready){break}};if([DateTime]::UtcNow -gt $end){throw 'DS Ready timeout'};Start-Sleep -Milliseconds 500}while($true)
 & $h -Action Connect -Case $case -Dungeon p7
 Wait-State 'InstalledBaseline' {$seed.Phase -eq 1 -and $seed.PausedForComparison -and $a.Authenticated -and $b.Authenticated -and $a.WorldReady -and $b.WorldReady -and (Chunk-Text $ds) -eq (Chunk-Text $a) -and (Chunk-Text $ds) -eq (Chunk-Text $b) -and (Exact-Acks $ds $a 0) -and (Exact-Acks $ds $b 1) -and (Cells-Text $sa) -eq (Cells-Text $sb)}
 Archive 'before'
 $beforeA=$a;$beforeStore=$sa;$beforeDs=$ds
 Request (Join-Path $private 'recovery-fault.json.request') $case
 Wait-State 'Armed' {$fault.state -eq 'Armed'} 10
 Request (Join-Path $private 'material-seed.json.delta.request') ''
 $expected=if($case -eq 'checksum'){'Checksum'}else{'MissingRevision'}
 Wait-State 'RejectedPreservedStore' {$fault.state -eq 'Injected' -and $fault.completionReason -eq 'AssociatedResyncAccepted' -and $a.MaterialLastFailure -eq $expected -and $a.MaterialRejectedPayloadAvailable -and $a.MaterialResyncRequested -ge 1 -and $ds.MaterialResyncAccepted -ge 1 -and $ds.MaterialResyncActorSlot -eq 0 -and $a.MaterialRejectedCandidate.SnapshotId -eq $fault.originalHeader.snapshotId -and (Cells-Text $sa $a.MaterialRejectedCandidate) -eq (Cells-Text $beforeStore $a.MaterialRejectedCandidate)}
 Archive 'rejected'
 $rejected=$a.MaterialRejectedCandidate;$prior=$a.MaterialRejectedInstalled
 $old=@($beforeA.MaterialChunks|Where-Object {(Key $_) -eq (Key $rejected)})
 if($old.Count -ne 1 -or (Stamp-Text $old[0]) -ne (Stamp-Text $prior)){throw 'Preserved installed header differs from real installed baseline'}
 if($ds.MaterialLastAcceptedResync.ConnectionId -ne $fault.targetConnection -or $ds.MaterialLastAcceptedResync.SnapshotId -ne $fault.originalHeader.snapshotId -or $ds.MaterialLastAcceptedResync.BaseRevision -ne $fault.originalHeader.baseRevision){throw 'Accepted Resync did not identify original pending transfer'}
 Wait-State 'RecoveredBaselineAndExactPeerAck' {$seed.Phase -eq 2 -and $seed.PausedForComparison -and $fault.recoveryAcknowledged -and (Chunk-Text $ds) -eq (Chunk-Text $a) -and (Chunk-Text $ds) -eq (Chunk-Text $b) -and (Exact-Acks $ds $a 0) -and (Exact-Acks $ds $b 1) -and $a.MaterialBaselines -gt $beforeA.MaterialBaselines -and (Cells-Text $sa) -eq (Cells-Text $sb)}
 Archive 'recovered'
 $recoveredA=$a;$recoveredStore=$sa;$recoveredDs=$ds
 $current=@($a.MaterialChunks|Where-Object {(Key $_) -eq (Key $rejected)})
 if($current.Count -ne 1 -or $current[0].BaseRevision -ne 0){throw 'Target recovered without actual baseline'}
 if($current[0].Revision -lt $rejected.Revision){throw 'Recovered version regressed'}
 $replayFailure=if($current[0].Revision -gt $rejected.Revision){'StaleVersion'}else{'MissingRevision'}
 Request (Join-Path $private 'recovery-fault.json.request') 'replay'
 Wait-State 'OldPageRejectedWithoutRollback' {$fault.state -eq 'Replayed' -and $fault.replayCount -eq 1 -and $a.MaterialRejected -gt $recoveredA.MaterialRejected -and $a.MaterialLastFailure -eq $replayFailure -and $ds.MaterialResyncAccepted -eq $recoveredDs.MaterialResyncAccepted -and (Chunk-Text $a) -eq (Chunk-Text $recoveredA) -and (Cells-Text $sa) -eq (Cells-Text $recoveredStore) -and (Exact-Acks $ds $a 0)}
 # 等稀疏DS报告覆盖重放之后，再证明旧Resync没有被当作新恢复；仅等本任务有界1.2秒。
 Start-Sleep -Milliseconds 1200;Load-State
 if($ds.MaterialResyncAccepted -ne $recoveredDs.MaterialResyncAccepted -or $ds.MaterialPeerAcks[0] -ne $recoveredDs.MaterialPeerAcks[0] -or $ds.MaterialPeerAcks[1] -ne $recoveredDs.MaterialPeerAcks[1]){throw 'Old page/control changed accepted recovery or ACK state'}
 Archive 'replayed'
 Save-Json (Join-Path $caseEvidence 'result.json') @{Passed=$true;Mode=$case;InitialWorldTick=$beforeStore.WorldTick;FinalWorldTick=$sa.WorldTick;ExpectedFirstFailure=$expected;ExpectedReplayFailure=$replayFailure;Target=$rejected;RetainedInstalled=$prior;Recovered=$current[0];PerConnectionPerKeyAcks=$true;ActualStorePreserved=$true;OtherClientConsistent=$true;FiniteDiagnosticHoldSeconds=3;PausedCheckpoints=$true;KeyboardPlayerPath=$false;RecordedUtc=[DateTime]::UtcNow.ToString('O')}
 & $h -Action Collect -Case $case
 & $h -Action Cleanup -Case $case
 Status 'CasePassedAndOwnedCleanup' $case
}
Status 'Passed' 'Two real authenticated v4 Native recovery cases, exact target ACKs and actual Store samples; own database retained for final cleanup'
