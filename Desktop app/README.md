# CanAnalyzer (.NET 8 WPF MVVM)

Production-oriented Windows desktop migration of the Python `Gyrari CANalyser.py` tool.

## 1. Python Source Functionality Inventory
Behavior inferred from the migration source:

- CAN log import (`.trc`, `.log`, `.txt`)
- DBC import + permissive decode behavior
- Format detection/parsing priority by extension:
  - PEAK `.trc` (classic + TSV flavor)
  - BUSMASTER text/log
  - CSS/CL1000 semicolon format (`Timestamp;Type;ID;Data`)
  - candump fallback
  - generic free-text fallback parser
- DBC decode matching logic:
  - exact ID match (standard + extended)
  - extended fallback on J1939 PGN
  - permissive/manual signal decode fallback
- Decode diagnostics:
  - unmatched IDs
  - manual/permissive decode counts
  - DBC message summaries
- Signal list and grouped plotting
- Per-signal offsets
- Group-level y-axis lock behavior
- Layout preset export/import (JSON)
- Decoded CSV export
- Message summary table
- Debug/error details panel
- Performance strategy:
  - dataset caching
  - precomputed signal arrays
  - downsampling before plotting

## 2. Architecture

```
CanAnalyzer.sln
src/
  CanAnalyzer.Core/
    Domain/         # raw frames, decoded samples, summaries, presets, dataset cache
    Interfaces/     # parser/decode/pipeline/export contracts
    Parsing/        # format parsers + parser orchestration
    Decoding/       # DBC loader + permissive decoder
    Analysis/       # downsampling, dataset builder, raw frame filtering, pipeline
    Export/         # CSV export + preset JSON serialization
    Utilities/      # hex parsing, timestamp parsing, CAN/J1939 ID helpers

  CanAnalyzer.App/
    App.xaml(.cs)   # DI + startup
    MainWindow.xaml(.cs)
    Views/          # Analysis, Raw Frames, Settings/Diagnostics
    ViewModels/     # Main shell + tab viewmodels
    Models/         # UI models for signal/group/frame rows and plot panel
    Services/       # file dialogs + plot model builder
    Infrastructure/ # settings store + dialog adapters
    State/          # persisted app settings model

  CanAnalyzer.Tests/
    CoreBehaviorTests.cs
```

## 3. Tech Stack

- C# / .NET 8
- WPF
- MVVM with `CommunityToolkit.Mvvm`
- DI with `Microsoft.Extensions.DependencyInjection`
- Logging with `Microsoft.Extensions.Logging`
- JSON with `System.Text.Json`
- CSV export with `CsvHelper`
- Plotting with `OxyPlot.Wpf`

Why OxyPlot (instead of ScottPlot): this migration prioritizes MVVM-first binding and multi-axis panel composition in pure XAML/VM workflows for long-term maintainability.

## 4. Main UX Areas

- **Analysis tab**
  - signal selection
  - plot group editing (multi-signal groups, offsets, y-axis lock)
  - plot options and filters
  - multi-panel plotting
  - message summary table
  - diagnostics/debug text
- **Raw Frames (PCAN-style) tab**
  - virtualized frame grid
  - filters: ID, payload contains, type/channel, time, frame type, row cap
- **Settings / Diagnostics tab**
  - active files
  - recent files
  - last processing summary
  - detailed decode diagnostics + last error details

## 5. Build and Run

1. Install .NET 8 SDK and Visual Studio 2022 (or newer) with WPF workload.
2. Restore/build:
   - `dotnet restore CanAnalyzer.sln`
   - `dotnet build CanAnalyzer.sln -c Release`
3. Run:
   - `dotnet run --project src/CanAnalyzer.App/CanAnalyzer.App.csproj`

Tests:

- `dotnet test src/CanAnalyzer.Tests/CanAnalyzer.Tests.csproj`

## 6. Installer / Distribution (Send 1 File)

Two supported outputs are available:

1. **Portable single EXE** (no install required)
2. **Setup installer EXE** (`Setup.exe`)

### 6.1 Portable EXE (fastest)

Run in PowerShell from repo root:

- `.\scripts\Publish-CanAnalyzer.ps1`

Output:

- `artifacts\portable\CanAnalyzer-portable-win-x64.exe`

You can send this single file directly. It is self-contained and does not require .NET installation on target machines.

### 6.2 Setup Installer EXE (recommended for colleagues)

1. Install [Inno Setup 6](https://jrsoftware.org/isdl.php) once on the build machine.
2. Build installer:
   - `.\scripts\Build-CanAnalyzerInstaller.ps1`

Output:

- `artifacts\installer\CanAnalyzer-Setup-<version>.exe`

This is the one-file installer you can forward to operators/colleagues.

### 6.3 Useful options

- Runtime override:
  - `.\scripts\Publish-CanAnalyzer.ps1 -Runtime win-x64`
- Disable ReadyToRun (smaller build-time cost, slower startup):
  - `.\scripts\Publish-CanAnalyzer.ps1 -NoReadyToRun`
- Use existing restored packages only:
  - `.\scripts\Publish-CanAnalyzer.ps1 -NoRestore`
- Rebuild installer using existing publish output:
  - `.\scripts\Build-CanAnalyzerInstaller.ps1 -SkipPublish`

## 7. Settings and Presets

- App settings are stored in:
  - `%APPDATA%\CanAnalyzer\settings.json`
- Saved:
  - last used log/dbc paths
  - recent files
  - last window state
  - last plot options
  - last raw frame filters
- Plot presets can be exported/imported as JSON.

## 8. Known Approximations vs Python Dash UI

- The Python Plotly-specific **rangeslider** and dynamic client-side **cursor annotation overlay** are approximated by native OxyPlot zoom/pan + tracker behavior.
- DBC parser targets common `BO_` + `SG_` definitions and multiplexing patterns; highly exotic DBC constructs may require parser extensions.
- Desktop app keeps decoded/cache state in-memory for responsiveness (equivalent user outcome to server-side cache in Dash).
