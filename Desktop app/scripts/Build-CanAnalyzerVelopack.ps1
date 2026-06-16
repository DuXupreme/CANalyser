[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "",
    [string]$OutputRoot = "",
    [string]$Channel = "win",
    [switch]$NoRestore,
    [switch]$Upload,
    [string]$RepoUrl = "https://github.com/DuXupreme/CANalyser",
    [string]$GitHubToken = $env:GITHUB_TOKEN
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir "..")

# Houd dotnet-state lokaal in de repo, net als de andere build-scripts.
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

$projectPath = Join-Path $repoRoot "src\CanAnalyzer.App\CanAnalyzer.App.csproj"
$splashImage = Join-Path $repoRoot "installer\velopack\splash.png"
$iconPath = Join-Path $repoRoot "installer\velopack\app.ico"

# --- Versie bepalen (uit csproj indien niet meegegeven) ---
function Get-AppVersion {
    param([string]$ProjectPath)
    [xml]$xml = Get-Content -Path $ProjectPath
    $v = $xml.Project.PropertyGroup.Version | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($v)) {
        throw "Geen <Version> gevonden in $ProjectPath; geef -Version mee."
    }
    return $v.Trim()
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-AppVersion -ProjectPath $projectPath
}
Write-Host "CanAnalyzer Velopack-release v$Version ($Configuration, $Runtime, kanaal '$Channel')"

# --- 1. Publish (GEEN single-file: Velopack wil een normale publish-map) ---
$publishDir = Join-Path $OutputRoot ("velopack\publish\" + $Runtime)
if (Test-Path $publishDir) {
    Remove-Item -Path $publishDir -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

$publishArgs = @(
    "publish", $projectPath
    "-c", $Configuration
    "-r", $Runtime
    "--self-contained", "true"
    "-p:PublishSingleFile=false"
    "-p:PublishReadyToRun=true"
    "-p:DebugType=None"
    "-p:DebugSymbols=false"
    "-o", $publishDir
)
if ($NoRestore) { $publishArgs += "--no-restore" }

Write-Host "Publiceren..."
dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish faalde met exitcode $LASTEXITCODE." }

if (-not (Test-Path (Join-Path $publishDir "CanAnalyzer.exe"))) {
    throw "CanAnalyzer.exe niet gevonden in $publishDir."
}

# --- 2. vpk (local tool, gepind in .config/dotnet-tools.json) ---
Push-Location $repoRoot
try {
    Write-Host "vpk-tool herstellen..."
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore faalde." }

    # Releases-map leegmaken: lokale restanten van een vorige run zouden
    # botsen ("versie bestaat al"). De download-stap hieronder vult de map
    # opnieuw met de echte remote releases voor delta-updates.
    $releasesDir = Join-Path $OutputRoot "velopack\releases"
    if (Test-Path $releasesDir) {
        Remove-Item -Path $releasesDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $releasesDir -Force | Out-Null

    # --- 3. Vorige releases ophalen voor delta-updates (alleen bij upload) ---
    if ($Upload -and -not [string]::IsNullOrWhiteSpace($GitHubToken)) {
        Write-Host "Vorige releases ophalen (voor delta-updates)..."
        dotnet vpk download github `
            --repoUrl $RepoUrl `
            --token $GitHubToken `
            --channel $Channel `
            -o $releasesDir
        # Eerste release heeft nog niets om te downloaden; dat is geen fout.
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  (geen eerdere releases gevonden - eerste release.)"
        }
    }

    # --- 4. Packen (maakt Setup.exe, portable zip, nupkg en releases-feed) ---
    $packArgs = @(
        "vpk", "pack"
        "--packId", "CanAnalyzer"
        "--packVersion", $Version
        "--packDir", $publishDir
        "--mainExe", "CanAnalyzer.exe"
        "--packTitle", "CanAnalyzer"
        "--packAuthors", "Gyrari B.V."
        "--channel", $Channel
        "--splashImage", $splashImage
        "-o", $releasesDir
    )
    if (Test-Path $iconPath) { $packArgs += @("--icon", $iconPath) }

    Write-Host "Packen met Velopack..."
    dotnet @packArgs
    if ($LASTEXITCODE -ne 0) { throw "vpk pack faalde met exitcode $LASTEXITCODE." }

    Write-Host ""
    Write-Host "Release-bestanden staan in:"
    Write-Host "  $releasesDir"

    # --- 5. Uploaden naar GitHub Releases ---
    if ($Upload) {
        if ([string]::IsNullOrWhiteSpace($GitHubToken)) {
            throw "Upload gevraagd maar geen token. Zet `$env:GITHUB_TOKEN of geef -GitHubToken mee (PAT met 'repo'-scope)."
        }
        Write-Host "Uploaden naar GitHub Releases ($RepoUrl)..."
        dotnet vpk upload github `
            --repoUrl $RepoUrl `
            --token $GitHubToken `
            --channel $Channel `
            -o $releasesDir `
            --publish `
            --releaseName "CanAnalyzer $Version" `
            --tag "v$Version"
        if ($LASTEXITCODE -ne 0) { throw "vpk upload faalde met exitcode $LASTEXITCODE." }
        Write-Host "Upload klaar. De release staat op GitHub."
    }
    else {
        Write-Host ""
        Write-Host "Tip: voeg -Upload toe (met `$env:GITHUB_TOKEN) om naar GitHub Releases te publiceren."
    }
}
finally {
    Pop-Location
}
