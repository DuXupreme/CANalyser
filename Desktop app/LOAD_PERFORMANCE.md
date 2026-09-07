# Grote online selecties: meting en optimalisatie

## Aanleiding

Op 7 september 2026 meldde `load_decode_completed` 501.106 ms voor een selectie
van 48 online logs van 3 september. De gebeurtenis meldde PARTIAL, meer dan
10 miljoen meetpunten en 100.000–1 miljoen decodeerfouten. Deze totale tijd
bevat herstel- en bevestigingsvensters en eventuele herhaalde verwerking;
de online download is al afgerond voordat de timer begint.

## Wijzigingen

- De desktopworkflow bewaart een voorbereid resultaat tijdens integriteitsreview.
  PARTIAL wordt pas beschikbaar na expliciete bevestiging. De SHA-256 van beide
  invoerbestanden wordt opnieuw gecontroleerd voordat het resultaat wordt
  hergebruikt. Een gewijzigde bron, ook op hetzelfde pad, wordt opnieuw verwerkt.
  Annuleren en opnieuw proberen ruimen de voorbereide stores op. De bestaande
  `LoadAsync(..., Strict, ...)` blijft fouten blokkeren.
- De samplestore schrijft een aanvullende index in blokken van 256 punten per
  signaal. Reeksen lezen alleen hun eigen tijdstempels en waarden; ze blijven
  lazy en worden niet allemaal vooraf in RAM geladen. Volledige samples met
  exacte rawwaarden, kanaalidentiteit en herkomst blijven beschikbaar.
- Aantallen en Signal Watch-statistieken worden tijdens append bijgehouden.
  Datasetopbouw en Signal Watch hoeven niet alle samples opnieuw te lezen.
  Per signaal blijven de laatste sample, extrema en telling beschikbaar.
- Kanaalnamen, extended-aantallen en unieke raw-ID's worden tijdens bestaande
  verwerkingspasses verzameld. De decoder maakt geen nieuwe kandidatenlijst
  voor ieder frame meer.
- Diagnostics en telemetrie tonen afzonderlijke verwerkingstijden,
  bevestigingstijd en het aantal verwerkingspogingen. Zie `TELEMETRY.md`.

De index kost aanvullend 24 bytes tijdelijke schijfruimte per meetpunt
(circa 295 MB voor de echte selectie). In RAM blijven één buffer van 6 KiB per
signaal tijdens append en één blokverwijzing per maximaal 256 punten.
Het schrijven van de index voegt decodeerwerk toe; de winst komt uit het
vermijden van herhaalde imports en volledige scans bij datasetopbouw en analyse.

## Metingen op deze computer, Release / .NET 8

Synthetische vergelijking: 250.000 frames, 20% unmatched, 8 signalen over twee
kanalen, 1.600.000 meetpunten. De bronframes worden gegenereerd; MDF4-import en
WPF zijn geen onderdeel van deze vergelijking. Alle signaalreeksen worden gelezen.

| Stap | Vóór | Na eerste indexwijziging |
| --- | ---: | ---: |
| Decoderen en opslag | 2.665 ms | 5.125 ms |
| Datasetopbouw | 2.331 ms | 9 ms |
| Alle reeksen lezen | 19.576 ms | 447 ms |
| Totaal van deze stappen | 24.572 ms | 5.581 ms |
| Cumulatieve managed allocaties, geen piek-RAM | 9.251 MB | 965 MB |

Dit is circa 4,4× sneller voor deze gecombineerde stappen. De cijfers zijn
losse runs en worden beïnvloed door JIT, bestandscache en achtergrondbelasting.

Een grotere run met de verdere decoderoptimalisatie behield alle 12.800.000
meetpunten uit 2.000.000 gegenereerde frames: decoderen/opslag 18.533 ms,
datasetopbouw 7 ms en alle reeksen lezen 2.031 ms; totaal 20.571 ms.

De werkelijk gecachte selectie van **48 bestanden** is ook met de laatst gebruikte
DBC verwerkt, via de ingebouwde MF4-converter:

| Stap | Tijd |
| --- | ---: |
| Inlezen, converteren en samenvoegen | 64.942 ms |
| DBC laden | 203 ms |
| Decoderen en opslag | 37.224 ms |
| SHA-256 | 511 ms |
| Datasetopbouw | 39 ms |
| Totale pipeline inclusief overige overhead | 102.966 ms |

Resultaat: **5.049.897 frames, 12.292.860 meetpunten, 88 signalen**.
De 110.072 decodeerfouten blijven gemeld en het resultaat blijft PARTIAL.
Deze circa 103 seconden sluiten downloaden, schermopbouw en gebruikersinteractie
uit; ze zijn dus niet direct vergelijkbaar met het historische event van 501 seconden.
Een nieuwe meting in de uitgebrachte desktopapp is nodig voor de totale gebruikerstijd.
Inlezen/converteren is nu de grootste gemeten fase.

## Reproduceren

Vanuit de repositoryroot, met een .NET SDK en de bestaande NuGet-packages:

```powershell
$env:CANALYSER_LOAD_BENCHMARK_FRAMES = '250000' # of 2000000
dotnet test 'Desktop app/src/CanAnalyzer.Tests/CanAnalyzer.Tests.csproj' -c Release --filter FullyQualifiedName~DecodeBuildAndReadEverySignal --logger 'console;verbosity=detailed'
```

Voor een lokaal MF4- of ZIP-bestand en de bijbehorende DBC:

```powershell
$env:CANALYSER_BENCHMARK_LOG = '<lokaal logbestand>'
$env:CANALYSER_BENCHMARK_DBC = '<bijbehorende DBC>'
dotnet test 'Desktop app/src/CanAnalyzer.Tests/CanAnalyzer.Tests.csproj' -c Release --filter FullyQualifiedName~CachedOnlineImport --logger 'console;verbosity=detailed'
```

Beide benchmarks zijn opt-in. De gewone regressietests controleren onder andere
alle punten over blokgrenzen, stabiele tijdsortering, kanaalscheiding, exacte
BigInteger-rawwaarden, PARTIAL-kwaliteit, samenvattingen, expliciete toestemming,
hergebruik zonder herhaling, bronwijzigingen op hetzelfde pad en opruimen bij annuleren.
