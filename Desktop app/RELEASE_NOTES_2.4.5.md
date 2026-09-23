# CANalyser 2.4.5

- Online logperioden tonen alle resultaten, ook wanneer de API meer dan 200 bestanden vindt. Vroege logs verdwijnen daardoor niet meer achter recente bestanden.
- Logstart en uploadtijd blijven strikt gescheiden. De meetstart van een ingeladen deelbestand bevat nu de offset van het eerste record.
- Instellingen bevat Downloadcache wissen, met opslaggebruik en vrijgemaakte MB. Actieve en vergrendelde bestanden blijven behouden; AWS-logs worden niet verwijderd.
- Bevat tevens de verbeteringen uit 2.4.4 voor responsief inladen en crashdiagnostiek.

Gecontroleerd tegen 14 september: 371 logs beschikbaar; eerste meettijd 11:28:44 lokaal, uploadtijd 17:33:35 lokaal. Het dashboard toont beide tijden afzonderlijk.
