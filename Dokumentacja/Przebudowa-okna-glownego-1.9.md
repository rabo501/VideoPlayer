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
