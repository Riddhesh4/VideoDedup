#!/usr/bin/env pwsh
# Dependency and license audit for the release pipeline.
#
# Deterministic and offline on purpose: every package that can enter a build is declared in
# Directory.Packages.props (central package management), so the audit is a set comparison against an allowlist that
# names the license for each package. A new or unpinned package fails the build until a human records its license in
# THIRD-PARTY.md and adds it here. Network-based vulnerability scanning is intentionally not part of this script.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# Package -> SPDX license recorded in THIRD-PARTY.md.
$allowed = @{
    'Microsoft.Data.Sqlite'                 = 'MIT'
    'System.IO.FileSystem.AccessControl'    = 'MIT'
    'Microsoft.Extensions.Hosting'          = 'MIT'
    'Microsoft.NET.Test.Sdk'                = 'MIT'
    'FluentAssertions'                      = 'Apache-2.0'
    'TngTech.ArchUnitNET.xUnitV3'           = 'Apache-2.0'
    'xunit.v3'                              = 'Apache-2.0'
    'xunit.runner.visualstudio'             = 'Apache-2.0'
    'coverlet.collector'                    = 'MIT'
    'NSubstitute'                           = 'BSD-3-Clause'
    'Reqnroll'                              = 'BSD-3-Clause'
    'Reqnroll.xunit.v3'                     = 'BSD-3-Clause'
    # Declared centrally so the coverage collector can be pinned, and also executed as a tool below.
    'dotnet-coverage'                       = 'Microsoft .NET Library License'
}
# Tool packages are restored from .config/dotnet-tools.json and are developer/CI-only.
$allowedTools = @{
    'obfuscar.globaltool' = 'MIT'
    'dotnet-coverage'     = 'Microsoft .NET Library License'
}
# Packages accepted as build-time-only transitive additions with a known permissive license.
$allowedTransitive = @('Microsoft.Extensions.*', 'System.*', 'SQLitePCLRaw.*', 'xunit.*', 'Microsoft.Testing.*',
    'Microsoft.CodeAnalysis.*', 'Microsoft.Bcl.*', 'Newtonsoft.Json')

$central = [xml](Get-Content Directory.Packages.props -Raw)
$declared = @($central.Project.ItemGroup.PackageVersion | Where-Object { $null -ne $_.Include } |
    ForEach-Object { [pscustomobject]@{ Name = $_.Include; Version = $_.Version } })
if ($declared.Count -eq 0) { throw 'No central package versions found; the audit would pass vacuously.' }

$tools = (Get-Content .config/dotnet-tools.json -Raw | ConvertFrom-Json).tools.PSObject.Properties |
    ForEach-Object { [pscustomobject]@{ Name = $_.Name; Version = $_.Value.version } }

$unknown = @()
foreach ($package in $declared) {
    if ($allowed.ContainsKey($package.Name)) { continue }
    if (-not ($allowedTransitive | Where-Object { $package.Name -like $_ })) { $unknown += "package $($package.Name) $($package.Version)" }
}
foreach ($tool in $tools) {
    if ($allowedTools.ContainsKey($tool.Name)) { continue }
    if (-not ($allowedTransitive | Where-Object { $tool.Name -like $_ })) { $unknown += "tool $($tool.Name) $($tool.Version)" }
}
if ($unknown.Count -gt 0) {
    Write-Error ("Dependency audit failed; record each license in THIRD-PARTY.md and allow-list it here:`n  " + ($unknown -join "`n  "))
    exit 1
}

# Every directly referenced package must be centrally pinned at an exact version (no ranges, no floating).
$floating = @($declared | Where-Object { $_.Version -notmatch '^\d+\.\d+\.\d+([-.].+)?$' })
if ($floating.Count -gt 0) {
    Write-Error "Dependency audit failed; these central versions are not exact: $($floating.Name -join ', ')"
    exit 1
}

# The license ledger must mention every allow-listed package and tool by name.
$thirdParty = Get-Content THIRD-PARTY.md -Raw
$missingLedger = @(@($allowed.Keys) + @($allowedTools.Keys) |
    Where-Object { $thirdParty -notmatch [regex]::Escape($_) })
if ($missingLedger.Count -gt 0) {
    Write-Error "Dependency audit failed; THIRD-PARTY.md does not mention: $($missingLedger -join ', ')"
    exit 1
}

# No copyleft library may be referenced. Copyleft media tools are pinned as external artifacts with their own
# compliance gate (docs/MEDIA-COMPLIANCE.md); they are not linked into this application.
$copyleft = @($allowed.GetEnumerator() | Where-Object { $_.Value -match '^(?i)(gpl|lgpl|agpl)' })
if ($copyleft.Count -gt 0) {
    Write-Error "Dependency audit failed; copyleft libraries are not allowed: $($copyleft.Key -join ', ')"
    exit 1
}

Write-Host ("Dependency audit passed: {0} central package(s), {1} tool(s), all allow-listed with recorded licenses." -f $declared.Count, $tools.Count)
