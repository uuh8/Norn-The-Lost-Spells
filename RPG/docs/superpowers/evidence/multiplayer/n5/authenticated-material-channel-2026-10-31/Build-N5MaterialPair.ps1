$ErrorActionPreference='Stop'
$unity='F:\Develop Project\RPG\RPG'
$evidence=Join-Path $unity 'docs/superpowers/evidence/multiplayer/n5/authenticated-material-channel-2026-10-31'
$request=Join-Path $unity 'Temp/OnlineLab/command.request'
$result=Join-Path $unity 'Temp/OnlineLab/command-result.json'
$status=Join-Path $evidence 'final-native-build-progress.json'
function Read-Result {Get-Content -LiteralPath $result -Raw|ConvertFrom-Json}
function Wait-Result($Command,$After){
    $deadline=[DateTime]::UtcNow.AddMinutes(20)
    do {
        $r=Read-Result
        # 新版ConvertFrom-Json已将ISO时间解析为UTC DateTime；再Parse会丢Kind并错误减去本机时区。
        $recorded=if($r.RecordedAtUtc -is [DateTime]){$r.RecordedAtUtc.ToUniversalTime()}else{[DateTime]::Parse($r.RecordedAtUtc,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()}
        if($r.Command -eq $Command -and $recorded -gt $After){
            if(!$r.Passed){throw ('Unity operation failed: '+$r.Error)}
            return $r
        }
        if([DateTime]::UtcNow -gt $deadline){throw '等待超时：不终止Unity，也不重试结果不确定的操作'}
        Start-Sleep -Milliseconds 750
    } while($true)
}
# 当前已有唯一Hash刷新在运行；先等待其新结果，不插入第二个同类操作。
$hash=Wait-Result 'refresh-online-hashes' ([DateTime]::Parse('2026-10-31T12:12:25Z').ToUniversalTime())
Copy-Item -LiteralPath $result -Destination (Join-Path $evidence 'final-hash-refresh.json')
foreach($kind in @('client','server')){
    if(Test-Path $request){throw '存在其他Unity请求，停止排队，保留用户操作'}
    $submitted=[DateTime]::UtcNow
    @{State='Running';Command=('build-room-'+$kind);SubmittedUtc=$submitted.ToString('O')}|ConvertTo-Json|Set-Content -LiteralPath $status -Encoding utf8
    $stream=[IO.File]::Open($request,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try{$bytes=[Text.Encoding]::UTF8.GetBytes('build-room-'+$kind);$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()}
    $r=Wait-Result ('build-room-'+$kind) $submitted
    Copy-Item -LiteralPath (Join-Path $unity ('Tools/Networking/.artifacts/room-'+$kind+'-build.json')) -Destination (Join-Path $evidence ('final-'+$kind+'-build.json'))
    Copy-Item -LiteralPath $result -Destination (Join-Path $evidence ('final-'+$kind+'-command.json'))
    Write-Output ($kind+' final Native build succeeded at '+$r.RecordedAtUtc)
}
@{State='Finished';MatchedPair=$true;RecordedUtc=[DateTime]::UtcNow.ToString('O')}|ConvertTo-Json|Set-Content -LiteralPath $status -Encoding utf8
