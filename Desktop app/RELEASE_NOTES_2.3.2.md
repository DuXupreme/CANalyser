# CANalyser 2.3.2

- Instellingen / Diagnostics toont bovenaan de **huidige meting**: machine,
  logger, sessies, aantal logs, meetstart en datasetstatus. Een zichtbare tabel
  bevat de originele logbestanden met hun logger en sessie. Het bronarchief en
  de gebruikte DBC zijn opvraagbaar. De herkomst blijft bij de geladen dataset
  horen wanneer je een ander bestand voor de volgende import selecteert.
- Grote selecties openen sneller. Na bevestiging van PARTIAL wordt het reeds
  verwerkte resultaat hergebruikt als log en DBC inhoudelijk ongewijzigd zijn.
- Een compacte index per signaal voorkomt volledige herscans bij signaalopbouw
  en Signal Watch. Exacte tijden, rawwaarden, kanaalidentiteit en PARTIAL-status
  blijven behouden. De index gebruikt aanvullende tijdelijke schijfruimte.
- Diagnostics en telemetrie splitsen de looptijd uit naar inlezen/converteren,
  DBC laden, decoderen, hashes, datasetopbouw, schermvoorbereiding en
  bevestigingsvensters.
- Bronidentiteit blijft ook beschikbaar bij opnieuw openen van een lokale
  online-ZIP. Onbekende machine- of sessiegegevens worden expliciet als
  onbekend weergegeven.

De bestaande selectie op meettijd, ondersteuning voor meerdere sessies en
zichtbare onderbrekingen uit 2.3.1 blijven behouden.
