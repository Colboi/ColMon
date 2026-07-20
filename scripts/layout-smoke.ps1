param([string]$Configuration = "Debug")

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\Colmon.App\Colmon.App.csproj"

dotnet build $project -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }

$dll = Join-Path $root "src\Colmon.App\bin\$Configuration\net10.0-windows\Colmon.dll"
$probe = & dotnet $dll --layout-probe | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw "Layout probe failed with exit code $LASTEXITCODE." }

$below = $probe.cases | Where-Object input -eq "9.9%"
$boundary = $probe.cases | Where-Object input -eq "10%"
$full = $probe.cases | Where-Object input -eq "100%"
$missing = $probe.cases | Where-Object input -eq "unavailable"
$checks = [ordered]@{
    fifteenCharacterColumns = $probe.characterColumns -eq 15
    pixelWidthMatchesCharacterGrid = $probe.pixelWidth -eq ($probe.characterColumns * $probe.characterCellWidth)
    belowTenIsRed = $below.isLow -and $below.color -eq "#FF4C4C" -and $below.formatted -eq "9%"
    tenIsNormal = -not $boundary.isLow -and $boundary.color -eq "#0078D7" -and $boundary.formatted -eq "10%"
    fullValueParses = $full.value -eq 100 -and $full.formatted -eq "100%"
    missingValueIsUnavailable = $null -eq $missing.value -and $missing.formatted -eq "--%"
    noConflictUsesNotificationAnchor = $probe.placementCases.noConflictX -eq 1891
    conflictMovesWindowLeft = $probe.placementCases.conflictX -eq 1551
    conflictIsResolved = [bool]$probe.placementCases.conflictResolved
    camelCaseRpcParses = $probe.rpcParserCases.camelCaseRemaining -eq 12
    snakeCaseRpcParses = $probe.rpcParserCases.snakeCaseRemaining -eq 95
    alternateBucketRpcParses = $probe.rpcParserCases.alternateRemaining -eq 75
    recentValueIsRetained = [bool]$probe.staleCases.recentValueRetained
    expiredValueIsRejected = -not $probe.staleCases.expiredValueRejected
    unavailableValueIsRejected = -not $probe.staleCases.unavailableRejected
}
$passed = -not ($checks.Values -contains $false)
[ordered]@{ passed = $passed; checks = $checks; probe = $probe } | ConvertTo-Json -Depth 6
if (-not $passed) { throw "Layout smoke assertions failed." }
