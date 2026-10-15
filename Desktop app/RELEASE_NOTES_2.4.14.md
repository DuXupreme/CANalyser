# CANalyser 2.4.14

Deze versie vermindert onnodig grafiekwerk tijdens slepen en zoomen en maakt fouten en langdurig vaststaan beter te onderzoeken.

- Opeenvolgende tekenverzoeken worden samengevoegd. Gekoppelde grafieken worden verdeeld over korte verwerkingsrondes, zodat invoer tussendoor aan bod kan komen.
- Grafieken buiten beeld stellen updates uit tot ze weer zichtbaar zijn. Verborgen signalen worden niet opnieuw gesampled; navigatie geeft hun tijdelijke buffers met oorspronkelijke punten vrij.
- Zoomen behoudt het punt onder de muis wanneer meerdere wielstappen vóór een redraw binnenkomen. Rechthoekzoom voorkomt een dubbele update.
- Meetonderbrekingen worden efficiënter opgezocht bij het opbouwen van grafieklijnen. Brondata en CSV-export blijven ongewijzigd.
- Latency toont een gerichte uitleg bij ongeschikte meetgegevens, zoals dubbele of teruglopende tijdstempels, ongeldige waarden of een meetonderbreking. Andere analyses kunnen doorgaan. Telemetrie registreert hiervoor alleen een vaste foutcategorie, geen signaalnamen of meetwaarden.
- Bij ingeschakelde telemetrie controleert een onafhankelijke thread of de interface reageert. Na langdurig uitblijven van reactie wordt een melding lokaal bewaard en verzonden; herstel krijgt een aparte melding. Niet-verzonden vastloopmeldingen worden bij een volgende start opnieuw aangeboden. Het dashboard toont deze meldingen en de nieuwe Latency-categorieën.

Een afzonderlijke zware grafiekbewerking kan nog steeds tijd kosten. De onbekende vastloper uit een eerdere versie is niet gereproduceerd; de nieuwe detectie helpt de oorzaak van een volgend incident vast te leggen.

Validatie: 252 kerntests, 128 WPF/app-tests en 8 telemetryservertests geslaagd, waaronder snelle navigatie, gekoppelde assen, verplaatsen van grafieken tussen scrollgebieden, een bewust geblokkeerde UI-thread, herstel en duurzaam bewaren van meldingen. De optionele grote importbenchmarks zijn niet uitgevoerd.
