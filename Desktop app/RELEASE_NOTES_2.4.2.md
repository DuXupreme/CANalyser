# CANalyser 2.4.2

Deze stabiliteitsrelease voorkomt dat een zware analyse op online logs met
miljoenen meetpunten het beschikbare geheugen uitput, en maakt het hervatten na
een onverwachte afsluiting eenvoudiger.

- **Online logs** toont de laatste vijf gebruikte selecties. Met
  **Opnieuw selecteren** worden machine, datumbereik en bestanden hersteld.
- Een selectie wordt vóór de download direct lokaal opgeslagen. Daardoor blijft
  deze beschikbaar wanneer CANalyser later onverwacht wordt afgesloten.
- Joystick-, actuator- en delayanalyses lezen maximaal 250.000 gelijkmatig over
  de volledige meetperiode verdeelde punten per signaal rechtstreeks uit de
  schijfindex; de volledige brondata en CSV-export blijven ongewijzigd.
- De delaycorrelatie gebruikt een begrensd werkrooster en een grof-naar-fijn
  lagonderzoek, zodat zowel geheugengebruik als rekentijd begrensd blijven.
- Telemetry registreert start, voltooiing en fouten van zware analyses. Een
  persistente marker meldt bij de volgende start ook een hard onderbroken
  analyse, bijvoorbeeld na beëindiging door geheugendruk.
- **Actief gebruik en accuduur** staat als eigen subtab onder **CAN Analyse**;
  deze functie blijft inhoudelijk gescheiden van de joystick- en slaganalyses.
- **Joystick gebruiksanalyse** bevat weer een afzonderlijke actuator-
  slagbenutting voor Left, Right en Front: waargenomen min/max, P01/P99,
  robuust gebruikt slagpercentage, tijd nabij beide uitersten en tijd buiten de
  ingestelde referentieslag. Minimum, maximum en uiterste-band zijn instelbaar.

De analysestatus vermeldt expliciet wanneer een begrensde analyseweergave is
gebruikt en hoeveel bronpunten het grootste betrokken signaal bevatte.
