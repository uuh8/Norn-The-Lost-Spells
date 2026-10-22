#requires -Version 7.2
[CmdletBinding()]
param()
. (Join-Path $PSScriptRoot 'Online.Common.ps1')
if (!$IsWindows -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') {
    throw 'N0-1 的自动准备脚本目前仅支持 Windows x64；Linux 构建在 N8 验证。'
}
$layout = Get-OnlineLayout
$downloads = Join-Path $layout.Cache 'downloads'
$sdkZip = Join-Path $downloads (Split-Path -Leaf $layout.Lock.dotnet.url)
Invoke-CheckedDownload $layout.Lock.dotnet.url $sdkZip SHA512 $layout.Lock.dotnet.sha512
$sdkRoot = Join-Path $layout.Cache 'dotnet'
if (!(Test-Path -LiteralPath (Join-Path $sdkRoot "sdk/$($layout.Lock.dotnet.sdk)/dotnet.dll"))) {
    Expand-Archive -LiteralPath $sdkZip -DestinationPath $sdkRoot -Force
}

$protocZip = Join-Path $downloads "protoc-$($layout.Lock.protobuf.protocVersion)-win64.zip"
Invoke-CheckedDownload $layout.Lock.protobuf.protocUrl $protocZip SHA256 $layout.Lock.protobuf.protocSha256
$protocRoot = Join-Path $layout.Protocol "Tools/protoc/$($layout.Lock.protobuf.protocVersion)"
if (!(Test-Path -LiteralPath (Join-Path $protocRoot 'bin/protoc.exe'))) {
    Expand-Archive -LiteralPath $protocZip -DestinationPath $protocRoot -Force
}

$packageFile = Join-Path $downloads "google.protobuf.$($layout.Lock.protobuf.version).nupkg"
Invoke-CheckedDownload $layout.Lock.protobuf.url $packageFile SHA256 $layout.Lock.protobuf.sha256
$packageRoot = Join-Path $downloads "google.protobuf.$($layout.Lock.protobuf.version)"
[IO.Compression.ZipFile]::ExtractToDirectory($packageFile, $packageRoot, $true)
$pluginRoot = Join-Path $layout.Unity 'Assets/ThirdParty/Networking/Protobuf'
New-Item -ItemType Directory -Path $pluginRoot -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $packageRoot "lib/$($layout.Lock.protobuf.unityTarget)/Google.Protobuf.dll") -Destination $pluginRoot -Force
Copy-Item -LiteralPath (Join-Path $packageRoot 'Google.Protobuf.nuspec') -Destination $pluginRoot -Force
$pluginPath = Join-Path $pluginRoot 'Google.Protobuf.dll'
if ((Get-FileHash -LiteralPath $pluginPath -Algorithm SHA256).Hash -ne $layout.Lock.protobuf.unityDllSha256) { throw 'Unity Protobuf DLL 摘要不匹配。' }
Invoke-CheckedDownload $layout.Lock.protobuf.licenseUrl (Join-Path $pluginRoot 'LICENSE.txt') SHA256 $layout.Lock.protobuf.licenseSha256
# Unity 提供 Span/Memory 相关 BCL，但不会给 Player 自动提供单独的 Unsafe 程序集。
# 只补实际失败证实缺少的依赖；来源、摘要、许可证一起固定，不复制另一套 System.Memory。
$unsafeLock = $layout.Lock.protobufUnityDependency
$unsafePackage = Join-Path $downloads "unsafe.$($unsafeLock.version).nupkg"
Invoke-CheckedDownload $unsafeLock.url $unsafePackage SHA256 $unsafeLock.sha256
$unsafeRoot = Join-Path $downloads "unsafe.$($unsafeLock.version)"
[IO.Compression.ZipFile]::ExtractToDirectory($unsafePackage, $unsafeRoot, $true)
$unsafeDll = Join-Path $unsafeRoot "lib/$($unsafeLock.unityTarget)/System.Runtime.CompilerServices.Unsafe.dll"
if ((Get-FileHash -LiteralPath $unsafeDll -Algorithm SHA256).Hash -ne $unsafeLock.unityDllSha256) { throw 'Unsafe DLL 摘要不匹配。' }
Copy-Item -LiteralPath $unsafeDll -Destination $pluginRoot -Force
Invoke-CheckedDownload $unsafeLock.licenseUrl (Join-Path $pluginRoot 'Unsafe.LICENSE.txt') SHA256 $unsafeLock.licenseSha256
Copy-Item -LiteralPath (Join-Path $unsafeRoot 'System.Runtime.CompilerServices.Unsafe.nuspec') -Destination $pluginRoot -Force
$dotNet = Get-OnlineDotNet $layout
Write-Host "项目 SDK：$dotNet；protoc：$protocRoot；Unity Runtime DLL：$pluginRoot"
