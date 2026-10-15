# CANalyser 2.4.15

De logger van Vlindermachine 1 is op 15 oktober 2026 om 13:31:19 Nederlandse tijd overgezet naar Vlindermachine 3. Deze versie maakt beide machines afzonderlijk beschikbaar en behoudt de historische indeling van de oude metingen.

- Online logs heeft een eigen selectie voor Vlindermachine 3. Eerdere selecties van Vlindermachine 1 blijven bij die machine horen, ook wanneer je ze later herstelt.
- De broninformatie van geïmporteerde MF4-bestanden gebruikt de meetstart om Vlindermachine 1 en 3 te onderscheiden. Bestandsnamen, logger-ID, sessies en oorspronkelijke bronpaden blijven behouden.
- Analyse-export vult de machine alleen automatisch in wanneer alle bronbestanden eenduidig bij dezelfde machine horen. Bij ontbrekende meettijden of een combinatie van machines blijft het veld leeg, zodat je de identiteit zelf kunt controleren.
- Bestaande logbestanden worden niet verplaatst, hernoemd of verwijderd.

Validatie: 257 kerntests en 149 WPF/app-tests geslaagd, inclusief de omschakelgrens, historische broninformatie, export, het herstellen van oude en nieuwe selecties en bestaande MF4-imports. De bronkoppeling is ook gecontroleerd met twee bestaande MF4-bestanden. De optionele grote importbenchmarks zijn niet uitgevoerd.
