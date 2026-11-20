# VideoPlayer 1.7 – etap 2: poprawa wykrywania ruchu

## Zakres zmian

Zmiany dotyczą wyłącznie algorytmu wykrywania ruchu i krótkiej informacji w oknie ustawień. Nie zmieniono OCR, odtwarzania mpv, eksportu, integracji Dahua ani układu głównego okna.

### 1. Odrzucanie drobnych zakłóceń
- Zmiana jasności piksela jest liczona tylko wtedy, gdy co najmniej dwóch z jego ośmiu sąsiadów również przekracza ustawiony próg różnicy jasności.
- Ogranicza to wpływ pojedynczych błysków, szumu matrycy i artefaktów kompresji.
- Próg czułości nadal jest ustawiany w oknie konfiguracji.

### 2. Potwierdzanie zdarzenia
- Zdarzenie jest rejestrowane dopiero po wykryciu ruchu w dwóch kolejnych próbkach.
- Przy próbkowaniu 2 kl./s oznacza to potwierdzenie ruchu utrzymującego się przez około 0,5 sekundy.
- Początek zdarzenia jest cofany do pierwszej z potwierdzonych próbek, aby nie przesuwać znacznika do przodu.
- Pojedyncza, krótkotrwała zmiana obrazu nie tworzy zdarzenia.

### 3. Łączenie zdarzeń
- Przerwy do 1,5 sekundy są traktowane jako część tego samego zdarzenia.
- Dłuższa przerwa powoduje rozpoczęcie nowego zdarzenia po ponownym potwierdzeniu ruchu.
- Koniec zdarzenia odpowiada ostatniej próbce, w której ruch został wykryty, z uwzględnieniem półsekundowego kroku próbkowania.

### 4. Obszar detekcji i zgodność
- Nadal analizowany jest wyłącznie zaznaczony ROI; piksele poza nim nie wpływają na procent zmiany ani na prostokąt zdarzenia.
- Współrzędne prostokąta ruchu są nadal zapisywane w bazie SQLite i wykorzystywane przez podgląd zdarzenia.
- Parametry i format zapisanych znaczników pozostają bez zmian.

## Testy w Visual Studio

Po pobraniu aktualnej gałęzi `wersja-1.7`:

1. Skompiluj Debug x64 i Release x64.
2. Otwórz nagranie, w którym kamera jest nieruchoma, ale obraz zawiera szum lub kompresję. Uruchom analizę i sprawdź, czy liczba fałszywych zdarzeń jest mniejsza.
3. Użyj nagrania z krótkim błyskiem lub pojedynczą zmianą klatki. Taka zmiana nie powinna utworzyć zdarzenia.
4. Użyj nagrania z obiektem poruszającym się przez kilka sekund. Sprawdź początek, koniec i liczbę zdarzeń.
5. Sprawdź dwa epizody ruchu rozdzielone krótką przerwą (do 1,5 s) – powinny zostać połączone.
6. Sprawdź dwa epizody rozdzielone dłuższą przerwą – powinny zostać zapisane jako osobne zdarzenia.
7. Zaznacz mały ROI i wywołaj ruch poza nim. Ruch poza zaznaczeniem nie może być wykrywany.
8. Wykonaj analizę całego obrazu i ROI, a następnie otwórz podgląd kilku zapisanych zdarzeń.
9. Sprawdź anulowanie analizy i ponowne uruchomienie jej na tym samym nagraniu.
10. Otwórz ponownie nagranie i potwierdź, że zapisane znaczniki nadal są widoczne.

## Ważne uwagi do oceny

- Algorytm analizuje obraz w rozdzielczości 320×180 i z częstotliwością 2 próbek na sekundę. Jest to detekcja zmian obrazu, a nie klasyfikacja obiektów.
- Ruch bardzo szybki lub widoczny tylko w jednej próbce może zostać pominięty. Jeśli testy wykażą taki problem, kolejnym krokiem będzie regulowana częstotliwość próbkowania.
- Wyniki zależą od jakości nagrania, oświetlenia, kompresji i ustawień czułości oraz minimalnego obszaru.
- Nie zmieniaj numeru wersji aplikacji na 1.7.0 przed zakończeniem całego cyklu testów i stabilizacji.

## Status

Zmiany zapisano w gałęzi rozwojowej `wersja-1.7`. Nie wykonano kompilacji w Visual Studio ani testów na rzeczywistych nagraniach w tej aktualizacji. Etap wymaga weryfikacji przez użytkownika według powyższej listy.
