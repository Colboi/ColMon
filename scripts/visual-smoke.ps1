param(
    [switch]$KeepRunning,
    [switch]$LiveCodex,
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\Colmon.App\Colmon.App.csproj"
$config = if ($LiveCodex) { $null } else { Join-Path $root "config\visual-smoke.json" }
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$artifactKind = if ($LiveCodex) { "live-codex-smoke" } else { "visual-smoke" }
$runDirectory = Join-Path $root "artifacts\$artifactKind\$stamp"
$statePath = Join-Path $runDirectory "state.json"
$probePath = Join-Path $runDirectory "taskbar-probe.json"
$screenshotPath = Join-Path $runDirectory "taskbar.png"
$detailPath = Join-Path $runDirectory "taskbar-detail.png"
$resultPath = Join-Path $runDirectory "result.json"

New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
dotnet build $project -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }

$dll = Join-Path $root "src\Colmon.App\bin\$Configuration\net10.0-windows\Colmon.dll"
& dotnet $dll --probe | Set-Content -LiteralPath $probePath -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw "Taskbar probe failed with exit code $LASTEXITCODE." }

$appArguments = @($dll)
if ($config) { $appArguments += @("--config", $config) }
$appArguments += @("--artifact-dir", $runDirectory)
$process = Start-Process dotnet -ArgumentList $appArguments -PassThru
try {
    $deadline = (Get-Date).AddSeconds(15)
    while (-not (Test-Path -LiteralPath $statePath)) {
        if ($process.HasExited) { throw "Colmon exited early with code $($process.ExitCode)." }
        if ((Get-Date) -gt $deadline) { throw "Timed out waiting for taskbar state evidence." }
        Start-Sleep -Milliseconds 200
    }

    if ($LiveCodex) {
        $dataDeadline = (Get-Date).AddSeconds(25)
        do {
            Start-Sleep -Milliseconds 250
            try { $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json }
            catch { $state = $null }
            if ($state -and $null -ne $state.display.remainingPercent) { break }
            if ($process.HasExited) { throw "Colmon exited before live Codex data arrived." }
        } while ((Get-Date) -lt $dataDeadline)
        if (-not $state -or $null -eq $state.display.remainingPercent) {
            throw "Timed out waiting for live Codex weekly data."
        }
    }
    else {
        Start-Sleep -Seconds 2
        $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    }
    $taskbar = $state.rectangles.taskbar
    $app = $state.rectangles.app

    Add-Type -AssemblyName System.Drawing
    $bitmap = [System.Drawing.Bitmap]::new($taskbar.width, $taskbar.height)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CopyFromScreen($taskbar.left, $taskbar.top, 0, 0, $bitmap.Size)
        }
        finally { $graphics.Dispose() }
        $bitmap.Save($screenshotPath, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $bitmap.Dispose() }

    $detailLeft = [Math]::Max($taskbar.left, $app.left - 40)
    $detailRight = [Math]::Min($taskbar.right, $state.rectangles.notificationArea.left + 40)
    $detailWidth = $detailRight - $detailLeft
    $detailBitmap = [System.Drawing.Bitmap]::new($detailWidth, $taskbar.height)
    try {
        $detailGraphics = [System.Drawing.Graphics]::FromImage($detailBitmap)
        try {
            $detailGraphics.CopyFromScreen($detailLeft, $taskbar.top, 0, 0, $detailBitmap.Size)
        }
        finally { $detailGraphics.Dispose() }
        $detailBitmap.Save($detailPath, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $detailBitmap.Dispose() }

    $insideTaskbar = $app.left -ge $taskbar.left -and $app.top -ge $taskbar.top -and
        $app.right -le $taskbar.right -and $app.bottom -le $taskbar.bottom
    $fixedCharacterWidth = $state.display.pixelWidth -eq
        ($state.display.characterColumns * $state.display.characterCellWidth)
    $checks = [ordered]@{
        attachedToTaskbar = [bool]$state.attached
        containedInTaskbar = [bool]$insideTaskbar
        nonZeroGeometry = [bool]($app.width -gt 0 -and $app.height -gt 0)
        fifteenCharacterColumns = [bool]($state.display.characterColumns -eq 15)
        pixelWidthMatchesCharacterGrid = [bool]$fixedCharacterWidth
    }
    if ($LiveCodex) {
        $trafficMonitorRunning = $null -ne (Get-Process TrafficMonitor -ErrorAction SilentlyContinue)
        $trafficMonitorDetected = $null -ne ($state.placement.occupiedWindows |
            Where-Object processName -eq "TrafficMonitor" | Select-Object -First 1)
        $checks.liveCodexValuePresent = [bool]($null -ne $state.display.remainingPercent)
        $checks.externalWindowOverlapAvoided = [bool](-not $state.placement.overlapsExternalWindow)
        $checks.runningTrafficMonitorDetected = [bool](-not $trafficMonitorRunning -or $trafficMonitorDetected)
    }
    else {
        $checks.belowTenPercentUsesLowState = [bool]$state.display.isLow
    }
    $passed = -not ($checks.Values -contains $false)
    $result = [ordered]@{
        timestamp = Get-Date -Format o
        passed = $passed
        checks = $checks
        processId = $process.Id
        state = $statePath
        screenshot = $screenshotPath
        detailScreenshot = $detailPath
        log = Join-Path $runDirectory "colmon.jsonl"
    }
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resultPath -Encoding utf8
    $result | ConvertTo-Json -Depth 6
    if (-not $result.passed) { throw "Visual smoke assertions failed. See $resultPath" }
}
finally {
    if (-not $KeepRunning -and -not $process.HasExited) {
        Stop-Process -Id $process.Id
        [void]$process.WaitForExit(5000)
    }
}
