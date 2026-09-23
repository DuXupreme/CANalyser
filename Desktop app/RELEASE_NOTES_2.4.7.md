# CANalyser 2.4.7

- STRICT opnieuw proberen hergebruikt ingelezen logdata als de broninhoud niet is gewijzigd. ZIP-uitpakken en MF4-conversie worden dan overgeslagen. Een gewijzigde DBC wordt opnieuw gevalideerd en gedecodeerd; zonder bronwijzigingen wordt ook de bestaande decodeeruitkomst hergebruikt.
- Ctrl+Z maakt slepen en zoomen in de analysegrafieken en het losse grafiekvenster ongedaan, inclusief gekoppelde assen.
- Uitschakelen van downsampling vraagt vooraf om bevestiging, met Nee als standaardkeuze.

De fixes voor complete online-logperioden en grote selecties uit 2.4.6 blijven behouden. De broninhoud wordt bij hergebruik gecontroleerd; gewijzigde logs worden opnieuw ingelezen.
