# CANalyser 2.4.3

Joystickgebruik en actuatorbereik zijn opnieuw ingericht rond interpreteerbare resultaten.

- Slagminimum en -maximum worden per actuator automatisch uit alle geldige posities in de volledige log afgeleid. Een geselecteerd tijdvenster gebruikt hetzelfde vaste logbereik.
- Drie actuatoroverzichten tonen typische benutting (P01–P99), volledig bereik, totale benutting en tijd nabij beide uitersten. Positieverschillen worden correct gedeeld door de behaalde slag.
- Dichtheidskaart, uitslagverdeling en joystickpercentages gebruiken dezelfde tijdgewogen brondata. Meetgaten en niet-eindige waarden tellen niet mee als gebruikstijd.
- Lege tijdvensters en ongeldige instellingen wissen eerdere resultaten. De selectie valt niet meer stilzwijgend terug op de hele log.
- Instellingen en afspelen zijn uitklapbaar. Het traject toont slechts vijf seconden en zoekt rechtstreeks het relevante stuk log op.
- De joystick- en slagcijfers gebruiken originele samples; de bestaande begrenzing voor de zwaardere delay/kinematica-analyses blijft behouden. Actief gebruik en accuduur, DBC/DBF-bewerking en eerdere stabiliteitsfixes blijven beschikbaar.

Het gemeten logbereik is geen bewijs van mechanische eindstops. Joysticknormalisatie gebruikt het midden van het waargenomen bereik, zolang fysieke kalibratie ontbreekt.

## Validatie

- Releasebuild zonder waarschuwingen of fouten.
- 152 geautomatiseerde tests geslaagd; Core-dekking 88,95% regels en 81,62% branches.
- WPF-weergave en bindingen gecontroleerd met testdata op twee vensterbreedtes, inclusief lege tijdvensters en ongeldige instellingen.
