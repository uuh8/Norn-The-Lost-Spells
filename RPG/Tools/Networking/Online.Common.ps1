#requires -Version 7.2
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-OnlineLayout {
    # 从脚本自身定位，避免从 IDE / 任意终端目录调用时误写到另一层 RPG。
    $unityRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
    $workspaceRoot = Split-Path -Parent $unityRoot
    [PSCustomObject]@{
        Unity = $unityRoot
        Workspace = $workspaceRoot
        Server = Join-Path $workspaceRoot 'Server'
        Protocol = Join-Path $workspaceRoot 'Shared/Protocol'
        Cache = Join-Path $unityRoot '.tools'
        Lock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'dependency-lock.json') -Raw | ConvertFrom-Json
    }
}

function Get-OnlineDotNet {
    param($Layout)
    $localDotNet = Join-Path $Layout.Cache 'dotnet/dotnet.exe'
    $candidate = if (Test-Path -LiteralPath $localDotNet) { $localDotNet } else { (Get-Command dotnet -ErrorAction Stop).Source }
    Push-Location -LiteralPath $Layout.Server
    try {
        $actualVersion = & $candidate --version
        if ($LASTEXITCODE -ne 0 -or $actualVersion.Trim() -ne $Layout.Lock.dotnet.sdk) {
            throw 'SDK 与锁定版本不同，请先执行 Initialize-OnlineTools.ps1。'
        }
    } finally { Pop-Location }
    return $candidate
}

function Invoke-CheckedDownload {
    param([string]$Url, [string]$Destination, [string]$Algorithm, [string]$ExpectedHash)
    New-Item -ItemType Directory -Path (Split-Path -Parent $Destination) -Force | Out-Null
    if (!(Test-Path -LiteralPath $Destination)) {
        $previousProgress = $ProgressPreference
        try {
            $ProgressPreference = 'SilentlyContinue'
            # 先下载到临时文件；网络中断不能留下一个会被误认成完整包的缓存。
            Invoke-WebRequest -Uri $Url -OutFile ($Destination + '.partial') -TimeoutSec 1200
            Move-Item -LiteralPath ($Destination + '.partial') -Destination $Destination
        } finally { $ProgressPreference = $previousProgress }
    }
    $actualHash = (Get-FileHash -LiteralPath $Destination -Algorithm $Algorithm).Hash
    if ($actualHash -ne $ExpectedHash) { throw "依赖摘要不匹配，请检查缓存：$Destination" }
}
