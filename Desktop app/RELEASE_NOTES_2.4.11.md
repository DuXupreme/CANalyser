# CANalyser 2.4.11

CSV-export onthoudt eigen presets en kolomvolgorde. De weergavelimieten zijn gelijkgetrokken tussen Analyse, Instellingen, losse grafiekvensters en de frametabellen.

- Maak, laad, wijzig en verwijder lokale CSV-exportpresets. Elke preset bewaart de gekozen signalen, kolommen, kolomvolgorde, tijdreferentie, scheidingstekens en UTF-8-opmaak.
- Verplaats CSV-kolommen met de pijltjes omhoog en omlaag. Het voorbeeld en de export gebruiken dezelfde volgorde.
- Signalen die in de geopende log ontbreken worden gemeld en blijven bij het bijwerken in de preset bewaard. Dubbele presetnamen en mislukte opslag worden zichtbaar afgehandeld.
- Het CSV-exportvenster kan scrollen op kleinere schermen; de exportknop blijft bereikbaar.
- Max punten per signaal en downsampling zijn gedeeld tussen Analyse, Instellingen en alle losse grafiekvensters. De losse slider is vervangen door hetzelfde getalveld; de puntlimiet is alleen actief bij downsampling.
- Max rijen heet nu Rijen per pagina en wordt gedeeld door Instellingen, Raw Frames en Busmaster. Wijzigen zet beide frametabellen terug op pagina 1; filters resetten behoudt de gekozen paginagrootte.
- Instellingen en presets worden eerst volledig naar een tijdelijk bestand geschreven, zodat een opslagfout de bestaande instellingen intact laat.
- Frametabellen worden leeggemaakt bij het vervangen van een log. Na een afgebroken of mislukte import kunnen de weergave-instellingen veilig worden aangepast.

De puntlimiet, downsampling en paginagrootte beperken alleen de weergave. Analyse en CSV-export blijven de oorspronkelijke meetdata gebruiken.

Validatie: 216 kerntests en 32 app-tests geslaagd, inclusief presetopslag, kolomvolgorde, gedeelde limieten, paginering en bestandsfouten. Release-build zonder waarschuwingen of fouten. Het exportvenster is gecontroleerd op twee vensterhoogtes.
