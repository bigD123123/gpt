# Research og produktretning: Trainer Studio

Dato: 22. oktober 2026
Status: første fungerende version med v0.2-forbedringer

## Formål og afgrænsning

Trainer Studio er tænkt som en moderne, lokalt kørende Windows-app til
hukommelsesundersøgelse, profiler og en no-code Trainer Builder. Første version
arbejder udelukkende med et selvudviklet og dokumenteret testmål. Den er
udgangspunktet for senere støtte til programmer, som brugeren ejer eller
udtrykkeligt har tilladelse til at undersøge.

Projektet omfatter ikke online-spil, serverstyrede værdier, anti-cheat,
omgåelse af sikkerhedssoftware, skjulte funktioner, kodeinjektion, kernel-drivere
eller automatisk ændring af ukendte processer. Alle ændringer skal være synlige,
bekræftede af brugeren og kunne logges. Der er ingen AI-funktion i produktet.

Målet er ikke at kopiere et eksisterende program. Målet er at gøre den mest
almindelige, lovlige arbejdsgang nemmere at forstå:

```text
Vælg et godkendt mål
→ observer en værdi
→ første søgning
→ ændr værdien i målet
→ indsnævr resultater
→ test fundet
→ opret en trainerfunktion
→ gem, valider og genbrug profil
```

## Metode og kildebegrænsning

Researchen er foretaget mod officielle projektsider, åbne GitHub-repositorier og
Cheat Engines indbyggede tutorial-kilde. Det er stærkt kildegrundlag for
funktioner, licenser og den dokumenterede arbejdsgang.

Cloudmiljøets netværksproxy returnerede HTTP 403 for DuckDuckGo, Reddit, Cheat
Engine-forummet, WeMods supportsite samt de officielle WeMod- og ArtMoney-sider.
Derfor bruges der **ikke** påståede Reddit- eller forumfund i denne rapport.
Udsagn om WeMod og ArtMoney nedenfor er mærket som produktinspiration, når de
ikke kunne bekræftes direkte. De bør efterprøves i en almindelig browser, før de
bruges som markedstal eller som dokumentation for en konkret aktuel funktion.

## Hvad de undersøgte værktøjer viser

| Værktøj | Dokumenterede styrker | Hvad det bruges som reference for | Konsekvens for Trainer Studio |
|---|---|---|---|
| Cheat Engine | Værdisøgning, kandidatfiltre, pointerværktøjer, adresse-/fryselister, trainergenerator, debugger/disassembler, scripts og flere avancerede moduler. | Fleksibilitet og den klassiske søgeproces. | Byg de hyppige scanninger og en klar trainerarbejdsgang selv; udsæt systemnære og scriptbaserede dele. |
| Cheat Engine Trainer Generator | Hotkeys, freeze-interval, formdesign samt `.CETRAINER` og standalone `.EXE` findes i kildetræet. | At brugere forventer en genbrugelig menu efter et fund. | Giv no-code builderen navn, kontroltype, handling, tastaturgenvej, kategori og test før gemning. |
| WeMod | Direkte verificering var blokeret i dette miljø. Produktet er kun brugt som overordnet UI-reference. | Spilbibliotek, kompakt trainerflade og synlige hotkeys. | Brug idéen om profiler og en enkel funktionsliste, men genbrug ingen kode eller produktdata. |
| ArtMoney | Direkte verificering var blokeret i dette miljø. | Den traditionelle arbejdsgang “find og ændr værdi”. | Gør samme grundopgave guidet, med tydelige forklaringer og historik. |
| x64dbg | Windows-debugger med x86/x64-fokus og plug-ins; projektet er GPLv3. | Senere avanceret analyse og debugger-UX. | Er ikke fundament for begynderproduktet og må ikke kodegenbruges uden GPL-konsekvens. |
| ReClass.NET | x86/x64, struktur- og pointervisning, hukommelsesvisning/scanner, symbolsupport, debugger og plug-ins; MIT-licens. | Senere struktur- og pointervisualisering. | Kan studeres og eventuelt bruges som licenskompatibel inspiration. Første version implementeres selv. |

### Cheat Engine

Cheat Engine beskriver sig selv som et “development environment focused on
modding games and applications for personal use”. Kildetræet viser, at det er et
meget stort produkt: almindelig scanmotor, pointer scanner, gemte scans,
trainergenerator, debugger/disassembler, Lua/scripts, plug-ins, speedhack og
andre systemnære komponenter.

De mest relevante dokumenterede scanmuligheder er exact value, unknown initial
value, increased, decreased, changed og unchanged over flere heltals- og
decimaltyper. Trainergeneratorens kildetræ viser hotkeys, freeze-interval,
formdesign og eksportformater.

Cheat Engines egne tutorials er særligt nyttige for produktdesignet:

- Unit 1 siger direkte, at begyndere ikke behøver forstå alle knapper i starten.
  Procesvalg og den store værktøjsflade er derfor en reel indlæringsbarriere.
- Unit 2 kræver korrekt datatype, første søgning, en ændring i målprogrammet,
  flere næste søgninger og til sidst en manuel adresseændring.
- Unit 3 siger direkte, at mange sidder fast ved “New Scan”, når den oprindelige
  værdi er ukendt. Det understøtter en guidet “hvad skete der?”-arbejdsgang.
- Unit 7 og 9 beskriver pointere og multi-level-pointers som vanskelige. Det
  giver et klart argument for profilstatus og guidet reparation efter genstart.

Det dokumenterer ikke, at Cheat Engine er dårligt. Det dokumenterer, at et
kraftfuldt ekspertværktøj kræver, at brugeren selv forstår mange frakoblede trin.
Trainer Studio skal bevare præcisionen, men forklare sammenhængen mellem dem.

### Cheat Engines licens og genbrug

Repositoryets synlige rod i den undersøgte revision havde ikke en klart navngivet
topniveau-licensfil. Kode, formularer, ikoner og tekst fra Cheat Engine må derfor
ikke kopieres ind i dette projekt uden en særskilt licensafklaring. Vi kan lære af
adfærd, offentlige formater og dokumentation og derefter skrive en selvstændig
implementering.

Særligt `speedhack2.pas` viser mekanismer, der bygger på DLL-injektion. Det er
uden for denne produktlinje og skal ikke indgå i planen.

### WeMod og ArtMoney

WeMod og ArtMoney er relevante som produktreference, ikke som kodekilde.
WeMods inspirationsværdi er et bibliotek af profiler, en kompakt visning af
spilfunktioner og brug af hotkeys. ArtMoney er relevant som reference til den
velkendte manuelle værdisøgning. Da siderne ikke kunne hentes herfra, indeholder
rapporten ikke påstande om aktuelle abonnementer, præcise funktioner, formater
eller brugerfeedback.

Begge er proprietære produkter. Ingen kode, aktiver eller interne profilformater
skal antages genbrugelige.

### x64dbg

x64dbg er et åbent Windows-debuggerprojekt med x86/x64-fokus og en avanceret
arbejdsgang. Det er nyttigt, hvis en senere *avanceret tilstand* skal gøre
struktur- eller debuggerinformation lettere at forstå. Projektets GPLv3-licens
betyder, at direkte kodegenbrug medfører GPL-forpligtelser for et afledt værk.
Det er derfor udelukkende en designreference i denne plan.

### ReClass.NET

ReClass.NET dokumenterer blandt andet strukturer, noder med flere datatyper,
pointer preview, memory viewer/scanner, ændringsmarkering, symboler, debugger og
plug-ins. Det er interessant for en senere visualisering af stabile pointerstier.
MIT-licensen er lettere at arbejde med end GPLv3, men den første version holder
alligevel sin egen, lille implementering. Det reducerer afhængigheder og gør den
tidlige testbarhed bedre.

## Dokumenterede behov og produktforslag

Det følgende skelner bevidst mellem dokumentation og designbeslutninger.

### Dokumenteret i officielle kilder

1. Begyndere får at vide, at de ikke behøver forstå alle knapper med det samme.
2. Exact-value-søgning kræver flere trin og korrekt datatype.
3. Unknown-initial-value-søgning kan få brugere til at sidde fast allerede ved
   den første søgning.
4. Pointere og især multi-level-pointers er vanskelige.
5. Cheat Engine har allerede trainergenerator, hotkeys, freeze og eksport, men
   avanceret tilpasning fører videre til Lua/formdesign.
6. Cheat Engine dækker langt mere end en enkel scanner. Fuld funktionslighed er
   et flerårigt produktprogram, ikke en realistisk første leverance.

### Forslag baseret på ovenstående

Følgende er vores egne produktforslag. De er ikke fremlagt som fund fra Reddit,
fora eller WeMod-brugere.

| Forslag | Problem | Hvordan det virker | Nytte | Sværhed | Lignende funktion |
|---|---|---|---|---|---|
| Guidet scan-wizard | Rækkefølgen af trin er uklar. | Appen spørger, om værdien er kendt, og derefter om den steg, faldt, ændrede sig eller var uændret. | Meget høj | Lav | CE-tutorial, men integreret i UI. |
| Forklaring ved hvert filter | Tekniske ord skjuler betydningen. | Kort hjælpetekst som “vis kun værdier, der faldt siden sidste søgning”. | Meget høj | Lav | Delvist i tutorials. |
| Scan-tidslinje og fortryd | Tidligere beslutninger forsvinder hurtigt. | Gem hvert snapshot/filter med kandidatantal; brugeren kan gå tilbage. | Høj | Mellem | Gemte scans findes i CE, men ikke som begynderflow. |
| Kandidattæller og fremdriftsgraf | Mange resultater er svære at tolke. | Vis antal, udvikling pr. scan og typiske næste trin. | Høj | Mellem | Scannerresultater findes bredt; grafen er produktforslag. |
| Datatypehjælper | Forkert bytebredde eller float stopper søgningen. | Forklar type, eksempel og foreslå sidste vellykkede type. | Høj | Mellem | Typevalg findes i CE/ArtMoney-lignende værktøjer. |
| Adressekort med oprindelse | Et fund mangler forklaring og tillid. | Gem hvilken scanserie, observerede værdi og reference der skabte fundet. | Høj | Mellem | Watch lists findes bredt; proveniens er forbedring. |
| No-code Trainer Builder | En adresse er ikke en brugbar menu. | Opret deklarativ knap, toggle, slider eller talfelt fra et valgt fund. | Meget høj | Mellem | CE Trainer Generator. |
| Test før gemning | Forkerte fund bliver hurtigt permanente. | “Prøv funktion” med før/efter-visning og eksplicit godkendelse. | Høj | Mellem | Produktforslag. |
| Ændringslog og sikker gendannelse | Brugeren mister overblik over skriverier. | Auditlog med før/efter/tid og sessionbaseret gendan. | Høj | Mellem | Freeze/address lists findes i CE. |
| Profil-sundhedstjek | Rå adresser fejler efter genstart eller opdatering. | Grøn/gul/rød status baseret på målidentitet, version og validering. | Meget høj | Mellem | Pointer scan er teknisk forløber; UX er ny. |
| Guidet pointer-reparation | Pointerstier er vanskelige at forstå. | Vis brudt led, observerede krav og en afgrænset reparationsguide. | Høj | Høj | Pointer scanner findes i CE. |
| Tilgængelig mørk UI og høj-DPI | Ældre desktopmønstre er svære at læse. | Skalerbar layout, kontrast, tastaturnavigation og dansk/engelsk. | Høj | Mellem | Moderne Windows-app-praksis. |
| Testlab med øvelser | Tutorials ligger ofte ved siden af produktet. | Det indbyggede mål indeholder dokumenterede øvelser og distraktørværdier. | Høj | Mellem | CE Tutorial, men direkte koblet til appen. |
| Profil-sammenligning | Spilopdateringer gør ændringer uklare. | Sammenlign gemt fingeraftryk/reference med kendt god profil. | Mellem | Høj | Produktforslag. |
| Lokale backup-/eksportpakker | Profiler kan mistes eller deles ukontrolleret. | Versioneret JSON plus eksplicit importforhåndsvisning. | Mellem | Lav | CE-tabeller er beslægtet, men formatet er eget. |

De første ti rækker opfylder kravet om mindst ti konkrete forbedringer. Prioritet
bestemmes af nytte og ikke af hvor mange knapper, der kan tilføjes.

## Teknisk sammenligning og valg

| Teknologi | Styrker | Begrænsning for dette projekt | Vurdering |
|---|---|---|---|
| C# + WPF | Moden Windows-desktop, data binding, drag-and-drop, gode tabeller/paneler, let Win32-integration senere og enkel distribution. | Mindre “ny” visuelt end WinUI uden ekstra tema-arbejde. | Valgt til v1. |
| C# + WinUI 3 | Moderne Fluent-UI og Microsofts Windows App SDK. | Højere leveringsrisiko for kompleks docking, integration og projektopsætning i første version. | Mulig senere frontend. |
| C++ + Qt | Høj ydeevne og tværplatforms-UI. | Mere kompleks hukommelsessikkerhed, builds og Windows-distribution; ingen reel gevinst i den første testmål-version. | Ikke valgt nu. |
| Rust + egui/Qt | Stærk type-/hukommelsessikkerhed. | Større friktion i Windows-UI og Win32-økosystemet for den ønskede produktform. | Interessant for senere scanmotor, ikke v1. |

Valget er C#/.NET 8 med WPF i første version. .NET 8 er allerede brugt i det
aktuelle buildmiljø. Kernens logik er UI-uafhængig, så en senere WinUI 3-frontend
kan indføres uden at omskrive scanning, profilformat eller Trainer Builder.

GPU er ikke et krav. Den oplyste Ryzen 7 3800X, 16 GB RAM og RTX 2060 er mere end
tilstrækkelig til den planlagte WPF-app og lokale testarbejdsgang.

## Foreslået arkitektur

```text
TrainerStudio.App             WPF: begyndertilstand, senere avanceret tilstand
             │
TrainerStudio.Core            scanning, datatyper, profiler, trænerhandlinger
             │
IApprovedMemoryTarget         lille, kontrolleret grænseflade til et godkendt mål
             │
OwnedTestTarget               selvudviklet testbuffer i v0.2

Senere, separate moduler:
TrainerStudio.Targets         autoriseret Windows-adapter
TrainerStudio.Storage         SQLite-bibliotek/historik og JSON-import/eksport
TrainerStudio.TestTarget      selvstændigt testprogram med kontrolleret IPC
TrainerStudio.Tests           enheds-, genstarts- og integrationsprøver
```

Kernen må ikke afhænge af WPF, vinduer, vilkårlige proces-id'er eller direkte
Win32-kald. Den modtager snapshots og et lille sæt læse-/skriveoperationer fra
en `IApprovedMemoryTarget`. Det gør scanner- og profilregler testbare og
forhindrer, at UI'et ved en fejl får fri adgang til computeren.

### Første scanmotor

Første version skal dække de almindelige datatyper og regler:

- `Int8`, `UInt8`, `Int16`, `UInt16`, `Int32`, `UInt32`, `Int64`, `UInt64`,
  `Float`, `Double` og `Boolean`.
- Første søgning med præcis værdi eller ukendt startværdi.
- Næste søgning med præcis, steget, faldet, ændret eller uændret.
- Kandidater er snapshots, ikke udokumenterede automatiske rettelser.
- Kandidatloft, tidstagning og fejlbeskeder er synlige for brugeren.

Ydelsesarbejde kommer efter korrekthed: chunk-baseret læsning, kontrolleret
parallelisering, annullering og UI-fremdrift. Ingen optimering må ændre betydningen
af en scanregel.

### Trainer Builder

Et fund skal blive en deklarativ funktion, ikke et vilkårligt script. En funktion
indeholder mindst:

- navn, beskrivelse, kategori, ikon senere og valgfri hotkey;
- kontroltype: knap, kontakt, slider eller talindtastning;
- handling: sæt værdi, tilføj/træk fra, fasthold værdi eller gendan original;
- datatype, valideret værdi samt stabil-reference-status;
- testresultat, aktiv tilstand og auditposter.

Forskellen mellem handlingerne skal stå klart i UI:

| Handling | Betydning | Begrænsning |
|---|---|---|
| Sæt værdi | Skriver én eksplicit værdi efter godkendelse. | Spillet kan straks ændre den igen. |
| Tilføj/træk fra | Læser værdi og skriver en beregnet ny værdi efter godkendelse. | Kræver passende datatype og gyldig reference. |
| Fasthold | Kontrollerer periodisk og gendanner ønsket værdi, så længe brugeren har aktiveret funktionen. | Er ikke det samme som at ændre spillets logik. |
| Gendan original | Gendanner værdien, som appen selv huskede i den aktuelle session. | Må aldrig love at kende “originalen” efter genstart. |

En overskrift som “Uendelige liv” skal behandles ærligt: et enkelt observeret
livsfelt kan være midlertidigt, have flere kopier eller overskrives af programmets
egen logik. V1 kan tilbyde “Fasthold observeret livsværdi”; den må ikke love en
universel funktion baseret på et enkelt fund.

### Profiler og stabilitet efter genstart

En gemt rå adresse er ikke stabil nok. Referenceformatet skal angive styrke og
forventninger:

1. **Sessionadresse** — gælder kun til målprogrammets lukning.
2. **Modul + relativ offset** — kræver modulnavn og et versions-/fingeraftryk.
3. **Valideret pointersti** — rod, offsets, pointerbredde, datatype og forventet
   kontrolværdi/-interval.
4. **Reparationsopskrift** — beskriver hvordan brugeren kan genfinde feltet,
   hvis validering mislykkes.

Ved profilindlæsning skal appen tjekke mål-id, arkitektur, version/fingeraftryk,
referenceopløsning, datatype og kontrolværdi. Status skal være tydelig:

| Status | Betydning | Handling |
|---|---|---|
| Grøn | Mål og reference validerer. | Funktionen kan tilbydes efter brugerens godkendelse. |
| Gul | Reference ser gyldig ud, men version/fingeraftryk afviger. | Bed om test før aktivering og forklar afvigelsen. |
| Rød | Reference eller kontrolværdi er ugyldig. | Deaktivér funktionen og start reparationsflowet. |

Profiler gemmes lokalt som versionerede JSON-filer i v1. SQLite er passende, når
profilbibliotek, søgehistorik og sammenligninger bliver større. Import og eksport
skal have en forhåndsvisning og aldrig overskrive en profil uden brugerens valg.

## Første fungerende version: hvad der er med

Den aktuelle v0.2-kerne og WPF-skal leverer følgende mod det indbyggede testmål:

- Et lokalt, selvudviklet testmål med dokumenterede værdier og distraktører,
  som kan åbnes i et separat WPF-testvindue. Det deler bevidst proces med
  hovedappen i v0.2.
- Exact/unknown/increased/decreased/changed/unchanged-søgning over de beskrevne
  primitive datatyper.
- En mørk dansk begynderflade med testhandlinger, søgefelter og resultattabel.
- Oprettelse af funktionsdata fra et valgt søgeresultat.
- Knap, kontakt, slider og talindtastning som virkende, gemte kontroltyper i
  WPF-skallen. Slideren validerer minimum, maksimum og trin; kontakt er
  afgrænset til fastholdelse og knappen kan gendanne en sessionsværdi.
- Et kompakt **Min trainer**-panel med funktionskort grupperet efter kategori.
  Panelet viser de rigtige kontroller direkte og genbruger den samme
  godkendelse, auditlog og stopadfærd som editoren.
- Kopiering, flytning og sletning af funktioner i den aktuelle trainerprofil.
  Sletning af en aktiv fastholdelse stopper og gendanner først værdien.
- Sæt, tilføj, fasthold og sessionbaseret gendan med eksplicit bekræftelse ved
  aktivering/deaktivering.
- Lokal, atomisk JSON-gemning og indlæsning af profiler, modulrelative offsets
  og validering af målidentitet, arkitektur, version og build-fingeraftryk.
- Profilbibliotek med duplikering, eksport, kontrolleret import som ny kopi og
  op til ti automatiske backups af tidligere gemte profilversioner. Importen
  accepterer kun valideret JSON på højst 2 MiB og aktiverer aldrig en funktion.
- Lokal JSONL-auditlog af testhandlinger, søgeresultater, trainerhandlinger,
  profilhændelser, korrigerende fastholdelseswrites og synlige fejl.
- En "hold øverst"-indstilling for det kompakte testvindue.
- En synlig "Stop alle"-handling og `Esc`, der stopper aktive fastholdelser
  uden at skrive en ny værdi eller nulstille målet.
- Automatiske kernetests for scansekvenser, datatyper, profilrundtur, freeze,
  versionsafvigelse, afvist deaktivering, auditlog, modulrelativ genstart og
  gentagne ammunition-scenarier.

Følgende er **bevidst ikke** klar i v0.2 og må ikke beskrives som færdige:

- Tilknytning til vilkårlige eller eksterne processer.
- Separat testprogram via IPC og en genstartstest mod et rigtigt Windows-vindue.
  Kernen simulerer allerede en ny modulbase ved genstart, men det er ikke samme
  som en test af en separat proces.
- Moduloffsets mod eksterne Windows-processer, pointer scanning og guided
  pointer repair. Den indbyggede testmodel bruger allerede modulrelative
  offsets, men har ikke en Windows-procesadapter.
- Drag-and-drop, ikoner, temasystem, fuld dansk/engelsk-ressourcepakke,
  globale hotkeys og en selvstændig eksporteret trainer.
- Grafer, scanhistorik i UI, CE-tabelimport, selvstændig trainer-eksport,
  plug-ins, debugger eller strukturinspektion.

Denne tydelige grænse er vigtig: testmodellen beviser scanner- og builderlogik,
men den er ikke et løfte om bred procesunderstøttelse.

## Prioriteret plan

### Fase 1 — Research og sikker produktgrænse

- Afslut denne rapport og hold kildehenvisninger opdaterede.
- Skriv et enkelt regler-dokument: kun ejede/autoriserede lokale mål, ingen
  online- eller sikkerhedsomgåelse.
- Fastlæg v1's testmål og accepttests.

### Fase 2 — Grundarkitektur og begynderoplevelse

- Behold C#/.NET/WPF-skal og UI-uafhængig kerne.
- Indfør dansk/engelsk-ressourcer, tilgængelig kontrast og høj-DPI-afprøvning.
- Tilføj scanwizard, korte forklaringer og en synlig arbejdsstatus.

### Fase 3 — Scanner, historik og testmål

- Tilføj annullering, søgehistorik, scan-tidslinje og kandidatgraf.
- Flyt testmålet til et separat selvudviklet Windows-program med dokumenteret
  kommunikation og genstartsscenarier.
- Afprøv 32/64-bit-identitet uden at gætte pointerbredde.

### Fase 4 — Trainer Builder og personligt panel

- Udvid det eksisterende kompakte trainerpanel med hotkeys, ikonmetadata og
  drag-and-drop-rækkefølge. Kategorier, kopiering og knapbaseret rækkefølge er
  allerede på plads.
- Tilføj “prøv før gem” som en særskilt workflow-knap og udvid den eksisterende
  auditlog med filtrering/eksport.
- Gør "hold øverst" og tilpassede temaer tilgængelige i det kompakte
  trainerpanel.

### Fase 5 — Profiler og genstart

- Udvid det lokale profilbibliotek med målgenkendelse og flere profiler pr.
  program. Duplikering, eksport/import og backup er allerede implementeret.
- Udvid den eksisterende validering af version/fingeraftryk og modulrelative
  referencer med grøn/gul/rød-status direkte i bibliotekslisten.
- Test hvert profilforløb efter rigtig genstart, ikke kun én session.

### Fase 6 — Avanceret, men stadig afgrænset analyse

- Tilføj pointersti-visualisering og guidet reparation til godkendte testmål.
- Tilføj realtidsmonitorering, grafer, profiler-sammenligning og fejllog.
- Vurdér først derefter strukturvisning og et godkendt udvidelsessystem.

### Fase 7 — Kvalitet og distribution

- Kør enheds-, integrations-, genstarts-, høj-DPI- og tilgængelighedstests.
- Byg en Windows x64-udgave og dokumentér, hvad der kun er compilet, men ikke
  kørt i et ikke-Windows-buildmiljø.
- Lav installationsvejledning, changelog og brugerrettet hjælp.

## Acceptkriterier for næste milepæl

Før måltypen udvides, skal v0.2 kunne bevise:

1. At et selvudviklet mål kan vælges og identificeres korrekt.
2. At exact/unknown og alle næste filtre indsnævrer kandidater korrekt.
3. At en brugerbekræftet ændring er synlig og logget.
4. At et fund kan blive til en deklarativ trainerfunktion.
5. At en profil gemmes og indlæses uden tab af funktionens metadata.
6. At ugyldig identitet eller version giver en tydelig advarsel.
7. At fasthold og gendan opfører sig korrekt i én aktiv session.
8. At testserier gentages uden uventede fejl.

## Kilder

- [Cheat Engine – repository og README](https://github.com/cheat-engine/cheat-engine)
- [Cheat Engine README, rå tekst](https://raw.githubusercontent.com/cheat-engine/cheat-engine/master/README.md)
- [Cheat Engine memory scanner](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/memscan.pas)
- [Cheat Engine Tutorial Unit 1](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/Tutorial/Unit1.pas)
- [Cheat Engine Tutorial Unit 2](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/Tutorial/Unit2.pas)
- [Cheat Engine Tutorial Unit 3](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/Tutorial/Unit3.pas)
- [Cheat Engine Tutorial Unit 7](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/Tutorial/Unit7.pas)
- [Cheat Engine Tutorial Unit 9](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/Tutorial/Unit9.pas)
- [Cheat Engine Trainer Generator](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/trainergenerator.pas)
- [Cheat Engine EXE Trainer Generator](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/frmExeTrainerGeneratorUnit.pas)
- [Cheat Engine Pointer Scanner](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/pointerscannerfrm.pas)
- [ReClass.NET](https://github.com/ReClassNET/ReClass.NET)
- [ReClass.NET MIT-licens](https://github.com/ReClassNET/ReClass.NET/blob/master/LICENSE)
- [x64dbg](https://github.com/x64dbg/x64dbg)
- [x64dbg GPLv3-licens](https://github.com/x64dbg/x64dbg/blob/development/LICENSE)
- [Microsoft: ReadProcessMemory](https://learn.microsoft.com/windows/win32/api/memoryapi/nf-memoryapi-readprocessmemory)
- [Microsoft: VirtualQueryEx](https://learn.microsoft.com/windows/win32/api/memoryapi/nf-memoryapi-virtualqueryex)
- [WeMod](https://www.wemod.com/) — ikke tilgængelig fra dette cloudmiljø under researchen.
- [ArtMoney](https://www.artmoney.ru/) — ikke tilgængelig fra dette cloudmiljø under researchen.
