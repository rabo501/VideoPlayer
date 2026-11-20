# VideoPlayer 1.7 — etap 4: Centrum zdarzeń

## Cel

Etap 4 porządkuje zdarzenia zapisane dla bieżącego nagrania. Wspólna lista obejmuje:
- zdarzenia wykryte przez analizę ruchu,
- zdarzenia z potwierdzonym ręcznie numerem tablicy,
- zdarzenia dodane ręcznie przez operatora.

## Zmiany

### 1. Klasyfikacja wpisów
Typ wpisu jest wyznaczany z istniejących danych:
- **Ruch** — wpis wygenerowany przez detektor ruchu i posiadający przedział czasu,
- **Tablica** — wpis OCR rozpoczynający się od „Tablica rejestracyjna:”,
- **Ręczne** — pozostałe znaczniki operatora.

Nie dodano wymaganych pól do bazy danych i nie zmieniono formatu istniejących rekordów.

### 2. Filtrowanie i wyszukiwanie
W zakładce „Zdarzenia” dodano panel Centrum zdarzeń:
- wyszukiwanie w opisie, numerze tablicy, marce i typie,
- filtrowanie wszystkich zdarzeń, ruchu, tablic, zdarzeń ręcznych,
- filtr tablic powiązanych czasowo ze zdarzeniem ruchu,
- licznik zdarzeń według kategorii i liczby widocznych wyników.

### 3. Powiązanie OCR z detekcją ruchu
Potwierdzony wpis tablicy jest informacyjnie wiązany ze zdarzeniem ruchu, jeżeli czas odczytu mieści się w przedziale ruchu z tolerancją 3 sekund przed lub po nim.

To powiązanie nie scala i nie usuwa rekordów. Nie jest też automatycznym potwierdzeniem, że pojazd z tablicą spowodował wykryty ruch. Operator nadal weryfikuje obraz i czas.

### 4. Zachowanie istniejących funkcji
- Dwuklik na wpisie nadal przenosi odtwarzacz do jego czasu.
- Podgląd obszaru ruchu pozostaje dostępny dla zdarzeń ruchu.
- Edycja i usuwanie znacznika nadal działają na oryginalnym rekordzie.
- Raporty CSV/PDF nadal korzystają z pełnej listy znaczników, a nie tylko z aktualnie przefiltrowanego widoku.
- Nie zmieniono OCR, algorytmu detekcji ruchu, eksportu ani menu.

## Testy w Visual Studio

1. Skompiluj Debug x64 i Release x64.
2. Otwórz nagranie zawierające zapisane znaczniki, zdarzenia ruchu i zdarzenia tablic.
3. Sprawdź, czy lista pokazuje typ wpisu i prawidłowy czas.
4. Wybierz kolejno filtry: Wszystkie, Ruch, Tablice, Ręczne.
5. Wpisz fragment numeru tablicy lub marki w polu wyszukiwania.
6. Wybierz „Tablice powiązane z ruchem” i sprawdź wpisy w pobliżu przedziałów detekcji ruchu.
7. Kliknij „Wyczyść” i sprawdź przywrócenie całej listy.
8. Dwuklikiem przejdź do zdarzenia, sprawdź podgląd obszaru ruchu, edycję i usuwanie.
9. Wygeneruj raport CSV i PDF przy aktywnym filtrze. Raporty powinny nadal zawierać wszystkie zapisane znaczniki.
10. Zamknij i ponownie otwórz nagranie; sprawdź, czy zapisane zdarzenia nadal są dostępne.

## Dane i zgodność

Zmiany wykorzystują dotychczasową tabelę `TimelineMarkers`. Nie jest wymagana migracja bazy SQLite. Klasyfikacja i relacje czasowe są obliczane w pamięci podczas odświeżania widoku.
