# VideoPlayer 1.7 — etap 5: analiza wielokanałowa Dahua

## Cel

Rozbudowa istniejącej obsługi kilku kanałów Dahua tak, aby operator mógł analizować ten sam przedział czasu z wielu kamer, porównywać zdarzenia i przygotować materiał dowodowy z wybranych kanałów.

Prace prowadzone są na gałęzi `wersja-1.7`. Numer wersji aplikacji pozostaje `1.6.0` do czasu zakończenia rozwoju i testów.

## Stan zastany

W kodzie istnieją już:
- podgląd na żywo 1/4/9/16 kanałów;
- wybór kanałów do podglądu;
- odtwarzanie archiwum Dahua na wielu wybranych kanałach przez `CLIENT_PlayBackByTimeEx`;
- wspólne przyciski start/pauza/stop, suwak czasu i wybór prędkości;
- wspólny przedział czasu pobierany z pól daty i godziny;
- pobieranie pojedynczego kanału DAV i konwersja do MP4 przez FFmpeg.

Etap 5 nie powinien dublować tych funkcji. Powinien je wzmocnić i uzupełnić.

## Podetapy

### 5A — niezawodność synchronizacji archiwum

Zakres:
- kontrola, które kanały faktycznie rozpoczęły odtwarzanie;
- jednoznaczna informacja o kanałach niedostępnych lub bez nagrania;
- bezpieczne zatrzymanie wszystkich uruchomionych uchwytów po błędzie;
- wspólne sterowanie pauzą, wznowieniem, zatrzymaniem, zmianą prędkości i pozycją;
- kontrola stanu odtwarzania po zakończeniu zakresu i po utracie połączenia;
- zachowanie synchronizacji logicznej względem wspólnego czasu rejestratora.

Ważne: wywołania NetSDK dla poszczególnych kanałów są osobnymi operacjami. Interfejs powinien raportować rzeczywisty wynik dla każdego kanału i nie może sugerować, że wszystkie kanały działają, jeśli uruchomiła się tylko część.

### 5B — wspólne zdarzenia z wielu kanałów

Zakres:
- wspólna lista zdarzeń dla aktualnie analizowanego urządzenia i zakresu czasu;
- identyfikacja urządzenia, kanału i nazwy kamery przy każdym zdarzeniu;
- filtrowanie według kanału, typu zdarzenia i przedziału czasu;
- przejście do odpowiedniego kanału i czasu po dwukliku;
- możliwość porównania zdarzeń z kilku kamer bez kopiowania ani scalania oryginalnych rekordów.

Zdarzenia pozostają przypisane do źródłowego nagrania/kanału. Powiązanie zdarzeń czasowych ma charakter informacyjny i nie stanowi automatycznego potwierdzenia, że dotyczą tego samego obiektu.

### 5C — eksport materiału z wielu kanałów

Zakres:
- wybór kilku kanałów i wspólnego przedziału czasu;
- pobieranie nagrań z każdego wybranego kanału;
- konwersja DAV do MP4 przez istniejący FFmpeg;
- postęp i status osobno dla każdego kanału oraz łączny postęp operacji;
- możliwość kontynuowania pozostałych kanałów po błędzie jednego pobrania;
- raport końcowy zawierający kanały zakończone powodzeniem i kanały z błędem;
- jednoznaczne nazwy plików z nazwą urządzenia, kanałem i zakresem czasu.

Pliki pozostają lokalnie na komputerze, w dotychczasowym katalogu nagrań Dahua:
`%LOCALAPPDATA%\\VideoPlayer\\nagrania_dahua\\`.

## Ograniczenia i zasady

- Bez wysyłania nagrań poza komputer.
- Bez zmiany istniejącego menu i zaakceptowanego interfejsu użytkownika.
- Bez zmiany formatu bazy SQLite w ramach samej synchronizacji.
- Nie zmieniać algorytmów OCR ani detekcji ruchu.
- Nie zakładać, że wszystkie kanały mają nagranie w całym wskazanym zakresie.
- Nie raportować synchronizacji jako pełnej, jeśli któryś z wybranych kanałów nie uruchomił odtwarzania.
- Nie zmieniać numeru wersji aplikacji przed zakończeniem testów.

## Kolejność realizacji

1. 5A — poprawność i odporność istniejącego zsynchronizowanego odtwarzania.
2. 5B — wspólna lista zdarzeń wielokanałowych.
3. 5C — wielokanałowe pobieranie i eksport.

Każdy podetap powinien być dostarczony oddzielnie, aby można go było skompilować i przetestować przed przejściem dalej.

## Testy akceptacyjne

### 5A
1. Uruchom odtwarzanie archiwum na 1, 4 i większej liczbie kanałów.
2. Sprawdź, czy status pokazuje liczbę kanałów uruchomionych i nieuruchomionych.
3. Sprawdź pauzę, wznowienie, stop, zmianę prędkości i przesuwanie suwaka.
4. Sprawdź zachowanie, gdy jeden z kanałów nie ma nagrania.
5. Sprawdź zmianę urządzenia i rozłączenie podczas odtwarzania.
6. Sprawdź, czy po błędzie wszystkie uchwyty są zwalniane.

### 5B
1. Wyświetl zdarzenia z co najmniej dwóch kanałów.
2. Filtruj według kanału i typu.
3. Sprawdź kolejność chronologiczną.
4. Dwuklikiem przejdź do właściwego kanału i czasu.
5. Potwierdź, że oryginalne zdarzenia nie są duplikowane ani usuwane.

### 5C
1. Pobierz zakres z dwóch kanałów.
2. Sprawdź nazwy i kompletność plików MP4.
3. Sprawdź postęp pojedynczych kanałów i całej operacji.
4. Zasymuluj błąd jednego kanału i sprawdź, czy pozostałe są nadal obsługiwane.
5. Sprawdź raport końcowy i możliwość odtworzenia każdego poprawnie pobranego pliku w głównym odtwarzaczu.

## Status

Dokument opisuje zakres Etapu 5. Istniejące funkcje wielokanałowe są punktem wyjścia, a nie potwierdzeniem zaliczenia testów Etapu 5. Implementacja i testy podetapów 5A–5C pozostają do wykonania.
