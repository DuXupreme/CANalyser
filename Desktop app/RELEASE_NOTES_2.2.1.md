# CANalyser 2.2.1

## Grote online-logselecties

- Grote MF4-selecties lopen niet meer via de 5,72 MB-responslimiet van AWS Amplify.
- CANalyser ontvangt tijdelijke, alleen-lezen downloadlinks per geselecteerd S3-bestand.
- De geselecteerde MF4-bestanden worden rechtstreeks gedownload en veilig lokaal tot één ZIP samengevoegd.
- De voortgang blijft gebaseerd op de totale grootte uit de online-loglijst.
- De bestaande ZIP-route blijft beschikbaar voor compatibiliteit met CANalyser 2.2.0.

## Validatie

- De desktopapplicatie bouwt zonder waarschuwingen of fouten.
- Alle 78 geautomatiseerde tests slagen.
- De Next.js-productiebuild en lintcontrole slagen met de nieuwe downloadplan-route.
