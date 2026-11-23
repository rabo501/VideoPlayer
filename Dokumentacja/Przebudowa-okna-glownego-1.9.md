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
