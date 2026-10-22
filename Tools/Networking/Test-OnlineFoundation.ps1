#requires -Version 7.2
[CmdletBinding()]
param()
. (Join-Path $PSScriptRoot 'Online.Common.ps1')
$layout = Get-OnlineLayout
& (Join-Path $PSScriptRoot 'GenerateProtocol.ps1') -Check
& (Join-Path $PSScriptRoot 'Invoke-OnlineDotNet.ps1') -DotNetArguments @('test', 'Norn.Online.sln', '--configuration', 'Release', '--logger', 'trx', '--results-directory', (Join-Path $layout.Server 'TestResults'))

# 使用真实 TCP/HTTP 进程补充 TestServer；端口由操作系统分配，不占用用户常用端口。
$portProbe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$portProbe.Start()
$servicePort = ([Net.IPEndPoint]$portProbe.LocalEndpoint).Port
$portProbe.Stop()
$instanceName = 'foundation-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$evidenceRoot = Join-Path $PSScriptRoot '.artifacts'
New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null
& (Join-Path $PSScriptRoot 'Start-LocalOnline.ps1') -NoBuild -Port $servicePort -InstanceName $instanceName
try {
    $serviceUrl = "http://127.0.0.1:$servicePort"
    $info = Invoke-RestMethod -Uri ($serviceUrl + '/api/system/info') -TimeoutSec 5
    if ($info.service -ne 'norn.business' -or $info.protocolVersion -ne 1) { throw '真实进程返回了错误的服务/协议版本。' }
    # common.proto 的公开 Wire Fixture：version=1、sequence=42、message="ping"。
    [byte[]]$requestBytes = 0x08,0x01,0x10,0x2A,0x1A,0x04,0x70,0x69,0x6E,0x67
    $replyPath = Join-Path $evidenceRoot 'ping-reply.bin'
    $response = Invoke-WebRequest -Uri ($serviceUrl + '/api/system/ping') -Method Post -ContentType 'application/x-protobuf' -Body $requestBytes -OutFile $replyPath -PassThru -TimeoutSec 5
    if ($response.StatusCode -ne 200 -or [Convert]::ToBase64String([IO.File]::ReadAllBytes($replyPath)) -ne [Convert]::ToBase64String($requestBytes)) {
        throw '真实 HTTP / Protobuf 往返校验失败。'
    }
    [byte[]]$wrongVersion = $requestBytes.Clone()
    $wrongVersion[1] = 2
    $versionResponse = Invoke-WebRequest -Uri ($serviceUrl + '/api/system/ping') -Method Post -ContentType 'application/x-protobuf' -Body $wrongVersion -SkipHttpErrorCheck -TimeoutSec 5
    if ($versionResponse.StatusCode -ne 409) { throw '真实进程没有拒绝错误协议版本。' }
    $largeResponse = Invoke-WebRequest -Uri ($serviceUrl + '/api/system/ping') -Method Post -ContentType 'application/x-protobuf' -Body ([byte[]]::new(1025)) -SkipHttpErrorCheck -TimeoutSec 5
    if ($largeResponse.StatusCode -ne 413) { throw '真实 Kestrel 入口没有拒绝超大请求。' }
    [PSCustomObject]@{
        Status = 'passed'
        RecordedAtUtc = [DateTime]::UtcNow.ToString('O')
        Sdk = $layout.Lock.dotnet.sdk
        Runtime = $layout.Lock.dotnet.runtime
        Protobuf = $layout.Lock.protobuf.version
        ProtocolVersion = $info.protocolVersion
        RealProcessHttpProtobuf = $true
        RealProcessVersionMismatchRejected = $true
        RealProcessOversizedPayloadRejected = $true
        KcpMultiplayerValidated = $false
        UnityDedicatedServerValidated = $false
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidenceRoot 'foundation-report.json') -Encoding utf8
    Write-Host '新业务服测试与真实进程 HTTP / Protobuf Smoke 通过。'
} finally {
    & (Join-Path $PSScriptRoot 'Stop-LocalOnline.ps1') -InstanceName $instanceName
}
