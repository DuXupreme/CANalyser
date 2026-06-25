# CANalyser 2.0.0-beta.1

Breking changes:

- tijd wordt exact als nanoseconden opgeslagen; oude `float`/`double`-tijdtypen zijn vervangen;
- import is standaard strikt en blokkeert op afgewezen data- of DBC-fouten;
- PARTIAL is een expliciete noodmodus en maakt nooit zero-fill- of vervangingswaarden;
- CSV behoudt de bestaande kolommen en voegt volledige frame-, raw-, kwaliteit- en hashprovenance toe;
- presets gebruiken versie 2 met `SignalIdentity`; v1-labels migreren alleen wanneer de match uniek is;
- DBC's die de editor niet aantoonbaar lossless kan terugschrijven openen alleen-lezen;
- plot-LOD is expliciet zichtbaar en analyses/export blijven op bronresolutie.

Praktijkvalidatie:

- de aangeleverde CSS/CL1000 golden logs worden volledig line-accounted en tegen SHA-256 vastgepind;
- decode-aantallen voor de hoofd-trowel-DBC worden gekruist met Python `cantools`;
- DBC-overlapmessages worden in partial-mode niet langer gedecodeerd;
- signal-loze bekende DBC-berichten tellen niet meer als decodefout;
- duplicate signalnamen in verschillende muxgroepen worden uit de originele DBC-regels gereconstrueerd.

Niet vrijgeven als stabiele `2.0.0` voordat de volledige praktijk-golden-suite, 10M-framebenchmark en acceptatie door de eindgebruiker zijn goedgekeurd.
