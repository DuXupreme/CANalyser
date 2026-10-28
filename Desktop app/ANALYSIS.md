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

## Reactietijd en actuatorvolging

Kies Links, Rechts, Voor of een vrij gekozen paar setpoint/positie en kies de tijdreferentie.
De twee referenties beantwoorden verschillende vragen:

- **Setpointwijziging (SetpointChange):** tijd vanaf een betekenisvolle wijziging in de
  setpointlijn tot een aanhoudende positiebeweging in de richting van die wijziging.
  Dit beschrijft de samenhang tussen de twee gelogde signalen. Een dalende setpointlijn
  bewijst op zichzelf niet dat de actuator moet inschuiven.
- **Nieuwe positievraag (PositionRequest):** tijd vanaf een commandowijziging die een
  nieuwe bewegingsrichting vraagt, afgeleid uit setpoint minus de laatst bekende positie.
  Deze vergelijking vereist dezelfde eenheid en hetzelfde nulpunt voor beide signalen.
  Een dalend setpoint dat nog boven de positie ligt vraagt nog steeds beweging omhoog;
  de klok voor een omkering begint pas wanneer een latere commandowijziging werkelijk
  een vraag in de andere richting veroorzaakt.

Bij de positievraag gebruikt de analyse uitsluitend de laatste feedbacksample **vóór**
het commandotijdstip. De oude en nieuwe setpointwaarde worden met dezelfde feedbackwaarde
vergeleken. Een feedbacksample op precies het commandotijdstip geldt dus niet als eerdere
kennis. Een vraag begint buiten de positietolerantie; terugkeer binnen de halve tolerantie
geeft de detectie weer vrij. Dit verschil tussen inschakelen en vrijgeven voorkomt steeds
opnieuw starten rond één grens. Feedbackveranderingen kunnen de detectie vrijgeven, maar
maken zelf geen nieuwe commandoactie. Wijzigingen binnen een nog openstaande vraag in
dezelfde richting starten de klok niet opnieuw.
Een commando dat de openstaande vraag terugtrekt tot binnen de neutrale band of naar
de andere richting, breekt de meting af, ook tijdens het bevestigen van de reactie.
Bij een omkering kan de positie eerst nog de oude kant op bewegen; de reactie wordt
dan vanaf het waargenomen keerpunt beoordeeld. Zij hoeft niet eerst terug over de
positie op het commandotijdstip te komen.
Een losse laatste positieverandering vóór het commando is onvoldoende bewijs voor
een bestaande beweging in de andere richting. Bij verder rustige voorgeschiedenis
wordt zo'n onzekere situatie uitgesloten. Begint de positie pas na de nieuwe vraag
eerst de andere kant op te bewegen, dan moet ook die beweging aanhouden voordat
de analyse een keerpunt gebruikt; een losse piek telt daarvoor niet mee.

De positietolerantie is iets anders dan de bewegingsgrens: de eerste bepaalt wanneer
setpoint en positie ver genoeg uiteenliggen om een richting te vragen; de tweede bepaalt
hoeveel gemeten beweging als reactie telt. De automatische positietolerantie hangt niet af
van een handmatig verhoogde bewegingsgrens. Beide kunnen expliciet in de gedecodeerde
positie-eenheid worden ingesteld; er wordt geen afstand in millimeters verondersteld.

Gemiddelde, minimum, maximum en P95 staan in milliseconden, apart voor starts vanuit rust
en correcties/omkeren, en horen altijd bij de gekozen referentie.
Alleen geaccepteerde metingen tellen mee. P95 gebruikt lineaire interpolatie tussen de
gesorteerde meetwaarden. Een klik op een getal selecteert een bijbehorende actie; de
eventtabel toont ook uitgesloten acties en de reden. De detailgrafiek toont oorspronkelijke
samples, een legenda, het commandomoment, de bewegingsgrens en de bevestigde reactie.
Een setpointwijziging zonder nieuwe richtingsvraag krijgt bij de tweede methode de
uitsluiting **NoNewDirectionRequest**; zij levert daar geen nulmeting op. Waar een eerdere
setpointactie bij een latere positievraag hoort, blijven de oorspronkelijke actie en het
daadwerkelijke referentietijdstip afzonderlijk traceerbaar. De twee gemiddelden kunnen op
verschillende acties zijn gebaseerd en mogen niet van elkaar worden afgetrokken.

`MotionResponseAnalyzer` detecteert aanhoudende kleine sprongen, geleidelijke starts en
omkeringen op originele samples. Er is geen verborgen eis van 30% van het totale bereik.
De automatische grens gebruikt de waargenomen kleinste stappen en een schatting uit
herhaalde lokale wisselingen. Dit is geen fysieke kalibratie of gegarandeerde
sensorresolutie. Onder Geavanceerd kan een absolute positiegrens worden ingesteld.
Een grote grens telt ook de tijd om die afstand af te leggen mee. Bij geleidelijke,
ruisende signalen meet de methode de tijd tussen waarneembare betekenisvolle veranderingen,
niet noodzakelijk de zuivere mechanische dode tijd.

Een reactie moet aanhouden (standaard minstens 20 ms en minstens een sampleinterval).
Voorgeschiedenis schaalt mee met de feedbacksampleafstand. Eerder begonnen beweging,
meetgaten, overlappende commando's, onvoldoende voorgeschiedenis en afgebroken metingen
krijgen een expliciete uitsluiting. Een omkering meet vertrek vanaf het keerpunt;
een snelheidswijziging tijdens bestaande beweging wordt niet met een verzonnen delay gevuld.
Een meetgat sluit betrokken acties uit en wist niet de geldige acties elders in de log.
De maximale zoektijd blijft zichtbaar: reacties buiten dit venster ontbreken in de
gemeten verdeling. Daarom moeten uitsluitingen altijd samen met het gemiddelde worden gelezen.

Tijdstippen zijn bron-sampletijdstippen; per actie staat een mogelijk interval op basis van
de omliggende samples. Gelijktijdige samples bewijzen geen exacte fysieke 0 ms. Dit interval
beschrijft alleen de waargenomen grenspassages; sensorfiltering en tijd om de meetgrens te
passeren vallen daar niet onder. Correlatie is een optionele, afzonderlijke vormvergelijking
en is nooit de gemiddelde reactietijd. Die vergelijking gebruikt een begrensd werkrooster.
Geen van beide referenties meet zelfstandig het elektrische motorsignaal of het exacte
fysieke begin van beweging. Eerdere uitkomsten op basis van setpointwijzigingen blijven
uitkomsten van die methode; ze worden niet achteraf een algemene fysieke actuatorvertraging.
Vergelijk of combineer resultaten alleen bij dezelfde referentie, grenzen en selectie.
De positievraag is afgeleid uit gelogde doelpositie en feedback. Ontbrekende motorsturing,
interne regelbanden, filtering en sensortijdstempels kunnen niet uit deze twee signalen
worden gereconstrueerd. Een langsbewegende positie maakt zonder commandowijziging geen
nieuw meetbaar commandotijdstip.

Volgafwijking gebruikt setpoint en positie in dezelfde oorspronkelijke eenheid en hetzelfde
nulpunt. De nieuwe samenvatting is tijdgewogen over meetintervallen met een detecteerbare
setpoint- of positieverandering in de laatste 100 ms. De grootste afwijking is inspecteerbaar.
Het uitklapbare overzicht van alle actuatoren behoudt de fout over het volledige venster,
inclusief stilstand. Het optionele bedrijfsfilter geldt voor volgafwijking en slagbenutting;
de reactietijdmeting gebruikt het gekozen tijdvenster zonder dat bedrijfsfilter.

## Joystickgebruik

Joystickgebruik staat vooraan; gekalibreerde actuator-slagbenutting is een apart uitklapbaar
onderdeel. Beide werken onafhankelijk. Elke joystickas heeft een eigen minimum, neutraal
en maximum. Fysieke gebruikscijfers verschijnen alleen na bevestiging van deze kalibratie.
Bij een nieuwe log moet de kalibratie opnieuw worden bevestigd. Zonder kalibratie blijven
relatieve XY/radiusgrafieken beschikbaar, maar bewijzen loguitersten niet wat neutraal of
voluit is. Automatische signaalkeuze combineert geen assen van verschillende CAN-berichten.
De knoppen voor procentassen (-100/0/100) en genormaliseerde assen (-1/0/1) vullen alleen
de zes waarden in en trekken een eerdere bevestiging in; ze bevestigen geen kalibratie.

De fysieke uitslag is `max(abs(X), abs(Y))` na kalibratie per as. Standaardklassen:
neutraal ≤10%, klein tot 30%, middelgroot tot 60%, groot onder 90%, bijna/voluit vanaf90%.
Alle percentages gebruiken gezamenlijk gemeten seconden in het venster, inclusief stilstand.
De oudere radiale verdeling blijft apart; een diagonale uitslag van 65%/65% is geen volle as.
Een korte kleine correctie is een volledig waargenomen neutraal→maximaal30%→neutraal-episode
van maximaal1s. Voluit vasthouden betekent minstens2s onafgebroken ≥90% op minstens één as.
Deze definities beschrijven het ingangssignaal, niet de bedoeling van de bestuurder.
Meetgaten onderbreken episodes; venstergrenzen verzinnen geen begin/einde. Buiten-kalibratietijd
blijft zichtbaar. Eventlijsten en voorvertoningen zijn begrensd voor responsiviteit; de
statistieken en export behouden alle geldige perioden. De voorvertoning vermeldt afkapping.

## Overige tabs

- **CAN professioneel:** verkeer en geschatte busbelasting gelden voor het gekozen kanaal
  over de volledige log, met ingestelde bitrates. Het gebruikstijdvenster filtert deze tab niet.
  Loggerfilters, ontbrekende frames en dubbele Rx/Tx-registratie beïnvloeden de schatting.
- **Actief gebruik en batterijduur:** classificatie volgt het gekozen activiteitssignaal en
  de drempel. Energie uit SOC en batterijduur zijn schattingen. Onbekende periodes en
  meetdekking blijven zichtbaar; SOC-stijging kan laden of een BMS-correctie betekenen.
  Accuconfiguratie is standaard onbekend. Kies alleen een gedocumenteerd signaal dat het
  aantal actieve packs telt (geen bitmasker), of bevestig een handmatig aantal. De analyse
  toont bron, dekking, aantalswisselingen en perioden. Een geobserveerde wisseling onderdrukt
  accuduurprognoses, ook bij een verandering op de laatste sample. Het aantal SOC-kanalen of
  CAN-berichten bewijst geen fysiek accuaantal. Totale bruikbare kWh blijft een afzonderlijke,
  handmatige instelling en wordt niet nogmaals met het accuaantal vermenigvuldigd.

## Analysepakket

**Bestand → Machine-analyse exporteren** maakt lokaal een schema-3-ZIP met CSV en JSON.
De export berekent het volledige standaardplan opnieuw over de gehele geladen dataset,
met het passende opgeslagen analyseprofiel. Detailvensters, de geselecteerde joystick of
actuator, SignalWatch, tabkeuze, plotzoom en downsampling veranderen deze export niet.
Bevestig en bewaar aangepaste detailinstellingen eerst als ze ook voor de standaardexport
moeten gelden. Zonder passend opgeslagen profiel wordt de automatische herkenning gebruikt;
ontbrekende signalen en onbevestigde fysieke instellingen blijven expliciete beperkingen.

De exportmap wordt onthouden; iedere export krijgt een unieke naam. De map en de optie
voor volledige samples staan onder **Instellingen / Diagnostics → Vaste machine-export**.
Machine- en loggeridentiteit worden uit de onderbouwde bronmetadata overgenomen; onbekende
identiteit en onbevestigde loggerklokken worden niet aangevuld met aannames.

- `manifest.json`: schema/methodeversie, appversie, bron- en DBC-hash, loggeroorsprong,
  klokkwaliteit, volledigheid, bronidentiteit en opties. `AnalysisScope` en
  `StatisticsScope` zijn `FullDataset`.
- `signal_statistics.csv`: alle gedecodeerde signalen met CAN-identiteit en eenheid,
  aantallen, minimum/maximum, samplegemiddelde/M2, tijddekking en tijdintegraal.
- `signal_minutes.csv`: tijddekking en integraal per signaal per minuut voor trends.
  Minuten zijn relatief aan de loggeroorsprong. UTC-uitlijning vereist een bevestigde klok;
  een minuutwaarde is geen exact gebeurtenistijdstip.
- `analyses.json` en `analysis_values.csv`: compacte numerieke module-uitkomsten met
  het gebruikte profiel, methoden, eenheden, dekking, status en beperkingsredenen.
  Joystickgebruik, beide reactiereferenties, bewegingsgebonden volgfout, slagbenutting,
  CAN-kanalen, actief gebruik, accuconfiguratie en datakwaliteit blijven afzonderlijk.
- `analysis_details.json`: dezelfde volledige standaarduitkomst met alle oorspronkelijke
  events, episodes en intervallen. Het manifest verwijst naar dit bestand via
  `AnalysisDetailsEntry`. De compacte samenvatting wordt niet berekend uit ingekorte data.
- `signals.json`, `sources.json` en `source_registry.json`: signaalidentiteit, eenheid,
  bronindices en bronmetadata met hashes. Een archiefhash alleen bewijst niet dat twee
  verschillende ZIP-bestanden geen oorspronkelijke bronnen delen.
- `gaps.json`, `channel_gaps.json`, `exclusions.json` en `continuity_breaks.json`:
  ontbrekende dekking per kanaal en expliciete uitsluitingen/onderbrekingen.
  `logger_diagnostics.json` en `messages.json` bewaren loggerdiagnostiek en berichtaantallen.
- Optioneel `samples.csv`, standaard uit: alle gedecodeerde meetpunten met exacte
  loggerrelatieve nanoseconden, UTC indien bekend, signaal-ID, kwaliteit, frame-index en
  bekende oorspronkelijke bronindices. Dit kan het pakket aanzienlijk groter maken.

Analyses en algemene signaalstatistieken gebruiken de hele geïmporteerde dataset. Een
PARTIAL-import blijft gedeeltelijk, ook wanneer het volledige geladen bereik wordt geëxporteerd.
Algemene tijdweging houdt de vorige eindige waarde vast
tot de volgende eindige sample, bij oplopende tijd en maximaal de opgegeven sampleafstand
(standaard 5 s). Intervallen die bekende importgaten raken worden uitgesloten. De laatste
sample krijgt geen verzonnen duur. Iedere analysetab behoudt zijn eigen gapbeleid.
Niet-eindige waarden worden leeg/null; ontbrekende analyses zijn geen nulmetingen.

Het tabblad **Vergelijken** toont meerdere pakketten naast elkaar met eenheden, beperkingen
en signaalminuuttrends. Alleen verenigbare schema-3-resultaten met volledige bronidentiteit,
één bekende machine en aantoonbaar niet-overlappende perioden krijgen een gezamenlijke uitkomst.
Identieke verenigbare bronsets tellen eenmaal. Gedeeltelijke overlap blokkeert samenvoegen;
de uitleg noemt de betrokken bestanden. Methode, DBC, kalibratie, referentie en filters
moeten overeenkomen. Verschillende uitkomsten blijven individueel leesbaar met een reden.

Het gezamenlijke signaalgemiddelde is `som(integral_value_seconds) / som(covered_seconds)`.
Percentages gebruiken verenigbare tellers en noemers. Reactietijd-P95 wordt uitsluitend
opnieuw berekend uit alle geaccepteerde events; ontbreekt die verdeling, dan is een
gezamenlijke P95 niet beschikbaar. Prognoses worden niet opgeteld. Niet iedere module
heeft een vergelijkingsselector of een trend; er worden geen trends verzonnen.

De standaardas is relatieve tijd per opname. Automatische UTC-uitlijning vereist bevestigde
klokken. Een datumfilter selecteert hele pakketten; het snijdt geen opname bij en berekent
geen deelperiode opnieuw. Meetgaten en onvolledig gedekte minuten onderbreken de trendlijn.
Schema 2 blijft leesbaar met beperkingen, zonder onbewezen gezamenlijke berekeningen.
Vergelijk batterijveroudering alleen onder passende belasting, temperatuur en SOC-dekking;
SOC-afgeleide energie is geen onafhankelijke capaciteitsmeting.

CSV is UTF-8, kommagescheiden met een punt als decimaalteken. Parquet is niet opgenomen.
De export leest sampleopslag sequentieel en publiceert pas na succes; annuleren of een fout
behoudt een eventueel bestaand doelbestand.

## Verificatie

Regressietests gebruiken synthetische signalen. De WPF-controle in `artifacts/analysis-review`
controleert tabs, bindingen, kanaalselectie, ongeldige vensters, scrollen en verstelbare panelen.
Een echte meetlog met bijbehorende DBC blijft nodig om signaalkeuze, eenheden en beschikbare
referenties voor een concrete machine te verifiëren.
