# Windows-målmaskine

De oplyste målforhold er Windows 11 Pro 25H2, build 26200.9711, en AMD Ryzen 7 3800X, 16 GB RAM og NVIDIA GeForce RTX 2060 med 6 GB grafikhukommelse. Lagerpladsen er tilstrækkelig til appen, Python-miljøet og en Windows-build.

OpenAI-udgaven kræver kun almindelig CPU/RAM til brugerfladen og billedbehandling; denne maskine er passende. RTX 2060 er ikke nødvendig ved API-brug. En lokal billedmodel kan afprøves, men 6 GB VRAM og 16 GB system-RAM begrænser modelstørrelsen og kan give langsommere svar eller delvis CPU-kørsel. Start derfor med OpenAI eller en lille kvantiseret lokal billedmodel, og mål hastighed og kvalitet på den faktiske maskine.

Windows-versionen er nyere end cloudmiljøets Linux-platform. Følgende skal derfor stadig kontrolleres på målmaskinen: vinduesoptagelse via HWND, DPI/skærmskalering, flere skærme, Credential Manager, stop under et live API-kald og den pakkede `.exe` med testprogrammet som underproces.

Kør dette i PowerShell fra projektmappen for at indsamle de resterende skærmoplysninger:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\collect-windows-info.ps1
```

Scriptet viser Windows-build, CPU, samlet RAM, GPU/driver, antal skærme, opløsninger og systemets DPI-skalering som JSON. Det indsamler ikke enheds-id, produkt-id, serienumre, brugernavn eller API-nøgler. Del kun outputtet, hvis du vil have hjælp til at kontrollere skærmopsætningen.

Enheds-id og Windows-produkt-id er ikke nødvendige for udvikling eller fejlfinding og bør ikke gemmes i projektet.
