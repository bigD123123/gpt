# Programmets opbygning

```text
Tk-brugerflade (ejer tilstand og handlinger)
    → nyt skærmbillede + revision + mål
    → baggrundstråd: valgt udbyder analyserer og beslutter
    → kø: JSON bliver til en valideret Decision
    → evt. brugerens godkendelse af set
    → TestLab: stopkontrol + revisionskontrol + aflæsningskontrol
    → tilladt ændring + læsning af faktisk resultat
    → log + opdateret brugerflade
    → næste billede, eller stop ved mål/fejl/tringrænse
```

- `vision_agent/ui.py`: skærmvalg, indstillinger, nøglelagerdialog, start/stop, godkendelse, ét igangværende kald og hændelsesvisning. Alle Tk-operationer og tilstandsændringer sker i hovedtråden. Svar fra tidligere kørsler ignoreres med et generationsnummer.
- `vision_agent/providers.py`: OpenAI, lokal Ollama eller tydeligt adskilt demo. Den rigtige AI modtager ikke testværdien som tekst; værdien skal læses fra billedet. Testværdien anvendes kun som input til demoudbyderen. Ingen automatiske fallback-beslutninger ved AI-fejl.
- `vision_agent/core.py`: eksplicit JSON-format og kontrolleret test-interface. Uafhængig af GUI og netværk.
- `vision_agent/audit.py`: roterende JSON Lines-log med UTC-tidsstempler. Loggens billedhash identificerer observationen, men erstatter ikke et gemt billede.
- `tests/`: grænse- og udbydertests, rigtig lokal HTTP-transport samt GUI-smoketest.
- `scripts/`: installation og Windows-pakning; isoleret virtuel Linux-skærm til GUI-smoketest.

## Tilladte beslutninger

```json
{"observed_value":20,"action":"add","amount":10,"reason":"Et trin nærmere målet 70"}
```

`observed_value` er et heltal fra 0 til 100. Ukendte felter, ukendte handlinger, booleske værdier som tal og ugyldige intervaller afvises. `add` kræver en ændring fra −10 til 10 eksklusive 0; `set` kræver en værdi fra 0 til 100 og brugerens godkendelse; `finish` kræver amount 0 og at den faktiske værdi er målet. JSON genvalideres ved handlingsgrænsen, så en anden udbyder heller ikke kan springe valideringen over.

Tilladelsen til et `set` er knyttet til den aktuelle beslutning og revision. Hvis værdien ændres, eller agenten stoppes, kan en gammel godkendelse ikke anvendes. Godkendelse giver aldrig modellen yderligere funktioner.

Resultatkontrol læser den faktiske testtilstand efter ændring og sammenligner med den forventede nye værdi. Det er ikke et nyt AI-kald. Hvis målet ikke er nået, optages et nyt billede på næste trin. Der er dermed både deterministisk kontrol og ny visuel observation, men ingen ekstra visuel slutkontrol efter sidste handling.

## Udvidelser

Et separat testprogram skal selv tilbyde en lokal autoriseret API med et tilsvarende begrænset sæt handlinger, revisionskontrol og tilstandslæsning. Tilføj adaptertests, før det forbindes til agenten. Den nuværende app er et færdigt kontrolleret tællereksperiment, ikke et generelt system til vilkårlig Windows-automatisering eller flere samtidige talfelter.

Modelvalg og mål gælder for den aktuelle session. API-nøglen kan gemmes i nøglelager; øvrige indstillinger nulstilles ved programstart. Der er ingen telemetri, automatisk opdatering eller skjult baggrundsproces.
