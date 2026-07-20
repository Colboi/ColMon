param([string]$Configuration = "Debug")

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\Colmon.App\Colmon.App.csproj"
$config = Join-Path $root "config\visual-smoke.json"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$runDirectory = Join-Path $root "artifacts\control-smoke\$stamp"
$logPath = Join-Path $runDirectory "colmon.jsonl"
$resultPath = Join-Path $runDirectory "result.json"

New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
dotnet build $project -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }

$dll = Join-Path $root "src\Colmon.App\bin\$Configuration\net10.0-windows\Colmon.dll"
& dotnet $dll --config $config --artifact-dir $runDirectory --control-smoke
$applicationExitCode = $LASTEXITCODE

$events = Get-Content -LiteralPath $logPath | ForEach-Object { $_ | ConvertFrom-Json }
$hiddenEvent = $events | Where-Object name -eq "control-smoke.hidden" | Select-Object -Last 1
$shownEvent = $events | Where-Object name -eq "control-smoke.shown" | Select-Object -Last 1
$exitEvent = $events | Where-Object name -eq "tray.exit.clicked" | Select-Object -Last 1
$trayDisposedEvent = $events | Where-Object name -eq "tray.disposed" | Select-Object -Last 1

$checks = [ordered]@{
    processExitedSuccessfully = $applicationExitCode -eq 0
    hideCommandHidEveryWindow = $null -ne $hiddenEvent -and $hiddenEvent.data.visibleCount -eq 0
    hiddenStateOffersShowCommand = $null -ne $hiddenEvent -and $hiddenEvent.data.menuAction -eq "show"
    showCommandShowedEveryWindow = $null -ne $shownEvent -and
        $shownEvent.data.windowCount -gt 0 -and $shownEvent.data.visibleCount -eq $shownEvent.data.windowCount
    shownStateOffersHideCommand = $null -ne $shownEvent -and $shownEvent.data.menuAction -eq "hide"
    exitCommandWasInvoked = $null -ne $exitEvent
    trayIconWasDisposed = $null -ne $trayDisposedEvent
}
$passed = -not ($checks.Values -contains $false)
$result = [ordered]@{
    timestamp = Get-Date -Format o
    passed = $passed
    checks = $checks
    log = $logPath
}
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resultPath -Encoding utf8
$result | ConvertTo-Json -Depth 6
if (-not $passed) { throw "Control smoke assertions failed. See $resultPath" }
