# CANalyser 2.0.0

Breking changes:

- tijd wordt exact als nanoseconden opgeslagen; oude `float`/`double`-tijdtypen zijn vervangen;
- import is standaard strikt en blokkeert op afgewezen data- of DBC-fouten;
- PARTIAL is een expliciete noodmodus en maakt nooit zero-fill- of vervangingswaarden;
- CSV behoudt de bestaande kolommen en voegt volledige frame-, raw-, kwaliteit- en hashprovenance toe;
- presets gebruiken versie 2 met `SignalIdentity`; v1-labels migreren alleen wanneer de match uniek is;
- DBC's die de editor niet aantoonbaar lossless kan terugschrijven openen alleen-lezen;
- plot-LOD is expliciet zichtbaar en analyses/export blijven op bronresolutie.

Decode-gedrag:

- DBC-overlapmessages worden in partial-mode niet langer gedecodeerd;
- signal-loze bekende DBC-berichten tellen niet meer als decodefout;
- duplicate signalnamen in verschillende muxgroepen worden uit de originele DBC-regels gereconstrueerd.

`2.0.0` is uitgebracht als stabiele release. De 10M-framebenchmark op de doelhardware en brede eindgebruiker-acceptatie waren bij vrijgave nog niet afgerond en blijven aanbevolen als vervolgvalidatie.
