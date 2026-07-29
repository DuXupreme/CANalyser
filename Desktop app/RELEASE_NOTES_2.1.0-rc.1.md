# CANalyser 2.1.0-rc.1

Deze release candidate richt zich op begeleid DBC-herstel, directe analyse van
Actuator Testbench CSV-bestanden en een responsievere laadervaring.

## Nieuw

- Een wizard voor import- en DBC-fouten, bereikbaar via **Fouten oplossen**.
- Duidelijke uitleg per conflict: betrokken frame en signalen, betekenis,
  technische details en een concrete aanbevolen aanpassing.
- Visuele bit-layout met markering van overlappende en conflicterende signalen.
- Herstelkopie opslaan en opnieuw valideren zonder de originele DBC te wijzigen.
- Directe import van meerdere Actuator Testbench CSV-runs zonder DBC.
- Automatische uitlijning op de eerste STEP-doelwijziging en voorbereide
  overlays voor positie, fout, PWM, stroom, busspanning en vermogen.
- Een geanimeerde oranje/blauwe twisted-wire laadindicator in de statusbalk en
  in het lege subplotgebied tijdens langdurige bewerkingen.

## Verbeterd

- Decodefouten worden per CAN-ID en oorzaak samengevat, waaronder afwijkende
  DLC, geblokkeerde definities, frameformaten en signaalextractie.
- De herstelwizard geeft gerichte voorstellen voor dubbele signalen,
  bit-overlap, multiplexing en payloadlengte.
- De laadanimatie gebruikt vooraf opgebouwde frames en stopt terwijl een
  herstelvenster geopend is, zodat de interface responsief blijft.
- Langdurige decode- en herberekenacties tonen consistent voortgang zonder de
  statusregel horizontaal te verdringen.

## Validatie voor stabiele 2.1.0

- Doorloop intern een probleemlog met DBC-herstel van begin tot eind.
- Importeer en vergelijk minimaal twee representatieve Actuator CSV-runs.
- Controleer installatie en automatische update op de doelhardware.
- Publiceer pas daarna dezelfde broncode als `2.1.0`.
