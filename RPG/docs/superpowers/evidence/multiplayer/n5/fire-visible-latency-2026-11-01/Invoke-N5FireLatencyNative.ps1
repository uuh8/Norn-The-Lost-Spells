# 本轮只操作新建测试账户和已记录所有权进程；密码/Token只保存在ignored私有目录。
[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('StartBusiness','CreateUsers','RefreshTokens','CreatePrivateRoom','ResetClosedClient','Launch','Inspect','Checkpoint','StopMap','Cleanup')][string]$Action,
      [ValidateSet('p7','p8')][string]$Dungeon='p7',[ValidateSet('a','b')][string]$Client='a',[string]$Label='checkpoint')
$ErrorActionPreference='Stop'
$repo='F:\Develop Project\RPG';$unity=Join-Path $repo 'RPG'
$private=Join-Path $unity '.tools/networking-checks/n5-fire-latency-20261101'
$stage=Join-Path $unity 'docs/superpowers/evidence/multiplayer/n5/fire-visible-latency-2026-11-01'
$sessionPath=Join-Path $private 'session.private.json';$utf8=[Text.UTF8Encoding]::new($false)
function Save($p,$v){[IO.File]::WriteAllText($p,($v|ConvertTo-Json -Depth 35),$utf8)}
function Read($p){Get-Content -LiteralPath $p -Raw|ConvertFrom-Json -AsHashtable}
function Api($method,$path,$body,$user){
 $headers=@{'X-Norn-Protocol'='4';'X-Norn-Manifest'=$script:s.ManifestHash};if($user){$headers.Authorization='Bearer '+$user.Token}
 $params=@{Uri=$script:s.Url+$path;Method=$method;Headers=$headers;TimeoutSec=10}
 if($null -ne $body){$params.ContentType='application/json';$params.Body=$body|ConvertTo-Json -Depth 15 -Compress}
 Invoke-RestMethod @params
}
function Track($p,$kind){@{ProcessId=$p.Id;StartTicks=$p.StartTime.ToUniversalTime().Ticks;StartUnix=([double]($p.StartTime.ToUniversalTime().Ticks-621355968000000000)/10000000);Executable=$p.Path;Kind=$kind}}
if($Action -eq 'StartBusiness'){
 if(Test-Path $sessionPath){throw 'Existing private stage session; inspect before repeating'}
 if(@([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners()|Where-Object Port -eq 5080).Count){throw '5080 occupied; do not affect existing service'}
 New-Item -ItemType Directory -Path $private -Force|Out-Null
 $connections=Read (Join-Path $unity '.tools/local-mysql/connections.private.json') # 仅本次正常服务启动需要，绝不输出。
 $manifest=Read (Join-Path $unity 'Assets/_Project/Resources/Online/ContentManifest.json');$gameplay=Read (Join-Path $unity 'Assets/_Project/Resources/Online/GameplayManifest.json')
 foreach($var in @('NORN_N5_MATERIAL_PROBE','NORN_N5_MATERIAL_SEQUENCE','NORN_N5_MATERIAL_EXTENDED_CONFIG','NORN_N5_INTEREST_PROBE','NORN_N5_RECOVERY_PROBE','NORN_N5_REPLICA_PROBE')){[Environment]::SetEnvironmentVariable($var,$null,'Process')}
 $env:NORN_N5_PLAYER_RESOURCE_PROBE='1' # 仅本任务Development DS额外只读实例库存，不启用材料写入fixture。
 $env:Online__Enabled='true';$env:Online__AutoMigrate='true';$env:Online__RoomsEnabled='true';$env:Online__Instances__Enabled='true'
 $env:ConnectionStrings__Online=$connections.Test;$env:Online__MigrationConnectionString=$connections.TestMigration
 $env:Online__ManifestPath=Join-Path $unity 'Assets/_Project/Resources/Online/ContentManifest.json';$env:Online__GameplayConfigHash=$gameplay.GameplayConfigHash
 $env:Online__Instances__BuildPath=Join-Path $unity 'Builds/OnlineRooms/Server/NornRoomServer.exe';$env:Online__Instances__WorkRoot=Join-Path $private 'instances'
 $env:Online__Instances__BusinessBaseUrl='http://127.0.0.1:5080/';$env:Online__Instances__FirstPort='18500';$env:Online__Instances__LastPort='18503'
 $name='n5player-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
 & (Join-Path $unity 'Tools/Networking/Start-LocalOnline.ps1') -NoBuild -Port 5080 -InstanceName $name
 $script:s=@{Url='http://127.0.0.1:5080';BusinessName=$name;ManifestHash=$manifest.Hash;GameplayHash=$gameplay.GameplayConfigHash;Maps=@{};Processes=@();Prefix='n5p'+[Guid]::NewGuid().ToString('N').Substring(0,7)}
 Save $sessionPath $s
 $ready=Invoke-WebRequest -Uri ($s.Url+'/health/ready') -TimeoutSec 5;if($ready.StatusCode -ne 200){throw 'Database readiness failed'}
 Save (Join-Path $stage 'business-start.json') @{InstanceName=$name;Protocol=4;TestSchema=$true;DiagnosticInjection=$false;RecordedUtc=[DateTime]::UtcNow.ToString('O');GameplayHash=$s.GameplayHash}
 Write-Output 'Isolated test-schema business ready on original ServiceProfile 5080; injection disabled.';return
}
$script:s=Read $sessionPath
if($Action -eq 'CreateUsers'){
 if($s.Maps.ContainsKey($Dungeon)){throw 'Existing map users; do not recreate'}
 $users=@{}
 foreach($role in @('a','b')){
  $username=$s.Prefix+$Dungeon+$role;$password='N5Player_'+[Guid]::NewGuid().ToString('N')
  $null=Api 'Post' '/api/online/accounts' @{Username=$username;Password=$password} $null
  $login=Api 'Post' '/api/online/sessions' @{Username=$username;Password=$password;ProtocolVersion=4;ContentManifestHash=$s.ManifestHash} $null
  $user=@{Username=$username;Password=$password;Token=$login.Token};$profile=Api 'Get' '/api/online/profile' $null $user;$user.PlayerId=$profile.PlayerId
  $slot=0;foreach($item in @($profile.Items|Where-Object Area -eq 'Warehouse')){
   $null=Api 'Post' '/api/online/inventory/move' @{CommandId=[Guid]::NewGuid().ToString('N');ExpectedVersion=$profile.Version;InstanceId=$item.InstanceId;Destination='Backpack';Slot=$slot} $user
   $profile=Api 'Get' '/api/online/profile' $null $user;$slot++
  }
  Save (Join-Path $stage ($Dungeon+'-'+$role+'-fresh-profile.json')) $profile
  $users[$role]=$user
 }
 $s.Maps[$Dungeon]=@{Users=$users;Clients=@{};InputAudit=Join-Path $stage ($Dungeon+'-os-input.jsonl')};Save $sessionPath $s
 Write-Output ($Dungeon+' fresh isolated accounts created; original starter assets unchanged; room not created.');return
}
$map=$s.Maps[$Dungeon];$mapPath=Join-Path $private ($Dungeon+'.private.json')
if($Action -eq 'RefreshTokens'){
 foreach($role in @('a','b')){$user=$map.Users[$role];$login=Api 'Post' '/api/online/sessions' @{Username=$user.Username;Password=$user.Password;ProtocolVersion=4;ContentManifestHash=$s.ManifestHash} $null;$user.Token=$login.Token}
 Save $sessionPath $s;Save $mapPath $map
 Write-Output 'Only owned API sessions refreshed through normal login; asset and TTL unchanged.';return
}
if($Action -eq 'CreatePrivateRoom'){
 if($map.RoomId){throw 'Private room already recorded'}
 $room=Api 'Post' '/api/online/rooms' @{DungeonId=$Dungeon} $map.Users.a
 $room=Api 'Post' ('/api/online/rooms/'+$room.RoomId+'/members') @{} $map.Users.b
 $map.RoomId=$room.RoomId;$map.PrivateRoomSetup=$true;Save $sessionPath $s;Save $mapPath $map
 Save (Join-Path $stage ($Dungeon+'-private-room-setup.json')) $room
 Write-Output ($Dungeon+' private room allocated through existing API; Ready/Start left to original UI.');return
}
if($Action -eq 'ResetClosedClient'){
 $record=$map.Clients[$Client];if(!$record){throw 'No prior client record'}
 if(Get-Process -Id $record.ProcessId -ErrorAction SilentlyContinue){throw 'Prior process still exists; do not replace user session'}
 Save (Join-Path $stage ($Dungeon+'-'+$Client+'-prior-closed-process.json')) $record
 foreach($p in $s.Processes){if($p.ProcessId -eq $record.ProcessId -and $p.StartTicks -eq $record.StartTicks){$p.Stopped=$true}}
 $map.Clients.Remove($Client);Save $sessionPath $s;Save $mapPath $map
 Write-Output 'Prior client already exited; record archived, no process stopped.';return
}
if($Action -eq 'Launch'){
 if($map.Clients.ContainsKey($Client)){throw 'Existing owned Native record; inspect instead of relaunch'}
 $dir=Join-Path $private $Dungeon;New-Item -ItemType Directory -Path $dir -Force|Out-Null
 $cfg=Join-Path $dir ($Client+'.private.json');$report=Join-Path $dir ($Client+'-portal.json');$command=Join-Path $dir ($Client+'-portal.request');$log=Join-Path $dir ($Client+'.log')
 Save $cfg @{Output=$report;Command=$command;PortalScenePath='';MovementOutput=(Join-Path $dir ($Client+'-movement.json'));MovementCommand=(Join-Path $dir ($Client+'-movement.request'))}
 $exe=Join-Path $unity 'Builds/OnlineAccounts/Menu/NornMenuClient.exe'
 $old=$env:NORN_N5_REPLICA_PROBE;$env:NORN_N5_REPLICA_PROBE=Join-Path $dir ($Client+'-store.json')
 # 本轮验收用户明确要求真实键鼠；此窗口用于交互，因此可见启动，其余服务仍Hidden。
 try{$p=Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -ArgumentList @('-force-d3d11','-screen-width','960','-screen-height','600','-screen-fullscreen','0','-logFile',('"'+$log+'"'),'--norn-portal-acceptance',('"'+$cfg+'"')) -WindowStyle Normal -PassThru}finally{$env:NORN_N5_REPLICA_PROBE=$old}
 $record=Track $p 'NativeClient';$record.Config=$cfg;$record.Log=$log;$record.Label=$Dungeon+'-'+$Client
 $map.Clients[$Client]=$record;$s.Processes+=@($record);Save $sessionPath $s;Save $mapPath $map
 Save (Join-Path $stage 'process-ledger.json') @($s.Processes | Select-Object ProcessId,StartUnix,Executable,Kind,Stopped)
 Write-Output ($Dungeon+' '+$Client+' formal menu Native launched PID '+$p.Id);return
}
if($Action -in @('Inspect','Checkpoint')){
 $room=Api 'Get' '/api/online/rooms/current' $null $map.Users.a
 $instance=$null;try{$instance=Api 'Get' ('/api/online/rooms/'+$room.RoomId+'/instance') $null $map.Users.a}catch{if($_.Exception.Response.StatusCode.value__ -ne 404){throw}}
 if($instance){
  $statePath=Join-Path $private ('instances/'+$instance.InstanceId+'/state.json')
  if(Test-Path $statePath){$ds=Read $statePath;$p=Get-Process -Id $ds.ProcessId -ErrorAction Stop
   if($p.StartTime.ToUniversalTime().Ticks -ne $ds.ProcessStartTicks -or $p.Path -ne (Join-Path $unity 'Builds/OnlineRooms/Server/NornRoomServer.exe')){throw 'DS ownership mismatch'}
   if(-not @($s.Processes|Where-Object ProcessId -eq $p.Id).Count){$rec=Track $p 'DedicatedServer';$rec.StatePath=$statePath;$rec.Label=$Dungeon;$s.Processes+=@($rec);Save $sessionPath $s}
  }
 }
 if($Action -eq 'Inspect'){
  [pscustomobject]@{RoomId=$room.RoomId;State=$room.State;Members=$room.Members.Count;InstanceState=$instance.State;InstanceId=$instance.InstanceId;DsPid=$ds.ProcessId;WorldEpoch=$ds.WorldEpoch;MovementTick=$ds.MovementTick;Accepted=$ds.Accepted;MaterialChunks=$ds.MaterialChunks.Count;MaterialFaulted=$ds.MaterialFaulted;RecordedUtc=[DateTime]::UtcNow.ToString('O')}|ConvertTo-Json;return
 }
 if($Label -notmatch '^[a-z0-9-]{1,50}$'){throw 'Invalid checkpoint label'}
 $out=Join-Path $stage ($Dungeon+'/'+$Label);if(Test-Path $out){throw 'Checkpoint exists; preserve prior evidence'};New-Item -ItemType Directory -Path $out -Force|Out-Null
 Save (Join-Path $out 'room.json') $room;if($instance){Save (Join-Path $out 'instance.json') $instance};if($ds){Copy-Item -LiteralPath $statePath -Destination (Join-Path $out 'ds.json')}
 foreach($role in @('a','b')){
  if(!$map.Clients.ContainsKey($role)){continue};$cfg=Read $map.Clients[$role].Config
  foreach($pair in @(@($cfg.Output,($role+'-portal.json')),@(($cfg.Output+'.ui.json'),($role+'-ui.json')),@($cfg.MovementOutput,($role+'-movement.json')),@((Join-Path $private ($Dungeon+'/'+$role+'-store.json')),($role+'-store.json')))){if(Test-Path $pair[0]){Copy-Item -LiteralPath $pair[0] -Destination (Join-Path $out $pair[1])}}
 }
 Save (Join-Path $out 'sampling.json') @{RecordedUtc=[DateTime]::UtcNow.ToString('O');ContinuousSimulation=$true;DiagnosticsOnly=$true;Paused=$false}
 Write-Output ($Dungeon+' '+$Label+' current continuous-running checkpoint archived.');return
}
if($Action -eq 'StopMap'){
 foreach($record in $s.Processes){
  if($record.Stopped -or !($record.Label -eq $Dungeon -or $record.Label -like ($Dungeon+'-*'))){continue}
  $p=Get-Process -Id $record.ProcessId -ErrorAction SilentlyContinue
  if($p){if($p.StartTime.ToUniversalTime().Ticks -ne $record.StartTicks -or $p.Path -ne $record.Executable){throw 'Owned map process identity changed'}
   if($record.Kind -eq 'NativeClient'){if(!$p.CloseMainWindow()){throw 'Normal client close unavailable; no forced close'};if(!$p.WaitForExit(10000)){throw 'Native client normal close pending; no forced close'}}
   else{Stop-Process -Id $p.Id;$p.WaitForExit(10000)|Out-Null}
  }
  $record.Stopped=$true
 }
 Save $sessionPath $s
 Save (Join-Path $stage ($Dungeon+'-process-cleanup.json')) @{OnlyOwnedMap=$true;RecordedUtc=[DateTime]::UtcNow.ToString('O')}
 Write-Output ($Dungeon+' owned map processes stopped; business remains for independent next map.');return
}
if($Action -eq 'Cleanup'){
 foreach($record in $s.Processes){if($record.Stopped){continue};$p=Get-Process -Id $record.ProcessId -ErrorAction SilentlyContinue;if($p){if($p.StartTime.ToUniversalTime().Ticks -ne $record.StartTicks -or $p.Path -ne $record.Executable){throw 'Owned process identity changed'};Stop-Process -Id $p.Id;$p.WaitForExit(10000)|Out-Null}}
 & (Join-Path $unity 'Tools/Networking/Stop-LocalOnline.ps1') -InstanceName $s.BusinessName
 Save (Join-Path $stage 'process-cleanup.json') @{OnlyOwned=$true;RecordedUtc=[DateTime]::UtcNow.ToString('O')}
 Write-Output 'Owned Native/DS/business stopped; no user process touched.'
}
