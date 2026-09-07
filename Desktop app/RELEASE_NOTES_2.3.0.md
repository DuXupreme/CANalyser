# CANalyser 2.3.0

## Datum en tijd van metingen

- De absolute starttijd van MF4-metingen blijft behouden in de analyse.
- Plotcursors, flags, ruwe frames en diagnostiek tonen de meetdatum en lokale tijd met UTC-offset.
- CSV-export bevat daarnaast UTC-tijd en exacte Unix-nanoseconden; de relatieve meettijd blijft beschikbaar.

## Veilige selectie van online logs

- Meerdere MF4-bestanden mogen alleen samen worden geopend als logger en sessie gelijk zijn en de deelnummers opeenvolgen.
- Ongeldige selecties krijgen uitleg voordat de download begint; dezelfde controle geldt bij ZIP-import.
- De nieuwste sessie wordt standaard geselecteerd. Een enkel bestand blijft toegestaan.

## Gebruik en lokale opslag

- Downsampling staat standaard uit; de instelbare limiet blijft 5.000 punten per trace.
- Identieke online selecties worden uit de lokale cache hergebruikt. De cache wordt begrensd tot circa 2 GB en zeven dagen.
- De MF4-converter is beschikbaar in de portable distributie.
- Info bevat een directe link naar het CANedge-dashboard.

## Validatie

- Alle 99 automatische tests slagen op de lokale werkkopie buiten OneDrive.
- De afzonderlijke 10M-framebenchmark op doelhardware blijft aanbevolen.

## Nog te onderzoeken

- Waarom machine 2 kleine opeenvolgende logdelen maakt terwijl een maximum van circa 100 MB wordt verwacht. De sessiecontrole bewijst niet welke loggerinstelling het bestand heeft afgesloten.
