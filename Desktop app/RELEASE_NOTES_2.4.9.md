# CANalyser 2.4.9

Grote datasets vragen minder tijdelijke opslag en grafieken reageren sneller bij opnieuw tekenen en het aanpassen van de weergave.

- Instellingen / Diagnostiek toont de grootte en volledige opslaglocaties van downloadcache, ruwe frames, gedecodeerde samples en tijdelijke MF4-conversie.
- Grafieken bouwen en zware analyses draaien op de achtergrond. Bij snel opeenvolgende wijzigingen verschijnt alleen het nieuwste resultaat.
- Legenda aanpassen behoudt de bestaande grafieken, verborgen signalen en zoom. Grafieken hergebruiken bronarrays en gemeten tekst.
- Downsampling houdt rekening met het zichtbare tijdvenster en de schermbreedte, behoudt extremen en eindpunten en laat meetgaten zichtbaar. Analyse- en exportdata behouden hun oorspronkelijke resolutie.
- Tijdelijke sampleopslag bewaart herhaalde signaalmetadata compacter. Decoderen en het lezen van signaalindexen zijn geoptimaliseerd.
- Actieve achtergrondlezers behouden toegang tot hun dataset; tijdelijke bestanden worden bij normale afsluiting en afgebroken parsing beter opgeruimd.

Validatie: 203 Core-tests en 20 WPF-tests, inclusief zoom/reset, gekoppelde assen, legenda, verborgen signalen, meetgaten, datasetwissels en exacte decodewaarden. De Release-build slaagt zonder waarschuwingen of fouten.

Lokale synthetische metingen laten snellere grafiekopbouw en minder geheugentoewijzing zien. Werkelijke prestaties blijven afhankelijk van de log, geselecteerde signalen en hardware; de MF4-converter is niet vervangen.
