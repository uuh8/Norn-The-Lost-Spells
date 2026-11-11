param([ValidateSet('core-red','core-green-and-hooks-red','probe-red','green','regression','review-fix')][string]$Mode='core-red')
$ErrorActionPreference='Stop';$u='F:\Develop Project\RPG\RPG'
$e=Join-Path $u 'docs/superpowers/evidence/multiplayer/n5/fire-visible-latency-2026-11-01/instrumented-2026-11-01'
New-Item -ItemType Directory -Path $e -Force|Out-Null
function Read($p){if(Test-Path -LiteralPath $p){Get-Content -LiteralPath $p -Raw|ConvertFrom-Json}}
function Utc($v){if($v -is [DateTime]){return $v.ToUniversalTime()};[DateTime]::Parse($v,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()}
function Request($p,$body){if(Test-Path -LiteralPath $p){throw "Existing request; do not overwrite $p"};$stream=[IO.File]::Open($p,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None);try{$b=[Text.Encoding]::UTF8.GetBytes($body);$stream.Write($b,0,$b.Length)}finally{$stream.Dispose()}}
function Status($state){@{Mode=$Mode;State=$state;RecordedUtc=[DateTime]::UtcNow.ToString('O')}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $e ($Mode+'-progress.json')) -Encoding utf8}
Status 'RefreshSource';$after=[DateTime]::UtcNow;Request (Join-Path $u 'Temp/OnlineLab/command.request') 'refresh-source';$end=$after.AddMinutes(5)
do{$v=Read (Join-Path $u 'Temp/OnlineLab/command-result.json');if($v -and $v.Command -eq 'refresh-source' -and (Utc $v.RecordedAtUtc) -gt $after){if(!$v.Passed){throw $v.Error};break};if([DateTime]::UtcNow -gt $end){throw 'Refresh pending/uncertain; inspect instead of repeat'};Start-Sleep -Milliseconds 500}while($true)
function Test($kind,$names,$label){Status ('Running-'+$label);$after=[DateTime]::UtcNow;Request (Join-Path $u ('Temp/SinglePlayer/run-'+$kind+'.request.json')) (@{TestNames=@($names)}|ConvertTo-Json);$end=$after.AddMinutes(10)
 do{$v=Read (Join-Path $u ('Temp/SinglePlayer/'+$kind+'-report.json'));if($v -and (Utc $v.UpdatedUtc) -gt $after -and $v.State -in @('Finished','Error','Blocked')){break};if([DateTime]::UtcNow -gt $end){throw 'Test request outcome uncertain'};Start-Sleep -Milliseconds 500}while($true)
 $reportTarget=Join-Path $e ($label+'-report.json');if(Test-Path $reportTarget){throw 'Result target already exists; preserve prior attempt first'}
 Copy-Item -LiteralPath (Join-Path $u ('Temp/SinglePlayer/'+$kind+'-report.json')) -Destination $reportTarget
 $xml=Join-Path $u ('Temp/SinglePlayer/'+$kind+'-results.xml');$validXml=$v.State -eq 'Finished' -and (Test-Path $xml) -and (Get-Item -LiteralPath $xml).LastWriteTimeUtc -gt $after
 if($validXml){Copy-Item -LiteralPath $xml -Destination (Join-Path $e ($label+'-results.xml'))}
 @{State=$v.State;CurrentXmlCopied=[bool]$validXml;RequestedUtc=$after.ToString('O')}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $e ($label+'-artifact-validity.json'))
 Write-Host ($label+' '+$v.State+' '+$v.Passed+'/'+$v.Failed+' '+$v.UpdatedUtc);return $v
}
$core='Game.Core.Tests.WorldReplicationTimingTraceTests'
$hdr=@('Game.ElementField.Tests.WorldReplicaStoreTests.TimingHeaderReadsPublishedVersionAndRejectsUnpublishedCandidate','Game.ElementField.Tests.WorldReplicaStoreTests.TimingHeaderDoesNotExposeEvictedInterestOrUnknownChunk')
$hooks=@('Game.ElementField.Tests.WorldReplicaStoreTests.TimingInstalledEventOccursAfterSuccessfulPublishOnly','Game.Multiplayer.Tests.WorldReplicationCoordinatorTests.TimingOwnerRecordsOnlySuccessfulChangedCommit','Game.Multiplayer.Tests.WorldReplicationCoordinatorTests.TimingPreparedPagesCarryFrozenPeerStampWithoutClaimingTransportSend','Game.Multiplayer.Tests.WorldReplicationCoordinatorTests.TimingNeedsBaselineResyncAndAcceptedControlRetainCandidateStamp','Game.Multiplayer.Tests.WorldReplicationCoordinatorTests.NativeClientIngressChecksRealAuthenticationBeforeBoundedPageQueue','Game.Multiplayer.Tests.WorldReplicationCoordinatorTests.TimingBudgetRejectionReportsActualTokensAndLeavesPageCursorUnchanged','Game.Multiplayer.Tests.WorldReplicationCoordinatorTests.TimingResyncBranchesPreserveFailureAndRealInstalledVersion','Game.Rendering.Tests.GpuFireParcelSurfaceTests.RendererTimingRecordsInstalledVersionAndSeedWithoutChangingVisualOutput','Game.Rendering.Tests.GpuFireParcelSurfaceTests.RendererTimingRecordsActualViewProbabilityAndBudgetRejection')
if($Mode -eq 'core-red'){$v=Test 'editmode' (@($core)+$hdr) 'core-and-header-red';if($v.State -ne 'Finished' -or $v.Failed -ne 13 -or $v.Passed -ne 0){throw 'Expected 13 meaningful missing implementation RED cases; inspect XML'}}
if($Mode -eq 'core-green-and-hooks-red'){
 $v=Test 'editmode' (@($core)+$hdr) 'core-and-header-green';if($v.State -ne 'Finished' -or $v.Failed -ne 0 -or $v.Passed -ne 13){throw 'Core/header GREEN not achieved'}
 $v=Test 'editmode' $hooks 'behavior-hooks-red';if($v.State -ne 'Finished' -or $v.Failed -lt 8){throw 'Expected real missing hook behavior RED; inspect cases'}
}
if($Mode -eq 'core-green-and-hooks-red' -or $Mode -eq 'probe-red'){
 $v=Test 'playmode' @('Game.Run.Tests.FormalDungeonWorldPlayModeTests.ExplicitTimingProbeDoesNotInstallWithoutConfig','Game.Run.Tests.FormalDungeonWorldPlayModeTests.ExplicitTimingWindowExportsAfterDetachAndPreservesNetworkStamp','Game.Run.Tests.FormalDungeonWorldPlayModeTests.ExplicitTimingOwnerDestructionDetachesAndExportsInterruptedPrefix') 'probe-lifecycle-red'
 if($v.State -ne 'Finished' -or $v.Failed -ne 4 -or $v.Passed -ne 0){throw 'Expected four missing explicit probe lifecycle RED cases'}
}
if($Mode -eq 'green'){
 $v=Test 'editmode' (@($core)+$hdr+$hooks) 'timing-green';if($v.State -ne 'Finished' -or $v.Failed -ne 0){throw 'Timing GREEN failed'}
 $v=Test 'playmode' @('Game.Run.Tests.FormalDungeonWorldPlayModeTests') 'formal-and-timing-green';if($v.State -ne 'Finished' -or $v.Failed -ne 0){throw 'Formal/timing lifecycle GREEN failed'}
}
if($Mode -eq 'regression'){
 $names=(Read (Join-Path $u 'docs/superpowers/evidence/multiplayer/n5/native-recovery-gates-2026-10-31/full-request.json')).TestNames
 $names=@($names)+@($core,'Game.Rendering.Tests.GpuFireParcelSurfaceTests');$v=Test 'editmode' $names 'n5-and-rendering-regression';if($v.State -ne 'Finished' -or $v.Failed -ne 0){throw 'N5/Rendering regression failed'}
 # GREEN已完整跑过同一正式PlayMode fixture；本模式只扩大到其余受影响EditMode，避免无变化重复。
}
if($Mode -eq 'review-fix'){
 $v=Test 'editmode' @('Game.Core.Tests.WorldReplicationTimingTraceTests.WarmEnabledRecordHasZeroPositivelyCalibratedAllocationEvents') 'timing-record-allocation';if($v.State -ne 'Finished' -or $v.Failed -ne 0 -or $v.Passed -ne 1){throw 'Timing Record allocation verification failed'}
 $v=Test 'playmode' @('Game.Run.Tests.FormalDungeonWorldPlayModeTests') 'review-fix-lifecycle';if($v.State -ne 'Finished' -or $v.Failed -ne 0 -or $v.Passed -ne 9){throw 'Review fix lifecycle regression failed'}
}
Status 'Finished'
