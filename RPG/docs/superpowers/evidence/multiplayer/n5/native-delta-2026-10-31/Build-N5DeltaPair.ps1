$ErrorActionPreference='Stop'
$unity='F:\Develop Project\RPG\RPG'
$e=Join-Path $unity 'docs/superpowers/evidence/multiplayer/n5/native-delta-2026-10-31'
$temp=Join-Path $unity 'Temp'
function Read-Json($p){Get-Content -LiteralPath $p -Raw|ConvertFrom-Json}
function Utc($x){if($x -is [DateTime]){return $x.ToUniversalTime()};[DateTime]::Parse($x,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()}
function Status($s,$d){@{State=$s;Detail=$d;RecordedUtc=[DateTime]::UtcNow.ToString('O')}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $e 'build-progress.json') -Encoding utf8}
function Wait-Test($after){
    $end=[DateTime]::UtcNow.AddMinutes(10)
    do{
        $v=Read-Json (Join-Path $temp 'SinglePlayer/editmode-report.json')
        if((Utc $v.UpdatedUtc) -gt $after -and $v.State -in @('Finished','Blocked','Error')){
            if($v.State -ne 'Finished' -or $v.Failed -ne 0){throw ('Test failure: '+($v|ConvertTo-Json -Compress))};return $v
        }
        if([DateTime]::UtcNow -gt $end){throw 'Test wait timed out; do not rerun or terminate Editor'}
        Start-Sleep -Milliseconds 750
    }while($true)
}
function Submit($command){
    $path=Join-Path $temp 'OnlineLab/command.request'
    if(Test-Path $path){throw 'Another Editor request exists'}
    $now=[DateTime]::UtcNow
    $stream=[IO.File]::Open($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try{$b=[Text.Encoding]::UTF8.GetBytes($command);$stream.Write($b,0,$b.Length)}finally{$stream.Dispose()}
    return $now
}
function Wait-Command($command,$after){
    $end=[DateTime]::UtcNow.AddMinutes(20)
    do{
        $v=Read-Json (Join-Path $temp 'OnlineLab/command-result.json')
        if($v.Command -eq $command -and (Utc $v.RecordedAtUtc) -gt $after){if(!$v.Passed){throw ('Editor operation failed: '+$v.Error)};return $v}
        if([DateTime]::UtcNow -gt $end){throw 'Operation wait timed out; outcome uncertain, do not rerun'}
        Start-Sleep -Milliseconds 750
    }while($true)
}
Status 'WaitingGreen' 'Current coordinator fixture, no duplicate request'
$red=Read-Json (Join-Path $e 'identity-red-report.json')
$g=Wait-Test (Utc $red.UpdatedUtc);if($g.Passed -ne 15){throw 'Unexpected GREEN case count'}
Copy-Item -LiteralPath (Join-Path $temp 'SinglePlayer/editmode-report.json') -Destination (Join-Path $e 'identity-green-report.json')
Copy-Item -LiteralPath (Join-Path $temp 'SinglePlayer/editmode-results.xml') -Destination (Join-Path $e 'identity-green-results.xml')
$request=Join-Path $temp 'SinglePlayer/run-editmode.request.json';if(Test-Path $request){throw 'Another test request exists'}
$submitted=[DateTime]::UtcNow
Copy-Item -LiteralPath (Join-Path $unity 'docs/superpowers/evidence/multiplayer/n5/interest-recovery-2026-10-31/full-request.json') -Destination (Join-Path $e 'full-request.json')
Copy-Item -LiteralPath (Join-Path $e 'full-request.json') -Destination $request
Status 'FullRegression' '26 existing fixtures; expected 308 including one diagnostic identity case'
$all=Wait-Test $submitted;if($all.Passed -ne 308){throw 'Unexpected full case count'}
Copy-Item -LiteralPath (Join-Path $temp 'SinglePlayer/editmode-report.json') -Destination (Join-Path $e 'full-report.json')
Copy-Item -LiteralPath (Join-Path $temp 'SinglePlayer/editmode-results.xml') -Destination (Join-Path $e 'full-results.xml')
Status 'HashRefresh' 'No resource export; source-only diagnostic changes'
$start=Submit 'refresh-online-hashes';$v=Wait-Command 'refresh-online-hashes' $start
Copy-Item -LiteralPath (Join-Path $temp 'OnlineLab/command-result.json') -Destination (Join-Path $e 'hash-refresh.json')
foreach($kind in @('client','server')){
    Status 'Building' ('build-room-'+$kind)
    $start=Submit ('build-room-'+$kind);$v=Wait-Command ('build-room-'+$kind) $start
    Copy-Item -LiteralPath (Join-Path $unity ('Tools/Networking/.artifacts/room-'+$kind+'-build.json')) -Destination (Join-Path $e ($kind+'-build.json'))
    Copy-Item -LiteralPath (Join-Path $temp 'OnlineLab/command-result.json') -Destination (Join-Path $e ($kind+'-command.json'))
    Write-Output ($kind+' build done '+$v.RecordedAtUtc)
}
Status 'Finished' 'Matched Native pair; tests 308/0; no service launched by this script'
