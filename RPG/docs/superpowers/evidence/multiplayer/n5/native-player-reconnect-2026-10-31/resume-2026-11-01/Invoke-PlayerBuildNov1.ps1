param([Parameter(Mandatory)][ValidateSet('hash','client','server')][string]$Kind)
$ErrorActionPreference='Stop';$u='F:\Develop Project\RPG\RPG';$e=Join-Path $u 'docs/superpowers/evidence/multiplayer/n5/native-player-reconnect-2026-10-31/resume-2026-11-01'
function Read($p){if(Test-Path $p){Get-Content -LiteralPath $p -Raw|ConvertFrom-Json}}
function Utc($v){if($v -is [DateTime]){return $v.ToUniversalTime()};[DateTime]::Parse($v,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()}
if((Read (Join-Path $e 'regression-progress.json')).State -ne 'Finished'){throw 'Regression not finished; no build submitted'}
$cmd=switch($Kind){hash{'refresh-online-hashes'}client{'build-account-menu-client'}server{'build-room-server'}}
if(Test-Path (Join-Path $e ($Kind+'-command.json'))){throw 'Already recorded; inspect before duplicate operation'}
$request=Join-Path $u 'Temp/OnlineLab/command.request';if(Test-Path $request){throw 'Another request exists'}
$after=[DateTime]::UtcNow
@{Kind=$Kind;Command=$cmd;State='Requested';SubmittedUtc=$after.ToString('O')}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $e ($Kind+'-progress.json')) -Encoding utf8
$stream=[IO.File]::Open($request,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None);try{$b=[Text.Encoding]::UTF8.GetBytes($cmd);$stream.Write($b,0,$b.Length)}finally{$stream.Dispose()}
$end=$after.AddMinutes(40)
do{$v=Read (Join-Path $u 'Temp/OnlineLab/command-result.json');if($v -and $v.Command -eq $cmd -and (Utc $v.RecordedAtUtc) -gt $after){break};if([DateTime]::UtcNow -gt $end){throw 'Observer timeout; original request uncertain, do not resend'};Start-Sleep -Milliseconds 1000}while($true)
Copy-Item -LiteralPath (Join-Path $u 'Temp/OnlineLab/command-result.json') -Destination (Join-Path $e ($Kind+'-command.json'))
if($Kind -ne 'hash'){
 $artifact=if($Kind -eq 'client'){'account-menu-client'}else{'room-server'}
 Copy-Item -LiteralPath (Join-Path $u ('Tools/Networking/.artifacts/'+$artifact+'-build.json')) -Destination (Join-Path $e ($Kind+'-build.json'))
}
@{Kind=$Kind;Command=$cmd;State='Returned';Passed=$v.Passed;RecordedUtc=[DateTime]::UtcNow.ToString('O')}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $e ($Kind+'-progress.json')) -Encoding utf8
if(!$v.Passed){throw $v.Error};Write-Output ($cmd+' returned '+$v.RecordedAtUtc)
# 不自动接下一构建：用户暂停时不会有隐藏的DS接续。
