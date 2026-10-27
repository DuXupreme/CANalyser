# CANalyser 2.4.16

Deze versie verbetert het slepen, zoomen en gebruiken van geladen grafieken.

- Grafiekdata voor een nieuw tijdvenster wordt op de achtergrond voorbereid. Achterhaalde verzoeken worden geannuleerd; alleen het actuele resultaat wordt weergegeven.
- Cursors en meetmarkeringen gebruiken een aparte tekenlaag, zodat bewegen niet telkens alle grafieklijnen opnieuw laat berekenen. De markeringen blijven werken bij het wisselen van venster en bij gewone OxyPlot-weergaven.
- Dichte puntgrafieken worden in kleine groepen getekend. Dit verhelpt een gemeten lange blokkade bij het sluiten van zulke grafiekvensters, zonder meetpunten weg te laten. De afronding van enkele overlappende cirkelranden kan minder dan een pixel verschillen.
- Annulering werkt ook tijdens het laden en sorteren van signaaldata en het voorbereiden van een apart grafiekvenster. Een datasetwissel of gewijzigd grafiekverzoek stopt verouderd werk. De knop Annuleren kan grafiekopbouw stoppen.
- Geannuleerd werk publiceert geen gedeeltelijke data. Bestanden blijven beschikbaar tot de laatste actieve lezer klaar is; opnieuw proberen blijft mogelijk.
- Oorspronkelijke meetwaarden blijven behouden. Tests controleren onder meer pieken, datagaten, exacte weergave, gekoppelde assen en export zonder gekoppeld venster.

Een grafiek kan tijdens het voorbereiden kort de vorige weergave tonen. Deze release is geen bevestiging dat iedere mogelijke oorzaak van het eerder gemelde volledige vastlopen is gevonden; daarvoor blijft een procesdump van dat incident nodig.

Validatie: 199 WPF/app-tests en 272 kerntests uitgevoerd en geslaagd, inclusief 10 miljoen frames zonder afkapping en het decoderen en laden van 6,4 miljoen signaalwaarden. Core-dekking: 89,55% regels en 82,33% vertakkingen. Twee optionele tests waarvoor een extern logbestand nodig is, zijn niet uitgevoerd. De renderer is gecontroleerd op 100%, 150% en 200% DPI, inclusief clipping en speciale markerstijlen.
