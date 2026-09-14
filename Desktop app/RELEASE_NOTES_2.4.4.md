# CANalyser 2.4.4

- Plotgroepimport bouwt grafieken op de achtergrond op. De bestaande laadindicator blijft actief en de interface blijft tijdens de berekening reageren.
- Fatale fouten worden lokaal vastgelegd en bij de volgende start opnieuw aangeboden aan de telemetrieserver. Meldingen blijven bewaard tot de server ontvangst bevestigt.
- Onverwacht afgesloten sessies worden afzonderlijk gemeld; een nog draaiende tweede appinstantie wordt niet als crash aangemerkt.
- De telemetrieserver accepteert crashmeldingen en meldingen over eerder onderbroken bewerkingen.

Crashregistratie bevat uitsluitend technische metadata, zonder foutteksten, stacktraces, bestandspaden of CAN-data. Een onverwachte afsluiting kan ook door geforceerd stoppen of stroomuitval ontstaan. De nieuwe registratie werkt vanaf deze versie en kan oudere crashes niet herstellen.

Validatie van de reparatie: 156 .NET-tests, 5 telemetrieservertests en een WPF-dispatchertest voor responsiviteit en herstel na importfouten.
