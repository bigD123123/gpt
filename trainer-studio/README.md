# Trainer Studio

Trainer Studio er en separat, AI-fri Windows-prototype for den almindelige
arbejdsgang **find værdi → indsnævr → opret funktion → gem profil**. Den er
skrevet helt fra bunden i C#/.NET og WPF; den genbruger ikke kode fra Cheat
Engine, x64dbg, WeMod, ArtMoney eller ReClass.NET.

Den nuværende version arbejder **kun** med sit indbyggede, selvudviklede
testmål. Knappen **Åbn testspil** viser målet i sit eget WPF-vindue, men det
forbliver bevidst i samme proces som Trainer Studio. Den åbner ikke vilkårlige
processer, tager ikke et proces-id eller en adresse som input og indeholder ikke
anti-cheat-omgåelse, kodeinjektion, speedhack, online-funktioner eller AI.

Læs [research og roadmap](RESEARCH.md) for kildegrundlag, licenser,
produktvalg og de funktioner, der bevidst er senere arbejde.

Version **0.2.1** retter kontrasten i lukkede og åbne rullemenuer,
deaktiverede knapper, tabeloverskrifter, resultatrækker og scrollbars.
Versionsnummeret vises i hovedvinduets titellinje.

## Hvad v0.2 kan

- Søge i det ejede testmåls data med `Exact`, `Unknown`, `Increased`,
  `Decreased`, `Changed` og `Unchanged`.
- Arbejde med signed/unsigned heltal, float, double og boolske værdier.
- Oprette deklarative trainerfunktioner som knap, kontakt, slider eller
  talindtastning.
- Udføre `Indstil`, `Tilføj`, `Fasthold` og sessionbaseret `Gendan oprindelig`.
  En ændring kræver en tydelig bekræftelse.
- Gemme og indlæse versionerede JSON-profiler med modulrelative offsets.
- Åbne **Min trainer**: et kompakt vindue med kategorier og direkte knapper,
  kontakter, sliders og talfelter for de funktioner, brugeren selv har oprettet.
- Kopiere, flytte og fjerne funktioner i traineren. Rækkefølgen og kategorierne
  gemmes i profilen.
- Duplikere en profil, eksportere den til en flytbar
  `.trainerprofile.json`-fil og importere den som en ny, selvstændig lokal kopi.
  Importerede profiler og funktioner får nye id'er, så en import aldrig
  overskriver en eksisterende trainer.
- Oprette op til ti automatiske sikkerhedskopier af den forrige version, hver
  gang en eksisterende profil gemmes igen.
- Validere mål-id, arkitektur, version, build-fingeraftryk og læsbarhed, før en
  profil beskrives som klar.
- Skrive lokale auditposter for testhandlinger, scanninger, funktioner,
  profiler og fejl.
- Simulere genstart med en ny modulbase i kernetestene; den gemte
  modulrelative reference bliver opløst på ny.

Det indbyggede testmål accepterer i denne version kun **Int32** som skrivbar
trainerreference. De øvrige datatyper er med i scanmotoren og dens tests, men
en valgt kandidat med en anden type kan ikke gøres til en skrivende funktion i
v0.2.

## Hvad v0.2 ikke kan endnu

- Tilknytte sig eksterne spil eller andre processer.
- Lave pointer scanning, kodepatching, debugging eller strukturinspektion.
- Importere Cheat Engine-tabeller eller eksportere en selvstændig trainer.
- Tilbyde globale hotkeys, drag-and-drop, ikoner eller fuld dansk/engelsk
  sprogskift. En indtastet genvej gemmes som profilmetadata til senere arbejde.
- Bevise integration mod en separat Windows-testproces. Det indbyggede testmål
  er bevidst i proces, så scannerens og profilernes regler kan testes sikkert.

## Krav på Windows

- Windows 10 eller 11, 64-bit.
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), hvis du vil
  bygge fra kildekoden.

Din Ryzen 7 3800X og 16 GB RAM er rigeligt til denne prototype. Den bruger ikke
AI eller GPU.

## Kør fra kildekoden

Åbn PowerShell i mappen `trainer-studio` og kør:

```powershell
dotnet run --project .\tests\TrainerStudio.Core.Tests\TrainerStudio.Core.Tests.csproj -c Release
dotnet run --project .\tests\TrainerStudio.Theme.Tests\TrainerStudio.Theme.Tests.csproj -c Release -- .\artifacts\theme-tests
dotnet run --project .\src\TrainerStudio.App\TrainerStudio.App.csproj -c Release
```

Den første kommando skal slutte med `PASS: 16 Trainer Studio core tests`.
Den anden kontrollerer de faktisk renderede WPF-farver på Windows og gemmer
PNG-billeder i `artifacts\theme-tests`. Den tredje starter WPF-appen.

## Prøv programmet

Følgende forløb finder testmålets valuta uden at gætte:

1. Lad datatype stå på **Int32**, sammenligning på **Præcis værdi** og værdi på
   **50**. Tryk **Første søgning**. Der er med vilje flere kandidater.
   Du kan trykke **Åbn testspil** for at udføre handlingerne i et separat
   testvindue i stedet for i venstre panel.
2. Tryk **Saml +25** i venstre side.
3. Skriv **75** og tryk **Næste søgning**.
4. Tryk **Saml +25** igen, skriv **100**, og tryk **Næste søgning**. Nu er der
   én kandidat.
5. I Trainer Builder vælger du fx **Knap**, **Indstil værdi**, giver funktionen
   et navn og skriver `999`. Tryk **Opret funktion fra eneste kandidat**.
6. Tryk **Åbn min trainer**, find funktionen i dens kategori, tryk **Udfør** og
   godkend dialogen. Valutaen bliver synligt opdateret. Den gamle boks
   **Udfør valgt** virker fortsat, hvis du hellere vil teste fra editoren.
7. Tryk **Gem profil**. Luk og åbn appen igen, vælg profilen under **Gemte
   profiler**, tryk **Indlæs valgt** og derefter **Valider profil**.

For at prøve en kontakt vælger du efter samme søgeforløb **Kontakt**. Den bliver
automatisk en **Fasthold værdi**-funktion. Brug `100` som værdi, aktivér den,
ændr valutaen med **Saml +25**, og se den periodiske fastholdelse gendanne den
valgte værdi. Slå kontakten fra for at gendanne den værdi, der gjaldt, da
kontakten blev aktiveret.

En slider kræver minimum, maksimum og trin. En talindtastning lader dig vælge
den konkrete værdi, når funktionen anvendes. Gendan-knappen virker kun i samme
app-session, fordi en tidligere sessions “oprindelige værdi” ikke kan antages at
være sikker efter genstart.

Brug **Kopiér valgt**, **Flyt op**, **Flyt ned** og **Slet valgt** i editoren
til at organisere menuen. Hvis en aktiv kontakt slettes, stopper programmet
først fastholdelsen og gendanner sessionsværdien. Brug **Duplikér profil**, når
du vil eksperimentere uden at ændre originalen. **Eksportér profil** laver en
flytbar datafil; **Importér profil** opretter altid en ny lokal kopi og aktiverer
ikke funktioner automatisk.

**STOP ALLE (Esc)** stopper straks alle aktive fastholdelser uden at skrive nye
værdier eller nulstille testspillet. Brug **Nulstil testspil** kun når du også
vil starte data og søgning forfra.

## Lokale data og log

Windows gemmer data her:

```text
%LOCALAPPDATA%\TrainerStudio\Profiles\*.trainerprofile.json
%LOCALAPPDATA%\TrainerStudio\Profiles\Backups\<profil-id>\*.trainerprofile.json
%LOCALAPPDATA%\TrainerStudio\events.jsonl
```

Profiler bruger schema-version 2. `events.jsonl` er en tekstfil med én
JSON-hændelse pr. linje og indeholder kun synlige testmålshændelser, funktion-id,
før/efter-værdier og fejlbeskeder. Den indeholder ingen API-nøgler, scripts eller
oplysninger fra andre processer.

## Byg en selvstændig Windows .exe

Kør i PowerShell fra `trainer-studio`:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-windows.ps1
```

Scriptet kører kerne- og Windows-tematestene og publicerer en selvstændig x64-udgave i
`artifacts\TrainerStudio-win-x64\TrainerStudio.exe` sammen med en
SHA-256-fil. Modtageren behøver ikke .NET SDK for at køre den publicerede
udgave.

Du kan også gøre det manuelt:

```powershell
dotnet publish .\src\TrainerStudio.App\TrainerStudio.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\artifacts\TrainerStudio-win-x64
```

Den publicerede EXE kan kompileres fra dette Linux-bygmiljø, men den kan ikke
startes eller visuelt afprøves her. Kør derfor mindst kernetesten og det korte
forløb ovenfor på Windows, før den deles.

## Udviklingskontroller kørt her

```text
dotnet run --project tests/TrainerStudio.Core.Tests/TrainerStudio.Core.Tests.csproj -c Release
dotnet build src/TrainerStudio.App/TrainerStudio.App.csproj -c Release -p:EnableWindowsTargeting=true
```

Begge var grønne den 22. oktober 2026: 16 kernetests bestod, og WPF-projektet
byggede med 0 advarsler. WPF-vinduet kræver Windows for en faktisk GUI-kørsel.
