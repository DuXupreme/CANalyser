<p align="center">
  <img src="src/CanAnalyzer.App/Resources/Branding/canalyser-lockup.svg" alt="CANalyser" width="520">
</p>

# CANalyser 2.5.1 (.NET 8 WPF)

CANalyser is de enige productiecode voor traceerbare analyse van Classic CAN en CAN FD. De Python/Dash-prototypeversie is gearchiveerd onder `legacy/` en is nadrukkelijk niet geschikt voor productieanalyses.

> Versie `2.5.1` voegt een compact los Signal Watch-venster toe met **Altijd bovenop**, dezelfde volglijst en cursorwaarden uit gewone en losse grafieken. Formaat en positie worden onthouden. Zie [release notes 2.5.1](RELEASE_NOTES_2.5.1.md) en de [vernieuwingen in 2.5.0](RELEASE_NOTES_2.5.0.md).
> Versie `2.4.16` verplaatste grafiekvoorbereiding naar de achtergrond, annuleert achterhaalde verzoeken en verbetert cursor- en markerweergave. Exacte weergave blijft beperkt tot het zichtbare tijdvak; verschuiven, zoomen en resetten herstellen automatische downsampling.
> Versie `2.4.12` herstelt toegang tot beveiligde online logs met een aanmeldvenster. Inloggegevens kunnen optioneel maximaal 30 dagen versleuteld worden onthouden; Afmelden verwijdert ze.
> Versie `2.4.11` bewaart eigen CSV-exportpresets en kolomvolgorde. Puntlimieten, downsampling en rijen per pagina zijn gedeeld tussen de bijbehorende vensters; deze instellingen beperken alleen de weergave, nooit de analyse of CSV-export.
> Versie `2.4.10` voegde CSV-export met signaal-, kolom-, tijd- en opmaakkeuze toe. Versie `2.4.9` verbeterde grafiekrespons en tijdelijke opslag; versie `2.4.8` voegde gekalibreerde slagbenutting en analyse-export toe.
> Versie `2.4.2` onthoudt de laatste vijf online-logselecties, maakt zware delay-/joystick-/actuatoranalyses geheugenveilig op logs met miljoenen meetpunten en herstelt de afzonderlijke actuator-slagbenutting. Afgebroken analyses worden bij de volgende start in telemetry gemeld.
> Versie `2.4.1` herstelt het bewerken en onderling converteren van DBC en BUSMASTER DBF.
> Versie `2.4.0` voegt **Actief gebruik en batterijduur** toe aan Analyse: detectie via motor-RPM, BMS-stroom, actuatorkracht of SOC-daling, met gebruiksaandeel, energie en batterijduur.
> Versie `2.3.1` liet complete online sessies samen selecteren en toont echte meetonderbrekingen als duidelijke gaten in de grafiek.
> De eerdere versie `2.2.1` voegde directe CANedge MF4/ZIP-import en de workflow **Online logs**
> toe. Grote selecties worden rechtstreeks uit S3 gedownload en lokaal samengevoegd,
> zodat de responslimiet van het dashboard geen logs meer blokkeert. De laatst gekozen
> DBC wordt hergebruikt en de 10M-framebenchmark op de doelhardware blijft aanbevolen.

## 1. Data-integriteitscontract

- Exacte relatieve tijd als signed 64-bit nanoseconden; seconden zijn uitsluitend een weergave/projectie.
- De oorspronkelijke logger-starttijd blijft behouden. Een UTC-notatie of monotone tijdreeks bewijst geen correcte klok. Loggerdatum, serverontvangst en klokkwaliteit blijven afzonderlijk; zonder bewijs blijft de klok onbevestigd. Plotcursors, flags en ruwe frames tonen lokale datum/tijd met UTC-offset; CSV kan ISO 8601 UTC, Unix-nanoseconden en `clock_quality` exporteren.
- Exacte raw signaalwaarden als `BigInteger` en fysieke waarden als `double`.
- Iedere bronregel wordt verklaard als non-data, geaccepteerd of afgewezen.
- Strikte import is standaard; PARTIAL vereist expliciete bevestiging en synthetiseert nooit waarden.
- Bronregel, frame-index, kanaal, richting, frameformat, DLC/payload en FD-flags blijven bewaard.
- Append-only disk-backed frame- en samplestores voorkomen zware frame-/sampleobjectlijsten in RAM.
- SHA-256 van bron en DBC, appversie en datasetstatus gaan mee in diagnostics en CSV.
- Door CANalyser gedownloade archieven bewaren per MF4 een versiegebonden manifest met herkomst, klokkwaliteit, grootte en SHA-256. Cachehergebruik controleert de inhoud opnieuw; een checksum bewijst inhoudsintegriteit, geen juiste datum of machine-installatie. Oudere ZIP-bestanden zonder manifest blijven importeerbaar met onbevestigde herkomst/tijdkwaliteit.
- Machine-installatiegegevens uit de CANedge-header of onderbouwde bronmetadata staan los van de loggerdatum. Onzekere datums wijzen een verplaatste logger niet automatisch aan een machine toe; strijdige bewezen installaties worden niet samengevoegd.
- Waargenomen meetstilte blijft per CAN-kanaal behouden, binnen en tussen bestanden. Interne loggerdiagnostiek blijft als ruwe data beschikbaar maar vult geen machinegaten. Grijze gebieden, lijnonderbrekingen en analyses gebruiken de betrokken kanalen; ontbrekende tijd wordt geen stilstand of nulbelasting.
- Klokterugsprongen in MF4/PEAK-reeksen worden niet door sorteren verborgen. Tijdcorrecties worden niet uit uploadtijd afgeleid.
- Formaatprobes selecteren één parser; de generieke parser is nooit een automatische fallback.
- Ondersteunde invoer:
  - CANedge MDF 4.11 `.mf4` (via de ingebouwde, gecontroleerde CSS Electronics-converter)
  - dashboard-ZIP met maximaal 10.000 MF4-delen uit één logger (maximaal 512 MB per bestand en 4 GB uitgepakt totaal); deelnummers binnen een sessie moeten opeenvolgen, tenzij manifest 2 ontbrekende tussendelen expliciet verklaart als versiegebonden uitsluitingen
  - PEAK `.trc` (classic + TSV flavor)
  - BUSMASTER text/log
  - CSS/CL1000 semicolon format (`Timestamp;Type;ID;Data`)
  - candump Classic/FD
- DBC- en BUSMASTER DBF-decoding is fail-closed:
  - exact ID match (standard + extended)
  - extended fallback on J1939 PGN
  - ambigue PGN-matches en lengtefouten leveren geen waarden
  - Intel/Motorola, signed/unsigned, IEEE float/double en multiplex-ranges
- De Database-editor opent en bewerkt DBC en BUSMASTER DBF en converteert in
  beide richtingen via **Opslaan/converteren**.
- Decode diagnostics:
  - unmatched IDs
  - manual/permissive decode counts
  - DBC message summaries
- Signal list and grouped plotting
- Automatische weergavedownsampling is standaard en past de getoonde punten aan het tijdvenster en de beeldruimte aan. Exact detail is tijdelijk beschikbaar voor het zichtbare venster, ook in losse plotvensters. Dit verandert geen samples, analyse of export.
- Per-signal offsets
- Group-level y-axis lock behavior
- Layout preset export/import (JSON)
- Decoded CSV export met signaal- en kolomkeuze, relatieve/absolute meettijd en instelbare CSV-opmaak
- Eigen CSV-exportpresets bewaren signalen, kolommen, kolomvolgorde en opmaak lokaal; ontbrekende signalen worden gemeld en blijven bij het bijwerken in de preset bewaard.
- Puntlimiet en downsampling worden gedeeld door Analyse, Instellingen en losse plotvensters. Rijen per pagina wordt gedeeld door Instellingen, Raw Frames en Busmaster. Beide beperken alleen de weergave.
- Inzichten toont automatisch de herkende analyseonderwerpen, dekking en beperkingen. Bevestigde analyseprofielen bewaren signaalrollen en fysieke instellingen; DBC-wijzigingen maken gewijzigde bevestigingen ongeldig. Details en standaardoverzicht gebruiken dezelfde berekeningen.
- Machine-analyse exporteren gebruikt altijd de volledige geladen opname en het passende opgeslagen profiel. Schema 3 bewaart analyses, alle signaalstatistieken, minuuttrends, bronidentiteit en ontbrekende dekking. Samples staan standaard uit; de exportmap wordt onthouden.
- Vergelijken opent meerdere analysepakketten voor cijfers en signaaltrends. Alleen verenigbare resultaten met voldoende bron- en tijdsbewijs worden samengevoegd; gedeeltelijke bronoverlap blokkeert samenvoegen. Onbevestigde klokken geven geen automatische UTC-uitlijning.
- SignalWatch toont gekozen signalen met sampletijd, ouderdom en meetgatstatus bij een cursor of de laatste sample.
- De knop **Signal Watch** in de werkbalk (of **Los venster** in Busmaster) opent een compacte volglijst naast de grafieken. **Altijd bovenop** staat standaard aan en kan uit; positie, formaat en die keuze worden onthouden. Het venster deelt de gekozen signalen en cursorwaarden met Busmaster, onafhankelijk van de zoekfilters daar. Ook cursors en A/B-acties in losse grafiekvensters werken door, zolang ze bij de huidige opname horen. **Signalen kiezen…** opent de volglijst in Busmaster. Sluiten van het kleine venster behoudt de volglijst.
- Actief gebruik en batterijduur in CAN Analyse: instelbare detectie op motor-RPM,
  BMS-stroom, actuatorkracht of SOC-daling, met aan-tijd, gebruiksaandeel,
  SOC/energie per toestand en prognoses voor een instelbare accucapaciteit.
- Actief gebruik en accuduur heeft een eigen subtab onder CAN Analyse en blijft
  gescheiden van joystick- en actuator-slaganalyse.
- Joystickgebruik toont X/Y-verdeling en, na bevestigde kalibratie, fysieke
  uitslagklassen. Actuator-slagbenutting is een afzonderlijke analyse met
  percentielen, tijd nabij uitersten en tijd buiten de bevestigde referentieslag.
- Online CANedge-logs per machine en periode selecteren, downloaden en direct analyseren zonder AWS-sleutels op de pc
- Bij een met wachtwoord beveiligd dashboard vraagt Online logs om dezelfde gebruikersnaam en hetzelfde wachtwoord als de website. Met **Inloggegevens 30 dagen onthouden** worden geaccepteerde gegevens via Windows DPAPI versleuteld voor de huidige Windows-gebruiker bewaard. De vaste vervaldatum schuift niet op bij gebruik; Afmelden verwijdert de opgeslagen aanmelding. Zonder vinkje geldt de aanmelding alleen tot het afsluiten van CANalyser. Inloggegevens worden alleen naar het dashboard gestuurd.
- De laatste vijf online downloadselecties worden direct lokaal opgeslagen en kunnen na herstart of crash met één klik opnieuw worden geselecteerd.
- Een complete sessie kan in één handeling worden toegevoegd of uitgevinkt. Meerdere sessies van dezelfde logger mogen samen worden geanalyseerd; de absolute tijd en meetonderbrekingen blijven behouden.
- Online markeringen kunnen met een reden worden gedeeld en hersteld wanneer het dashboard deze functies ondersteunt. Gemarkeerde bestanden blijven op de server staan maar worden niet gedownload. Manifest 2 bewaart de uitgesloten objectversies en markeringsrevisies, ook voor verborgen tussendelen. Alleen zulke expliciete uitsluitingen mogen een onderbreking in deelnummers verklaren; onverklaarde ontbrekende delen blijven geblokkeerd.
- Identieke online selecties worden maximaal zeven dagen lokaal hergebruikt; oude bestanden worden automatisch verwijderd en de cache wordt tot circa 2 GB begrensd.
- Instellingen toont download- en analyseopslag afzonderlijk. Ongebruikte beheerde analysesessies kunnen worden gewist; een aparte actie sluit eerst de dataset en wacht op achtergrondlezers. Actieve sessies, bronlogs, DBC's, profielen en exports blijven beschermd. Oude opslag zonder eigenaarregistratie blijft behouden.
- Multi-run Actuator Testbench CSV comparison: direct wide-CSV import, automatic
  alignment on the first STEP target transition, and ready-made overlays for
  position, error, PWM, current, bus voltage, and power (no DBC required)
- Message summary table
- Debug/error details panel
- Performance strategy:
  - dataset caching
  - schijfgebonden signaalindex met begrensde analyseweergaven voor multi-million-point logs
  - downsampling before plotting

## 2. Architecture

```
CanAnalyzer.sln
src/
  CanAnalyzer.Core/
    Domain/         # exacte frames, samples, identities, provenance en presets
    Interfaces/     # parser/decode/pipeline/export contracts
    Parsing/        # format parsers + parser orchestration
    Decoding/       # DbcParserLib 1.8.0 + strikte decoder
    Analysis/       # gedeelde analyseservice, profielen, export/vergelijking, pipeline
    Export/         # CSV export + preset JSON serialization
    Utilities/      # hex parsing, timestamp parsing, CAN/J1939 ID helpers

  CanAnalyzer.App/
    App.xaml(.cs)   # DI + branded startup splash
    MainWindow.xaml(.cs)
    Views/          # Analysis, Raw Frames, Settings/Diagnostics, Info
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

- **Inzichten**
  - automatisch overzicht met dekking en configuratiestatus
  - reactietijd, actuatorvolging, joystickgebruik en CAN-details
  - profiel bevestigen en bewaren; melding bij nog niet opgeslagen wijzigingen
- **Vergelijken**
  - analyse-ZIP's openen, machine/periode kiezen en cijfers of trends vergelijken
  - uitleg bij overlap, onbekende klok of onverenigbare resultaten
- **Busmaster / SignalWatch**
  - berichten en gekozen signalen, met cursorwaarde en ouderdom
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

Machine-analyse exporteren en vergelijken:

1. Open log en DBC, laad de opname en controleer **Inzichten**. Een gekozen pad is pas de actieve bron na laden; **Behoud** herstelt de keuze van de al geladen opname.
2. Controleer signaalrollen en bevestig alleen bekende fysieke kalibratie, referenties, activiteit, capaciteit en bitrates. Kies **Bevestig en bewaar profiel** om die instellingen voor standaardexport te gebruiken.
3. Kies **Machine-analyse exporteren**. De volledige geladen opname wordt opnieuw berekend, onafhankelijk van detailvensters, tabkeuzes en plotzoom. De eerste export onthoudt de gekozen map; de volgende krijgt een nieuwe bestandsnaam.
4. Open een of meer analyse-ZIP's in **Vergelijken**. Standaard staan trends op relatieve tijd. Een UTC-datumfilter selecteert hele pakketten met bevestigde klokgegevens; het snijdt de analyse niet bij.
5. De exportmap en optionele volledige samples zijn instelbaar onder **Instellingen / Diagnostics → Vaste machine-export**. Statistieken en minuuttrends van alle signalen zijn altijd opgenomen.

CSV delen (bijvoorbeeld met Parker):

1. Kies **Export CSV**. Selecteer signalen met de vinkjes, de zoekfunctie of **Uit analyse** (de aangevinkte signalen in de analysetab).
2. Kies kolommen afzonderlijk of gebruik **Compact delen** voor tijd, CAN-ID, bericht, signaal, waarde, kanaal, eenheid en datasetstatus. **Alle kolommen** bewaart ook alle bronmetadata.
3. Kies oorspronkelijke logtijd, relatief vanaf het eerste logrecord, of relatief vanaf het eerste geëxporteerde meetpunt. Alle signalen gebruiken hetzelfde nulpunt; `timestamp_utc` en `unix_time_ns` blijven absoluut. Zonder absolute logstart blijven die twee kolommen leeg.
4. Kies **Software / internationaal** (komma, decimale punt, UTF-8) of **Excel (NL)** (puntkomma, decimale komma, UTF-8 met BOM). Scheidingsteken, decimalen en BOM zijn ook los instelbaar. Tab is eveneens beschikbaar.
5. Controleer **Voorbeeld vernieuwen** en kies **Opslaan als…**. Eén rij per meetpunt, vaste kolomkoppen, CRLF-regels, volledige numerieke precisie en CSV-aanhalingstekens waar nodig. De hele log wordt geëxporteerd voor de gekozen signalen, zonder grafiekfilters, downsampling of interpolatie.

Een lopende export is annuleerbaar via de bestaande annuleerknop. Bij annulering of een fout wordt een bestaand doelbestand niet vervangen.

Actuator Testbench runs vergelijken:

1. Kies in de toolbar **Actuator CSV** en lees de korte uitlegpopup.
2. Selecteer meerdere `actuator_*.csv`-logs tegelijk.
3. CANalyser lijnt per bestand de eerste STEP-doelwijziging uit op `t=0`.
4. De Analyse-tab opent automatisch groepen voor positie, fout, PWM, stroom,
   busspanning en vermogen. Klik legendanamen om traces aan/uit te zetten.
5. Negatieve tijd is de aanloop vóór de stap. Een DBC is niet nodig.

Tests:

- `dotnet test src/CanAnalyzer.Tests/CanAnalyzer.Tests.csproj`

### Actief gebruik en batterijduur

Open in **Analyse** het paneel **Actief gebruik en batterijduur** en klik na het
controleren van de instellingen op **Bereken actief gebruik**. De tijdvelden
bovenaan begrenzen ook deze analyse; plotzoom, frame-ID-filter, offsets en
downsampling hebben geen invloed op de berekening uit de oorspronkelijke signalen.

- Kies een gemeten activiteitssignaal: bijvoorbeeld motor-RPM, BMS current of
  actuatorkracht. Herkenbare signalen worden voorgeselecteerd. Grenswaarden van
  100 rpm, 5 A en 1000 N zijn alleen startwaarden; controleer de gedecodeerde eenheid
  en de grens voor je machine. Bij stroom kies je de positieve of negatieve
  ontlaadrichting van het BMS; beide richtingen tellen ook laadstroom mee.
- Selecteer een SOC-signaal op een schaal van 0–100%. Als alternatief voor een
  activiteitssignaal kan **Activiteit afleiden uit SOC-daling** worden aangevinkt.
  Dat gebruikt standaard vensters van 60 s en een daling van 5 procentpunt/uur.
- Een optioneel aan/uit-signaal bepaalt aan-tijd. Zonder dit signaal is de geldige
  meetdekking een expliciet gelabelde schatting van aan-tijd. Onbekende activiteit
  tijdens gemeten aan-tijd maakt het getoonde gebruiksaandeel een ondergrens.
- Korte, bekende pauzes binnen een werkperiode tellen mee als actief gebruik
  (standaard maximaal 10 s); werkperiodes korter dan 3 s worden verworpen. Deze
  grenzen zijn instelbaar. Importgaten en sampleafstanden boven de ingestelde
  limiet (standaard 5 s) worden nooit overbrugd of als stilstand geboekt.
- Het capaciteitveld begint op **15 kWh**, de SOC-reserve op **20%**; dit zijn
  startwaarden, geen bevestigde machinegegevens. Bevestig de totale bruikbare
  capaciteit en leg de actieve accuconfiguratie vast voordat energie en prognoses
  als resultaat beschikbaar worden. Het accuaantal vermenigvuldigt de ingevoerde
  totale capaciteit niet. Energie is een schatting: `SOC-afname / 100 × bruikbare kWh`. Per toestand wordt
  deze energie alleen gedeeld door tijd met overeenkomstige SOC-dekking. Het
  netto verschil tussen begin- en eind-SOC wordt apart getoond en kan ook daling
  over onbekende periodes bevatten. SOC-stijging wordt afzonderlijk gerapporteerd.
- Batterijduur wordt geschat voor continu actief gebruik en voor dezelfde gemeten
  verhouding actief/inactief, inclusief resterende tijd tot de reserve. Daarvoor
  zijn een bevestigde activiteitsdefinitie en capaciteit, een bruikbare constante
  accuconfiguratie en minstens 90% SOC-dekking per gebruikte toestand vereist. Onbekende
  gebruiksverdeling, geen meetbare daling of meer dan 0,5 procentpunt SOC-stijging
  leveren geen voorspelling op. Een verouderde laatste SOC geeft geen resterende
  tijd. De werkelijke duur kan door belasting, temperatuur en BMS-correcties afwijken.

De tijdlijn onder de resultaten laat actieve, inactieve, uitgeschakelde en
onbekende periodes zien. Gewijzigde instellingen wissen de vorige resultaten,
zodat oude cijfers niet bij nieuwe instellingen blijven staan.

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
- Opgeslagen:
  - last used log/dbc paths
  - recent files
  - last window state
  - last plot options
  - last raw frame filters
  - bevestigde analyseprofielen, SignalWatch-selectie en machine-exportmap/opties
- Plot presets worden als JSON versie 2 met `SignalIdentity` geëxporteerd. V1-labels migreren alleen bij één unieke match.

## 8. Releaseblokkades voor 2.0.0

- 10 miljoen frames importeren, filteren en exporteren zonder truncatie of `OutOfMemoryException`;
- Release-build, tests en afgesproken coverage-drempels groen.

Release- en uploadscripts worden nooit automatisch uitgevoerd tijdens ontwikkeling.
