$ErrorActionPreference='Stop'
$taskRoot='C:\Users\zhy\Documents\Codex\2026-10-31\task'
$unity='F:\Develop Project\RPG\RPG'
$e=Join-Path $unity 'docs/superpowers/evidence/multiplayer/n5/native-delta-2026-10-31'
$private=Join-Path $unity '.tools/networking-checks/n5-native-delta-20261031'
$h=Join-Path $taskRoot 'Invoke-N5DeltaNative.ps1'
function Read-Json($p){Get-Content -LiteralPath $p -Raw|ConvertFrom-Json}
function Status($s,$d){@{State=$s;Detail=$d;RecordedUtc=[DateTime]::UtcNow.ToString('O')}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $e 'native-progress.json') -Encoding utf8}
function Chunk-Text($v){(@($v.MaterialChunks|Sort-Object Domain,X,Y,Z|ForEach-Object {"$($_.Domain),$($_.X),$($_.Y),$($_.Z):$($_.WorldEpoch),$($_.Generation),$($_.Revision),$($_.Checksum),$($_.Words),$($_.Resident)"}) -join '|')}
Status 'WaitingForBuilds' 'No service before matched Native builds'
$end=[DateTime]::UtcNow.AddMinutes(30)
while(!(Test-Path (Join-Path $e 'build-progress.json')) -or (Read-Json (Join-Path $e 'build-progress.json')).State -ne 'Finished'){
    if([DateTime]::UtcNow -gt $end){throw 'Matched builds not finished; no services started'};Start-Sleep -Milliseconds 750
}
if(Test-Path (Join-Path $unity '.tools/local-mysql/state.json')){throw 'Existing database state; check ownership before using or stopping'}
Status 'StartingOwnedDatabase' 'Private loopback MySQL; no system instance'
& (Join-Path $unity 'Tools/Networking/Start-LocalDatabase.ps1')
Copy-Item -LiteralPath (Join-Path $unity '.tools/local-mysql/state.json') -Destination (Join-Path $e 'owned-database.json')
$admin=Join-Path $unity '.tools/mysql/mysql-8.4.13-winx64/bin/mysqladmin.exe'
$end=[DateTime]::UtcNow.AddSeconds(25)
do{
    & $admin ('--defaults-extra-file='+(Join-Path $unity '.tools/local-mysql/control.private.ini')) ping --silent 2>$null|Out-Null
    if($LASTEXITCODE -eq 0){break};if([DateTime]::UtcNow -gt $end){throw 'Owned database did not become ready'};Start-Sleep -Milliseconds 500
}while($true)
Status 'StartingBusiness' 'Isolated test schema; fresh users and tickets'
& $h -Action StartBusiness
& $h -Action CreateRoom -Dungeon p7
$instance=Read-Json (Join-Path $e 'p7-start.json');$dsPath=Join-Path $private ('instances/'+$instance.InstanceId+'/state.json')
$end=[DateTime]::UtcNow.AddSeconds(50)
do{
    if(Test-Path $dsPath){$v=Read-Json $dsPath;if($v.Error){throw $v.Error};if($v.Ready){break}}
    if([DateTime]::UtcNow -gt $end){throw 'DS Ready timed out'};Start-Sleep -Milliseconds 500
}while($true)
& $h -Action Connect -Dungeon p7
foreach($phase in @(1,2)){
    Status ('Checkpoint'+$phase) 'Wait for actual Store installation and DS ACK; no fabricated version'
    $end=[DateTime]::UtcNow.AddSeconds(60)
    do{
        $ready=$false
        try{
            $ds=Read-Json $dsPath;$seed=Read-Json (Join-Path $private 'material-seed.json')
            $a=Read-Json (Join-Path $private 'p7-client0.report.json');$b=Read-Json (Join-Path $private 'p7-client1.report.json')
            if($ds.MaterialFaulted -or $a.MaterialFaulted -or $b.MaterialFaulted){throw 'Replica fault; preserve logs'}
            $text=Chunk-Text $ds
            $ready=$seed.Phase -eq $phase -and $seed.PausedForComparison -and $a.Authenticated -and $b.Authenticated -and $ds.MaterialAcks -gt 0 -and $ds.MaterialChunks.Count -gt 0 -and $text -eq (Chunk-Text $a) -and $text -eq (Chunk-Text $b)
            if($phase -eq 2){$ready=$ready -and @($a.MaterialChunks|Where-Object BaseRevision -gt 0).Count -gt 0 -and @($b.MaterialChunks|Where-Object BaseRevision -gt 0).Count -gt 0}
        }catch{
            if($_.Exception.Message -like '*Replica fault*'){throw}
            # 报告文件写入窗口可能不完整，仅重读同一进程结果；不重启Native或吞掉完成后的错误。
        }
        if($ready){break};if([DateTime]::UtcNow -gt $end){throw ('Native checkpoint '+$phase+' not converged; processes retained')};Start-Sleep -Milliseconds 500
    }while($true)
    Copy-Item -LiteralPath $dsPath -Destination (Join-Path $e ('checkpoint'+$phase+'-ds.json'))
    Copy-Item -LiteralPath (Join-Path $private 'material-seed.json') -Destination (Join-Path $e ('checkpoint'+$phase+'-seed.json'))
    foreach($i in @(0,1)){Copy-Item -LiteralPath (Join-Path $private ('p7-client'+$i+'.report.json')) -Destination (Join-Path $e ('checkpoint'+$phase+'-client'+$i+'.json'))}
    if($phase -eq 1){
        $request=Join-Path $private 'material-seed.json.delta.request'
        $stream=[IO.File]::Open($request,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None);$stream.Dispose()
    }
}
& $h -Action Collect
& python (Join-Path $taskRoot 'check-native-delta.py')
if($LASTEXITCODE -ne 0){throw 'Native delta consistency failed; preserve evidence and processes'}
Status 'Passed' 'Two real clients installed actual Delta; both checkpoints match DS; awaiting owned cleanup'
