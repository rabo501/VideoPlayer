# Indeks dokumentacji VideoPlayer 1.8.0

**Stan przeglądu:** 20.11.2026  
**Wersja:** `1.8.0`

Celem indeksu jest wskazanie dokumentów, z których należy korzystać, materiałów historycznych oraz archiwum wcześniejszych etapów. Istotne informacje historyczne zachowano w chronologii zmian.

## 1. Dokumenty aktualne — pozostawić

| Plik | Status | Przeznaczenie |
|---|---|---|
| `VideoPlayer-1.8.0-Dokumentacja.md` | **AKTUALNY** | Główna instrukcja użytkownika i wdrożenia wydania 1.8.0. |
| `VideoPlayer-1.8.0-Informacje-o-wydaniu.md` | **AKTUALNY** | Zakres zmian (MVVM, wydzielone serwisy, optymalizacja OCR, testy) i uwagi do wydania 1.8.0. |
| `VideoPlayer-1.8.0-Lista-publikacyjna.md` | **AKTUALNY** | Checklista testów jednostkowych, kompilacji, paczki i testów przed dystrybucją 1.8.0. |
| `Ochrona-danych-logowania-Dahua.md` | **AKTUALNY TECHNICZNIE** | DPAPI, migracja danych logowania i ograniczenia przenoszenia konfiguracji. |
| `Przebudowa-Archiwum-Dahua.md` | **AKTUALNY TECHNICZNIE** | Szczegóły NetSDK (`DahuaSessionService`), archiwum, pobierania DAV i PTZ. |
| `Dahua-NetSDK-Instrukcja.md` | **AKTUALNY SKRÓT** | Wskazuje aktualne zasady i odsyła do szczegółowego opisu archiwum Dahua. |
| `HISTORIA-ZMIAN-VideoPlayer-do-1.8.0.md` | **ARCHIWUM / KRONIKA — ZACHOWAĆ** | Skonsolidowana chronologia zmian i decyzji technicznych aż do wydania 1.8.0. |

## 2. Materiały historyczne z linii 1.7 i wcześniejszych

| Plik | Decyzja | Powód |
|---|---|---|
| `ETAP-6-Raportowanie-i-wydanie-1.7-Wersja-1.7.md` | **ARCHIWALNY** | Opis prac nad archiwum spraw i raportowaniem z wydania 1.7.0; funkcje włączono do wydania 1.8.0 i opisano w chronologii oraz instrukcji głównej. |
| Pliki `ETAP-1`..`ETAP-5`, `Przebudowa-Interfejsu-Dahua.md`, `README_Etap6_7.md` | **USUNIĘTE WCZEŚNIEJ** | Skonsolidowane w `HISTORIA-ZMIAN-VideoPlayer-do-1.8.0.md`. |
| `Dokumentacja-VideoPlayer-1.3.2.docx`, `VideoPlayer-1.3.2-pelna-dokumentacja.zip` | **ARCHIVE / POZA REPO** | Historyczna dokumentacja 1.3.2; nie stosować zamiast dokumentacji 1.8.0. |

## 3. Zasada nadrzędna

W razie sprzeczności obowiązuje aktualny kod rozwiązania `VideoPlayer.slnx`, instrukcja `VideoPlayer-1.8.0-Dokumentacja.md` i dokumentacja wydania 1.8.0. Materiały oznaczone jako archiwalne nie powinny być wykorzystywane jako bieżące instrukcje obsługi lub wdrożenia.
