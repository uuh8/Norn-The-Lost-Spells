param(
    [Parameter(Mandatory=$true)][string]$Label,
    [Parameter(Mandatory=$true)][string[]]$TestNames,
    [switch]$PlayMode,
    [int]$TimeoutSeconds=360
)
$ErrorActionPreference='Stop'
$root=Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../../../..')
Set-Location -LiteralPath $root
New-Item -ItemType Directory -Path 'Temp/SinglePlayer','Temp/ElementPerformance' -Force | Out-Null
Set-Content -LiteralPath 'Temp/ElementPerformance/grid-liquid-capture.request' -Value 'refresh' -Encoding utf8
$deadline=[DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$sources=@{}
foreach($folder in Get-ChildItem Assets/_Project/Scripts -Directory){$sources['Game.'+$folder.Name]=$folder.FullName}
$sources['Game.Project.Editor']=(Join-Path $root 'Assets/_Project/Editor')
foreach($assembly in Get-ChildItem Assets/_Project/Tests -Filter '*.asmdef' -Recurse){$name=(Get-Content $assembly.FullName -Raw | ConvertFrom-Json).name;$sources[$name]=$assembly.DirectoryName}
while($true){
    $fresh=$true
    foreach($entry in $sources.GetEnumerator()){
        $latest=Get-ChildItem -LiteralPath $entry.Value -Filter '*.cs' -File -Recurse | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
        $dll=Get-Item -LiteralPath ('Library/ScriptAssemblies/'+$entry.Key+'.dll') -ErrorAction SilentlyContinue
        if(!$dll -or ($latest -and $dll.LastWriteTimeUtc -lt $latest.LastWriteTimeUtc)){$fresh=$false;break}
    }
    if($fresh -and !(Test-Path -LiteralPath 'Temp/ElementPerformance/grid-liquid-capture.request')){break}
    if([DateTime]::UtcNow -ge $deadline){throw 'Current source assemblies did not compile; inspect Editor.log.'}
    Start-Sleep -Milliseconds 500
}
$mode=if($PlayMode){'playmode'}else{'editmode'}
$requested=[DateTime]::UtcNow
@{TestNames=$TestNames} | ConvertTo-Json | Set-Content -LiteralPath ('Temp/SinglePlayer/run-'+$mode+'.request.json') -Encoding utf8
while($true){
    $reportPath='Temp/SinglePlayer/'+$mode+'-report.json'
    if(Test-Path -LiteralPath $reportPath){
        $report=Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        if(([DateTime]$report.UpdatedUtc).ToUniversalTime() -ge $requested){
            if($report.State -in @('Blocked','Error')){throw $report.Error}
            if($report.State -eq 'Finished'){break}
        }
    }
    if([DateTime]::UtcNow -ge $deadline){throw 'Native TestRunner timed out; no success claimed.'}
    Start-Sleep -Milliseconds 500
}
[xml]$xml=Get-Content -LiteralPath ('Temp/SinglePlayer/'+$mode+'-results.xml') -Raw
foreach($test in $TestNames){
    if(@($xml.SelectNodes('//test-case') | Where-Object {$_.fullname -eq $test -or $_.fullname.StartsWith($test+'.')}).Count -eq 0){throw ('Test not discovered: '+$test)}
}
Copy-Item -LiteralPath ('Temp/SinglePlayer/'+$mode+'-results.xml') -Destination (Join-Path $PSScriptRoot ($Label+'.xml'))
Copy-Item -LiteralPath $reportPath -Destination (Join-Path $PSScriptRoot ($Label+'.json'))
Write-Output ($Label+': Passed='+$report.Passed+' Failed='+$report.Failed)
foreach($failure in $xml.SelectNodes('//test-case[@result="Failed"]')){Write-Output ($failure.fullname+': '+$failure.failure.message.'#cdata-section')}
if($report.Failed -gt 0){exit 1}
