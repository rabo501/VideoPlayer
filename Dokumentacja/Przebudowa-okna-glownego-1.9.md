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
- pierwsza wersja nakładki WPF została zastąpiona nagłówkiem w osobnym wierszu nad hostem wideo, aby uniknąć ograniczenia nakładania elementów WPF na WindowsFormsHost (airspace);
- nagłówek zawiera oznaczenie MPV i nazwę odtwarzacza; jest widocznym elementem interfejsu, a nie nakładką na obraz;
- zachowanie PlayerViewportLayer, VideoHost i istniejącego hostowania mpv bez zmian;
- bez ingerencji w przełączanie warstw OCR/Dahua, ich nakładki, obsługę strumieni, skalowanie i logikę odtwarzania.

Wymagana jest kompilacja i kontrola w Windows: otwarcie lokalnego nagrania, sterowanie odtwarzaniem, zmiana skali obrazu oraz sprawdzenie, czy etykieta nie utrudnia obsługi obrazu.


## Siódmy zakres zmian — nagłówek panelu Dahua
- przebudowanie paska statusu połączenia na czytelny układ dwukolumnowy: osobny status SDK oraz komunikat o urządzeniu lub bieżącej operacji;
- wyraźniejsze rozdzielenie informacji technicznej od statusu pracy rejestratora/kamery;
- delikatne zwiększenie odstępów wokół podzakładek panelu Dahua;
- zachowanie nazw `DahuaSdkStatusBadge`, `DahuaStatusLabel`, `DahuaSubTabs`, bindingów i całej logiki połączenia;
- bez zmian w SDK, PTZ, presetach, podglądzie Live, archiwum i pobieraniu nagrań.

Wymagana jest kompilacja i kontrola w Windows: status SDK po uruchomieniu, połączenie/rozłączenie z NVR, odczyt danych z urządzenia oraz przełączanie podzakładek Live/PTZ i Archiwum NVR.


## Ósmy zakres zmian — formularz archiwum NVR
- zgrupowanie wyszukiwania pojedynczego nagrania w odrębnym panelu: wybór kanału oraz zakres dat i godzin;
- uporządkowanie etykiet początku i końca zakresu w jednym wierszu, przy zachowaniu osobnych pól daty i czasu;
- wyróżnienie głównego przycisku wyszukiwania oraz dodanie czytelnego nagłówka wyników;
- niewielkie zwiększenie wysokości tabeli wyników;
- zachowanie nazw kontrolek, bindingów, zdarzeń kalendarza, wyboru kanału, odtwarzania, pobierania i synchronizacji wielokanałowej;
- bez zmian w zapytaniach do NVR, formacie czasu, pobieraniu nagrań i logice archiwum.

Wymagana jest kompilacja i test w Windows: wybór kanału, dat i godzin, wyszukanie nagrań, zaznaczenie wyniku, odtworzenie oraz wczytanie nagrania do głównego odtwarzacza. Należy też sprawdzić osobno odtwarzanie synchroniczne wielu kanałów.


## Dziewiąty zakres zmian — konfigurowalny układ, pełny ekran i monitory
- dodanie zapisu ustawień interfejsu w profilu użytkownika Windows (LocalAppData): rozmiar i położenie okna, stan maksymalizacji, szerokość i widoczność panelu narzędzi, wybrana zakładka, prędkość odtwarzania i skala obrazu;
- odtwarzanie zapisanej geometrii z kontrolą, czy okno pozostaje na dostępnym ekranie; ustawienia nie są zapisywane w katalogu programu ani w bazie spraw;
- pełny ekran realizowany w tym samym oknie i na tym samym natywnym hoście wideo; ukrywane są panele narzędzi oraz dolne paski, a okno jest maksymalizowane bez przenoszenia hostów `WindowsFormsHost`/`PictureBox` między oknami;
- przycisk oraz pozycje menu do włączania i wyłączania pełnego ekranu; po wyjściu przywracana jest poprzednia widoczność panelu, pasków i stan okna;
- skróty: F11 — pełny ekran podglądu, Esc — powrót do zwykłego układu, Spacja — pauza/odtwarzanie, strzałki lewo/prawo — klatka, I/O — początek/koniec fragmentu, S — zrzut klatki, R — reset widoku, Ctrl+O — otwarcie pliku, Ctrl+D — tryb Dahua;
- bez zmian w komunikacji IPC z mpv, odtwarzaniu, synchronizacji suwaka, eksporcie, OCR i obsłudze SDK Dahua.

Wymagane testy Windows: zamknięcie i ponowne uruchomienie z zapamiętanym rozmiarem/położeniem; zmiana szerokości i ukrywanie panelu; odłączenie podglądu na jednym i dwóch monitorach; powrót klawiszem Esc; skróty podczas odtwarzania i podczas edycji pól tekstowych.


### Korekta dziewiątego zakresu — stabilność podglądu Live
- zrezygnowano z przenoszenia natywnych hostów `WindowsFormsHost`/mpv oraz `PictureBox` Dahua do osobnego okna;
- takie przepinanie uchwytów okiennych mogło zatrzymać odświeżanie strumienia Live po powrocie;
- pełny ekran działa teraz przez maksymalizację głównego okna i ukrycie panelu narzędzi oraz dolnych pasków, bez zmiany rodzica kontrolki renderującej obraz;
- zachowano ten sam host i kontekst SDK/MPV przez cały czas przełączania.


## Dziesiąty zakres — skalowanie Dahua Live
- wybór dopasowania, wypełnienia i powiększeń 125–200% działa również w głównym podglądzie Dahua Live;
- SDK renderuje bezpośrednio do uchwytu okna PictureBox, dlatego skalowanie realizowane jest przez zmianę rozmiaru i położenia tego samego PictureBox wewnątrz przycinanego panelu;
- uchwyt HWND przekazany do SDK pozostaje niezmienny, a zmiana skali nie restartuje strumienia;
- reset widoku przywraca dopasowanie również dla Dahua;
- bazowe proporcje obrazu przyjęto jako 16:9; tryb wypełnienia i większe powiększenia mogą przycinać krawędzie.

Wymagane testy: wszystkie tryby skali podczas Live, zmiana kanałów, przełączanie Player/Dahua, reset widoku oraz płynność obrazu. Należy ponownie sprawdzić dotychczasowe skalowanie mpv.


### Korekta skalowania — właściwe okna renderujące Dahua
Poprzednia implementacja skalowała tylko `_dahuaPrimaryPictureBox`, podczas gdy standardowy podgląd Live jest uruchamiany w dynamicznych slotach `DahuaMultiViewSlot`. SDK otrzymuje uchwyt `slot.PictureBox`, dlatego poprzednia zmiana nie wpływała na widoczny strumień. Skorygowano `SetDahuaPrimaryScale`: skala jest teraz stosowana bezpośrednio do wszystkich aktywnych slotów, a ich PictureBox jest powiększany i przesuwany wewnątrz panelu przycinającego. Reset ustawia powiększenie 1× i zeruje przesunięcie. Tryb wypełnienia uwzględnia proporcje 16:9.
