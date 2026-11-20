# VideoPlayer — chronologia rozwoju do wersji 1.7.0

**Stan:** 20.11.2026  
**Gałąź aktualna:** `wersja-1.7`  
**Wersja aplikacji:** `1.7.0`

Ten dokument zachowuje istotne informacje z dokumentacji etapowej i starszych opisów technicznych. Jest dokumentem historycznym, a nie instrukcją obsługi. Aktualne procedury użytkownika i wdrożenia znajdują się w `VideoPlayer-1.7.0-Dokumentacja.md`.

## 1. Początki projektu i wersje wcześniejsze

### Linia 1.3.2

W starszej dokumentacji opisano podstawowy odtwarzacz WPF oparty na mpv i FFmpeg, obsługę fragmentów nagrań, zrzuty klatek, historię, znaczniki zdarzeń oraz pierwsze wydzielenie usług aplikacji.

W historycznym rozwiązaniu:
- mpv był uruchamiany jako proces z osadzonym oknem obrazu i sterowany przez IPC;
- FFmpeg odpowiadał za analizę mediów i eksport;
- historia i znaczniki były początkowo zapisywane w plikach JSON w profilu użytkownika;
- później dane przeniesiono do lokalnej bazy SQLite `VideoPlayer.db` w katalogu aplikacji, z jednorazowym importem starszych danych JSON.

Starsze instrukcje opisujące JSON jako bieżący magazyn danych są nieaktualne. Obecnym źródłem danych aplikacji jest lokalna baza SQLite.

### Linia 1.5.0 — przebudowa obsługi Dahua

W tej fazie przebudowano interfejs obsługi rejestratora:
- oddzielono konfigurację urządzeń od okna codziennej obsługi;
- wprowadzono wielokanałowy podgląd live w układach 1/4/9/16;
- dodano wspólne sterowanie odtwarzaniem archiwum;
- rozwijano kalendarz dostępności nagrań i narzędzia PTZ.

Opis interfejsu z tej fazy jest historyczny. Późniejsze zmiany przeniosły wybór kanałów archiwum i układu do zakładki „Archiwum NVR” w głównym oknie.

## 2. Stabilizacja i rozdzielenie odpowiedzialności

### Etap 1 — stabilizacja IPC i SQLite

W gałęzi rozwojowej `wersja-1.7`:
- poprawiono obsługę jawnych identyfikatorów żądań IPC mpv;
- zabezpieczono pulę oczekujących odpowiedzi przed kolizją identyfikatorów;
- przed migracją schematu SQLite dodano kopię przez SQLite Backup API, uwzględniającą zatwierdzone dane WAL;
- migracja jest blokowana, jeśli wykonanie wymaganej kopii się nie powiedzie.

Użytkownik potwierdził poprawne działanie etapu po kompilacji i testach.

### Etap 2 — wykrywanie ruchu

Udoskonalono analizę różnic jasności:
- odrzucanie izolowanych zmian pikseli na podstawie sąsiedztwa;
- potwierdzanie ruchu w dwóch kolejnych próbkach;
- cofnięcie początku zdarzenia do pierwszej potwierdzonej próbki;
- łączenie zdarzeń oddzielonych przerwą do 1,5 sekundy;
- zachowanie ograniczenia analizy do wskazanego ROI i zapisu prostokąta wykrytego ruchu.

Użytkownik potwierdził, że wyniki są poprawne.

## 3. OCR i wyszukiwanie tablic

### Etap 3A — OCR wieloklatkowy

Dla lokalnego nagrania analizowanych jest do pięciu sąsiednich klatek. Zaznaczenie tablicy jest przenoszone na kolejne próbki, obraz prostowany perspektywicznie, a wyniki Tesseract porównywane. W rankingu:
- odrzucane są wyniki krótsze niż 4 znaki;
- średnia pewność OCR ma pierwszeństwo;
- liczba zgodnych klatek i długość tekstu służą jako kryteria pomocnicze.

Operator nadal musi zweryfikować numer na obrazie i ręcznie potwierdzić zapis zdarzenia. Dla podglądu Dahua zachowano OCR pojedynczej klatki.

### Etap 3B — wyszukiwanie w całym nagraniu

Dodano lokalne próbkowanie nagrania przez FFmpeg i OCR wskazanego obszaru:
- interwały próbkowania 1/2/5/10 sekund;
- limit 1200 próbek z automatycznym zwiększeniem odstępu przy długich nagraniach;
- łączenie sąsiednich trafień w przedziały czasowe;
- postęp i anulowanie analizy.

Ważne ograniczenie: wyszukiwanie wykorzystuje ten sam obszar obrazu wskazany przez operatora. Nie jest to automatyczne wykrywanie tablic w dowolnym miejscu kadru ani śledzenie pojazdu.

Użytkownik zaakceptował pozostawienie etapu 3 w obecnym zakresie; nie wdrożono proponowanego detektora ONNX ani trackingu.

## 4. Centrum zdarzeń

Etap 4 uporządkował zdarzenia bieżącego nagrania:
- kategorie: ruch, tablice i zdarzenia ręczne;
- wyszukiwanie w opisie, numerze tablicy, marce i typie;
- filtry kategorii oraz tablic powiązanych czasowo z ruchem;
- informacyjne wiązanie tablicy ze zdarzeniem ruchu, gdy czas odczytu mieści się w zakresie ruchu z tolerancją 3 sekund.

Powiązanie czasowe nie dowodzi, że oba wpisy dotyczą tego samego obiektu. Nie scala ani nie usuwa oryginalnych rekordów. Raporty powinny obejmować pełną listę zapisanych znaczników, niezależnie od filtra widoku.

## 5. Wielokanałowa obsługa Dahua

W ramach Etapu 5 rozwijano:
- raportowanie częściowego uruchomienia kanałów archiwum, z podaniem kanałów niedostępnych;
- oddzielenie wyboru kanałów live od wyboru kanałów archiwum;
- niezależny układ live i układ archiwum;
- wybór układu 1/4/9/16 oraz kanałów bezpośrednio w zakładce „Archiwum NVR”;
- wspólny przedział czasu i sterowanie zsynchronizowanym odtwarzaniem archiwum.

Ważna zmiana interfejsu: starsze dokumenty opisują osobne okno/modalny wybór kanałów oraz wybór kanałów w panelu live. Aktualny przepływ wyboru archiwum znajduje się w zakładce „Archiwum NVR”. Nie należy traktować starszych zrzutów ani kroków instrukcji jako bieżącej procedury.

Użytkownik zaakceptował przebudowany wybór kanałów archiwum. Zakresy wspólnych zdarzeń i wielokanałowego eksportu nie powinny być uznawane za wdrożone tylko dlatego, że pojawiały się w pierwotnym planie Etapu 5.

## 6. Archiwum spraw i raportowanie

### Etap 6A — lokalne archiwum spraw

Dodano tabele `Cases` i `CaseSources` w lokalnej bazie SQLite:
- sprawa przechowuje numer, tytuł, miejsce, uwagi i daty;
- jedna sprawa może mieć wiele powiązanych nagrań;
- ponowny zapis numeru (bez rozróżniania wielkości liter) aktualizuje metadane i dopisuje źródło;
- usunięcie sprawy usuwa wyłącznie rekord sprawy i powiązania, nie usuwa nagrań, znaczników ani eksportów.

Użytkownik potwierdził poprawne działanie.

### Etap 6B — raporty PDF i CSV

Raporty zostały rozbudowane o dane sprawy, źródła, zdarzenia, zakresy czasu, parametry techniczne i historię eksportów. CSV używa UTF-8 z BOM i separatora średnika; zabezpiecza komórki przed interpretacją jako formuły arkusza.

Istotna korekta: raport wybranej sprawy pobiera dane ze wszystkich powiązanych nagrań i może być generowany bez otwierania plików MP4. W raporcie uwzględniane są również niedostępne źródła, jeśli zachowały się ich dane w bazie. Przycisk „Bieżące nagranie” czyści wybór sprawy i przywraca zakres raportowania pojedynczego pliku.

W trakcie testów wystąpiła awaria generowania PDF bez otwartego MP4. Przyczyną było utworzenie obiektu postępu po przejściu na wątek roboczy, a następnie aktualizacja kontrolek WPF z niewłaściwego kontekstu. Przeniesiono utworzenie obiektu postępu na wątek UI i poprawiono warunek dopuszczający raport sprawy bez aktywnego źródła. Użytkownik potwierdził, że PDF działa prawidłowo.

## 7. Wydanie 1.7.0

Na gałęzi `wersja-1.7` ustawiono:
- `Version` i `InformationalVersion`: 1.7.0;
- `AssemblyVersion` i `FileVersion`: 1.7.0.0;
- oznaczenie w tytule okna, README i dokumentacji wydania.

Użytkownik zatwierdził wersję 1.7.0. Aktualna dokumentacja użytkownika i wdrożeniowa, informacje o wydaniu oraz checklista publikacji są w osobnych plikach 1.7.0.

## 8. Zasady, które pozostają ważne

- Nagrania, OCR, dane spraw, znaczniki i raporty są przetwarzane lokalnie.
- Archiwum spraw przechowuje ścieżki do źródeł, nie kopiuje i nie modyfikuje nagrań.
- Wyniki OCR i detekcji ruchu wymagają weryfikacji operatora.
- Czasy w raportach są względne względem początku pliku, a nie automatycznie czasem zegara kamery.
- Zmiany w Dahua należy weryfikować na rzeczywistym urządzeniu; częściowe uruchomienie kanałów musi być jasno raportowane.
- Przed migracją bazy należy wykonać kopię; testy migracji wykonywać na kopii danych.
- Nie deklarować kompilacji ani testów sprzętowych jako wykonanych bez rzeczywistego uruchomienia ich w Visual Studio i na urządzeniu docelowym.

## 9. Dokumenty źródłowe

Ten zapis konsoliduje najważniejsze informacje z:
- `ETAP-1-Stabilizacja-Wersja-1.7.md`;
- `ETAP-2-Wykrywanie-ruchu-Wersja-1.7.md`;
- `ETAP-3A-OCR-Wieloklatkowy-Wersja-1.7.md`;
- `ETAP-3B-Wyszukiwanie-tablic-Wersja-1.7.md`;
- `ETAP-4-Centrum-zdarzen-Wersja-1.7.md`;
- `ETAP-5-Analiza-wielokanalowa-Dahua-Wersja-1.7.md`;
- `ETAP-6-Raportowanie-i-wydanie-1.7-Wersja-1.7.md`;
- `Przebudowa-Interfejsu-Dahua.md`;
- `HISTORIA-README_Etap6_7.md`.

Szczegółowe opisy implementacji i checklisty etapowe są materiałem archiwalnym. W razie rozbieżności obowiązuje aktualna instrukcja 1.7.0 oraz bieżący kod gałęzi `wersja-1.7`.
