# Analyse en langdurige metingen

## Slagbenutting

Het doel is vaststellen hoeveel geldige bedrijfstijd een actuator bij de ingestelde
onder- of bovengrens doorbrengt. Veel grensgebruik kan wijzen op behoefte aan meer
beschikbaar bereik. Een begrensd setpoint bewijst niet dat de gebruiker verder wilde
sturen en vertelt niet hoeveel extra slag nodig is.

De referentie is per actuator configureerbaar: een gekalibreerd middensignaal of
handmatig midden, met een halve slagbreedte als signaal of handmatige waarde. Positie,
setpoint, midden en halve slag moeten dezelfde gedecodeerde eenheid gebruiken.
Percentage = 100 * (positie - midden + halve slag) / (2 * halve slag).
Log-minimum en log-maximum zijn geen vervanging voor bekende grenzen. Ontbrekende
referenties leveren geen percentage op. Buiten het bereik gemeten waarden worden
apart vermeld en niet naar 0 of 100% afgeknipt.

De analyse toont minimum/maximum, tijdgewogen P01/P99, verdeling, tijd aan beide grenzen,
aantal bezoeken en langste bezoek. Het grensgebied is instelbaar. Een optioneel
bedrijfsstatussignaal beperkt beoordeling tot een gekozen waarde. Kalibratieveranderingen,
bekende importgaten en te grote sampleafstanden tellen niet als geldige bedrijfstijd.
Er wordt niet voorbij de laatste geldige sample geëxtrapoleerd. Handmatige referenties
worden bij een nieuwe dataset gewist om onbedoeld hergebruik te voorkomen.

Setpointcijfers gebruiken de gezamenlijke dekking van positie, referentie, bedrijfsstatus
en setpoint. Gemiddelde absolute volgafwijking staat in procentpunten van het bereik.
Vergelijk feedbackgrenzen en setpointgrenzen samen, bij voorkeur binnen één bedrijfstoestand.

## Overige tabs

- **Joystickgebruik:** vaste kalibratie bepaalt schaal en midden. Zonder kalibratie is
  de verdeling relatief aan log-min/max; fysieke neutraal- en maximale-uitslagpercentages
  blijven onbekend. De ingestelde kalibratie geldt voor beide geselecteerde assen.
- **Actuatorvolging:** werkelijk setpoint versus feedback, zonder aangenomen joystickmenging
  of onafhankelijke normalisatie. De gemiddelde absolute fout is tijdgewogen in gedecodeerde
  positie-eenheden. Tijdvenster en bedrijfsfilter volgen de gebruiksanalyse.
- **Latency / Delay:** eerste drempelpassage en kruiscorrelatie zijn aparte resultaten.
  De analyse volgt het gebruikstijdvenster. Bekende importgaten vereisen een aaneengesloten
  selectie; ontbrekende reacties blijven zichtbaar. Een volgende significante commandoverandering
  beëindigt het zoeken naar de vorige reactie. Constante signalen en te zware correlatieberekeningen
  geven geen schijnresultaat. Markers voor vormverandering zijn slechts indicatief.
  Gelogde drempelpassage is geen onafhankelijke meting van fysieke bewegingsstart.
- **CAN professioneel:** verkeer en geschatte busbelasting gelden voor het gekozen kanaal
  over de volledige log, met ingestelde bitrates. Het gebruikstijdvenster filtert deze tab niet.
  Loggerfilters, ontbrekende frames en dubbele Rx/Tx-registratie beïnvloeden de schatting.
- **Actief gebruik en batterijduur:** classificatie volgt het gekozen activiteitssignaal en
  de drempel. Energie uit SOC en batterijduur zijn schattingen. Onbekende periodes en
  meetdekking blijven zichtbaar; SOC-stijging kan laden of een BMS-correctie betekenen.

## Analysepakket

**Bestand → Export analyses** maakt lokaal een ZIP met CSV en JSON. Vul machine-, logger-
en batterij-ID in; onbekend blijft leeg. Bij een online ZIP met één herkenbaar logger-ID
wordt die logger voorgesteld. Controleer dit bij gemengde bronnen of accuwissels.
Analyses worden opnieuw berekend met de huidige signalen, referenties en tijdvensters.

- `manifest.json`: schema/methodeversie, appversie, bron- en DBC-hash, UTC-oorsprong,
  volledigheid, bronbestanden, identiteit en interpretatieregels.
- `signal_statistics.csv`: alle gedecodeerde signalen met CAN-identiteit en eenheid,
  aantallen, minimum/maximum, samplegemiddelde/M2, tijddekking en tijdintegraal.
- `signal_minutes.csv`: tijddekking en integraal per signaal per minuut voor trends.
  Minuten zijn relatief aan de loggeroorsprong; UTC geeft de absolute koppeling.
- `analyses.json` en `analysis_values.csv`: numerieke uitkomsten, instellingen,
  slag- en joystickverdelingen, latencyhistogrammen, CAN-statistieken en actief-gebruikintervallen
  met SOC/energie. `DisplayMetrics` is alleen voor lezen, niet voor berekeningen.
- `gaps.json` en `messages.json`: bekende importgaten en berichtaantallen.
- Optioneel `samples.csv`: alle gedecodeerde meetpunten met exacte relatieve nanoseconden,
  UTC indien bekend, signaal-ID, kwaliteit en frame-index. Dit kan groot worden.

Algemene signaalstatistieken gebruiken de hele geïmporteerde dataset; afzonderlijke analyses
behouden hun eigen tijdselectie. Algemene tijdweging houdt de vorige eindige waarde vast
tot de volgende eindige sample, bij oplopende tijd en maximaal de opgegeven sampleafstand
(standaard 5 s). Intervallen die bekende importgaten raken worden uitgesloten. De laatste
sample krijgt geen verzonnen duur. Iedere analysetab behoudt zijn eigen gapbeleid.
Niet-eindige waarden worden leeg/null; ontbrekende analyses zijn geen nulmetingen.

Samenvoegen gebeurt in het ontvangende programma. Ontdubbel bronbestanden en controleer
**overlap van machine/logger en absolute meetperiode**, ook bij anders samengestelde ZIPs.
De archiefhash alleen detecteert geen overlap tussen verschillende archieven. Zonder betrouwbare
UTC of identiteit is automatisch samenvoegen niet verantwoord. Totaal gemiddelde:
`som(integral_value_seconds) / som(covered_seconds)`. Percentielen en sessiegemiddelden
mogen niet rechtstreeks gemiddeld worden. Voeg histogrammen alleen samen bij dezelfde bins
en referentie. Vergelijk batterijveroudering onder vergelijkbare belasting, temperatuur en
SOC-dekking; SOC-afgeleide energie is geen onafhankelijke capaciteitsmeting.

CSV is UTF-8, kommagescheiden met een punt als decimaalteken. Parquet is niet opgenomen.
De export leest sampleopslag sequentieel en publiceert pas na succes; annuleren of een fout
behoudt een eventueel bestaand doelbestand.

## Verificatie

Regressietests gebruiken synthetische signalen. De WPF-controle in `artifacts/analysis-review`
controleert tabs, bindingen, kanaalselectie, ongeldige vensters, scrollen en verstelbare panelen.
Een echte meetlog met bijbehorende DBC blijft nodig om signaalkeuze, eenheden en beschikbare
referenties voor een concrete machine te verifiëren.
