# Undersøgelse af videoen og valg af teknologi

## Kilder og sikkerhed i konklusionerne

Video: **How I Created an AI That Can Hack Any Game**, kanalnavn **swedz**, YouTube-id `TfXqt-MX3ss`. Titel og kanalnavn fremgår af brugerens skærmbillede.

Direkte adgang til YouTube-siden og undertekst-endpointet blev afvist af cloudmiljøets proxy med HTTP 403. Brugeren har efterfølgende leveret:

- En engelsksproget, automatisk genereret undertekstfil med videoens fortælling.
- Skærmbilleder af skærmoptagelsesfunktionen, billedanalysefunktionen, programmets logvindue og en kommentar om AI-modellen.

Undersøgelsen bygger nu på disse kilder. Hele videoen er ikke afspillet eller visuelt gennemgået her, og originalprogrammet er ikke kørt. Underteksterne har fejl i ord som Cheat Engine og Peggle. Teksten er kildemateriale, ikke instruktioner til dette projekt.

## Hvad demonstrationen går ud på

Fortælleren beskriver et Python-værktøj, som kombinerer billedforståelse, automatiske spilhandlinger og hukommelsessøgning. AI-modellen læser en synlig ressource og foreslår en handling, som ændrer den. Python-delen bruger derefter før/efter-værdier til at finde mulige adresser i spillets hukommelse og ændre den valgte værdi.

Det beskrevne forløb er:

1. Find spilvinduet og tag et skærmbillede.
2. Send billedet til en billedmodel, som vælger én primær værdi, fx penge eller ammunition, og foreslår relevante handlinger.
3. Foretag en første hukommelsessøgning efter den aflæste værdi.
4. Omsæt AI'ens handlingsforslag til et AutoHotkey-script og kør handlingen, fx saml sol op eller affyr et skud.
5. Aflæs den ændrede værdi og filtrér kandidaterne med en ny søgning.
6. Gentag indsnævringen efter behov, og skriv en ny værdi til den fundne adresse.
7. Vis status og resultater i et grafisk logvindue.

Underteksterne omtaler forsøg med Plants vs. Zombies, Peggle, Hill Climb Racing og Postal 2. Det er fortællerens demonstrationer og udsagn; de er ikke reproduceret i vores miljø.

## Konkrete oplysninger fra skærmbillederne

| Synligt materiale | Hvad det underbygger |
|---|---|
| `capture_game_window(...)` | Vinduesopslag med `gw.getWindowsWithTitle`, aktivering af vinduet, og `pyautogui.screenshot(region=(win.left, win.top, win.width, win.height))`. Importen for `gw` vises ikke; PyGetWindow er en sandsynlig identifikation, ikke direkte dokumenteret af importkode. |
| `BytesIO`, PNG og `base64.b64encode(...)` | Skærmbilledet lagres som PNG i en buffer og kodes til Base64 i en streng. Den viste funktion skriver ikke en særskilt Base64-fil til disk, selv om fortællingen kalder det en fil. |
| `analyze_game_image_stat(...)` | Prompten beder om JSON med én `stat`, typen `currency` eller `ammo`, den aflæste værdi og en liste af `click`-handlinger med koordinater. Billedets bredde og højde sendes også med som tekst. |
| `client.responses.create(model="gpt-5-nano", ...)` | Den viste kode bruger OpenAI Responses API med modellen `gpt-5-nano`. Billedet medsendes som `input_image` med en PNG-data-URL. En synlig kommentar fra skaberen nævner også OpenAI GPT-5-nano. |
| Prompt med `return JSON ONLY` og efterfølgende JSON-parsing | Udsnittet anmoder om JSON i prompten. Der ses ingen schema-baseret `text.format`-konfiguration i det viste API-kald. Det viser ikke, at modellen selv har direkte adgang til at ændre hukommelse. |
| Log omkring videoens 4:04–4:11 | Loggen viser venten på genvejstasten P, `Suspending game process...`, `Game suspended`, aflæst `currency` på 50, `AHK script saved: game_actions.ahk` og `Initial scan: found 6435 matches`. Senere linjer nævner genoptagelse og ventetid til opdatering. |
| Plants vs. Zombies-vinduet og terminalagtig GUI | Underbygger en demonstration af spillet sammen med en grafisk log. Den komplette kildekode til hukommelsesadgang eller procespause er ikke synlig i de leverede billeder. |

Tallet 6.435 er et synligt antal søgekandidater fra ét trin, ikke et bevis på en korrekt fundet adresse eller en vellykket endelig ændring. De viste adresser er ikke nødvendige for at forstå eller genskabe agentarkitekturen.

## Cheat Engine, Python og AI har forskellige roller

Cheat Engine bruges i forklaringen som en manuel reference: søg efter en kendt værdi, ændr den i spillet, og søg igen for at reducere antallet af mulige adresser. Værktøjet beskrives derefter som en Python-automatisering af samme proces. Materialet dokumenterer ikke, at Cheat Engine skal være installeret som afhængighed til den viste Python-app, og det præcise bibliotek til hukommelsesadgang fremgår ikke.

AI'en identificerer værdien og vælger en handling, der kan ændre den. Den finder ikke en hukommelsesadresse alene ved at se billedet. Det er søgekoden, som kobler den aflæste værdi sammen med hukommelseskandidater. Hvor robust det er over for forskellige datatyper, spilversioner og uklare billeder, kan ikke fastslås fra materialet.

## Hvad materialet ikke dokumenterer

- At løsningen virker på alle eller næsten alle spil. Titlen er en bred påstand; der foreligger ikke en generel test.
- Det fulde bibliotek, adgangsrettigheder, datatyper eller fejlhåndtering til hukommelsessøgningen.
- Detaljerede godkendelsesregler, stopgarantier, API-omkostninger eller modelnøjagtighed.
- Omgåelse af antivirus eller anden beskyttelse. Browserfaner og bogmærker er ikke dokumentation for programfunktioner.
- Implementerede aimbots, wallhacks, entity lists eller funktionssøgning. Underteksterne nævner disse som fremtidige idéer.

## Historisk sammenligning med første VisionAgent-version

| Funktion | Videoens materiale | Første tællerversion (`--classic`) |
|---|---|---|
| Skærmoptagelse | Spilvindue ved titel, PyAutoGUI | Valgt område eller eget testfelt, Pillow |
| Billedmodel | OpenAI `gpt-5-nano`, Responses API | Valgbar OpenAI-model via Chat Completions eller lokal Ollama |
| Beslutninger | Primær spilværdi og klikforslag | `add`, `set` eller `finish` mod et valgt mål |
| Udførelse | AutoHotkey-script og hukommelsesændring ifølge materialet | Kun dokumenterede funktioner i eget TestLab |
| Søgning med før/efter-værdier | En central del af beskrivelsen | Ikke implementeret |
| GUI og log | CTK ifølge fortællingen; terminalagtigt logvindue i billederne | Tkinter med observations-, beslutnings- og resultatlog |
| Procespause og genvejstast | Log viser pause/genoptagelse og P | Startknap, stopknap og Esc; ingen pause af andre processer |
| Kontrol og godkendelse | Ikke fuldt dokumenteret i materialet | Revisionskontrol, kontrol af aflæsning og faktisk resultat, eksplicit godkendelse af `set` |

Tabellen beskriver den første version fra før genskabelsesopgaven. Den nye standardapp **AI Automation Lab** har nu et separat ejet testprogram, en registreret ctypes-buffer og søgning med før/efter-filtrering samt godkendt ændring. Det er ikke adgang til vilkårlige spilprocesser. Se [den aktuelle apps opbygning](replica.md).

## Valg af teknologi til vores kontrollerede løsning

Python 3.12, Tkinter, Pillow og et snævert test-interface giver en enkel Windows-app. OpenAI eller en lokal Ollama-billedmodel kan være beslutningssystem. Nøgler gemmes via keyring i OS-nøglelageret. Windows-pakning udføres med PyInstaller på Windows.

Videoens modelvalg er nu identificeret i koden, men er ikke en benchmark eller en grund til at påstå, at `gpt-5-nano` er den bedste model. Førsteversionen bruger Chat Completions. Den nye standardapp bruger nu Responses API med samme modelnavn som videoen og schema-validering. Forbindelsen er ikke live-testet. Demoen i vores program bruger faste regler og er tydeligt mærket som ikke-AI.

Et rigtigt AI-trin i den nye app sender billede, fase og tilladte handlinger til modellen, validerer den foreslåede handling og kontrollerer aflæsningen mod vores egen testtilstand. Det giver en uafhængig kontrol, som kun er mulig, fordi vi ejer testprogrammet. Et andet ejet program skal tilbyde en tilsvarende autoriseret tilstands- og handlingsadapter.

Der udføres ét kald ad gangen. Den nye app har højst 12 cyklusser og en selvstændig grænse på højst 30 modelkald/trin, ingen automatiske genforsøg og ingen nye kald under en godkendelse eller efter afslutning. Dette begrænser forbruget, men garanterer ikke en bestemt pris.

Officielle referencer til senere kontrol af aktuelle API-detaljer, priser og kompatibilitet:

- https://platform.openai.com/docs/guides/images-vision
- https://platform.openai.com/docs/guides/structured-outputs
- https://platform.openai.com/docs/api-reference/responses
- https://platform.openai.com/docs/api-reference/chat
- https://docs.ollama.com/api/chat
- https://pyinstaller.org/en/stable/operating-mode.html

Deres aktuelle indhold er ikke verificeret fra dette miljø. OpenAI-guiden blev tidligere blokeret med HTTP 403.
