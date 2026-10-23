# Trainer Studio

Det aktuelle projekt er **Trainer Studio v0.3**, en AI-fri Windows-app med
procestilknytning, hukommelsessøgning, pointerstier, Trainer Builder og gemte
profiler. Se [Trainer Studio-vejledningen](trainer-studio/README.md) for
installation, funktioner og den tydelige skelnen mellem automatiserede tests
og senere afprøvning i rigtige spil.

Windows-pakker ligger under [GitHub Releases](https://github.com/bigD123123/gpt/releases).
Trainer Studio bygges fra mappen `trainer-studio`; det bruger ikke Python,
AI-modeller eller API-nøgler.

## Tidligere projekt: AI Automation Lab

En Windows-app med dansk brugerflade, som genskaber det centrale forløb fra **How I Created an AI That Can Hack Any Game** i et testmiljø, vi selv kontrollerer:

**Skærmbillede → AI-aflæsning → første søgning → tilladt handling → nyt billede → indsnævring → godkendt ændring → kontrol.**

Appen åbner sit eget lille testspil med valuta og ammunition. Søgningen arbejder på testspillets registrerede hukommelsesbuffer med rigtige adresser. Det mørke logvindue viser AI'ens aflæsninger, handlinger, kandidater og resultater. En direkte ændring kræver din godkendelse.

Videoen er undersøgt ud fra dine uploadede undertekster og skærmbilleder. De viser blandt andet OpenAI `gpt-5-nano`, AutoHotkey og hukommelsessøgning. Vores udgave bruger dokumenterede handlinger i sit eget testprogram i stedet for at generere AutoHotkey-kode eller tilslutte sig vilkårlige spilprocesser. Se [gennemgangen af videoen](docs/undersoegelse.md) og [den nye apps opbygning og afgrænsning](docs/replica.md).

## Installér på Windows

1. Installér **Python 3.12, 64-bit** fra https://www.python.org/downloads/windows/. Behold Python Launcher og Tcl/Tk i installationen.
2. Hent projektmappen og pak den ud. Åbn PowerShell i mappen, fx med højreklik → Åbn i Terminal.
3. Kør:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\scripts\install-windows.ps1
   ```

   `Bypass` gælder kun denne PowerShell-proces; scriptet ændrer ikke den permanente politik. Installationen opretter `.venv`, henter afhængigheder fra PyPI og kører automatiske tests. Der kræves ikke administratorrettigheder.

4. Dobbeltklik på **Start.cmd**.

## Prøv først uden API-nøgle

1. Behold **Demo (ingen AI)**.
2. Tryk **Start testprogram**. Hold testprogrammets vindue synligt, så det kan optages.
3. Vælg **Automatisk**, **Valuta** eller **Ammunition**. Behold fx slutværdien **999**, grænsen **6 cyklusser** og højst **12 AI-kald/trin**.
4. Tryk **Start AI-forløb**.

Følg loggen: Den første søgning finder flere mulige adresser. Testprogrammet udfører derefter en tilladt handling, værdien aflæses igen, og søgningen indsnævres. Når der er én kandidat tilbage, kan du trykke **Godkend ændring** for at tillade den foreslåede slutværdi. **Afvis** annullerer. Programmet kontrollerer resultatet. Den normale demo finder **3 → 2 → 1** kandidater og kræver seks observationer/beslutninger inklusive slutkontrollen. Demoens afsluttende aflæsning bruger kendte testdata, ikke billedgenkendelse. Brug **Nulstil testprogram** før et nyt forsøg fra udgangstilstanden.

Demoen bruger faste regler og testprogrammets kendte tilstand. Den afprøver forløbet og skærmoptagelsen, men udfører **ikke billedgenkendelse eller AI-beslutninger**. Brug OpenAI eller en lokal billedmodel til det.

**STOP / Esc** i hovedvinduet annullerer fortsættelsen og en eventuel ventende godkendelse. **P** starter forløbet, når hovedvinduet har fokus; genvejstasterne er ikke globale. Esc i testvinduet stopper testprogrammets handlinger. Stop blokerer straks afsendelsen af nye ændringer; en kommando, der allerede er afsendt, kan ikke trækkes tilbage. Et allerede afsendt AI-kald kan fortsat afsluttes og koste penge; svaret må ikke udløse en handling efter stop.

## Tilslut en rigtig AI

### OpenAI

Vælg OpenAI og fx **`gpt-5-nano`**, som vises i videoens kode. Den nye forbindelse bruger **Responses API** med et struktureret JSON-svar. Du kan vælge en anden model, hvis din konto har adgang til den, og den understøtter billeder og det krævede svarformat.

Gem din API-nøgle gennem appens nøglelagerdialog. På Windows bruges **Windows Credential Manager** via `keyring`. Nøglen skrives ikke i kildekode eller en indstillingsfil. En eksisterende miljøvariabel `VISION_API_KEY` har forrang. Dit ChatGPT-abonnement erstatter ikke API-betaling.

Ved OpenAI-brug sendes billeder af testprogrammet til OpenAI. På Windows optages testprogrammets eget vindueshåndtag; Linux-smoketesten bruger det løftede vindues skærmområde. Sørg for, at området ikke indeholder private oplysninger. Den rigtige AI får billedet og de tilladte handlinger; den får ikke facitværdierne fra testprogrammet som en erstatning for billedaflæsning.

Ved **Automatisk** vælger modellen selv valuta eller ammunition. Ved et eksplicit valg låses modellen til den valgte type fra første billede. Programmet kontrollerer aflæsningen mod testprogrammets egen tilstand og afviser et forkert eller forældet svar. En model kan fejle; der skiftes ikke automatisk til demo ved fejl.

### Lokal Ollama-model

Installér Ollama fra https://ollama.com/ og hent en model med billedforståelse, fx:

```powershell
ollama pull llama3.2-vision
```

Lad Ollama køre, vælg **Ollama (lokal)**, og angiv navnet på den installerede model. Billeder sendes til `127.0.0.1:11434`. Denne model kræver betydelig RAM og fungerer bedst med GPU. Modeller uden billedforståelse kan ikke bruges til dette forløb.

Live OpenAI, lokal modelkvalitet og Windows-specifikke funktioner skal afprøves med dine egne forudsætninger. Se den konkrete [teststatus](docs/teststatus.md); implementeret kode er ikke det samme som en gennemført live-test.

## Hvad appen kan styre

Appen starter og kommunikerer med **sit eget testprogram**. Modellen kan foreslå de navngivne handlinger `collect`, `spend`, `fire` og `reload`. De udføres gennem testprogrammets dokumenterede interface.

Søgningen er begrænset til den buffer, testprogrammet selv stiller til rådighed. Der er ingen indtastning af et vilkårligt proces-id, vilkårlige adresser eller scripts. En ændring kræver både en entydig kandidat, en gyldig tilstandsrevision og din godkendelse. Den kobler sig ikke til Plants vs. Zombies eller andre eksisterende spil.

Den nuværende hovedapp vælger automatisk det testvindue, den selv har startet. Manuel valg af et andet vindue eller et udsnit i hovedappen er endnu ikke implementeret. Genvejstasten er fast **P** og virker kun, når hovedvinduet har fokus; en konfigurerbar eller global genvej er også senere arbejde. Det bevarer testgrænsen, mens Windows-optagelse og AI-kvalitet bliver valideret.

Billeder sendes kun som led i et aktivt trin. Cyklusgrænsen kan sættes fra 1 til 12 og er som standard 6. Den separate kaldgrænse kan sættes fra 1 til 30 og er som standard 12; programmet viser det aktuelle antal. Hver cyklus kan indeholde både en handlingsanalyse og en efterfølgende billedaflæsning; første observation og slutkontrol er yderligere trin. Der er ét igangværende AI-kald ad gangen og ingen automatiske genforsøg. Når udbyderen oplyser tokenforbrug, viser og logger appen input- og outputtokens, også hvis et færdigt svar ankommer efter STOP. Det er ikke en prisberegning, fordi modelpriser kan ændre sig; kontrollér den faktiske omkostning og sæt også en udgiftsgrænse på API-kontoen.

## Log og fejlfinding

Loggen ligger på Windows i:

```text
%LOCALAPPDATA%\VisionAgent\events.jsonl
```

Den roterer ved 2 MB og beholder tre backupfiler. `last-plan.json` i samme mappe gemmer seneste kørsels beslutninger og resultater som data; filen er ikke et script og bliver ikke kørt som kode. API-nøgler, rå HTTP-svar og almindelige skærmoptagelser gemmes ikke i loggen. Beslutninger og AI-begrundelser kan indeholde oplysninger fra det optagne område, så behandl loggen derefter.

- **Ingen billedoptagelse:** sørg for en aktiv desktop, og hold testvinduet synligt og udækket.
- **Forkert aflæsning:** hold testvinduet synligt med tydelige tal, og prøv en passende billedmodel. En afvisning er ikke et bevis på, at tallet er fundet korrekt.
- **Ingen entydig kandidat:** følg forløbets grænse; appen skal ikke skrive til flere adresser for at prøve sig frem.
- **AI-forbindelse fejler:** kontrollér modelnavn, konto/adgang, kvote eller den lokale Ollama-server. Del aldrig API-nøglen i logfiler eller fejlrapporter.

## Byg en Windows .exe

Kør på Windows efter installation:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-windows.ps1
```

Filerne bliver `dist\AI-Automation-Lab.exe` og en tilhørende SHA-256-fil. Den pakkede app starter sit eget testprogram fra samme eksekverbare program via `--test-game`. Byggescriptet starter den færdige EXE i en kort pakket smoketest og kontrollerer start, tilstand, registreret buffer, IPC og lukning. Modtageren behøver ikke Python. Pakningen bevarer processens standardinput og standardoutput til kommunikationen med testprogrammet og forsøger at skjule konsollen på Windows.

Byggescriptet bruger PyInstaller og medtager Windows-nøglelagerets backend. Windows-bygning og kørsel kan ikke bekræftes alene fra dette Linux-miljø. Test derfor både hovedvindue, testprogram, billedoptagelse, stopfunktion og nøglelager på Windows før distribution. Filen er ikke kodesigneret, så SmartScreen kan advare.

Der ligger også en manuel GitHub Actions-workflow i `.github/workflows/windows-build.yml`. Når projektet er lagt på GitHub, kan du vælge **Actions → Build Windows application → Run workflow** og hente artefaktet efter en vellykket kørsel. En skrevet workflow er ikke en gennemført bygning.

## Udvikling og tests

```bash
python -m venv .venv
# Linux:
.venv/bin/python -m pip install -r requirements.txt
.venv/bin/python -m unittest discover -s tests -v
.venv/bin/python -m vision_agent
```

Windows bruger `.venv\Scripts\python.exe`. GUI og skærmoptagelse kræver en aktiv desktop. På Linux kan Tcl/Tk kræve en separat OS-pakke. I dette cloudmiljø kan logplaceringen sættes til et skrivbart sted med `LOCALAPPDATA=/workspace/gpt/.runtime`.

Cloudhjælperne `sh scripts/install-cloud.sh` og `sh scripts/cloud-gui-smoke.sh` klargør miljøet og kører GUI-kontroller på en midlertidig virtuel skærm. Se [teststatus](docs/teststatus.md) for præcis hvilke tests der er gennemført; der er ingen webpreview. Den oplyste Windows-maskine og et privat skærmoplysningsscript er beskrevet i [Windows-målmaskinen](docs/windows-target.md).

Den første udgave af projektet var et enkelt tællereksperiment med `add`, `set` og `finish`. Den kan fortsat startes fra kildekoden med `.venv/bin/python -m vision_agent --classic` (Windows: `.venv\Scripts\python.exe -m vision_agent --classic`). [Den oprindelige arkitekturbeskrivelse](docs/arkitektur.md) og [videoundersøgelsen](docs/undersoegelse.md) forklarer udgangspunktet. [docs/replica.md](docs/replica.md) beskriver det nye søgeforløb.
