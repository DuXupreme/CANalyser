# CANalyser 2.3.1

## Sessies selecteren

- Met **Sessie toevoegen** worden alle getoonde MF4-bestanden van de aangeklikte sessie geselecteerd.
- Met **Sessie uitvinken** wordt die sessie uit de huidige selectie verwijderd.
- Meerdere sessies van dezelfde machine kunnen samen worden gedownload en geanalyseerd.
- Binnen iedere sessie blijft de controle op opeenvolgende MF4-deelnummers actief.

## Zichtbare meetonderbrekingen

- De absolute loggerklok bewaart de werkelijke tijd tussen geselecteerde sessies.
- Grafieklijnen worden niet meer doorgetrokken over een onderbreking.
- Onderbrekingen van minstens één seconde krijgen een grijze markering met de duur, bijvoorbeeld **Geen data: 9 s**.

## Validatie

- De Release-build slaagt zonder waarschuwingen of fouten.
- Alle 99 geautomatiseerde tests slagen, inclusief een regressietest met precies negen seconden tussen twee sessies.
