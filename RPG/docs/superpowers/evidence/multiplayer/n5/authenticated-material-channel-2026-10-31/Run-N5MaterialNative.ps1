$ErrorActionPreference='Stop'
$taskRoot='C:\Users\zhy\Documents\Codex\2026-10-31\task'
$unity='F:\Develop Project\RPG\RPG'
$evidence=Join-Path $unity 'docs/superpowers/evidence/multiplayer/n5/authenticated-material-channel-2026-10-31'
$private=Join-Path $unity '.tools/networking-checks/n5-authenticated-material-20261031'
$status=Join-Path $evidence 'native-run-progress.json'
function Status($State,$Detail){@{State=$State;Detail=$Detail;RecordedUtc=[DateTime]::UtcNow.ToString('O')}|ConvertTo-Json|Set-Content -LiteralPath $status -Encoding utf8}
function Read-Json($Path){Get-Content -LiteralPath $Path -Raw|ConvertFrom-Json}
Status 'WaitingForBuilds' 'No business/DS/client launched before final matched pair completes'
$deadline=[DateTime]::UtcNow.AddMinutes(25)
while(!(Test-Path (Join-Path $evidence 'final-native-build-progress.json')) -or (Read-Json (Join-Path $evidence 'final-native-build-progress.json')).State -ne 'Finished'){
    if([DateTime]::UtcNow -gt $deadline){throw '构建未完成；不启动Native，不终止有效构建'}
    Start-Sleep -Milliseconds 750
}
Status 'StartingBusiness' 'Isolated test schema and own loopback instance'
& (Join-Path $taskRoot 'Invoke-N5MaterialNative.ps1') -Action StartBusiness
& (Join-Path $taskRoot 'Invoke-N5MaterialNative.ps1') -Action CreateRoom -Dungeon p7
$instance=Read-Json (Join-Path $evidence 'p7-start.json')
$dsState=Join-Path $private ('instances/'+$instance.InstanceId+'/state.json')
Status 'WaitingForDSReady' 'Real HTTP instance launch; no ticket before Ready'
$deadline=[DateTime]::UtcNow.AddSeconds(50)
do {
    if(Test-Path $dsState){$ds=Read-Json $dsState;if($ds.Error){throw ('DS initialization failed: '+$ds.Error)};if($ds.Ready){break}}
    if([DateTime]::UtcNow -gt $deadline){throw 'DS未在既定Ready窗口内就绪'}
    Start-Sleep -Milliseconds 500
}while($true)
& (Join-Path $taskRoot 'Invoke-N5MaterialNative.ps1') -Action Connect -Dungeon p7
Status 'WaitingForReplicaInstallation' 'Two real Native clients and accepted install ACKs'
$deadline=[DateTime]::UtcNow.AddSeconds(45)
do {
    $ds=Read-Json $dsState
    if($ds.MaterialFaulted){throw ('Material replication faulted: '+$ds.MaterialFailure)}
    $a=Join-Path $private 'p7-client0.report.json';$b=Join-Path $private 'p7-client1.report.json'
    if((Test-Path $a) -and (Test-Path $b)){
        $first=Read-Json $a;$second=Read-Json $b
        if($first.MaterialFaulted -or $second.MaterialFaulted){throw 'Client material replication faulted'}
        if($first.Authenticated -and $second.Authenticated -and $first.MaterialInstalled -gt 0 -and $second.MaterialInstalled -gt 0 -and $ds.MaterialAcks -gt 0 -and $ds.MaterialChunks.Count -gt 0){break}
    }
    if([DateTime]::UtcNow -gt $deadline){throw '真实材料安装/ACK没有在45秒诊断窗口完成；保留进程与日志待核查'}
    Start-Sleep -Milliseconds 500
}while($true)
& (Join-Path $taskRoot 'Invoke-N5MaterialNative.ps1') -Action Collect
& python (Join-Path $taskRoot 'check-material-native.py')
if($LASTEXITCODE -ne 0){throw '逐Chunk精确一致性检查失败；保留真实错误，不重跑客户端'}
Status 'Passed' 'Two clients exactly match authoritative nonempty chunk stamps; processes await owned cleanup'
