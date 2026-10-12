# CANalyser 2.4.12

Online logs kunnen weer worden geopend wanneer het CANedge-dashboard met een gebruikersnaam en wachtwoord is beveiligd. CANalyser vraagt bij HTTP 401 om de dashboardgegevens en probeert de loglijst of download opnieuw.

- Nieuw aanmeldvenster met gemaskeerd wachtwoord, uitleg bij een geweigerde aanmelding en veilig annuleren.
- Optie **Inloggegevens 30 dagen onthouden**, standaard uit. Geaccepteerde gegevens worden met Windows DPAPI versleuteld voor de huidige Windows-gebruiker bewaard. De vervaldatum wordt niet bij ieder gebruik verlengd.
- Zonder vinkje blijven de gegevens alleen tijdens de huidige CANalyser-sessie in het geheugen. **Afmelden** verwijdert ook een opgeslagen aanmelding.
- Verlopen, onleesbare of geweigerde opgeslagen gegevens worden niet als geldige aanmelding hergebruikt.
- Inloggegevens gaan uitsluitend naar de dashboard-API via HTTPS; aparte bestandsdownloads ontvangen geen dashboardwachtwoord.
- Een geannuleerde aanmelding of verbindingsfout blijft zichtbaar en wordt niet meer vervangen door de melding dat er geen bestanden zijn. Bij een geannuleerde download blijft de selectie behouden.

Validatie: 241 kerntests en 56 app-tests geslaagd, inclusief aanmeld- en downloadverzoeken, versleutelde opslag, herstarten, de vaste 30-dagentermijn, afmelden en WPF-venstergedrag. De daadwerkelijke aanmelding met persoonlijke dashboardgegevens is niet onderdeel van deze geautomatiseerde tests.
