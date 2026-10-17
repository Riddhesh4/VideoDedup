#!/usr/bin/env pwsh
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

dotnet publish src/VideoDedup.Domain/VideoDedup.Domain.csproj -c Release -o publish
if ($LASTEXITCODE -ne 0) {
    throw "Domain publish failed with exit code $LASTEXITCODE"
}

dotnet tool restore
if ($LASTEXITCODE -ne 0) {
    throw "Local tool restore failed with exit code $LASTEXITCODE"
}

& "$PSScriptRoot\obfuscate.ps1"
if ($LASTEXITCODE -ne 0) {
    throw "Obfuscation failed with exit code $LASTEXITCODE"
}

$assemblyPath = Join-Path $root "publish\VideoDedup.Domain.dll"
$bytes = [IO.File]::ReadAllBytes($assemblyPath)
$text = [Text.Encoding]::GetEncoding(28591).GetString($bytes)
if ($text.Contains("ScanState") -or $text.Contains("IScanOrchestrator")) {
    throw "Obfuscation gate failed: recognizable type names survived in $assemblyPath"
}

Write-Host "Obfuscation gate passed: type names stripped."
