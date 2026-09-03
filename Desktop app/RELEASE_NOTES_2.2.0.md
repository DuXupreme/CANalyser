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
- PEAK `Rx`-frames met een 11-bit-ID blijven correct standaard-CAN en worden niet als extended frame geïnterpreteerd.
- Extra paddingbytes in een CAN-frame blokkeren een kortere, volledig passende DBC-definitie niet meer.

## Plotweergave

- Uitgeschakelde downsampling wordt altijd gerespecteerd; volledige resolutie wordt niet meer stilzwijgend vervangen.
- De standaardlimiet bij ingeschakelde downsampling is verhoogd naar 5.000 representatieve punten per trace.
- Lange analyse-statusmeldingen lopen door op een volgende regel en worden niet meer rechts afgekapt.

## Validatie

- 78 geautomatiseerde tests slagen.
- De volledige converterketen is aanvullend getest met een echt MF4-bestand uit logger `48EDFD35`.
- De Next.js-dashboardproductiebuild en lintcontrole slagen.
