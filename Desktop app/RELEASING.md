# CanAnalyzer releasen (Velopack)

> **CANalyser 2.0 releaseblokkade:** `2.0.0-beta.1` mag niet als stabiele release worden gepubliceerd voordat de synthetische suite, 10M-framebenchmark en praktijk-golden-suite slagen. De implementatie voert nooit automatisch een upload of release uit.

CanAnalyzer gebruikt [Velopack](https://velopack.io) voor de installer én voor
automatische updates. De update-feed loopt via **GitHub Releases** van
`github.com/DuXupreme/CANalyser`.

> **Let op — bestaande gebruikers.** Auto-update werkt alleen voor mensen die via
> de **Velopack-installer** hebben geïnstalleerd. Wie de app nog via de oude Inno
> Setup-`Setup.exe` heeft, moet één keer handmatig de nieuwe Velopack-versie
> installeren. Daarna gaan alle volgende updates vanzelf.

## Eenmalig

- .NET 8 SDK geïnstalleerd.
- Een GitHub Personal Access Token met **`repo`**-scope (voor het uploaden van
  releases). Zet die in de omgeving voordat je publiceert:
  ```powershell
  $env:GITHUB_TOKEN = "ghp_xxx"
  ```
- De `vpk`-tool staat gepind in `.config/dotnet-tools.json` (versie volgt de
  Velopack-NuGet-versie, nu **1.2.0**). Het script herstelt hem automatisch.

## Een nieuwe versie uitbrengen

1. **Versie bumpen** in `src/CanAnalyzer.App/CanAnalyzer.App.csproj`
   (`<Version>` én `<FileVersion>`), bv. `1.0.0` → `1.0.1`. Velopack-versies
   moeten oplopende SemVer zijn.
2. **App sluiten** (anders wordt de build-output gelockt).
3. **Bouwen + publiceren** naar GitHub Releases:
   ```powershell
   .\scripts\Build-CanAnalyzerVelopack.ps1 -Upload
   ```
   Het script: publiceert self-contained → haalt vorige releases op (voor
   delta-updates) → `vpk pack` (incl. splash-image) → `vpk upload github`.

Zonder `-Upload` bouwt het script alleen lokaal naar
`artifacts/velopack/releases/` (handig om te testen). Daar vind je dan o.a.
`CanAnalyzer-win-Setup.exe`, de portable zip, de `.nupkg`'s en `releases.win.json`.

## Wat gebruikers ervaren

- **Eerste keer**: ze downloaden `CanAnalyzer-win-Setup.exe` van de GitHub-release
  en installeren. Tijdens installeren verschijnt de splash-image.
- **Daarna**: bij het opstarten checkt de app op nieuwe versies en biedt aan te
  updaten + herstarten. Handmatig kan ook via tab **Instellingen/Diagnostiek →
  Updates → "Controleer op updates"**.

## Splash-image vervangen

Vervang `installer/velopack/splash.png` door je eigen afbeelding (PNG/JPG/GIF).
Het pad wordt automatisch opgepikt door `Build-CanAnalyzerVelopack.ps1`.
Optioneel kun je `installer/velopack/app.ico` toevoegen voor een eigen icoon op
de installer en snelkoppeling.

## Verouderd: Inno Setup

De oude Inno Setup-installer (`installer/CanAnalyzer.iss` +
`scripts/Build-CanAnalyzerInstaller.ps1`) wordt **niet meer gebruikt** en biedt
geen auto-update. De bestanden blijven alleen ter referentie staan.
