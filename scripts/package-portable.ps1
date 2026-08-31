param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "0.2.2"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\Colmon.App\Colmon.App.csproj"
$solution = Join-Path $root "Colmon.slnx"
$packageRoot = [IO.Path]::GetFullPath((Join-Path $root "artifacts\package"))
$packageName = "Colmon-$Version-$Runtime-portable"
$stagingRoot = [IO.Path]::GetFullPath((Join-Path $packageRoot "staging"))
$publishDirectory = Join-Path $stagingRoot "publish"
$payloadDirectory = Join-Path $stagingRoot $packageName
$zipPath = Join-Path $packageRoot "$packageName.zip"

function Assert-ChildPath([string]$Candidate, [string]$Parent) {
    $fullCandidate = [IO.Path]::GetFullPath($Candidate)
    $fullParent = [IO.Path]::GetFullPath($Parent).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    if (-not $fullCandidate.StartsWith(
        $fullParent + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the package directory: $fullCandidate"
    }
}

function Get-ChildRelativePath([string]$Candidate, [string]$Parent) {
    Assert-ChildPath $Candidate $Parent
    $fullCandidate = [IO.Path]::GetFullPath($Candidate)
    $fullParent = [IO.Path]::GetFullPath($Parent).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    return $fullCandidate.Substring($fullParent.Length + 1).Replace("\", "/")
}

foreach ($path in @($stagingRoot, $zipPath)) {
    Assert-ChildPath $path $packageRoot
}

New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
if (Test-Path -LiteralPath $stagingRoot) {
    Remove-Item -LiteralPath $stagingRoot -Recurse -Force
}
foreach ($path in @($zipPath)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Force
    }
}
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $payloadDirectory "config") -Force | Out-Null

dotnet build $solution -c $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE."
}

dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    --output $publishDirectory `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishTrimmed=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:Version=$Version
if ($LASTEXITCODE -ne 0) {
    throw "Publish failed with exit code $LASTEXITCODE."
}

$publishedExe = Join-Path $publishDirectory "Colmon.exe"
$publishedIcon = Join-Path $publishDirectory "colmon.ico"
foreach ($requiredFile in @($publishedExe, $publishedIcon)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Published file is missing: $requiredFile"
    }
}

$probe = & $publishedExe --layout-probe | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $probe.characterColumns -ne 15) {
    throw "Published executable probe failed."
}

Copy-Item -LiteralPath $publishedExe -Destination $payloadDirectory
Copy-Item -LiteralPath $publishedIcon -Destination $payloadDirectory
Copy-Item -LiteralPath (Join-Path $root "docs\portable-readme.txt") `
    -Destination (Join-Path $payloadDirectory "README.txt")
Copy-Item -LiteralPath (Join-Path $root "config\colmon.example.json") `
    -Destination (Join-Path $payloadDirectory "config\colmon.example.json")

$payloadFiles = Get-ChildItem -LiteralPath $payloadDirectory -File -Recurse |
    Sort-Object FullName |
    ForEach-Object {
        [ordered]@{
            path = Get-ChildRelativePath $_.FullName $payloadDirectory
            bytes = $_.Length
        }
    }
$manifest = [ordered]@{
    product = "Colmon"
    version = $Version
    runtime = $Runtime
    selfContained = $true
    generatedAt = (Get-Date).ToUniversalTime().ToString("o")
    privacy = [ordered]@{
        includesCredentials = $false
        includesCookies = $false
        includesUserSettings = $false
        includesLogs = $false
    }
    files = $payloadFiles
}
$manifest | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $payloadDirectory "manifest.json") -Encoding utf8

$unexpected = Get-ChildItem -LiteralPath $payloadDirectory -File -Recurse |
    ForEach-Object { Get-ChildRelativePath $_.FullName $payloadDirectory } |
    Where-Object { $_ -notin @(
        "Colmon.exe",
        "colmon.ico",
        "README.txt",
        "manifest.json",
        "config/colmon.example.json"
    ) }
if ($unexpected) {
    throw "Unexpected files entered the portable payload: $($unexpected -join ', ')"
}

Compress-Archive -LiteralPath $payloadDirectory -DestinationPath $zipPath -CompressionLevel Optimal

$result = [ordered]@{
    package = $zipPath
    bytes = (Get-Item -LiteralPath $zipPath).Length
    payloadFiles = (Get-ChildItem -LiteralPath $payloadDirectory -File -Recurse).Count
    publishedProbePassed = $true
}
$result | ConvertTo-Json -Depth 3
