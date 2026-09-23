# Crashregistratie en plotgroepimport

Plotgroepimport bouwt grafieken via de bestaande achtergrondtaak. Tijdens de opbouw
is Analysis.IsBusy actief, zodat de bestaande laadindicator blijft bewegen.
Een tweede import wordt gedurende de opbouw tegengehouden. Bij fouten wordt de
laadstatus hersteld en verschijnt de fout bij de importstatus.

Bij ingeschakelde telemetrie bewaart de app een sessiemarker in
`%APPDATA%/CanAnalyzer/pending-crashes`. Normaal afsluiten verwijdert deze marker.
Een fatale dispatcher- of AppDomain-exception bewaart `app_crashed` met alleen
het fouttype en de oorsprong, zonder exceptiontekst, stacktrace, paden of CAN-data.
Een achtergebleven marker zonder geregistreerde exception wordt
`app_unexpected_exit`; dit kan ook geforceerd afsluiten of stroomuitval betekenen.
Daarbij is timestamp_utc het begin van de vorige sessie, niet het onbekende afsluitmoment.

Bij een volgende start worden achtergebleven meldingen aangeboden. De oorspronkelijke
versie, installatie, sessie en event-ID blijven behouden. Verwijderen gebeurt pas na
een succesvolle HTTP-bevestiging; offline of geweigerde meldingen blijven staan voor
de volgende start. Een exclusief bestandshandle voorkomt dat een nog actieve tweede
appinstantie wordt gemeld. Dit vereist dat de app weer gestart wordt en lokale opslag
beschikbaar blijft. Eerdere crashes van versies zonder sessiemarker zijn niet te herstellen.

De telemetrieserver moet beide eventnamen accepteren. Het actuele dashboardcatalogus
toont ze als `App gecrasht` en `App onverwacht afgesloten`, met foutclassificatie.

Validatie:
- `dotnet test src/CanAnalyzer.Tests/CanAnalyzer.Tests.csproj`
- `dotnet run --project scripts/PlotImportCheck/PlotImportCheck.csproj`
- Worker: `node --test test/*.test.js`
