# Teststatus – AI Automation Lab

Valideret i dette Linux-cloudmiljø den 22. oktober 2026 med Python 3.12.14, Pillow 11.3.0 og keyring 25.6.0. Direkte Windows-kørsel og live AI er særskilte uafprøvede integrationer.

## Udført og bestået

**76 automatiske tests består** med:

```bash
.venv/bin/python -m unittest discover -s tests -q
```

Testene omfatter den første tællerversion og den nye apps:

- Billedbeslutninger, JSON-format, fasekrav, låst valg af valuta/ammunition og afvisning af ukendte handlinger.
- OpenAI Responses-payload med billede, schema og model; Ollama-payload. Modelsvar er simulerede, ikke live-kald.
- Provider-oplyste OpenAI- og Ollama-tokentællere, normalisering af ugyldige tællere og uændret stramt model-schema.
- Rigtig registreret ctypes-buffer, adressevalidering, datagrænser og kandidatfiltrering for både valuta og ammunition.
- Komplet forløb fra første observation gennem indsnævring og godkendt ændring til slutkontrol mod den valgte udbyders observation.
- Ti komplette kerneforløb i træk samt låsning til valgt valuta/ammunition og tidlig kontrol af ammunitionens maksimale mål.
- Forkert aflæsning, menneskelig ændring under forløbet, skiftet session, forældet godkendelse og forsøg på at genbruge en tilladelse.
- Atomisk revisionskontrol ved handlinger, stop, maksimal cyklusgrænse og manglende godkendelse.
- Lokal afsendelsesgrænse efter stop, stop under et langsomt RPC-svar og et sent resume-svar, som ikke må ophæve stoppet.
- Rigtig lokal HTTP-transport til vores testserver, afvisning af redirects og for store svar. Ingen eksterne modelkald i disse tests.
- Kildekørsel af den pakkede smoke-entrypoint med kontrol af starttilstand, buffer og sikker lukning.

`pip check` er også kørt uden brudte afhængigheder. Cloudinstallationen er genkørt, så den gemte installationskommando er afprøvet efter udvidelsen.

## Rigtig GUI og separat testproces

`scripts/cloud-gui-smoke.sh` starter en midlertidig Xorg/dummy-skærm og kører både den klassiske GUI-smoketest og `tests/replica_gui_smoke.py`. Den stopper sin egen skærm igen. Den nye test bruger den faktiske Tk-app, en separat testproces, JSONL-kommunikation og rigtige skærmoptagelser.

Følgende forløb er gennemført:

1. Start eget testprogram og optag dets viste pixels; billedet er ikke ensfarvet og indeholder den renderede brugerflade.
2. Kør demo fra valuta 50 til 75 til 100: **3 → 2 → 1** kandidater.
3. Bekræft, at værdien forbliver 100, mens godkendelse afventes, og at der ikke foretages nye modeltrin.
4. Godkend én ændring til **999**. Læs den faktiske værdi og buffer tilbage. Optag et nyt slutbillede.
5. Afslut efter seks demoaflæsninger/beslutninger. **Demo bruger kendte testdata; dette er ikke verifikation af AI-billedgenkendelse.**
6. Ignorér et sent svar efter stop. Afvis en ventende skrivning, og bekræft uændret værdi.
7. Foretag en manuel ændring under godkendelsesventetiden; den gamle godkendelse bliver afvist.
8. Simulér OpenAI-timeout; forløbet stopper uden fallback til demo og uden ændring af testværdien.
9. Hold et RPC-svar tilbage med en testbarriere. GUI-stopfunktionen returnerer på under 0,2 sekund i testen og lukker den lokale afsendelsesgrænse. Når svaret frigives, udføres ingen ændring.
10. Luk hovedappen og bekræft, at den ejede testproces afsluttes.
11. Sæt kaldgrænsen til én; forløbet stopper før et nyt billede/modeltrin og uden at ændre valutaen.
12. Simulér providerens tokenmetadata gennem seks trin; kontrollér UI, JSON-log og `last-plan.json`. Et målt svar efter STOP tælles, men må ikke udføre en handling.

Der er desuden afprøvet, at `present` løfter et tildækket testvindue før Linux-optagelsen, uden at ændre datarevisionen. Screenshot blev visuelt gennemgået. [Brugerfladebilledet](brugerflade.png) viser den rigtige app efter et gennemført demoforløb.

Testartefakter i `/tmp` er undtagelser til normal drift; appen gemmer ikke skærmbilleder på disk. Log og `last-plan.json` indeholder beslutninger/resultater som data.

## Ikke afprøvet og næste kontrol

| Område | Begrænsning og næste kontrol |
|---|---|
| OpenAI live | Ingen eksisterende VISION_API_KEY var tilgængelig. Gem din egen nøgle i Windows-nøglelageret, vælg OpenAI/gpt-5-nano, og gentag forløbet. Payload/parsing og fejlhåndtering er testet, modelnøjagtighed er ikke. |
| Ollama live | Der er ikke installeret en billedmodel/server med tilstrækkelig hardware her. Test en egnet lokal model separat. |
| Windows-native GUI og HWND-optagelse | Implementeret med Pillow window=hwnd mod testprogrammets eget vindue; det kræver afprøvning på Windows, også med skalering og flere skærme. Linux-GUI-testen bruger bbox-optagelse efter løft af vinduet. |
| Windows Credential Manager | Keyring-integration er skrevet, men faktisk lagring/sletning og genstart skal afprøves på Windows. |
| Windows .exe | Byggescriptet pakker samme program til hovedvindue og --test-game, medtager nøglelagerbackend og kører en pakket RPC-smoketest før artefaktet accepteres. Ingen Windows-builder er tilgængelig her; der leveres ingen bygget eller afprøvet Windows-binær. |
| Manuel vindues-/områdevalg | Hovedappen bruger kun det ejede testvindue og dets HWND. Manuel valg af andet vindue eller udsnit er ikke implementeret i hovedforløbet endnu. |
| Genvej | P virker, når hovedvinduet har fokus. Den kan endnu ikke ændres og er ikke global. |
| Videoens originalprogram | Undertekster og billeder er undersøgt, men hele videoen/originalprogrammet er ikke kørt eller reproduceret mod de nævnte spil. Vores søgning er afgrænset til egen registreret buffer. |
| Ny cloudopgave | Gemt konfigurationsudkast er ikke det samme som publicering eller verificeret gendannelse i en ny opgave. |

De udførte tests består. De uafprøvede integrationer er ikke dermed verificerede.

## Kort Windows-accepttest

1. Kør installationsscriptet og kontrollér, at alle automatiske tests består.
2. Start appen, start testprogrammet, og kør demoen med slutværdi 999. Forvent 3 → 2 → 1 kandidater og venten på godkendelse ved valuta 100.
3. Afvis først og kontrollér uændret værdi. Nulstil og gentag med godkendelse; forvent 999 og en tydeligt mærket demokontrol.
4. Test Esc i hvert vindue, stop under et modelkald og en manuel ændring under godkendelsesventetiden.
5. Test ved 100 % og 150 % skalering samt med testvinduet flyttet/tildækket. Kontrollér, at Windows-optagelsen faktisk viser det ejede testvindue.
6. Gem en nøgle, genstart, og prøv OpenAI. Kontrollér, at modellen aflæser de synlige tal og vælger tilladte handlinger. Test ugyldig nøgle/model uden at dele nøgle eller rå svar i logfiler.
7. Byg .exe og gentag på en Windows-maskine uden projektets Python-miljø. Kontrollér, at både hovedapp, underproces, stdio-kommunikation og nøglelager virker.

## Cloudkonfiguration

Installations- og startinstruktioner gemmes i miljøets konfigurationsudkast. Gennemgang, gemning i miljøindstillingerne og publicering udføres af brugeren. Ingen netværksregler, credential-værdier eller repository-medlemskab er ændret under genskabelsen. Live OpenAI i et begrænset cloudnetværk kræver adgang til api.openai.com og en eksisterende eller sikkert tilføjet nøglebinding.
