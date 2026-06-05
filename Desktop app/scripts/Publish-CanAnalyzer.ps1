[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputRoot = "",
    [switch]$NoReadyToRun,
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir "..")

$dotnetCliHome = Join-Path $repoRoot ".dotnet"
if (-not (Test-Path $dotnetCliHome)) {
    New-Item -ItemType Directory -Path $dotnetCliHome -Force | Out-Null
}

$env:DOTNET_CLI_HOME = $dotnetCliHome
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "artifacts"
}

if (-not (Test-Path $OutputRoot)) {
    New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
}

$publishDir = Join-Path $OutputRoot ("publish\" + $Runtime)
$projectPath = Join-Path $repoRoot "src\CanAnalyzer.App\CanAnalyzer.App.csproj"

if (Test-Path $publishDir) {
    Remove-Item -Path $publishDir -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

$publishArgs = @(
    "publish"
    $projectPath
    "-c", $Configuration
    "-r", $Runtime
    "--self-contained", "true"
    "-p:PublishSingleFile=true"
    "-p:IncludeNativeLibrariesForSelfExtract=true"
    "-p:EnableCompressionInSingleFile=true"
    "-p:PublishTrimmed=false"
    "-p:DebugType=None"
    "-p:DebugSymbols=false"
    "-o", $publishDir
)

if (-not $NoReadyToRun) {
    $publishArgs += "-p:PublishReadyToRun=true"
}

if ($NoRestore) {
    $publishArgs += "--no-restore"
}

Write-Host "Publishing CanAnalyzer ($Configuration, $Runtime)..."
dotnet @publishArgs

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$exePath = Join-Path $publishDir "CanAnalyzer.exe"
if (-not (Test-Path $exePath)) {
    throw "Expected executable not found: $exePath"
}

$portableDir = Join-Path $OutputRoot "portable"
if (-not (Test-Path $portableDir)) {
    New-Item -ItemType Directory -Path $portableDir -Force | Out-Null
}

$portableExe = Join-Path $portableDir ("CanAnalyzer-portable-" + $Runtime + ".exe")
Copy-Item -Path $exePath -Destination $portableExe -Force

Write-Host ""
Write-Host "Portable distributable created:"
Write-Host "  $portableExe"
Write-Host ""
Write-Host "Full publish folder:"
Write-Host "  $publishDir"
