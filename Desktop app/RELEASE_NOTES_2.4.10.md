# CANalyser 2.4.10

CSV-export biedt nu keuze uit signalen, kolommen, tijdreferentie en bestandsopmaak, zodat je gericht meetdata kunt delen.

- Kies signalen afzonderlijk, zoek op bericht/kanaal/CAN-ID of neem de aangevinkte signalen uit de analysetab over.
- Kies kolommen afzonderlijk of gebruik Compact delen voor tijd, CAN-ID, berichtnaam, signaalnaam, waarde, kanaal, eenheid en datasetstatus.
- Exporteer de oorspronkelijke logtijd of relatieve tijd vanaf het eerste logrecord of eerste geëxporteerde meetpunt. Alle signalen behouden hetzelfde nulpunt; absolute UTC-kolommen blijven absoluut.
- Opmaakpresets voor Software / internationaal en Excel (NL), met instelbare komma, puntkomma of tab, decimale punt/komma en UTF-8 met of zonder BOM.
- Controleer de eerste vijf gekozen meetpunten in een voorbeeld vóór het opslaan.
- De export draait op de achtergrond en is annuleerbaar. Een fout of annulering vervangt geen bestaand doelbestand.

Meetwaarden behouden hun volledige precisie, één rij per meetpunt. De gekozen signalen worden over de hele log geëxporteerd, zonder grafiekfilters, downsampling of interpolatie. Dit is CSV-export; TRC-export is niet toegevoegd.

Validatie: 216 Core-tests en 22 WPF-tests geslaagd in Release, inclusief CSV-opmaak, exacte tijdverschillen, kanaalselectie, annulering en de exportdialoog op twee vensterhoogtes.
