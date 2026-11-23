# VideoPlayer 1.9 — przebudowa głównego okna

## Cel
Nowa linia interfejsu głównego okna przy zachowaniu dotychczasowych funkcji i integracji.

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
Pierwsza iteracja układu. Wymaga kompilacji i oceny ergonomii przez użytkownika.
