# 本机验证编排：凭据只写 ignored 私有目录；证据只保存不含 Token/Ticket 的诊断输出。
[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('StartBusiness','CreateRoom','Connect','Reconnect','Replay','ReplayFresh','Collect','Cleanup')][string]$Action,
      [ValidateSet('p7','p8')][string]$Dungeon='p7')
$ErrorActionPreference='Stop'
$repo='F:\Develop Project\RPG'
$unity=Join-Path $repo 'RPG'
$private=Join-Path $unity '.tools/networking-checks/n5-authenticated-material-20261031'
$evidence=Join-Path $unity 'docs/superpowers/evidence/multiplayer/n5/authenticated-material-channel-2026-10-31'
$sessionPath=Join-Path $private 'session.private.json'
$utf8=[Text.UTF8Encoding]::new($false)
function Save-Json($Path,$Value){[IO.File]::WriteAllText($Path,($Value|ConvertTo-Json -Depth 30),$utf8)}
function Read-Json($Path){Get-Content -LiteralPath $Path -Raw|ConvertFrom-Json -AsHashtable}
function Api($Method,$Path,$Body,$User){
    $headers=@{'X-Norn-Protocol'='3';'X-Norn-Manifest'=$script:session.ManifestHash}
    if($User){$headers.Authorization='Bearer '+$User.Token}
    $args=@{Uri=$script:session.Url+$Path;Method=$Method;Headers=$headers;TimeoutSec=10}
    if($null -ne $Body){$args.ContentType='application/json';$args.Body=$Body|ConvertTo-Json -Depth 15 -Compress}
    Invoke-RestMethod @args
}
function Process-Record($Process,$Kind){
    @{ProcessId=$Process.Id;StartTicks=$Process.StartTime.ToUniversalTime().Ticks;Executable=$Process.Path;Kind=$Kind}
}
function Start-Native($User,$Ticket,$Label,$Duration,$ExpectRejection=$false){
    $config=Join-Path $private ($Label+'.private.json');$output=Join-Path $private ($Label+'.report.json');$log=Join-Path $private ($Label+'.log')
    Save-Json $config @{Ticket=$Ticket;Output=$output;DurationSeconds=$Duration;ExpectRejection=$ExpectRejection}
    $exe=Join-Path $unity 'Builds/OnlineRooms/Client/NornRoomClient.exe'
    $process=Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -ArgumentList @('-force-d3d11','-screen-width','800','-screen-height','450','-screen-fullscreen','0','-logFile',('"'+$log+'"'),'--norn-room-client',('"'+$config+'"')) -WindowStyle Hidden -PassThru
    $record=Process-Record $process 'NativeClient';$record.Output=$output;$record.Log=$log;$record.Label=$Label
    $script:session.Processes+=@($record);Save-Json $sessionPath $script:session
}
if($Action -eq 'StartBusiness'){
    if(Test-Path $sessionPath){throw '已有本轮会话，先核查状态，不重复启动'}
    New-Item -ItemType Directory -Path $private -Force|Out-Null
    $port=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0);$port.Start();$number=([Net.IPEndPoint]$port.LocalEndpoint).Port;$port.Stop()
    $manifest=Read-Json (Join-Path $unity 'Assets/_Project/Resources/Online/ContentManifest.json')
    $gameplay=Read-Json (Join-Path $unity 'Assets/_Project/Resources/Online/GameplayManifest.json')
    $connections=Read-Json (Join-Path $unity '.tools/local-mysql/connections.private.json')
    $env:NORN_N5_MATERIAL_PROBE=Join-Path $private 'material-seed.json'
    $env:Online__Enabled='true';$env:Online__AutoMigrate='true';$env:Online__RoomsEnabled='true';$env:Online__Instances__Enabled='true'
    # 与现有测试流程使用相同隔离 test Schema；不操作 dev Schema、真实玩家账户或资产。
    $env:ConnectionStrings__Online=$connections.Test;$env:Online__MigrationConnectionString=$connections.TestMigration
    $env:Online__ManifestPath=Join-Path $unity 'Assets/_Project/Resources/Online/ContentManifest.json'
    $env:Online__GameplayConfigHash=$gameplay.GameplayConfigHash
    $env:Online__Instances__BuildPath=Join-Path $unity 'Builds/OnlineRooms/Server/NornRoomServer.exe'
    $env:Online__Instances__WorkRoot=Join-Path $private 'instances'
    $env:Online__Instances__BusinessBaseUrl="http://127.0.0.1:$number/"
    $env:Online__Instances__FirstPort='18380';$env:Online__Instances__LastPort='18383'
    $name='n5native-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
    & (Join-Path $unity 'Tools/Networking/Start-LocalOnline.ps1') -NoBuild -Port $number -InstanceName $name
    $script:session=@{Url="http://127.0.0.1:$number";BusinessName=$name;ManifestHash=$manifest.Hash;GameplayHash=$gameplay.GameplayConfigHash;Processes=@();Rooms=@{};Prefix='n5v'+[Guid]::NewGuid().ToString('N').Substring(0,8)}
    Save-Json $sessionPath $script:session
    $ready=Invoke-WebRequest -Uri ($session.Url+'/health/ready') -TimeoutSec 5
    if($ready.StatusCode -ne 200){throw '数据库 readiness 失败'}
    Save-Json (Join-Path $evidence 'business-start.json') @{Url=$session.Url;InstanceName=$name;TestSchema=$true;ProtocolVersion=$manifest.ProtocolVersion;GameplayHash=$session.GameplayHash;RecordedAtUtc=[DateTime]::UtcNow.ToString('O')}
    Write-Output '本轮隔离业务服已就绪。';return
}
$script:session=Read-Json $sessionPath
if($Action -eq 'CreateRoom'){
    if($session.Rooms.ContainsKey($Dungeon)){throw '本轮该地图房间已存在'}
    $users=@()
    $attempt=[Guid]::NewGuid().ToString('N').Substring(0,4)
    for($i=0;$i -lt 2;$i++){
        $username=$session.Prefix+$Dungeon+$i+$attempt;$password='N5verify_'+[Guid]::NewGuid().ToString('N')
        $null=Api 'Post' '/api/online/accounts' @{Username=$username;Password=$password} $null
        $login=Api 'Post' '/api/online/sessions' @{Username=$username;Password=$password;ProtocolVersion=3;ContentManifestHash=$session.ManifestHash} $null
        $user=@{Username=$username;Token=$login.Token}
        $profile=Api 'Get' '/api/online/profile' $null $user
        $user.PlayerId=$profile.PlayerId
        # 当前注册已授予装备中的 Wand 与嵌套 Spell；保留该事实。只移动真正位于仓库的散装物品。
        $slot=0
        foreach($item in @($profile.Items|Where-Object Area -eq 'Warehouse')){
            $null=Api 'Post' '/api/online/inventory/move' @{CommandId=[Guid]::NewGuid().ToString('N');ExpectedVersion=$profile.Version;InstanceId=$item.InstanceId;Destination='Backpack';Slot=$slot} $user
            $profile=Api 'Get' '/api/online/profile' $null $user;$slot++
        }
        $user.Version=$profile.Version;$users+=@($user)
    }
    $room=Api 'Post' '/api/online/rooms' @{DungeonId=$Dungeon} $users[0]
    $room=Api 'Post' ('/api/online/rooms/'+$room.RoomId+'/members') @{} $users[1]
    foreach($user in $users){$room=Api 'Post' ('/api/online/rooms/'+$room.RoomId+'/ready') @{ExpectedRevision=$room.ReadyRevision;ExpectedInventoryVersion=$user.Version;Ready=$true} $user}
    $session.Rooms[$Dungeon]=@{RoomId=$room.RoomId;Users=$users;StartedAtUtc=[DateTime]::UtcNow.ToString('O')}
    Save-Json $sessionPath $session
    $started=Api 'Post' ('/api/online/rooms/'+$room.RoomId+'/start') @{ExpectedRevision=$room.ReadyRevision} $users[0]
    Save-Json (Join-Path $evidence ($Dungeon+'-start.json')) $started
    Write-Output ($Dungeon+' 已通过真实账户/资产移动/Ready 启动 DS；等待业务 Ready。');return
}
if($Action -eq 'Connect' -or $Action -eq 'Reconnect'){
    $room=$session.Rooms[$Dungeon]
    $instance=Api 'Get' ('/api/online/rooms/'+$room.RoomId+'/instance') $null $room.Users[0]
    Save-Json (Join-Path $evidence ($Dungeon+'-instance.json')) $instance
    if($instance.State -ne 'Ready'){throw ('实例尚未 Ready: '+$instance.State)}
    $work=Join-Path $private ('instances/'+$instance.InstanceId)
    $state=Read-Json (Join-Path $work 'state.json')
    if(-not @($session.Processes|Where-Object { $_.ProcessId -eq $state.ProcessId }).Count){
        $p=Get-Process -Id $state.ProcessId
        if($p.StartTime.ToUniversalTime().Ticks -ne $state.ProcessStartTicks -or $p.Path -ne (Join-Path $unity 'Builds/OnlineRooms/Server/NornRoomServer.exe')){throw 'DS 所有权不匹配'}
        $rec=Process-Record $p 'DedicatedServer';$rec.Label=$Dungeon;$rec.StatePath=Join-Path $work 'state.json';$session.Processes+=@($rec)
    }
    if($Action -eq 'Connect'){
        for($i=0;$i -lt 2;$i++){$ticket=Api 'Post' ('/api/online/rooms/'+$room.RoomId+'/ticket') @{} $room.Users[$i];Start-Native $room.Users[$i] $ticket ($Dungeon+'-client'+$i) 70}
    } else {
        $ticket=Api 'Post' ('/api/online/rooms/'+$room.RoomId+'/ticket') @{} $room.Users[0];Start-Native $room.Users[0] $ticket ($Dungeon+'-reconnect') 30
    }
    Save-Json $sessionPath $session;Write-Output ($Action+' 已启动真实 Native Client。');return
}
if($Action -eq 'Replay'){
    $latest=Join-Path $private ($Dungeon+'-reconnect.private.json')
    if(!(Test-Path $latest)){$latest=Join-Path $private ($Dungeon+'-client0.private.json')}
    $old=Read-Json $latest
    Save-Json (Join-Path $evidence ($Dungeon+'-replay-ticket-validity.json')) @{ExpiresUnixMilliseconds=$old.Ticket.ExpiresUnixMilliseconds;ReplayLaunchedUtc=[DateTime]::UtcNow.ToString('O');FromMostRecentTicket=$true}
    Start-Native $session.Rooms[$Dungeon].Users[0] $old.Ticket ($Dungeon+'-replay') 10 $true
    Write-Output '已用已消费 Ticket 验证既有一次性拒绝合同。';return
}
if($Action -eq 'ReplayFresh'){
    $room=$session.Rooms[$Dungeon]
    $ticket=Api 'Post' ('/api/online/rooms/'+$room.RoomId+'/ticket') @{} $room.Users[0]
    $label=$Dungeon+'-fresh-consume'
    Start-Native $room.Users[0] $ticket $label 30
    $deadline=[DateTime]::UtcNow.AddSeconds(25);$ready=$false
    do {
        $path=Join-Path $private ($label+'.report.json')
        if(Test-Path $path){$report=Read-Json $path;if($report.Authenticated -and $report.WorldReady){$ready=$true;break}}
        Start-Sleep -Milliseconds 500
    } while([DateTime]::UtcNow -lt $deadline)
    if(!$ready){throw '新 Ticket 尚未完成真实握手，未启动重复消费测试'}
    Copy-Item -LiteralPath $path -Destination (Join-Path $evidence ($label+'-authenticated.json'))
    Save-Json (Join-Path $evidence ($Dungeon+'-fresh-replay-validity.json')) @{ExpiresUnixMilliseconds=$ticket.ExpiresUnixMilliseconds;ReplayLaunchedUtc=[DateTime]::UtcNow.ToString('O');AfterRealAuthentication=$true}
    Start-Native $room.Users[0] $ticket ($Dungeon+'-fresh-replay') 10 $true
    Write-Output '新 Ticket 实际消费后立即复投；可用报告时间严格核对未过期。';return
}
if($Action -eq 'Collect'){
    foreach($item in $session.Processes){
        if($item.Kind -eq 'NativeClient' -and (Test-Path $item.Output)){Copy-Item -LiteralPath $item.Output -Destination (Join-Path $evidence ($item.Label+'-latest.json'))}
        if($item.Kind -eq 'DedicatedServer' -and (Test-Path $item.StatePath)){Copy-Item -LiteralPath $item.StatePath -Destination (Join-Path $evidence ($item.Label+'-ds-latest.json'))}
    }
    Write-Output '已收集不含凭据的 Native 诊断报告。';return
}
if($Action -eq 'Cleanup'){
    foreach($item in $session.Processes){
        $p=Get-Process -Id $item.ProcessId -ErrorAction SilentlyContinue
        if($p){if($p.StartTime.ToUniversalTime().Ticks -ne $item.StartTicks -or $p.Path -ne $item.Executable){throw '进程身份已改变，拒绝停止'};Stop-Process -Id $p.Id;$p.WaitForExit(10000)|Out-Null}
    }
    & (Join-Path $unity 'Tools/Networking/Stop-LocalOnline.ps1') -InstanceName $session.BusinessName
    Save-Json (Join-Path $evidence 'native-cleanup.json') @{StoppedOnlyOwnedProcesses=$true;RecordedAtUtc=[DateTime]::UtcNow.ToString('O')}
    Write-Output '本轮 Native 与业务进程已按身份清理。'
}
