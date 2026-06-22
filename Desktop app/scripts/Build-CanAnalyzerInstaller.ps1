[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputRoot = "",
    [switch]$SkipPublish,
    [switch]$NoRestore,
    [string]$InnoSetupCompilerPath = ""
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir "..")

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "artifacts"
}
if (-not (Test-Path $OutputRoot)) {
    New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
}

if (-not $SkipPublish) {
    $publishScript = Join-Path $scriptDir "Publish-CanAnalyzer.ps1"
    & $publishScript -Configuration $Configuration -Runtime $Runtime -OutputRoot $OutputRoot -NoRestore:$NoRestore
    if ($LASTEXITCODE -ne 0) {
        throw "Publish step failed."
    }
}

$publishDir = Join-Path $OutputRoot ("publish\" + $Runtime)
if (-not (Test-Path (Join-Path $publishDir "CanAnalyzer.exe"))) {
    throw "Publish output not found in $publishDir. Run Publish-CanAnalyzer.ps1 first."
}

function Get-InnoCompilerPath {
    param([string]$PreferredPath)

    if (-not [string]::IsNullOrWhiteSpace($PreferredPath) -and (Test-Path $PreferredPath)) {
        return (Resolve-Path $PreferredPath).Path
    }

    $cmd = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($cmd) {
        return $cmd.Source
    }

    $candidates = @(
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe"
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    return $null
}

function Get-AppVersion {
    param([string]$ProjectPath)

    try {
        [xml]$xml = Get-Content -Path $ProjectPath
        $version = $xml.Project.PropertyGroup.Version | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1
        if (-not [string]::IsNullOrWhiteSpace($version)) {
            return $version.Trim()
        }
    }
    catch {
        # Ignore and use fallback.
    }

    return ("1.0." + (Get-Date -Format "yyyyMMdd"))
}

$isccPath = Get-InnoCompilerPath -PreferredPath $InnoSetupCompilerPath
if ([string]::IsNullOrWhiteSpace($isccPath)) {
    throw "Inno Setup compiler (ISCC.exe) not found. Install Inno Setup 6 from https://jrsoftware.org/isdl.php"
}

$installerOutDir = Join-Path $OutputRoot "installer"
if (-not (Test-Path $installerOutDir)) {
    New-Item -ItemType Directory -Path $installerOutDir -Force | Out-Null
}

$projectPath = Join-Path $repoRoot "src\CanAnalyzer.App\CanAnalyzer.App.csproj"
$appVersion = Get-AppVersion -ProjectPath $projectPath
$issPath = Join-Path $repoRoot "installer\CanAnalyzer.iss"

if (-not (Test-Path $issPath)) {
    throw "Installer script not found: $issPath"
}

Write-Host "Building installer with Inno Setup..."
& $isccPath `
    ("/DSourceDir=$publishDir") `
    ("/DOutputDir=$installerOutDir") `
    ("/DAppVersion=$appVersion") `
    "/DAppName=CANalyser" `
    "/DAppPublisher=Gyrari B.V." `
    "/DExeName=CanAnalyzer.exe" `
    $issPath

if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE."
}

$installerFile = Get-ChildItem -Path $installerOutDir -Filter "CanAnalyzer-Setup-*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1

if (-not $installerFile) {
    throw "Installer build finished, but no Setup.exe was found in $installerOutDir."
}

Write-Host ""
Write-Host "Installer created:"
Write-Host "  $($installerFile.FullName)"
