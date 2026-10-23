# Windows-processer, spilmotorer og senere spiltest

Dato: 23. oktober 2026.

Denne vejledning beskriver v0.3's funktioner til rigtige, lokale
Windows-processer. Den skelner mellem kode, automatiserede tests og
afprøvning i et rigtigt spil. Der udvikles ingen nye testspil som erstatning
for brugerens senere spiltest.

**Status:** Windows-adapter, pointerhenvisninger og den nye brugerflade er
implementeret. Det er ikke en påstand om fuldt afprøvet spilkompatibilitet.
Den lokale teststatus og de obligatoriske Windows-kontroller står nedenfor
og i [vejledningen](../README.md). Se også [ændringslisten](../CHANGELOG.md).

## Hvad der skal ske, når et spil er tilgængeligt

1. Start et lokalt spil, som du ejer eller har tilladelse til at undersøge.
2. Opdater proceslisten i Trainer Studio, og vælg spillets faktiske programfil.
   En launcher er ofte en anden proces end selve spillet.
3. Tilknyt først med læseadgang. Find en kendt værdi med **Første søgning**,
   ændr værdien i spillet, og indsnævr med **Næste søgning**.
4. Kontrollér fundet, før du opretter en trainerfunktion. Skriveadgang og den
   konkrete ændring skal aktiveres synligt af brugeren.
5. Gem profilen. Efter genstart skal den tilknyttede proces og de gemte
   henvisninger valideres igen, før funktioner anvendes.

En proces med samme navn er ikke i sig selv bevis på, at det er samme spil,
build eller spilsession. Automatisk genkendelse skal tilbyde den relevante
profil; den må ikke automatisk genaktivere tidligere ændringer.

## Windows-adapterens opgaver

Kernen arbejder med en fælles målgrænseflade. Windows-adapteren står for
procesadgang, arkitektur, moduler og hukommelsesområder. Scanneren og Trainer
Builder behøver derfor ikke have hver deres Unity-, Unreal- og legacy-løsning.

Windows-adapteren har følgende opgaver i den implementerede løsning:

- Åbne et mål med de nødvendige læse- og forespørgselsrettigheder. Microsoft
  dokumenterer `PROCESS_VM_READ` for `ReadProcessMemory` og
  `PROCESS_QUERY_INFORMATION` for `VirtualQueryEx`.
- Holde skriveadgang adskilt. `WriteProcessMemory` kræver
  `PROCESS_VM_WRITE | PROCESS_VM_OPERATION`; `PROCESS_ALL_ACCESS` er ikke
  nødvendigt for disse operationer.
- Undersøge regioner med `VirtualQueryEx` og springe ulæselige, ikke-committede
  og guard-beskyttede sider over. En region kan ændre sig mellem forespørgsel
  og læsning, så fejl eller delvise resultater skal håndteres.
- Læse i afgrænsede blokke med annullering, kandidatgrænse og en synlig
  besked, hvis søgningen kun dækkede en del af den valgte hukommelse.
- Kontrollere procesidentitet og sessionsidentitet. En ny proces kan få et
  tidligere anvendt proces-id; gamle søgeresultater og gendannelsesværdier
  må ikke overføres alene ud fra dette id.
- Opdage proceslukning, stoppe fastholdelser og frigive Windows-handles.
  Afvist adgang skal give en forklaring frem for gentagne skjulte forsøg.
- Kontrollere en skrivning ved efterfølgende læsning. En vellykket
  Windows-skrivning er dog ikke bevis på, at spillet bruger den fundne værdi.

Der indgår ikke DLL-injektion, kernel-driver, ændring af sidebeskyttelse,
debugprivilegier eller omgåelse af beskyttede processer i dette design.

Processen sættes ikke på pause under scanningen. Blokkene kan være læst på
forskellige tidspunkter, så søgningen er ikke et atomisk snapshot af spillet.

Brugerfladen kan vælge **Medtag mapped områder**; standard er fravalgt.
Filteret tilføjer læsbare memory-mapped områder til søgningens mulige regioner,
mens skrivefilter, adresseinterval og læsebudget fortsat gælder.

## 32-bit, 64-bit og gamle spil

Trainer Studio distribueres som en x64-app til Windows 10/11. Målprocessens
arkitektur skal aflæses separat; et ældre x86-spil kan køre på 64-bit Windows.
`IsWow64Process2` rapporterer både mål- og værtarkitektur og findes fra Windows
10 version 1709. Hvis en ældre API anvendes som fallback, skal et ukendt svar
fortsat vises som ukendt og ikke gættes til x64.

Pointerbredde er normalt fire bytes for et x86-mål og otte bytes for et
x64-mål. Den må ikke vælges ud fra, at selve Trainer Studio er 64-bit.
Windows' modulopregning har også arkitekturforskelle: Microsoft dokumenterer
blandt andet `TH32CS_SNAPMODULE32` for en 64-bit-klient, der opregner et
32-bit-måls moduler.

Gamle spil kan bruge 8-/16-/32-bit-heltal, flydende tal, skalerede værdier eller
flere kopier af samme tal. Der findes derfor ingen universel regel om, at
penge altid er `Int32`, eller at liv altid er `Float`. Scanneren skal tilbyde
datatyper og søgefiltre uden at opfinde feltets betydning.

## Henvisninger, der kan genbruges

| Henvisning | Hvad gemmes? | Efter genstart |
|---|---|---|
| Sessionsadresse | Rå adresse og den aktuelle sessionsidentitet. | Skal afvises i en anden session. |
| Modulrelativ adresse | Faktisk modulnavn og en offset inden for modulets område. | Modulet findes igen, og baseadressen opløses på ny. |
| Pointersti | Modulrod, rod-offset, pointerbredde og en begrænset række offsets. | Alle led læses og valideres igen. |

En heapadresse bliver ikke stabil ved at trække hovedmodulets baseadresse fra
den. Kun adresser, der faktisk ligger i modulets kendte område, må gemmes som
direkte modulhenvisninger. Andre fund skal markeres som sessionsadresser,
indtil en gyldig stabil henvisning er fundet.

En pointer-scanner skal have grænser for dybde, offset, arbejde og antal
resultater samt mulighed for annullering. Resultater er kandidater: de skal
genopløses i den aktuelle proces og senere testes efter rigtig genstart.
En læsbar pointersti er ikke automatisk bevis på, at den stadig peger på den
rigtige spiller eller ressource.

Pointeranalysen prioriterer kendte modulområder og læser derefter resten af
det valgte område inden for det samme samlede bytebudget. Det giver mulige
modulrødder plads i en afgrænset søgning; det fordobler hverken læsningen
eller budgettet. Værdisøgninger følger fortsat normal adresserækkefølge.

**Hent stier fra profilen** indlæser gemte pointerhenvisninger til
genvalidering. Efter genstart kan de sammenholdes med et uafhængigt genfund
af den aktuelle værdi, så stierne kan krydstjekkes uden en ny fuld
pointersøgning. Profilens almindelige validering kræver ikke dette ekstra
krydstjek, men heller ikke den beviser feltets betydning i gameplay.

Profilen skal registrere mål, arkitektur, version og build-fingeraftryk.
Versionsafvigelser kræver gennemgang. En brudt eller sessionsfremmed
henvisning skal give en tydelig fejl. Gendannelse skal bruge den værdi, der
blev læst fra samme opløste adresse i samme session; en flyttet pointer må
ikke få en tidligere objekts værdi skrevet ind i et nyt objekt.

v0.3's henvisninger registrerer også rodmodulets SHA-256-fingeraftryk, så en
ændret DLL ikke godkendes alene, fordi hovedprogrammets EXE er uændret.
Profiler gemmes i format 3, og format 2 migreres ved indlæsning. En gammel
v0.2-app kan ikke indlæse den nye format-3-fil.

## Unity: Mono og IL2CPP

Unity kan bygges med forskellige scripting-backends. Unitys officielle
C#-referencekilde viser blandt andet `Mono2x` og `IL2CPP` som forskellige valg.
Mono og IL2CPP har forskellig eksekvering og forskellige repræsentationer af
spilkoden. Det giver ikke i sig selv en stabil adresse til et bestemt felt.

Den almindelige værdisøgning skal derfor arbejde på de læste bytes uanset
backend. Den er ikke en Mono-objektbrowser og udleder ikke automatisk
feltnavne fra C# eller IL2CPP-metadata.

Objekter kan blive oprettet og destrueret ved sceneskift, indlæsning og
respawn. Unitys referencekilde indeholder både et GC-interface og særskilt
håndtering af native objekters levetid. Designet antager ikke, at enhver
Unity-GC flytter alle objekter. Den praktiske regel er, at en observeret
heapadresse ikke er en permanent identitet.

Modulnavne eller filnavne, der ligner en Unity-runtime, kan bruges som et
fingerpeg i brugerfladen. Et sådant fingerpeg skal mærkes **mulig Unity**,
ikke **fuldt understøttet Unity-spil**.

## Unreal Engine

Det samme bytebaserede fundament kan anvendes på en godkendt Unreal-proces.
Spiller-, Actor- og komponentobjekter kan have forskellige levetider. Et
skift af level, pawn eller savegame skal derfor behandles som en anledning
til at genvalidere henvisninger, ikke som garanti for samme adresser.

Appen skal ikke love generel UObject-refleksion, automatiske egenskabsnavne
eller kendte offsets for alle Unreal-versioner. Den konkrete udgivelse,
compilerkonfiguration og spillets egne datastrukturer betyder noget.
Et navn, der ligner en Unreal-programfil, er kun et fingerpeg.

Epic-dokumentationen kunne ikke hentes i dette miljø. Afsnittets valg er
konservative designregler; de er ikke fremlagt som nye målinger eller som
verificeret støtte til en bestemt Unreal-version.

## Liv, penge, ammunition og hastighed

Liv, penge og ammunition kan forbindes til de almindelige skabeloner
**Indstil**, **Tilføj**, **Fasthold** og **Gendan**, når brugeren har
identificeret det rigtige skrivbare felt. Navnet på en trainerfunktion ændrer
ikke, hvad den teknisk kan gøre. At fastholde ét livsfelt kan eksempelvis
stadig lade spillets dødslogik køre.

Hastighed kan understøttes som en slider for en identificeret data-værdi,
eksempelvis en lokal tidsfaktor eller bevægelseshastighed. Der implementeres
ikke en universel speedhack, som ændrer Windows' tidsfunktioner eller
injicerer kode i spillet.

Unitys officielle `Time`-reference indeholder både `timeScale`,
`fixedDeltaTime` og separate realtids-/unscaled-værdier. Det er et konkret
eksempel på, at **spilhastighed** ikke nødvendigvis er ét tal, der påvirker
alle delsystemer ens. Unreal har tilsvarende dokumentationspunkter om
global time dilation, men adresser og faktisk adfærd skal bekræftes i det
konkrete spil. Ingen af motorernes API-navne betyder, at Trainer Studio
automatisk kan finde en tilsvarende værdi udefra.

## Implementering og test skal holdes adskilt

Denne tabel adskiller den implementerede kode fra den afprøvning, der faktisk
er gennemført. Windows-API- og renderingstestene skal bestå i CI før release.
**Afventer spil** betyder ikke, at kodearbejdet skal vente på spilfilerne.

| Område | Implementering | Automatisk kontrol | Rigtige spil |
|---|---|---|---|
| Datatyper og First/Next Scan-filtre | 11 typer, seks filtre, streaming, justering, adresser og læsebudget. | 16 kerne- og 12 scannertests er kørt lokalt med succes. | Afventer Unity-, Unreal- og legacy-spil. |
| Trainer Builder, profiler og kompakt menu | Forbundet til procesmål; opret/redigér/reparér/kopiér/flyt/slet. | Kernetests findes; nye Windows-renderingskontroller er obligatoriske før release. | Afventer afprøvning af faktiske ændringer. |
| Procesliste og eksplicit tilknytning | Læseadgang først, særskilt tilladelse til skrivning. | Otte Windows-API-testgrupper er implementeret og bygger; x64-/x86-kørsel afventer CI. | Afventer launcher- og spilprocesser. |
| Læsning, skrivning og fastholdelse i en proces | Windows-API'er, readback og stop ved proceslukning. | Automatiserede buffertests i egen og separat testproces; Windows-kørsel skal bestå før release. | Afventer gameplay, indlæsning og proceslukning i spil. |
| Sessionsadresser, moduler og pointerstier | Format 3, modulrod, SHA-256-buildkontrol og sessionsgrænser. | 18 referencetests er kørt lokalt med succes. | Afventer faktiske processers ASLR og genstart. |
| Afgrænset pointer-scan og genvalidering | Implementeret med ikke-negative offsets; UI-dybde 1–5 og arbejdsgrænser. | Almindelige byte-array-tests med kendte pointerkæder. | Afventer rigtige heapobjekter og flere genstarter. |
| Genkendelse af gemte profiler | Proces-/profilmatch tilbydes cirka hvert ottende sekund uden automatisk aktivering. | Identitets- og versionsregler testes i kernen; procesdelen kræver Windows. | Afventer konkrete exe-stier og builds. |
| Hastighed som identificeret datafelt | De almindelige værdi-/sliderkontroller kan anvendes. | Datatype, interval og skriveadfærd testes. | Selve feltet og effekten afventer et spil. |

Ved den lokale kontrol den 23. oktober 2026 bestod i alt **46 tests**
(16 kerne + 12 scanner + 18 reference). App, WPF-testprojekt og native
Windows-testsuite byggede med 0 advarsler og 0 fejl. Den selvstændige
x86-fixture kunne publiceres lokalt. Ti Windows-renderingskontroller og de native
Windows-API-kontroller afventer Windows-CI ved denne statusopdatering;
de skal bestå før release.

Automatiserede buffer- og pointertests er almindelige kodetests. De er ikke
selvstændige testspil og må ikke bruges som bevis på bred spilkompatibilitet.
For et rigtigt spil skal vi senere afprøve mindst genstart, levelskift,
save/load, flere aktive funktioner og stop, mens spillet ændrer værdier.

## Kilder og læsebegrænsninger

Microsoft Learn, Unitys manual og Epic Games' dokumentationssider returnerede
HTTP 403 fra dette miljø. Microsofts officielle dokumentationskilder og
Unitys officielle C#-referencekilde på GitHub blev læst med succes. Der er
ikke genbrugt kode fra dem i denne vejledning eller i adapterdesignet.
UnityCsReference har sin egen referencekodelicens og skal ikke behandles som
en fri kodebiblioteksafhængighed.

Læsbare, officielle kilder anvendt her:

- [Microsoft: ReadProcessMemory, dokumentationskilde](https://github.com/MicrosoftDocs/sdk-api/blob/docs/sdk-api-src/content/memoryapi/nf-memoryapi-readprocessmemory.md)
- [Microsoft: WriteProcessMemory, dokumentationskilde](https://github.com/MicrosoftDocs/sdk-api/blob/docs/sdk-api-src/content/memoryapi/nf-memoryapi-writeprocessmemory.md)
- [Microsoft: VirtualQueryEx, dokumentationskilde](https://github.com/MicrosoftDocs/sdk-api/blob/docs/sdk-api-src/content/memoryapi/nf-memoryapi-virtualqueryex.md)
- [Microsoft: IsWow64Process2, dokumentationskilde](https://github.com/MicrosoftDocs/sdk-api/blob/docs/sdk-api-src/content/wow64apiset/nf-wow64apiset-iswow64process2.md)
- [Microsoft: procesrettigheder](https://github.com/MicrosoftDocs/win32/blob/docs/desktop-src/ProcThread/process-security-and-access-rights.md)
- [Microsoft: modul-snapshots og arkitektur](https://github.com/MicrosoftDocs/sdk-api/blob/docs/sdk-api-src/content/tlhelp32/nf-tlhelp32-createtoolhelp32snapshot.md)
- [Microsoft: OpenProcess](https://github.com/MicrosoftDocs/sdk-api/blob/docs/sdk-api-src/content/processthreadsapi/nf-processthreadsapi-openprocess.md)
- [Microsoft: GetProcessTimes](https://github.com/MicrosoftDocs/sdk-api/blob/docs/sdk-api-src/content/processthreadsapi/nf-processthreadsapi-getprocesstimes.md)
- [Unity: scripting-backends i PlayerSettings](https://github.com/Unity-Technologies/UnityCsReference/blob/master/Editor/Mono/PlayerSettings.bindings.cs)
- [Unity: GarbageCollector-reference](https://github.com/Unity-Technologies/UnityCsReference/blob/master/Runtime/Export/Scripting/GarbageCollector.bindings.cs)
- [Unity: objektlevetid](https://github.com/Unity-Technologies/UnityCsReference/blob/master/Runtime/Export/Scripting/UnityEngineObject.bindings.cs)
- [Unity: Time-reference](https://github.com/Unity-Technologies/UnityCsReference/blob/master/Runtime/Export/Time/Time.bindings.cs)

Officielle videre-læsningslinks, hvis indhold ikke kunne hentes her:

- [Unity: Mono](https://docs.unity3d.com/6000.0/Documentation/Manual/Mono.html)
- [Unity: IL2CPP](https://docs.unity3d.com/6000.0/Documentation/Manual/IL2CPP.html)
- [Epic: Unreal Object Handling](https://dev.epicgames.com/documentation/en-us/unreal-engine/unreal-object-handling-in-unreal-engine)
- [Epic: AWorldSettings.TimeDilation](https://dev.epicgames.com/documentation/en-us/unreal-engine/API/Runtime/Engine/GameFramework/AWorldSettings/TimeDilation)
- [Epic: SetGlobalTimeDilation](https://dev.epicgames.com/documentation/en-us/unreal-engine/API/Runtime/Engine/Kismet/UGameplayStatics/SetGlobalTimeDilation)
