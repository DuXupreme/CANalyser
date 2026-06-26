# CANalyser 2.0.2

Deze patchrelease herstelt de plotweergave bij grote datasets en voegt privacy-minimale gebruikstelemetrie met dashboard toe.

## Fixes

- Analyseplots blijven zichtbaar bij grote signalen: als volledige resolutie te zwaar is voor interactieve weergave, schakelt CANalyser tijdelijk over op LOD/downsampling.
- Het aparte plotvenster loopt niet meer vast doordat dezelfde veilige LOD-logica wordt gebruikt.
- De app meldt duidelijk dat downsampling alleen de getekende grafiek raakt; brondata, decode, analyse, cursors/flags en export blijven volledig.

## Nieuw

- Gebruikstelemetrie staat standaard aan en kan door gebruikers met een enkele checkbox worden uitgeschakeld.
- Telemetrie verzamelt alleen technische events en geaggregeerde tellingen; geen CAN-/DBC-inhoud, bestandsnamen, paden, signaalnamen of frame IDs.
- Cloudflare Workers + D1 telemetry receiver is toegevoegd, inclusief visueel dashboard op `/dashboard`, summary en NDJSON-export.
- Telemetry endpoint voor deze release: `https://canalyser-telemetry.42069.workers.dev/events`.

## Verbeterd

- Diagnostics/Settings toont alleen de gebruikerskeuze voor telemetry; technische endpoint- en tokenvelden zijn uit de UI gehaald.
- Updatechecks, plot-acties, CSV export en load/decode resultaten worden als technische telemetry-events geregistreerd wanneer telemetry aan staat.
