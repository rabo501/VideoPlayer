# Indeks dokumentacji VideoPlayer 1.7.0

**Stan przeglądu:** 20.11.2026  
**Gałąź:** `wersja-1.7`

Celem indeksu jest wskazanie dokumentów, z których należy korzystać, materiałów historycznych oraz plików usuniętych z bieżącego zestawu dokumentacji. Istotne informacje historyczne zachowano w chronologii zmian.

## 1. Dokumenty aktualne — pozostawić

| Plik | Status | Przeznaczenie |
|---|---|---|
| `VideoPlayer-1.7.0-Dokumentacja.md` | **AKTUALNY** | Jedyna główna instrukcja użytkownika i wdrożenia. |
| `VideoPlayer-1.7.0-Informacje-o-wydaniu.md` | **AKTUALNY** | Zakres funkcji i uwagi do wydania 1.7.0. |
| `VideoPlayer-1.7.0-Lista-publikacyjna.md` | **AKTUALNY** | Checklista kompilacji, paczki i testów przed dystrybucją. |
| `ETAP-6-Raportowanie-i-wydanie-1.7-Wersja-1.7.md` | **ROBOCZY / AKTUALNY** | Szczegółowy zakres archiwum spraw, raportów i testów końcowych. |
| `Ochrona-danych-logowania-Dahua.md` | **AKTUALNY TECHNICZNIE** | DPAPI, migracja danych logowania i ograniczenia przenoszenia konfiguracji. |
| `Przebudowa-Archiwum-Dahua.md` | **TECHNICZNY / DO DROBNEJ AKTUALIZACJI** | Szczegóły NetSDK, archiwum, pobierania DAV i PTZ; część historycznych kroków wdrożenia nie obowiązuje. |
| `Dahua-NetSDK-Instrukcja.md` | **AKTUALNY SKRÓT** | Wskazuje aktualne zasady i odsyła do szczegółowego opisu archiwum Dahua. |
| `HISTORIA-ZMIAN-VideoPlayer-do-1.7.0.md` | **ARCHIWUM — ZACHOWAĆ** | Skonsolidowana chronologia zmian i decyzji technicznych. |

## 2. Pliki historyczne — usunięte

Poniższe dokumenty etapowe zostały usunięte z gałęzi `wersja-1.7` po akceptacji indeksu i chronologii. Zawierały wartościowe informacje, ale ich statusy testów, numery wersji, opisy interfejsu lub planowane zakresy były nieaktualne. Najważniejsze informacje przeniesiono do chronologii.

| Plik | Decyzja | Powód |
|---|---|---|
| `ETAP-1-Stabilizacja-Wersja-1.7.md` | **USUNIĘTY** | Etap zaakceptowany; historyczna checklista i status testów sprzed finalnej akceptacji. |
| `ETAP-2-Wykrywanie-ruchu-Wersja-1.7.md` | **USUNIĘTY** | Etap zaakceptowany; chronologia zachowuje parametry algorytmu. |
| `ETAP-3A-OCR-Wieloklatkowy-Wersja-1.7.md` | **USUNIĘTY** | Historyczny status 1.6.0 i stare checklisty; ważne ograniczenia i korekty OCR przeniesiono do chronologii. |
| `ETAP-3B-Wyszukiwanie-tablic-Wersja-1.7.md` | **USUNIĘTY** | Etap opisany jako roboczy; aktualny zakres i ograniczenia zachowano w chronologii i instrukcji. |
| `ETAP-4-Centrum-zdarzen-Wersja-1.7.md` | **USUNIĘTY** | Opis wdrożenia i testów etapowych; kluczowe zasady klasyfikacji i powiązań zachowano w chronologii. |
| `ETAP-5-Analiza-wielokanalowa-Dahua-Wersja-1.7.md` | **USUNIĘTY** | Zawiera nieaktualne etapy planowane jako niewykonane oraz starsze warianty wyboru kanałów. Chronologia zachowuje końcowy przepływ i zastrzeżenia. |
| `Przebudowa-Interfejsu-Dahua.md` | **USUNIĘTY** | Dokument wersji 1.5.0; opisuje starszy układ okna i sterowania archiwum. Ważne tło historyczne przeniesiono do chronologii. |
| `HISTORIA-README_Etap6_7.md` | **USUNIĘTY** | Duplikat `README_Etap6_7.md`; opisuje wcześniejszą architekturę JSON i dawne kroki wdrożenia. Istotna ewolucja bazy danych jest w chronologii. |
| `README_Etap6_7.md` | **USUNIĘTY** | Identyczna zawartość i ten sam blob Git co plik HISTORIA; zbędny duplikat. |

## 3. Starsze archiwum 1.3.2 — usunięte z bieżącego repozytorium

| Plik | Decyzja | Powód |
|---|---|---|
| `Dokumentacja-VideoPlayer-1.3.2.docx` | **DO USUNIĘCIA Z REPO** | Stara dokumentacja użytkownika; zastąpiona instrukcją 1.7.0. Zachować poza repo wyłącznie wtedy, gdy potrzebna jest dokumentacja historyczna. |
| `VideoPlayer-1.3.2-pelna-dokumentacja.zip` | **DO USUNIĘCIA Z REPO** | Duży, skompresowany pakiet starej dokumentacji, nie jest wymagany do pracy z wersją 1.7.0. Nie rozpakowywać ani nie traktować jako źródła bieżącej instrukcji. |

## 4. Uwagi do dokumentu technicznego Dahua

`Przebudowa-Archiwum-Dahua.md` zawiera nadal użyteczne szczegóły implementacyjne: rozdzielenie nazwy urządzenia od adresu połączenia, migrację konfiguracji, użycie `CLIENT_QueryRecordFile`, `CLIENT_PlayBackByTimeEx`, `CLIENT_DownloadByTimeEx`, konwersję DAV do MP4 oraz operacje PTZ. Nie rekomenduję jego usunięcia bez przeniesienia tych szczegółów do osobnego podręcznika technicznego.

W dokumencie pozostają jednak stare instrukcje pobrania gałęzi `dahua-archive-rebuild` i dawne kroki interfejsu. Nie są instrukcją dla aktualnej gałęzi `wersja-1.7`. Należy je zaktualizować przed kolejnym wydaniem lub zastąpić odrębnym dokumentem technicznym.

## 5. Stan porządkowania

1. Zatwierdzić chronologię `HISTORIA-ZMIAN-VideoPlayer-do-1.7.0.md`.
2. Zachować wyłącznie dokumenty z sekcji 1 jako aktywny zestaw dokumentacji.
3. Usunąć pliki oznaczone **USUNIĘTY** w sekcjach 2–3.
4. Przed usunięciem archiwum 1.3.2 upewnić się, że nie jest wymagane do celów formalnych lub audytowych.
5. Zaktualizować `Przebudowa-Archiwum-Dahua.md` i pozostawić go jako dokument techniczny.

## 6. Zasada nadrzędna

W razie sprzeczności obowiązuje kod aktualnej gałęzi `wersja-1.7`, instrukcja `VideoPlayer-1.7.0-Dokumentacja.md` i dokumentacja wydania 1.7.0. Pliki oznaczone do usunięcia są wyłącznie materiałem historycznym i nie powinny być wykorzystywane jako instrukcje obsługi lub wdrożenia.
