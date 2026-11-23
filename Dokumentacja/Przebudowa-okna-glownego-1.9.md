# VideoPlayer 1.9 — przebudowa głównego okna

## Cel
Nowa linia interfejsu głównego okna przy zachowaniu dotychczasowych funkcji i integracji.

## Drugi zakres zmian — panel narzędzi
- rozdzielenie raportowania od bieżącego centrum zdarzeń i OCR do osobnej zakładki „Raporty”;
- przeniesienie archiwum spraw, danych sprawy, miniatur oraz generowania CSV/PDF do jednego miejsca;
- doprecyzowanie nazw i podpowiedzi zakładek: Zdarzenia / OCR, Eksport, Kamery Dahua, Pliki i sesja;
- dodanie przycisku przejścia bezpośrednio z zakładki zdarzeń do raportów;
- zachowanie nazw kontrolek, bindingów i handlerów; zakładka „Pliki i sesja” pozostała na indeksie 4, więc skróty Pomoc/Informacje nadal wskazują właściwe miejsce.

## Pierwszy zakres zmian
- większy, czytelniejszy obszar roboczy z zachowaniem podglądu mpv, OCR i Dahua;
- uporządkowany pasek górny z oznaczeniem aplikacji, menu i przełączaniem trybu;
- wygodniejszy panel narzędzi po prawej, z możliwością zmiany szerokości;
- wyraźniej wydzielona oś czasu, pasek transportu i status operacji;
- aktualizacja wersji aplikacji do 1.9.0.

## Zasady
- zachować istniejące nazwy kontrolek XAML i handlery zdarzeń;
- zachować lokalny odtwarzacz mpv, Dahua Live/Archiwum, pobieranie DAV/MP4, PTZ, OCR, analizę ruchu, zdarzenia, sprawy i raporty;
- nie zmieniać formatów danych ani mechanizmów odtwarzania w ramach samej przebudowy;
- każdą większą zmianę układu sprawdzać kompilacją i testami regresji w Windows.

## Status
Pierwsza iteracja głównego układu została skompilowana przez użytkownika. Druga iteracja porządkuje panel narzędzi; wymaga kompilacji i testów funkcjonalnych w Windows.


## Trzeci zakres zmian — pasek sterowania odtwarzaniem
- wydzielenie sterowania mpv w osobny, wizualnie odseparowany panel;
- pogrupowanie klatkowania, odtwarzania, zrzutu klatki i analizy ruchu;
- zastosowanie zawijania przycisków przy mniejszej szerokości obszaru roboczego, zamiast wymuszania jednego długiego wiersza;
- wyróżnienie wyboru prędkości odtwarzania jako osobnej grupy;
- zachowanie istniejących nazw kontrolek, bindingów i handlerów; nie zmieniono logiki odtwarzania ani synchronizacji osi czasu.

Ten zakres wymaga kompilacji i sprawdzenia układu w Windows, szczególnie przy szerokim i zwężonym oknie.


## Czwarty zakres zmian — górny pasek narzędzi i nawigacji
- uporządkowanie górnego paska w trzy czytelne strefy: identyfikacja aplikacji, menu główne oraz szybkie akcje i wybór trybu pracy;
- ujednolicenie tła, obramowań, odstępów i wysokości elementów z panelem sterowania odtwarzaniem;
- zachowanie wszystkich dotychczasowych nazw kontrolek, handlerów, skrótów klawiaturowych i poleceń menu;
- bez zmian w logice przełączania trybów, otwierania plików, OCR, widoku, sesji i panelu narzędzi.

Ten zakres wymaga kompilacji i sprawdzenia w Windows przy typowej oraz minimalnej szerokości okna. Należy zwrócić szczególną uwagę na to, czy prawa strefa szybkich akcji mieści się bez obcinania.


## Piąty zakres zmian — oś czasu i pasek statusu
- wizualne uporządkowanie dolnej osi czasu: wyraźniejszy nagłówek, czytelniejsze wskazanie aktualnej pozycji i czasu trwania;
- pozostawienie bez zmian suwaka `PositionSlider`, warstwy `TimelineOverlay`, uchwytów zaznaczenia oraz ich zdarzeń — nie zmieniano mechanizmu synchronizacji osi czasu;
- ujednolicenie paska statusu: osobne oznaczenie statusu, czytelniejsze informacje o operacji i postępie oraz wydzielony obszar paska postępu;
- zachowanie wszystkich nazw kontrolek, bindingów i komend anulowania.

Wymagana jest kompilacja i test w Windows: przewijanie suwaka, synchronizacja czasu, zaznaczanie fragmentu uchwytami, znaczniki zdarzeń oraz prezentacja postępu i anulowania eksportu.

## Szósty zakres zmian — lewy panel podglądu wideo
- uporządkowanie warstwy lokalnego odtwarzacza przez dodanie dyskretnej etykiety „PODGLĄD WIDEO” w górnym lewym narożniku;
- etykieta jest wyłącznie informacyjna i nie przechwytuje myszy ani klawiatury, więc nie blokuje sterowania osadzonym odtwarzaczem;
- zachowanie PlayerViewportLayer, VideoHost i istniejącego hostowania mpv bez zmian;
- bez ingerencji w przełączanie warstw OCR/Dahua, ich nakładki, obsługę strumieni, skalowanie i logikę odtwarzania.

Wymagana jest kompilacja i kontrola w Windows: otwarcie lokalnego nagrania, sterowanie odtwarzaniem, zmiana skali obrazu oraz sprawdzenie, czy etykieta nie utrudnia obsługi obrazu.
