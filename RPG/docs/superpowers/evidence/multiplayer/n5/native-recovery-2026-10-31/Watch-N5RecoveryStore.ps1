$ErrorActionPreference='Stop'
$u='F:\Develop Project\RPG\RPG';$e=Join-Path $u 'docs/superpowers/evidence/multiplayer/n5/native-recovery-2026-10-31'
function Utc($x){if($x -is [DateTime]){return $x.ToUniversalTime()};[DateTime]::Parse($x,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()}
function Read-Json($p){Get-Content -LiteralPath $p -Raw|ConvertFrom-Json}
function Save-Json($p,$x){[IO.File]::WriteAllText($p,($x|ConvertTo-Json -Depth 35),[Text.UTF8Encoding]::new($false))}
$done=@{};$end=[DateTime]::UtcNow.AddMinutes(25)
while($done.Count -lt 2){
 foreach($case in @('checksum','missing-base')){
  if($done.ContainsKey($case)){continue}
  try{
   $private=Join-Path $u ('.tools/networking-checks/n5-native-recovery-20261031/'+$case);$ce=Join-Path $e $case
   $fault=Read-Json (Join-Path $private 'recovery-fault.json');if($fault.state -ne 'Injected' -or $fault.recoveryAcknowledged){continue}
   $a=Read-Json (Join-Path $private 'p7-client0.report.json');$store=Read-Json (Join-Path $private 'p7-client0.store.json');$before=Read-Json (Join-Path $ce 'before-a.json')
   $injected=@($fault.stateChanges|Where-Object state -eq 'Injected');if($injected.Count -ne 1){continue};$time=Utc $injected[0].utc
   $expected=if($case -eq 'checksum'){'Checksum'}else{'MissingRevision'}
   # 独立只读观察器要求两份真实采样都产生于拒绝/关联Resync之后，且恢复Baseline尚未发布。
   if((Utc $a.RecordedAtUtc) -lt $time -or (Utc $store.RecordedUtc) -lt $time -or $a.MaterialLastFailure -ne $expected -or !$a.MaterialRejectedPayloadAvailable -or $a.MaterialBaselines -ne $before.MaterialBaselines){continue}
   Save-Json (Join-Path $ce 'preserved-fresh-client.json') $a
   Save-Json (Join-Path $ce 'preserved-fresh-store.json') $store
   Save-Json (Join-Path $ce 'preserved-fresh-fault.json') $fault
   Save-Json (Join-Path $ce 'preserved-fresh-observation.json') @{Observed=$true;AfterAssociatedResyncUtc=$time.ToString('O');ClientReportUtc=$a.RecordedAtUtc;StoreReportUtc=$store.RecordedUtc;BaselineCountUnchanged=$true;RecordedUtc=[DateTime]::UtcNow.ToString('O')}
   $done[$case]=$true;Write-Output ($case+': captured fresh actual Store after rejection and before recovery')
  }catch{}
 }
 if([DateTime]::UtcNow -gt $end){throw 'Bounded fresh Store observation timed out; do not claim preservation'}
 Start-Sleep -Milliseconds 100
}
