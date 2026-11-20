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

## Postęp implementacji — pierwszy krok 5A

W `MainWindow.Dahua.cs` rozszerzono raportowanie wyniku uruchamiania zsynchronizowanego odtwarzania:
- program zbiera numery kanałów, dla których NetSDK nie zwrócił uchwytu odtwarzania;
- przy częściowym uruchomieniu status pokazuje liczbę uruchomionych kanałów oraz numery kanałów niedostępnych;
- status nie opisuje już wyniku częściowego jako pełnego uruchomienia wszystkich kanałów;
- gdy nie uruchomi się żaden kanał, dotychczasowa ścieżka błędu zatrzymuje operację.

To jest pierwszy, niewielki krok 5A. Nie oznacza jeszcze zakończenia testów synchronizacji ani całego podetapu 5A.

## Interfejs wyboru kanałów archiwum — implementacja

Dodano przycisk **Wybierz kanały** bezpośrednio przy sterowaniu archiwum w głównym panelu.

- Okno pokazuje kanały wybranego urządzenia Dahua wraz z nazwą i numerem.
- Zaznaczenie kanałów archiwum jest przechowywane niezależnie od checkboxów konfiguracji podglądu na żywo.
- Okno pokazuje bieżącą liczbę wybranych kanałów i limit wynikający z wybranego układu 1/4/9/16.
- Po przekroczeniu limitu pozostałe checkboxy są blokowane.
- Przycisk **Zastosuj** wymaga zaznaczenia co najmniej jednego kanału.
- Anulowanie nie zmienia poprzedniego wyboru.
- Przycisk **Start** używa wyboru z okna archiwum. Jeżeli liczba zaznaczonych kanałów przekracza limit układu, program wyświetla ostrzeżenie zamiast po cichu obcinać listę.

### Testy interfejsu do wykonania

1. Otwórz **Wybierz kanały** i zaznacz dwa lub więcej kanałów.
2. Zastosuj wybór i uruchom archiwum.
3. Zmień zaznaczenie kanałów podglądu na żywo w konfiguracji i sprawdź, czy wybór archiwum pozostaje bez zmian.
4. Zmień układ na mniejszy niż liczba wybranych kanałów i sprawdź komunikat po naciśnięciu **Start**.
5. Otwórz okno ponownie i sprawdź, czy poprzedni wybór został zachowany.
6. Anuluj zmianę i sprawdź, czy poprzedni wybór nadal obowiązuje.

## Doprecyzowanie wyboru kanału — interfejs

Rozdzielono dwa niezależne zastosowania wyboru kanału:

- **Pojedynczy kanał — wyszukiwanie / pobieranie** w zakładce „Archiwum NVR” służy do wyszukiwania nagrań na liście oraz pobrania jednego nagrania do głównego odtwarzacza.
- **Kanały do odtwarzania równoczesnego** w głównym oknie określają zestaw kamer, których archiwum jest odtwarzane jednocześnie.

Pod przyciskami sterowania odtwarzaniem znajduje się teraz osobny panel z aktualną listą kanałów wielokanałowych. Przycisk otwierający wybór ma nazwę **Wybór kanałów archiwum…**. Przy pojedynczej liście kanałów dodano wyjaśnienie, że nie zmienia ona zestawu odtwarzanego równocześnie.

Przy zmianie urządzenia poprzedni wybór wielokanałowy jest czyszczony, aby numery kanałów z jednego rejestratora nie zostały omyłkowo użyte dla innego.

### Testy czytelności i działania

1. W zakładce „Archiwum NVR” wybierz pojedynczy kanał i wyszukaj nagrania.
2. W głównym oknie wybierz kilka kanałów przez **Wybór kanałów archiwum…**.
3. Potwierdź, że panel „Kanały do odtwarzania równoczesnego” pokazuje nazwy i numery wybranych kamer.
4. Zmień pojedynczy kanał wyszukiwania/pobierania i sprawdź, że lista wielokanałowa się nie zmienia.
5. Zmień urządzenie i sprawdź, że poprzedni wybór wielokanałowy nie przechodzi na nowe urządzenie.

## Przebudowa obsługi archiwum — wybór kanałów i układu w jednym miejscu

Wybór kanałów i układu do odtwarzania równoczesnego został przeniesiony do zakładki **Archiwum NVR**. Nie zależy już od układu ustawionego w zakładce „Podgląd & PTZ”.

Nowy przebieg pracy:

1. Użytkownik wybiera urządzenie oraz zakres daty i czasu.
2. W sekcji „Odtwarzanie archiwum — wiele kanałów” wybiera układ: 1, 4, 9 lub 16 kanałów.
3. Zaznacza konkretne kanały bezpośrednio na liście w tej samej sekcji.
4. Widzi podsumowanie wybranych nazw i numerów kanałów.
5. Uruchamia odtwarzanie przyciskiem „Odtwarzaj wybrane kanały od wskazanego czasu”.

Dolny pasek głównego okna pozostaje miejscem do sterowania już uruchomionym odtwarzaniem (pauza/wznów, stop, prędkość). Nie ma tam już osobnego wyboru kanałów ani drugiego przycisku Start.

Pojedynczy wybór kanału w sekcji wyszukiwania/pobierania nadal służy do wyszukania pojedynczego nagrania na liście i pobrania go do głównego odtwarzacza. Nie steruje wyborem wielokanałowym.

### Rozdzielenie układów

- Układ w „Podgląd & PTZ” steruje wyłącznie podglądem na żywo.
- Układ w „Archiwum NVR” steruje wyłącznie liczbą pól i limitem kanałów odtwarzania archiwum.
- Zmiana układu archiwum nie zmienia układu live i odwrotnie.

### Testy

1. Ustaw w „Podgląd & PTZ” układ 1 kamery.
2. Przejdź do „Archiwum NVR”, ustaw 4 kanały i zaznacz CH 1–4.
3. Uruchom odtwarzanie. Powinny pojawić się cztery pola archiwum.
4. Zmień układ live na 9 kamer i sprawdź, że archiwum pozostaje w układzie 4 kanałów.
5. Zmień układ archiwum na 1 kanał i sprawdź, że lista pozwala zaznaczyć maksymalnie jeden kanał.
6. Wyszukaj nagrania pojedynczego kanału i sprawdź, że zmiana tej listy nie zmienia zaznaczenia wielokanałowego.
