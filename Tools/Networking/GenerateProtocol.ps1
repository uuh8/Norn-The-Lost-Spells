#requires -Version 7.2
[CmdletBinding()]
param([switch]$Check)
. (Join-Path $PSScriptRoot 'Online.Common.ps1')
$layout = Get-OnlineLayout
$compiler = Join-Path $layout.Protocol "Tools/protoc/$($layout.Lock.protobuf.protocVersion)/bin/protoc.exe"
if (!(Test-Path -LiteralPath $compiler)) { throw '缺少 protoc，请先运行 Initialize-OnlineTools.ps1。' }
if ((& $compiler --version).Trim() -ne "libprotoc $($layout.Lock.protobuf.protocVersion)") { throw 'protoc 版本不匹配。' }
$schemaRoot = Join-Path $layout.Protocol 'Protos'
$generatedRoot = Join-Path $layout.Protocol 'Runtime/Generated'
$temporaryRoot = Join-Path $layout.Cache ('protocol-generation/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
$schemas = @(Get-ChildItem -LiteralPath $schemaRoot -Filter '*.proto' -File | Sort-Object Name)
if ($schemas.Count -eq 0) { throw '没有可生成的 Schema。' }
& $compiler "--proto_path=$schemaRoot" "--csharp_out=$temporaryRoot" @($schemas.FullName)
if ($LASTEXITCODE -ne 0) { throw 'protoc 生成失败。' }
$generatedFiles = @(Get-ChildItem -LiteralPath $temporaryRoot -Filter '*.cs' -File)
$existingFiles = @(if (Test-Path -LiteralPath $generatedRoot) { Get-ChildItem -LiteralPath $generatedRoot -Filter '*.cs' -File })
$unexpected = @($existingFiles | Where-Object { $_.Name -notin $generatedFiles.Name })
if ($unexpected.Count -gt 0) { throw '发现旧生成文件；请审阅 Schema 删除及兼容性后单独清理，不自动删除未知文件。' }
foreach ($generatedFile in $generatedFiles) {
    $destination = Join-Path $generatedRoot $generatedFile.Name
    if ($Check) {
        if (!(Test-Path -LiteralPath $destination) -or
            (Get-FileHash -LiteralPath $destination).Hash -ne (Get-FileHash -LiteralPath $generatedFile.FullName).Hash) {
            throw "生成代码未更新或不可重复生成：$destination"
        }
    } else {
        New-Item -ItemType Directory -Path $generatedRoot -Force | Out-Null
        Copy-Item -LiteralPath $generatedFile.FullName -Destination $destination -Force
    }
}
Write-Host "协议生成$($(if ($Check) { '校验' } else { '写入' }))通过：$($generatedFiles.Count) 个 C# 文件。"
