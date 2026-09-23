# CANalyser 2.4.0

Het tabblad **Analyse** bevat nu **Actief gebruik en batterijduur**. Daarmee zie je
hoeveel van de aan-tijd de machine wordt gebruikt en wat dit betekent voor
SOC-verbruik, energie en batterijduur.

- Detectie via motor-RPM, BMS current, actuatorkracht of een ander gemeten signaal,
  met een instelbare grenswaarde. Bij BMS-stroom kan de ontlaadrichting worden gekozen.
- Alternatieve detectie op steilere SOC-dalingen, met een instelbaar tijdvenster.
- Actieve tijd, aan-tijd, gebruikspercentage, inactiviteit en onbekende periodes.
- SOC-afname, geschatte kWh en gemiddeld vermogen per gebruikstoestand.
- Geschatte totale en resterende batterijduur bij continu gebruik en bij het
  gemeten gebruikspatroon. Standaard 15 kWh bruikbare capaciteit en 20% reserve-SOC.
- Tijdlijn met werkperiodes en instelbare overbrugging van korte bekende pauzes.

Een aan/uit-signaal is optioneel; zonder dit signaal wordt meetdekking duidelijk
als schatting van aan-tijd vermeld. Datagaten worden niet als stilstand geboekt.
Energie wordt alleen verdeeld over periodes met geldige SOC-dekking. Onvoldoende
dekking of duidelijke SOC-stijging onderdrukt de batterijduurprognose.

Gebruik: open **Analyse → Actief gebruik en batterijduur**, controleer de gekozen
signalen, eenheden en grenswaarden en klik **Bereken actief gebruik**.

Deze release bouwt voort op 2.3.2 en behoudt de verbeteringen voor geïndexeerd laden,
online sessies en bronbestandsdiagnostiek.
