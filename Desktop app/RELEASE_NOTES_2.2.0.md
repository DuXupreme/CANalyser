# CANalyser 2.2.0

## Online CANedge-analyse

- Nieuwe knop **Online logs** voor selectie op machine en periode.
- Geselecteerde S3-logs worden via de bestaande dashboardserver als ZIP gedownload; CANalyser bevat geen AWS-sleutels.
- De laatst gekozen DBC wordt onthouden en na de download automatisch gebruikt.
- Directe import van losse CANedge `.MF4`-bestanden en dashboard-`.ZIP`-bestanden.

## Betrouwbare MF4-verwerking

- Gebundelde officiële CSS Electronics `mdf2peak`-converter, versie `24.12.19`.
- SHA-256-controle van de converter vóór iedere uitvoering.
- MF4-delen worden met de absolute UTC-starttijd tot één werkelijk chronologische tijdlijn samengevoegd, ook bij overlappende sessies.
- Veilige ZIP-extractie met limieten voor aantal bestanden en uitgepakte grootte.
- ZIP-downloads behouden logger- en sessiemappen, zodat gelijknamige deelbestanden niet meer botsen.

## Validatie

- 75 geautomatiseerde tests slagen.
- De volledige converterketen is aanvullend getest met een echt MF4-bestand uit logger `48EDFD35`.
- De Next.js-dashboardproductiebuild en lintcontrole slagen.
