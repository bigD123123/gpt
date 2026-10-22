# Genskabelse af videoens forløb

AI Automation Lab genskaber forbindelsen mellem billedaflæsning, AI-valgte handlinger og en søgning, som indsnævres med før/efter-værdier. Forsøget sker i et separat testprogram, der startes og ejes af appen.

## Det samlede forløb

Start med **Start testprogram**, og brug derefter **Start AI-forløb**. Standardværdien til den afsluttende ændring er 999, cyklusgrænsen er 6, og kaldgrænsen er 12. Cyklusgrænsen kan vælges fra 1 til 12 og kaldgrænsen fra 1 til 30. Brugeren kan lade modellen vælge værditypen automatisk eller låse forløbet til valuta eller ammunition.

Hovedappen binder optagelsen til det vindue, den selv starter. Manuel vindues- eller udsnitsvælger og en konfigurerbar genvej er endnu ikke en del af dette forløb. **P** virker med fokus i hovedvinduet, og **Esc** stopper.

```text
Åbn eget testprogram
    → optag det synlige testområde
    → AI vælger og aflæser valuta eller ammunition
    → søg den registrerede buffer efter værdien
    → AI vælger en tilladt handling, der ændrer værdien
    → testprogrammet udfører handlingen
    → optag og aflæs igen
    → filtrér de eksisterende søgekandidater
    → gentag, indtil én kandidat er tilbage eller grænsen nås
    → afvent brugerens godkendelse af en direkte ændring
    → skriv kun til den godkendte kandidat ved den aktuelle revision
    → kontrollér tilstand, buffer og nyt billede
    → log resultatet og afslut
```

En første søgning kan give mange kandidater, fordi flere celler indeholder samme tal. Efter en handling er det kun kandidater, der følger den nye værdi, som bliver tilbage. En ny søgning skal fortsat arbejde på de hidtidige kandidater; den må ikke starte forfra og præsentere det som en indsnævring.

Hvis aflæsningen ikke passer med testprogrammets faktiske værdi, er der ingen gyldig grund til at udføre næste handling. Hvis der stadig er flere kandidater ved grænsen, skal forløbet afsluttes uden direkte skrivning. En enkelt kandidat er heller ikke alene en tilladelse til at ændre en værdi: revision og godkendelse skal stadig være gyldige.

## Eget testprogram og registreret hukommelse

Testprogrammet indeholder en `ctypes`-buffer med heltalsceller. Bufferens adresser er rigtige adresser i testprocessen. Testprogrammet eksponerer selv dens registrerede celler gennem et dokumenteret interface.

Hovedappen starter testprogrammet som en underproces og bruger JSON-linjer over processens standardinput og standardoutput. Den behøver ikke en netværksserver, administrative rettigheder eller adgang til at åbne en fremmed proces. Ved pakning i en `.exe` startes samme program med argumentet `--test-game`.

Testprogrammet kan oplyse sine værdier, revision, registrerede buffer og synlige billedområde, udføre en tilladt handling og håndtere en godkendt ændring. Modellen får ikke lov til at kalde vilkårlige kommandoer. Protokollen er en afgrænset adapter til dette konkrete testprogram; den er ikke en generel proceshukommelses-API.

## AI'ens rolle

OpenAI-forbindelsen bruger Responses API med et struktureret JSON-format. Standardvalget `gpt-5-nano` svarer til modelnavnet, der er synligt i brugerens skærmbillede fra videoen. Det er ikke en påstand om, at modellen er bedst; modelvalg og kontoens tilgængelighed skal kontrolleres ved live-brug.

En beslutning indeholder:

- `stat_type`: `currency` eller `ammo`.
- `observed_value`: den værdi, modellen læser fra billedet.
- `action`: en tilladt navngiven handling eller `observe`. Afslutning afgøres af controllerens kontrollerede faseforløb.
- `reason`: en kort forklaring.

Ved den første observation vælger modellen en primær værdi. Resten af søgningen er knyttet til den værdi; modellen kan ikke pludselig skifte fra valuta til ammunition og genbruge de samme kandidater. I handlingsfasen vælger den mellem relevante handlinger for den valgte type. Ved en kontrol skal den aflæse igen og ikke udføre en ekstra handling.

Modellen ser skærmbilledet og de tilladte valgmuligheder. Python kontrollerer format, handlingsgrænse, aflæsning, revision og faktisk resultat. At disse grænser håndhæves af programmet ændrer ikke, at en rigtig model selv vælger blandt de tilladte handlinger.

Ollama kan bruges med en lokal billedmodel gennem samme beslutningsgrænse. Den tydeligt mærkede demoudbyder bruger derimod faste regler og kendte testværdier. Demoen dokumenterer forløbet, men er ikke en test af visuel AI.

## Handlinger og godkendelse

| Handling | Formål |
|---|---|
| `collect` | Saml ressourcer i det ejede testprogram. |
| `spend` | Brug ressourcer i det ejede testprogram. |
| `fire` | Affyr et testskud og ændr ammunitionen. |
| `reload` | Genlad i testprogrammet. |

Den direkte værdiændring efter søgningen er en separat godkendt operation via **Godkend ændring**. **Afvis** annullerer den. Modellen kan ikke selv gøre sin egen beslutning til brugerens tilladelse. Godkendelsen gælder kun den konkrete kandidat, foreslåede værdi og aktuelle tilstandsrevision. Ændringer i mellemtiden, en ny kørsel eller stop gør en gammel fortsættelse ugyldig.

**STOP (Esc)** forhindrer nye handlinger og annullerer ventende godkendelser. Et allerede afsendt modelkald kan stadig afsluttes hos udbyderen og medføre betaling; det sene svar må ikke fortsætte en stoppet kørsel.

Et forløb med seks cyklusser betyder ikke kun seks API-kald. Der er en første observation, op til en handlingsanalyse og en kontrolaflæsning pr. cyklus samt en afsluttende billedkontrol. Den særskilte kaldgrænse stopper før næste billede/modelkald. Programmet holder ét kald i gang ad gangen og genforsøger ikke automatisk et mislykket kald.

OpenAI og Ollama kan returnere tokenoptælling. Appen viser og logger input- og outputtokens for kørslen; et sent svar efter STOP tælles stadig, selv om beslutningen kasseres. Tallene er udbydermetadata og omregnes ikke til en pris.

## Ligheder og forskelle fra videoen

| Del | Videoens leverede materiale | Denne udgave |
|---|---|---|
| Billedanalyse | OpenAI `gpt-5-nano` og Responses API er synligt i kode. | Samme API-type og model som udgangspunkt, med et kontrolleret svarformat. |
| Forandring af værdien | AI-foreslåede klik omsættes til AutoHotkey. | Tilladte handlings-id'er udføres gennem eget testprogram. |
| Første søgning og gensøgning | Hukommelsessøgning beskrives og vises i loggen. | Søgning i testprogrammets registrerede buffer med rigtige adresser. |
| Målprogram | Materialet omtaler blandt andet Plants vs. Zombies og Postal 2. | Kun det lokale testprogram, som appen selv starter. |
| Logvindue | Mørkt terminalagtigt vindue. | Mørk log med observationer, kandidater, beslutninger og kontrolresultater. |
| Direkte ændring | Spilværdier ændres ifølge fortællingen. | Én godkendt ændring gennem testprogrammets eget interface. |
| Procespause | Loggen viser pause/genoptagelse af spilprocessen. | Ingen generel pause eller styring af andre processer. |

Der genereres ikke AutoHotkey, Python eller shellkode fra modelsvaret. Der er heller ikke et felt til vilkårlige proces-id'er eller hukommelsesadresser. Disse valg følger brugerens krav om klart definerede handlinger og et kontrolleret miljø.

## Verifikation

Relevante kontroller omfatter entydig indsnævring, flere kandidater, forkert aflæsning, uændret værdi, revisionskonflikt, ukendt adresse, manglende godkendelse og stop under et igangværende AI-kald. GUI-forløbet skal også vise testprogrammet og optage dets faktiske pixels.

Se [teststatus](teststatus.md) for gennemførte tests og de integrationer, der stadig kræver en rigtig model, API-konto eller Windows-maskine. Denne beskrivelse alene er ikke en rapport om, at alle disse kontroller er udført.

## Responsivitet og billedkilde

Kommunikationen med testprogrammet kører i én seriel baggrundskø. Tk-tråden venter ikke på RPC-svar. Stop lukker en lokal afsendelsesgrænse for `perform` og `write` straks og ugyldiggør efterfølgende svar. Et langsomt eller sent `resume` kan ikke ophæve et nyere stop. En allerede afsendt kommando kan ikke trækkes tilbage.

På Windows bruges Pillow-optagelse af testprogrammets eget HWND. På Linux bruger smoketesten det ejede vindues skærmområde efter at have løftet vinduet; hold derfor området frit for andre vinduer, især ved cloudforsøg med OpenAI. Windows-optagelsen er implementeret, men ikke afprøvet her.

Sidste handlingsplan gemmes som `last-plan.json` ved siden af loggen. Den indeholder data og bliver aldrig eksekveret.
