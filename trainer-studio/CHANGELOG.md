# Ændringer

## 0.3.0

- Nyt standardvindue til rigtige Windows-processer med synligt procesvalg,
  tilknytning med læseadgang og særskilt aktivering af værdiændringer.
- Windows-adapter til x86-/x64-mål med regionsforespørgsler, blokvis læsning,
  skrivekontrol, readback, proceslukning og frigivelse af handles.
- Responsiv, annullerbar First/Next Scan med 11 datatyper, seks filtre,
  naturlig eller bytevis justering, adresseinterval og synlige læse-/kandidatgrænser.
- Afgrænset pointer-scan og genvalidering med modulrod og ikke-negative
  offsets; fundne stier kan bruges i Builder. Gemte pointerhenvisninger kan
  hentes fra profilen og krydstjekkes mod et uafhængigt genfund efter genstart.
- Pointeranalysen prioriterer modulområder før øvrige områder inden for samme
  samlede læsebudget uden dobbelt læsning. Mapped områder kan vælges til;
  de er slået fra som standard.
- Sessionsadresser adskilles fra genbrugelige modul- og pointerhenvisninger.
  Exe-build og rodmodulets SHA-256-fingeraftryk kontrolleres før anvendelse.
- Sikker gendannelse bindes til den aktuelle processtart og den faktisk
  opløste adresse. Flyttede pointere stopper fastholdelser; overlappende
  fastholdelser afvises.
- Builder kan oprette, redigere og reparere funktioner fra nye fund samt
  kopiere, flytte og slette dem. Genveje virker lokalt i hovedvinduet og den
  kompakte trainer-menu, når fokus ikke står i et tekst- eller valgfelt.
- Profilformat 3 med migrering fra format 2. Gamle v0.2-apps kan ikke indlæse
  en profil, når den først er gemt i format 3.
- Proceslisten genkender match til gemte profiler cirka hvert ottende sekund
  og tilbyder dem uden automatisk tilknytning eller aktivering.
- **STOP ALLE** stopper fastholdelser, slår skriveadgang fra og annullerer
  igangværende søgning uden at skrive en ny værdi.
- Flere almindelige kode-, pointer-, Windows-API- og renderingstests.
  Windows-kontroller skal bestå i CI før release. Rigtige Unity-, Unreal- og
  legacy-spil er endnu ikke afprøvet; der er ikke udviklet nye testspil.
- Decimaltrin som `0.1` i Float-sliders håndteres uden at indføre ekstra
  binære afrundingscifre i inputtet. Heltalssliders med minimum eller maksimum
  uden for `±2^53` afvises og henvises til præcis talindtastning.

## 0.2.1

- Mørkt udseende i selve de lukkede rullemenuer, også når de er deaktiverede.
- Læselige valgte elementer i åbne rullemenuer.
- Mørke tabeloverskrifter, resultatrækker og markerede celler.
- Læselig tekst på deaktiverede knapper og mørke scrollbars.
- Versionsnummer i hovedvinduets titel, så den nye EXE kan genkendes.
- Windows-kontrol af de faktisk renderede farver før en release med gemte
  PNG-billeder som testdokumentation.

## 0.2.0

- Kompakt trainer-menu med kategorier, knapper, kontakter, sliders og talfelter.
- Kopiering, flytning og sletning af funktioner.
- Duplikering, import og eksport af profiler.
- Op til ti automatiske backups af tidligere profilversioner.
