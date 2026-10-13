# CANalyser 2.4.13

Downsampling uitschakelen bouwt voortaan alleen de oorspronkelijke meetpunten in het zichtbare tijdvak op. Hierdoor wordt bij een ingezoomde grafiek niet langer de volledige meetreeks opnieuw als grafiekpunten aangemaakt.

- Verschuiven, zoomen en resetten schakelen downsampling direct weer in, gedeeld tussen Analyse en alle losse grafiekvensters.
- Zware datapreparatie in losse grafiekvensters gebeurt op een achtergrondthread. Een voortgangsmelding blijft zichtbaar tijdens het bijwerken.
- Verouderde resultaten worden genegeerd wanneer je tijdens de berekening verder navigeert. De actuele zoomstand, lijnzichtbaarheid en automatische asgrenzen blijven behouden.
- Terugschakelen naar downsampling houdt de bestaande grafiek beschikbaar voor slepen en Ctrl+Z. Onnodige buffers met oorspronkelijke grafiekpunten worden vrijgegeven.
- Nieuwe grafieken starten met downsampling. Brondata, analyse, cursormetingen en CSV-export behouden hun volledige precisie.

Validatie: 241 kerntests en 85 app-tests geslaagd, inclusief zichtbare meetpunten uit een miljoen samples, lijnen/trapjes/markers, meetonderbrekingen, gekoppelde assen, navigatie tijdens berekenen en het sluiten van losse vensters. Een breed zichtbaar tijdvak kan nog steeds veel meetpunten bevatten; praktijktesten met de specifieke gebruikerslog vallen buiten deze geautomatiseerde controles.
