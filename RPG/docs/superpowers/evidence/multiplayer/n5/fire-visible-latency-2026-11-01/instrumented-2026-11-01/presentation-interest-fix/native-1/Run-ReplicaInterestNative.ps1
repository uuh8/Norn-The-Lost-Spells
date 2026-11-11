$ErrorActionPreference='Stop';[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$repo='F:\Develop Project\RPG';$u=Join-Path $repo 'RPG';$t='C:\Users\zhy\Documents\Codex\2026-10-31\task'
$e=Join-Path $u 'docs/superpowers/evidence/multiplayer/n5/fire-visible-latency-2026-11-01/instrumented-2026-11-01/presentation-interest-fix'
$n=Join-Path $e 'native-1';$private=Join-Path $u '.tools/networking-checks/n5-fire-interest-fix-20261101-1'
$helper=Join-Path $t 'Invoke-ReplicaInterestNative.ps1';$inputHelper=Join-Path $t 'native-timing-input.py';$map=Join-Path $private 'p8.private.json'
$businessStarted=$false;$databaseStarted=$false;$guard=$null
function Read-Safe($path){try{if(Test-Path -LiteralPath $path){Get-Content -LiteralPath $path -Raw|ConvertFrom-Json -ErrorAction Stop}}catch{return $null}}
function Utc($value){if($value -is [DateTime]){return $value.ToUniversalTime()};[DateTime]::Parse($value,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()}
function Register-OwnedDs {
 $sessionPath=Join-Path $private 'session.private.json';if(!(Test-Path -LiteralPath $sessionPath)){return}
 $session=Get-Content -LiteralPath $sessionPath -Raw|ConvertFrom-Json -AsHashtable
 $expected=Join-Path $u 'Builds/OnlineRooms/Server/NornRoomServer.exe';$found=@()
 $instances=Join-Path $private 'instances'
 if(Test-Path -LiteralPath $instances){foreach($file in Get-ChildItem -LiteralPath $instances -Recurse -File -Filter 'state.json'){
  $state=Read-Safe $file.FullName;if(!$state){continue};$process=Get-Process -Id $state.ProcessId -ErrorAction SilentlyContinue
  if($process -and $process.Path -eq $expected -and $process.StartTime.ToUniversalTime().Ticks -eq $state.ProcessStartTicks){$found+=@($process)}
 }}
 # 认证尚未完成/状态尚未落盘也必须登记：只接受本业务PID的直接子进程、精确路径和实际启动时间。
 $businessRecord=Read-Safe (Join-Path $u ('.tools/local-online/'+$session.BusinessName+'.json'))
 if($businessRecord){$business=Get-Process -Id $businessRecord.ProcessId -ErrorAction SilentlyContinue
  if($business -and $business.Path -eq $businessRecord.Executable -and $business.StartTime.ToUniversalTime().Ticks -eq $businessRecord.StartTimeUtcTicks){
   foreach($child in Get-CimInstance Win32_Process -Filter ('ParentProcessId='+$business.Id)){
    if($child.ExecutablePath -ne $expected){continue};$process=Get-Process -Id $child.ProcessId -ErrorAction Stop
    if($process.Path -eq $expected -and $process.StartTime.ToUniversalTime() -ge $business.StartTime.ToUniversalTime()){$found+=@($process)}
   }
  }
 }
 foreach($process in @($found|Sort-Object Id -Unique)){
  if(@($session.Processes|Where-Object {$_.ProcessId -eq $process.Id -and $_.StartTicks -eq $process.StartTime.ToUniversalTime().Ticks}).Count){continue}
  $ticks=$process.StartTime.ToUniversalTime().Ticks
  $session.Processes=@($session.Processes)+@(@{ProcessId=$process.Id;StartTicks=$ticks;StartUnix=[double](($ticks-621355968000000000)/10000000);Executable=$process.Path;Kind='DedicatedServer';Label='p8'})
 }
 [IO.File]::WriteAllText($sessionPath,($session|ConvertTo-Json -Depth 35),[Text.UTF8Encoding]::new($false))
}
function Status($state){@{State=$state;RecordedUtc=[DateTime]::UtcNow.ToString('O')}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $n 'native-progress.json');Write-Host $state}
function Native-Action($kind,$role='a',$label='checkpoint'){& $helper -Action $kind -Dungeon p8 -Client $role -Label $label}
function Input($role,$kind,$extra=@()){
 if(Test-Path -LiteralPath (Join-Path $n 'memory-stop.json')){throw 'Owned memory guard stopped this case'}
 & python $inputHelper --session $map --client $role $kind @extra
 if($LASTEXITCODE){throw "Native input stopped: $role $kind"}
}
function Wait-Condition([scriptblock]$test,[int]$seconds,[string]$description){$end=[DateTime]::UtcNow.AddSeconds($seconds);do{
 if(Test-Path -LiteralPath (Join-Path $n 'memory-stop.json')){throw 'Memory guard stopped case'}
 if(& $test){return};Start-Sleep -Milliseconds 100
}while([DateTime]::UtcNow -lt $end);throw ('Bounded wait failed: '+$description)}
foreach($kind in @('client','server')){$b=Read-Safe (Join-Path $e ($kind+'-progress.json'));if(!$b -or $b.State -ne 'Returned' -or !$b.Passed){throw 'Matching Native builds not confirmed; no launch'}}
$guardProof=Read-Safe (Join-Path (Split-Path $e) 'guard-self-test/verification.json');if(!$guardProof.TargetActuallyExitedNormally -or !$guardProof.UnlistedControlStillAlive){throw 'Normal close guard proof missing'}
if(Test-Path -LiteralPath (Join-Path $private 'session.private.json')){throw 'Private session already exists; inspect uncertain outcome, no repeat'}
try{
 Status 'StartOwnedDatabase'; & (Join-Path $u 'Tools/Networking/Start-LocalDatabase.ps1');$databaseStarted=$true
 Wait-Condition {@([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners()|Where-Object Port -eq 13384).Count -gt 0} 15 'private database listener'
 Status 'StartIsolatedTestBusiness';Native-Action 'StartBusiness';$businessStarted=$true;Native-Action 'CreateUsers'
 # 真正低内存联动已用两个隐藏小窗验证；正式守卫只读公开进程账本，关闭自有Native且不强杀。
 $python=(Get-Command python).Source
 $guard=Start-Process -FilePath $python -ArgumentList @(('"'+(Join-Path $t 'owned-native-memory-guard.py')+'"'),'--stage',('"'+$n+'"'),'--seconds','600') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $private 'guard.stdout.log') -RedirectStandardError (Join-Path $private 'guard.stderr.log')
 foreach($role in @('a','b')){
  Status ('LaunchAndImmediatelyOperate-'+$role);Native-Action 'Launch' $role
  $launchStart=[DateTime]::UtcNow
  Wait-Condition {$session=Read-Safe $map;$owned=Get-Process -Id $session.Clients.$role.ProcessId -ErrorAction SilentlyContinue;$owned -and $owned.MainWindowHandle -ne 0} 15 'owned visible Native window'
  $preFocusMenu=Test-Path -LiteralPath (Join-Path $private ('p8/'+$role+'-portal.json'))
  Input $role 'focus'
  Wait-Condition {Test-Path -LiteralPath (Join-Path $private ('p8/'+$role+'-portal.json'))} 20 'owned menu report'
  @{Role=$role;LaunchCheckUtc=$launchStart.ToString('O');MenuReportBeforeFocus=$preFocusMenu;MenuReportAfterFocus=$true;ObservedUtc=[DateTime]::UtcNow.ToString('O');OnlyHarnessFocusOrderChanged=$true}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $n ($role+'-startup-focus.json'))
  Input $role 'login';Input $role 'node' @('--name','^EnterHub$');Input $role 'navigate' @('--match','p8')
  Input $role 'node' @('--name','^Matchmaking$')
 }
 Status 'ActualReadyAndStart';foreach($role in @('a','b')){Input $role 'node' @('--name','^Ready$','--match','^准备$')};Input 'a' 'node' @('--name','^StartRoom$')
 Register-OwnedDs
 Wait-Condition {
  $a=Read-Safe (Join-Path $private 'p8/a-movement.json');$b=Read-Safe (Join-Path $private 'p8/b-movement.json')
  $a -and $b -and $a.Ready -and $b.Ready -and $a.ActorId -eq 1 -and $b.ActorId -eq 2
 } 50 'two actual authenticated actors'
 Native-Action 'Inspect';Native-Action 'Checkpoint' 'a' 'before-aim'
 Status 'ActualNearGroundAim';Input 'a' 'mouse' @('--dy','180');$afterAim=[DateTime]::UtcNow
 Wait-Condition {
  $m=Read-Safe (Join-Path $private 'p8/a-movement.json')
  $m -and (Utc $m.RecordedAtUtc) -gt $afterAim
 } 5 'fresh actual aim report'
 $aim=Read-Safe (Join-Path $private 'p8/a-movement.json')
 if($aim.AimPoint.x -lt -52 -or $aim.AimPoint.x -ge -50 -or $aim.AimPoint.z -lt 56 -or $aim.AimPoint.z -ge 58){throw 'Actual aim outside selected native Key footprint; do not change DS position or fabricate target'}
 $sa=Read-Safe (Join-Path $private 'p8/a-store.json');$sb=Read-Safe (Join-Path $private 'p8/b-store.json')
 foreach($s in @($sa,$sb)){if(!$s){throw 'No fresh actual baseline Store'};if(@($s.Cells|Where-Object {$_.Domain -eq 0 -and $_.ChunkX -eq -26 -and $_.ChunkY -eq 0 -and $_.ChunkZ -eq 28 -and $_.Material -eq 2 -and $_.Quantity -gt 0}).Count){throw 'Target Key already has Fire; cannot attribute its first Seed to this shot'}}
 if($aim.ActorCharges[0] -ne 30){throw 'Actor consumed before controlled shot; preserve and reject causal case'}
 Native-Action 'Checkpoint' 'a' 'before-timing'
 $outputs=@((Join-Path $private 'ds-timing.json'),(Join-Path $private 'p8/a-timing.json'),(Join-Path $private 'p8/b-timing.json'))
 foreach($path in $outputs){$v=Read-Safe ($path+'.status.json');if(!$v -or $v.State -ne 'Armed'){throw 'Instrumented process did not arm; never mix old Native build'}}
 # 尽量在DS稀疏状态写完后开始8s窗，避开既有15s状态JSON；该文件不是精确材料提交时间。
 Status 'AlignAfterExistingDsReport';$stateFile=Get-ChildItem -LiteralPath (Join-Path $private 'instances') -Recurse -Filter 'state.json' -File|Select-Object -First 1
 Wait-Condition {([DateTime]::UtcNow-(Get-Item -LiteralPath $stateFile.FullName).LastWriteTimeUtc).TotalSeconds -lt 1} 17 'fresh DS diagnostic state before timing'
 Status 'StartBoundedTimingWindow';foreach($command in @((Join-Path $private 'ds-timing.request'),(Join-Path $private 'p8/a-timing.request'),(Join-Path $private 'p8/b-timing.request'))){if(Test-Path -LiteralPath $command){throw 'Timing request already exists'};[IO.File]::WriteAllText($command,'start')}
 Wait-Condition {(@($outputs|Where-Object {(Read-Safe ($_+'.status.json')).State -eq 'Starting'}).Count -eq 3)} 4 'all timing owners starting'
 Start-Sleep -Milliseconds 350
 Status 'OneActualControlledFireball';Input 'a' 'mouse' @('--seconds','0.15')
 Wait-Condition {(@($outputs|Where-Object {(Read-Safe ($_+'.status.json')).State -eq 'Completed'}).Count -eq 3)} 15 'all detached and exported timing windows'
 foreach($path in $outputs){$v=Read-Safe $path;if(!$v -or $v.State -ne 'Completed' -or $v.DroppedCount -ne 0){throw 'Timing report incomplete/overflowed; preserve failure'}}
 Status 'ArchiveImmutableTimingReports';foreach($pair in @(@($outputs[0],'ds-timing.json'),@($outputs[1],'a-timing.json'),@($outputs[2],'b-timing.json'))){Copy-Item -LiteralPath $pair[0] -Destination (Join-Path $n $pair[1])}
 Start-Sleep -Milliseconds 1100;Native-Action 'Checkpoint' 'a' 'after-timing'
 @{State='Captured';RecordedUtc=[DateTime]::UtcNow.ToString('O');DrawIsCpuSubmissionOnly=$true;NoProtocolOrVisualParameterChanges=$true}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $n 'capture-result.json')
 Status 'CaptureCompleteCloseOwnedWindows'
}catch{
 @{State='Failed';RecordedUtc=[DateTime]::UtcNow.ToString('O');Error=$_.Exception.Message}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $n 'capture-result.json')
 throw
}finally{
 $cleanupErrors=[Collections.Generic.List[string]]::new()
 # 未知启动结果先读本轮自有会话，不重复启动；正常关窗失败时不走旧Cleanup的强制Native分支。
 if(Test-Path -LiteralPath (Join-Path $private 'session.private.json')){
  $businessStarted=$true
  Register-OwnedDs
  try{Native-Action 'StopMap'}catch{$cleanupErrors.Add($_.Exception.Message)}
  $session=Read-Safe (Join-Path $private 'session.private.json');$remainingNative=@()
  foreach($record in $session.Processes){if($record.Kind -eq 'NativeClient' -and !$record.Stopped){$owned=Get-Process -Id $record.ProcessId -ErrorAction SilentlyContinue;if($owned -and $owned.StartTime.ToUniversalTime().Ticks -eq $record.StartTicks -and $owned.Path -eq $record.Executable){$remainingNative+=@($owned.Id)}}}
  if($remainingNative.Count -eq 0){try{Native-Action 'Cleanup'}catch{$cleanupErrors.Add($_.Exception.Message)}}
  else{$cleanupErrors.Add('Owned Native normal close pending; no forced cleanup attempted')}
 }
 if($databaseStarted){try{& (Join-Path $u 'Tools/Networking/Stop-LocalDatabase.ps1')}catch{$cleanupErrors.Add($_.Exception.Message)}}
 [IO.File]::WriteAllText((Join-Path $n 'guard.stop'),'owned short case finished')
 if($guard){[void]$guard.WaitForExit(5000)}
 @{RecordedUtc=[DateTime]::UtcNow.ToString('O');BusinessCleanupAttempted=$businessStarted;PrivateDatabaseShutdownAttempted=$databaseStarted;GuardPid=if($guard){$guard.Id}else{0};OnlyOwned=$true;Errors=@($cleanupErrors)}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $n 'cleanup.json')
 if($cleanupErrors.Count){Status 'CleanupNeedsInspection'}else{Status 'OwnedShortCaseClosed'}
}
