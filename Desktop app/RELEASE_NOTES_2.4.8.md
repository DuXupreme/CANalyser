# CANalyser 2.4.8

- Slagbenutting gebruikt het gekalibreerde nulpunt en beschikbare bereik per actuator. Positie en setpoint worden ten opzichte van dezelfde grenzen beoordeeld.
- Nieuwe analyse-export als ZIP met CSV en JSON: signaalstatistieken, minuutgegevens, dekking, meetgaten, bronidentiteit en beschikbare analyseresultaten. Ruwe meetpunten zijn optioneel mee te nemen voor vergelijking over sessies, dagen en weken.
- Verbeterde signaallijst met scrollen en slepen aan de onderrand om de hoogte aan te passen. De diagnostiekpagina kan verticaal scrollen; de onderste informatiebalk is via de bovenrand te vergroten.
- 'Selecteer niets' deselecteert alle signalen, ook wanneer de zoekbalk een deel van de lijst verbergt.
- RAW frames (PCAN) kan filteren op een exacte hexadecimale waarde op een gekozen bytepositie (vanaf byte 0), naast 'Data bevat'. Ongeldige invoer geeft een melding.
- BUSMASTER filtert eerder op CAN-ID en registreert start, duur, voltooiing en fouten. Onderbroken bewerkingen kunnen na herstart worden gemeld; het telemetriedashboard herkent de nieuwe gebeurtenissen.
- Analyseberekeningen en exportgeldigheid aangescherpt, inclusief actuatorvolging, vertraging en kanaalselectie. Verbeteringen uit 2.4.7 blijven behouden.

Crashmeldingen vereisen ingeschakelde telemetrie. Bij een harde beëindiging volgt de melding pas na herstart; een tijdelijk vastgelopen scherm is nog geen afzonderlijke live hangmelding.
