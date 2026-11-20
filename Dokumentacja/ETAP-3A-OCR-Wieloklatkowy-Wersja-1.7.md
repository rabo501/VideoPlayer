# VideoPlayer 1.7 – etap 3A: OCR wieloklatkowy i konsensus odczytów

## Cel tej części etapu

Zwiększenie wiarygodności odczytu tablic przez porównanie wyników OCR z kilku sąsiednich klatek nagrania. Numer nadal musi zostać sprawdzony i ręcznie potwierdzony przez operatora.

## Wprowadzone zmiany

### 1. Analiza sąsiednich klatek
- Dla lokalnego nagrania program pobiera klatki w punktach: bieżąca pozycja oraz około -1,0 s, -0,5 s, +0,5 s i +1,0 s.
- Klatki poza zakresem nagrania są pomijane.
- Zaznaczenie czterech narożników tablicy jest przenoszone proporcjonalnie na kolejne klatki.
- Każdy wycinek jest prostowany perspektywicznie i analizowany lokalnie przez dotychczasowy silnik Tesseract.
- Dla podglądu Dahua zachowano dotychczasowe rozpoznawanie pojedynczej, bieżącej klatki.

### 2. Konsensus wyników
- Odczyty z poszczególnych klatek są normalizowane i grupowane według identycznego numeru.
- Najwyżej prezentowany jest numer, który powtórzył się w największej liczbie klatek.
- Przy takiej samej liczbie powtórzeń rozstrzyga średnia pewność OCR.
- Interfejs pokazuje liczbę zgodnych klatek oraz średnią pewność OCR.
- Użytkownik nadal musi zweryfikować numer na obrazie i zaznaczyć potwierdzenie ręczne. Sam wynik OCR nie zapisuje zdarzenia.

### 3. Zachowanie dotychczasowego przepływu
- Pozostawiono zaznaczanie tablicy czterema narożnikami, prostowanie perspektywy i podgląd wycinka.
- Nie zmieniono reguł walidacji numeru, pól marki pojazdu ani zapisu potwierdzonego zdarzenia.
- OCR pozostaje lokalny – klatki i wycinki są przetwarzane na komputerze użytkownika. Pliki tymczasowe są usuwane po analizie.

## Ograniczenie obecnej części etapu

Wieloklatkowy konsensus korzysta z tego samego zaznaczenia geometrycznego na sąsiednich klatkach. Jeżeli pojazd szybko przemieści się względem kamery, tablica może wyjść poza zaznaczony obszar i część próbek nie będzie użyteczna.

Ta część nie realizuje jeszcze automatycznego wykrywania położenia tablic w każdej klatce całego nagrania ani śledzenia pojazdu. Do takiego wyszukiwania potrzebny jest osobny mechanizm lokalizacji tablicy w obrazie i łączenia obserwacji w czasie. Nie należy utożsamiać go z obecnym konsensusem OCR.

## Testy w Visual Studio 2022

1. Skompiluj Debug x64 i Release x64.
2. Otwórz lokalne nagranie i uruchom OCR z zaznaczeniem tablicy.
3. Sprawdź, czy analiza kończy się bez błędów i czy status pokazuje liczbę zgodnych klatek oraz średnią pewność.
4. Użyj nagrania, na którym tablica jest czytelna w kilku sąsiednich klatkach. Sprawdź, czy ten sam numer jest powtarzany i otrzymuje wyższy priorytet.
5. Użyj nagrania, na którym jedna klatka jest rozmyta lub zasłonięta, a sąsiednie są czytelne. Sprawdź, czy powtarzający się odczyt jest prezentowany wyżej.
6. Sprawdź pozycję blisko początku i końca nagrania – próbki poza zakresem powinny zostać pominięte, bez błędu.
7. Sprawdź nagranie z szybko przemieszczającym się pojazdem – oceń wpływ stałego zaznaczenia na wynik.
8. Sprawdź, że bez ręcznego potwierdzenia nie można zapisać zdarzenia.
9. Potwierdź numer, zapisz zdarzenie i sprawdź jego obecność na osi czasu oraz w bazie.
10. Uruchom OCR w trybie podglądu Dahua i potwierdź, że nadal działa rozpoznawanie pojedynczej klatki.
11. Sprawdź anulowanie operacji i ponowne uruchomienie OCR.

## Status

Zmiany zapisano na gałęzi `wersja-1.7`. Nie wykonano kompilacji w Visual Studio ani testów na rzeczywistych nagraniach. Jest to pierwsza część etapu 3. Po weryfikacji wieloklatkowego OCR kolejnym zadaniem w tym etapie będzie automatyczne wyszukiwanie kolejnych wystąpień potwierdzonego numeru w nagraniu, z uwzględnieniem ograniczeń wynikających z lokalizacji tablicy w obrazie.

Numer wydania aplikacji pozostaje 1.6.0 do zakończenia cyklu rozwojowego i testów.


## Korekta po testach – krótkie fragmenty tablic

Po testach dodano korektę rankingu wyników OCR:
- odrzucane są odczyty krótsze niż 4 znaki, aby fragmenty 2–3 znakowe nie trafiały na listę kandydatów;
- ranking ponownie stawia średnią pewność OCR przed liczbą klatek z takim samym odczytem;
- liczba zgodnych klatek i długość tekstu rozstrzygają dopiero przy podobnej pewności.

Celem jest zachowanie jakości pojedynczego odczytu i wykorzystanie wieloklatkowości jako dodatkowego potwierdzenia, a nie jako mechanizmu premiującego powtarzające się fragmenty.
