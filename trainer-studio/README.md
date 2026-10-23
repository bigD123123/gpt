# Trainer Studio

**Version 0.3.0** er en Windows-app under udvikling til arbejdsgangen
**vælg proces → find værdi → opret funktion → gem profil → genbrug trainer**.
Appen er skrevet fra bunden i C#/.NET og WPF og indeholder ingen AI.

Standardvinduet arbejder nu med en Windows-proces, som du selv vælger.
Du behøver ikke hente dine spil for at fortsætte udviklingen: funktionerne
bygges og kontrolleres med automatiserede tests nu, mens kompatibiliteten med
dine Unity-, Unreal- og ældre spil først kan bekræftes ved senere spiltest.
Der er ikke udviklet et nyt testspil. Den gamle indbyggede testmodel findes
fortsat i kildekoden og de oprindelige kernetests, men er ikke appens normale
startvindue.

Programmet er beregnet til lokale singleplayer-programmer, som du ejer eller
har tilladelse til at undersøge. Det indeholder ikke omgåelse af anti-cheat
eller antivirus, kodeinjektion, drivere eller ændring af serverstyrede værdier.
Det genbruger ikke kode fra Cheat Engine, x64dbg, WeMod, ArtMoney eller ReClass.

Læs [ændringslisten](CHANGELOG.md), [proces- og spilmotorvejledningen](docs/PROCESS_SUPPORT.md)
og [research/roadmap](RESEARCH.md) for flere detaljer.

## Start på Windows

- Windows 10/11, 64-bit. Appen bygges som x64 og har kode til både x86- og
  x64-målprocesser. Faktisk spilkompatibilitet skal afprøves separat.
- Den publicerede `TrainerStudio.exe` er selvstændig; du behøver ikke installere
  .NET for at bruge udgivelsespakken.
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) er kun
  nødvendigt, hvis du vil bygge fra kildekoden.

Luk den gamle app, pak den nye ZIP ud i en ny mappe, og start
**TrainerStudio.exe**. Titlen skal vise **Trainer Studio v0.3.0**.
Din Ryzen 7 3800X og 16 GB RAM er tilstrækkelig til appen. GPU og API-nøgle er
ikke nødvendige. Store processer kan kræve flere afgrænsede søgninger.

## Første søgning i et spil, når det er tilgængeligt

1. Start spillet. Vælg det i proceslisten, og tryk **Forbind (kun læsning)**.
   Vælg spillets faktiske program frem for en launcher. Appen viser den valgte
   proces og åbner først kun med læseadgang.
2. Åbn fanen **Søg og opret funktion**. Vælg en passende datatype, fx
   **Int32** for et heltal eller **Float** for et decimaltal. Det er et valg,
   der skal afprøves; spilmotoren bestemmer ikke automatisk den rigtige type.
3. Hvis du kender tallet, vælg **Præcis værdi**, skriv tallet, og tryk
   **Første søgning**. Ellers vælg **Ukendt startværdi**.
4. Ændr tallet i spillet, fx ved at bruge lidt ammunition. Vælg derefter
   **Præcis værdi**, **Steget**, **Faldet**, **Ændret** eller **Uændret**, og
   tryk **Næste søgning**. Gentag efter behov.
5. Vælg en kandidat, og brug **Læs valgt igen** til at kontrollere den aktuelle
   værdi. Et læsbart tal er ikke bevis på, at det styrer det ønskede i spillet.
6. Giv funktionen et navn i Builder. Vælg kontrol og handling, og tryk
   **Opret fra valgt værdi / pointersti**. Gå til **Min trainer og profiler**
   og åbn **Åbn min trainer**.
7. Når du vil afprøve ændringen, slå **Tillad værdiændringer i det valgte
   program** til. Brug funktionen, kontrollér proces, adresse, datatype og
   værdi i bekræftelsen, og afprøv resultatet i spillet. Gem derefter profilen.

Du kan annullere en søgning med **Annullér søgning**. Den forrige færdige
søgesession bevares ved annullering. **Ryd søgning** starter bevidst forfra.

## Hvor meget bliver undersøgt?

Værdisøgningen læser i blokke og kører i baggrunden, så vinduet forbliver
responsivt. Der er følgende synlige grænser:

- Standardbudget: **256 MiB** læst hukommelse. Feltet accepterer 1–2.048 MiB.
- Kandidatgrænse: **100.000**. Tabellen viser højst **2.000** af kandidaterne;
  næste søgning arbejder fortsat på den gemte kandidatgruppe.
- **Kun skrivbare dataområder** er slået til som standard. Det kræver ikke
  skriveadgang; det filtrerer kun, hvilke områder der læses.
- **Medtag mapped områder** er slået fra som standard. Du kan slå det til,
  hvis søgningen også skal omfatte læsbare områder med memory-mapped data.
  Det øvrige læsebudget og filteret for skrivbare områder gælder fortsat.
- Du kan angive start- og slutadresse som hexadecimale tal. Slutadressen er
  eksklusiv. Tomme felter bruger målprocessens adresseområde.
- **Ujusterede værdier** undersøger hver byte i stedet for naturligt justerede
  adresser og kan derfor tage længere tid.

En meddelelse om **AFGRÆNSET SØGNING** betyder, at kandidat-, læse- eller
arbejdsbudgettet blev nået. En enkelt tilbageværende kandidat er da kun
entydig inden for den undersøgte del. Et tomt resultat beviser heller ikke,
at værdien mangler i resten af processen. Ulæselige blokke rapporteres.

Spillet kører videre under læsningen. En blokvis søgning er derfor ikke et
låst øjebliksbillede af hele processen; værdier kan ændre sig, mens søgningen
arbejder. Gentagne observationer og en afprøvet ændring i spillet er stadig
nødvendige.

## Opret og organisér din trainer

| Kontrol | Brug |
|---|---|
| Knap | Indstil, tilføj eller gendan en værdi én gang. |
| Kontakt | Fasthold en værdi, indtil den deaktiveres eller stoppes. |
| Slider | Indstil en værdi inden for gemt minimum, maksimum og trin. |
| Talindtastning | Indstil eller tilføj det tal, du skriver ved anvendelsen. |

Navn, kategori, beskrivelse, værdi og eventuel genvej gemmes i profilen.
**Redigér valgt** indlæser en eksisterende funktion i Builder; brug
**Opdatér valgt trainerfunktion** til at gemme rettelsen i den aktuelle profil.
Du kan også kopiere, flytte og slette funktioner. Profilen skal gemmes for at
bevare ændringerne på disken.

Hvis en reference er ugyldig, kan den repareres uden at miste navnet og
kategorien: vælg **Redigér valgt**, find værdien på ny, vælg det nye fund
eller en ny pointersti, og brug **Opdatér valgt trainerfunktion**. At rette
funktionsnavnet alene godkender ikke et gammelt offset efter en spilopdatering.

Genveje som `F1` eller `Ctrl+F2` virker i appens hovedvindue og den kompakte
trainer-menu, når fokus ikke står i et tekst- eller valgfelt. De er **ikke
globale spilgenveje** og virker derfor ikke, mens selve spillet har fokus.
Slider og talindtastning betjenes fra den kompakte trainer-menu.

Float-sliders kan bruge decimaltrin som `0.1`; brug punktum i talfelterne.
Int64-/UInt64-sliders med minimum eller maksimum uden for `±2^53` skal
betjenes med **Talindtastning**, så sliderens grafiske talrepræsentation ikke
giver tab af heltalspræcision.

**STOP ALLE (Esc)** stopper fastholdelser, slår skriveadgang fra og annullerer
en igangværende søgning. Det skriver ikke en ny værdi og lover ikke at rulle
tidligere engangsændringer tilbage. **Gendan oprindelig** kræver en kendt
værdi fra samme adresse i samme aktuelle processtart. Hvis en pointer flytter
sig, må den gamle værdi ikke gendannes i et nyt objekt.

## Gemte funktioner efter genstart

Der findes tre typer henvisninger:

- **Kun denne session:** fundet ligger uden for et kendt modul og gemmes som
  adresse bundet til den aktuelle processtart. Det bliver ugyldigt efter
  genstart. Det bliver ikke kaldt stabilt blot ved at gemme en tilfældig offset.
- **Modulrelativ:** et kendt modul og en offset inden for modulets område.
  Baseadressen opløses på ny efter genstart.
- **Pointersti:** en modulrod efterfulgt af pointerled. Pointerbredde,
  slutværdiens datatype og build-oplysninger gemmes og kontrolleres igen.

For at finde en pointersti: vælg først et aktuelt søgeresultat, gå til
**Pointerstier**, og tryk **Find stier til valgt værdi**. Brugerfladen tillader
dybde 1–5 og et afgrænset maksimalt offset. Søgningen finder kun stier med
modulrod og ikke-negative offsets. Den har også grænser for læsning,
pointerantal, stier og arbejde; den garanterer ikke at finde enhver mulig sti.

Pointeranalysen læser modulområder før de øvrige områder, så mulige rødder
prioriteres. Den bruger stadig det samme samlede læsebudget: områderne
læses ikke dobbelt, og budgettet bliver ikke automatisk større.
Den almindelige værdisøgning bruger normal adresserækkefølge.

Vælg en sti og **Brug valgt sti i Builder**, hvis du vil gemme den som en
funktion. **Genvalidér fundne stier** beholder de stier, der aktuelt løser til
det valgte søgeresultat. En bestået genvalidering beviser læsbarhed og
adresseopløsning, ikke at spillets betydning er uændret.

Gemte pointerhenvisninger kan indlæses i listen med **Hent stier fra
profilen**. Efter en genstart kan du dermed genfinde værdien uafhængigt,
vælge det nye søgeresultat og genvalidere profilens stier mod den nye adresse.
Du behøver ikke starte en ny fuld pointersøgning for at foretage dette
krydstjek. Almindelig profilvalidering kan stadig bruges uden en ny scanning.

Efter spillets genstart: forbind til den nye proces, vælg profilen på
**Min trainer og profiler**, tryk **Indlæs** og **Valider**. Funktionerne
aktiveres aldrig automatisk. Programidentitet, arkitektur, exe-build og
rodmodulets SHA-256-fingeraftryk kontrolleres. Ændrede builds kan kræve, at
værdien genfindes og funktionen repareres. Spillets egne sessioner, save/load,
levelskift og opdateringer skal stadig afprøves i det konkrete spil.

## Profiler, genkendelse og log

Proceslisten opdateres cirka hvert ottende sekund. Appen viser, når en kørende
proces matcher en gemt profil, men du vælger selv tilknytning og profil.
Genkendelsen er baseret på programidentitet, ikke en garanti om motorstøtte.
Unity-/Unreal-navne i listen er kun filbaserede fingerpeg.

Profiler kan duplikeres, eksporteres og importeres som `.trainerprofile.json`.
Import opretter nye profil- og funktions-id'er og aktiverer ingen funktioner.
Gemning beholder op til ti backups af profilens tidligere versioner.

```text
%LOCALAPPDATA%\TrainerStudio\Profiles\*.trainerprofile.json
%LOCALAPPDATA%\TrainerStudio\Profiles\Backups\<profil-id>\*.trainerprofile.json
%LOCALAPPDATA%\TrainerStudio\events.jsonl
```

v0.3 bruger **profilformat 3** og kan migrere format 2 ved indlæsning.
Når en profil er gemt i format 3, kan de gamle v0.2-apps ikke indlæse den.
Behold en eksport eller backup af den gamle fil, hvis den også skal bruges
med en ældre app.

Fanen **Hændelser** viser handlinger og fejl. `events.jsonl` indeholder én
JSON-post pr. hændelse, herunder målidentitet, funktions-id og relevante
før/efter-værdier. Loggen er ikke et fuldt hukommelsesdump og indeholder ingen
API-nøgler eller scripts.

## Implementeret, testet og afventer spil

| Område | Implementeret i v0.3 | Automatisk kontrol i denne udviklingsrunde | Rigtige spil |
|---|---|---|---|
| Procesliste, læsetilknytning og særskilt skriveadgang | Ja, Windows-adapter og brugerflade. | Otte Windows-API-testgrupper er implementeret og bygger; x64-/x86-kørsel afventer CI før release. | Afventer spil og launchere. |
| 11 datatyper, seks søgemetoder, streaming og annullering | Ja, med justering, adresseinterval og budgetter. | 16 kernetests og 12 scannertests er kørt lokalt med succes. | Ydelse og værdifund afventer konkrete spil. |
| Modul-, pointer- og sessionshenvisninger | Ja, inkl. buildkontrol og profilformat 3. | 18 referencetests er kørt lokalt med succes. | Genstart, save/load og levelskift afventer spil. |
| Skrivning, readback, fastholdelse og sessionsbaseret gendan | Ja, med stop ved flyttet reference og afvisning af overlappende fastholdelser. | Kerne-/referencetests samt Windows-API-suite før release. | Virkning på faktisk gameplay afventer spil. |
| Builder, redigering, profiler, genkendelse og kompakt menu | Ja. | Kernetests bestået; ti Windows-renderingskontroller skal køres i CI før release. | Brugervenlighed med rigtige profiler afventer. |
| Unity/Unreal/ældre spil | Fælles Windows-fundament; ingen generel motorinspektør. | Ingen automatiske tests beviser støtte til alle motorbuilds. | Ingen af brugerens spil er endnu afprøvet. |
| Hastighed | Slider til et identificeret datafelt kan oprettes. | Værdi- og intervalregler testes. | Feltet og dets effekt skal identificeres i spillet. |

Den 23. oktober 2026 bestod **46 platformuafhængige tests** lokalt:
16 kerne-, 12 scanner- og 18 referencetests. Appen og WPF-testprojektet
byggede med 0 fejl og 0 advarsler. Den native Windows-testsuite bygger også,
og publicering af dens selvstændige x86-fixture er gennemført lokalt.
Windows-API- og WPF-kontrollerne er obligatoriske i releaseworkflowet og
afventer faktisk Windows-kørsel ved denne
statusopdatering. Et grønt Linux-build beviser kompilering, ikke
Windows-kørsel; den endelige status fremgår af releasens workflowresultat.

Der er endnu ikke CE-tabelimport, selvstændig trainer-eksport, globale
hotkeys, drag-and-drop, debugger, kodepatching, strukturinspektion eller fuldt
dansk/engelsk sprogskift. Der er ingen universel speedhack; hastighed kræver
en identificeret data-værdi, som spillet faktisk bruger.

## Kør eller byg fra kildekoden

Åbn PowerShell i `trainer-studio`. På Windows kan du starte appen med:

```powershell
dotnet run --project .\src\TrainerStudio.App\TrainerStudio.App.csproj -c Release
```

De almindelige platformuafhængige kodetests køres med:

```powershell
dotnet run --project .\tests\TrainerStudio.Core.Tests\TrainerStudio.Core.Tests.csproj -c Release
dotnet run --project .\tests\TrainerStudio.Scanner.Tests\TrainerStudio.Scanner.Tests.csproj -c Release
dotnet run --project .\tests\TrainerStudio.Reference.Tests\TrainerStudio.Reference.Tests.csproj -c Release
```

Windows-API-testene bruger en kontrolleret buffer i automatiserede
testprocesser. De er ikke et testspil og erstatter ikke senere spiltest.
På Windows køres API- og rendertestene med:

```powershell
dotnet run --project .\tests\TrainerStudio.Windows.Tests\TrainerStudio.Windows.Tests.csproj -c Release
dotnet run --project .\tests\TrainerStudio.Theme.Tests\TrainerStudio.Theme.Tests.csproj -c Release -- .\artifacts\theme-tests
```

Byg en selvstændig Windows-EXE med:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-windows.ps1
```

Buildscriptet skal gennemføre de krævede tests, før det publicerer
`artifacts\TrainerStudio-win-x64\TrainerStudio.exe` og dens SHA-256-fil.
Renderingskontrollen gemmer PNG-billeder af felter og hovedvindue. Manuel
mus-/tastaturtest, høj DPI og rigtige spil kræver fortsat Windows-afprøvning.
