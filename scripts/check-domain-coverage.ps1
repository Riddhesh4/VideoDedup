$ErrorActionPreference = 'Stop'
$reportPath = 'TestResults/Domain-Coverage.cobertura.xml'
$minimum = 0.90

if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
    Write-Error "Coverage report is missing: $reportPath"
    exit 1
}

try {
    [xml]$report = Get-Content -LiteralPath $reportPath -Raw
} catch {
    Write-Error "Cannot parse coverage report '$reportPath': $_"
    exit 1
}

$packages = @($report.coverage.packages.package | Where-Object { $_.name -eq 'VideoDedup.Domain' })
if ($packages.Count -ne 1) {
    Write-Error "Expected exactly one VideoDedup.Domain package, found $($packages.Count)"
    exit 1
}

[double]$rate = 0
if (-not [double]::TryParse($packages[0].'line-rate', [Globalization.NumberStyles]::Float,
        [Globalization.CultureInfo]::InvariantCulture, [ref]$rate) -or
    [double]::IsNaN($rate) -or [double]::IsInfinity($rate) -or $rate -lt 0 -or $rate -gt 1) {
    Write-Error "Domain line-rate is missing or out of range: '$($packages[0].'line-rate')'"
    exit 1
}

Write-Host ("VideoDedup.Domain line coverage: {0:P2}; minimum: {1:P0}" -f $rate, $minimum)
if ($rate -lt $minimum) {
    Write-Error 'Domain line coverage is below the required minimum.'
    exit 1
}
