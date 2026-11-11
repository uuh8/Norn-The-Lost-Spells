from pathlib import Path
t=Path(__file__).parent;s=(t/'Run-N5RecoveryGatesNative.ps1').read_text(encoding='utf-8-sig')
prefix=s[:s.index("\nStatus 'WaitingPrerequisites'")]
tail=s[s.index("Archive 'history';$historyA="):]
bridge='''
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
'''
(t/'Continue-N5RecoveryGates3.ps1').write_text(prefix+bridge+tail,encoding='utf-8-sig',newline='\n')
print('Prepared same-process continuation; requires authentic held-A trace and exact recovered two-key ACKs, never fabricates raw covered snapshot.')
