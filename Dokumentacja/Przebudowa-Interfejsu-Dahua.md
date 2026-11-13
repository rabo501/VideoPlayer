# VideoPlayer 1.5 — przebudowa okna Dahua, podgląd wielokanałowy i archiwum

## Cel


Wersja dokumentacji: **1.5.0**. Wydanie obejmuje przebudowany interfejs operatora, osobne okno konfiguracji, podgląd wielokanałowy, wspólne sterowanie archiwum oraz automatyczne oznaczanie dostępności nagrań w kalendarzu.


Okno obsługi rejestratora zostało przebudowane w kierunku układu operatorskiego inspirowanego SmartPSS Lite. Konfiguracja urządzeń jest oddzielona od codziennej obsługi obrazu.

## Nowy układ


### Czytelność zaznaczenia i przycisków (1.5.0)

- Zaznaczone pozycje list urządzeń mają ciemnoniebieskie tło, biały tekst i jasnoniebieską ramkę.
- Stan wskazania kursorem ma odrębny kolor, a zaznaczenie pozostaje czytelne również po utracie fokusu.
- Ten sam styl zaznaczenia jest stosowany w oknie podglądu i w osobnym oknie konfiguracji.
- Przyciski kanałów zachowują zielone wyróżnienie aktywnego kanału; pozostałe przyciski korzystają z ciemnego tła i jasnego tekstu.


- Lewy panel: lista urządzeń, przycisk konfiguracji, lista kanałów z polami wyboru oraz wybór siatki 1/4/9/16.
- Środkowy panel: dynamiczna siatka obrazów. Każde pole ma własny uchwyt okna WinForms PictureBox i własny uchwyt strumienia NetSDK.
- Dolny panel środkowy: wspólna oś czasu archiwum, Start, Pauza/Wznów i Stop.
- Prawy panel: dotychczasowe narzędzia PTZ, presety, wyszukiwanie archiwum i eksport.

## Osobna konfiguracja

Okno `DahuaConfigurationWindow` służy do dodawania, edycji i usuwania urządzeń. Zapis nadal korzysta z `DahuaConfigService`; dane logowania są przechowywane przez Windows DPAPI CurrentUser. Nie zapisuje się haseł jawnie.

Plik konfiguracji nadal znajduje się w:
`%LOCALAPPDATA%\VideoPlayer\dahua_devices.json`

## Podgląd na żywo

1. Wybierz urządzenie.
2. Zaznacz kanały w lewym panelu.
3. Wybierz układ 1, 4, 9 albo 16.
4. Kliknij **Podgląd zaznaczonych**.

Program tworzy osobny uchwyt `CLIENT_RealPlayEx` dla każdego kanału. Liczba równoczesnych strumieni zależy od możliwości rejestratora, przepustowości sieci i wydajności komputera.

## Dostępność nagrań w kalendarzu

Po otwarciu kalendarza aplikacja sprawdza dostępność nagrań bez konieczności uruchamiania szczegółowego wyszukiwania. Dla aktualnie wyświetlanego miesiąca wykonuje zapytanie NetSDK dla każdego dnia i wybranego kanału. Zapytania są wykonywane sekwencyjnie, a wynik jest buforowany dla kombinacji urządzenie/kanał/miesiąc.

- Ciemniejsza zieleń oznacza dni, w których rejestrator zgłosił co najmniej jedno nagranie.
- Jaśniejsza zieleń oznacza dni potwierdzone przez ostatnie szczegółowe wyszukiwanie.
- Niebieski oznacza aktualnie wybraną datę.
- Zmiana kanału powoduje pobranie lub odtworzenie z pamięci dostępności dla nowego kanału.
- Zmiana miesiąca w kalendarzu uruchamia sprawdzenie kolejnego miesiąca.

Sprawdzenie wymaga połączenia z urządzeniem. Jeżeli nie ma aktywnego połączenia, aplikacja próbuje połączyć się automatycznie. Bieżący i przyszłe dni nie są odpytywane. Wyniki są buforowane w pamięci procesu do zamknięcia okna.

## Odtwarzanie archiwum

1. W prawym panelu otwórz zakładkę archiwum.
2. Ustaw początek i koniec zakresu.
3. Zaznacz kanały w lewym panelu.
4. Kliknij **Start** pod siatką obrazu.

Dla każdego kanału uruchamiane jest `CLIENT_PlayBackByTimeEx` z tym samym zakresem czasu. Pauza/wznowienie używa `CLIENT_PausePlayBack`, a przewijanie wspólnej osi czasu używa `CLIENT_SeekPlayBack` dla każdego aktywnego uchwytu.

Oś czasu w tej wersji jest prowadzona zegarem aplikacji od chwili uruchomienia odtwarzania i pozycją ostatniego przewinięcia. Nie jest jeszcze kalibrowana przez odczyt OSD z rejestratora; przy buforowaniu lub utracie pakietów może więc nieznacznie odbiegać od faktycznej pozycji wideo.

## Testy odbiorcze w Visual Studio 2022

Konfiguracja: **Release | x64**, .NET 8 Windows.

1. Wykonaj Clean Solution, a następnie Rebuild Solution.
2. Otwórz okno Dahua i sprawdź, czy lista urządzeń oraz kanałów jest widoczna.
3. Otwórz konfigurację, zmień nazwę istniejącego urządzenia, zapisz i zamknij. Sprawdź, czy lista w oknie głównym została odświeżona.
4. Wybierz układ 1 i uruchom jeden kanał.
5. Wybierz układ 4, zaznacz 2–4 kanały i uruchom podgląd.
6. Powtórz próbę dla 9 i 16 kanałów, jeśli rejestrator i komputer zapewniają wystarczające zasoby.
7. Wybierz ten sam zakres archiwum dla 2 kanałów i kliknij Start.
8. Sprawdź pauzę/wznowienie, przewijanie suwaka i zatrzymanie. Obraz powinien przejść na zbliżony wspólny czas na wszystkich kanałach.
9. Sprawdź odtwarzanie jednego zaznaczonego rekordu z tabeli — powinien zostać wybrany tylko jego kanał.
10. Przetestuj zmianę urządzenia i rozłączenie podczas podglądu oraz archiwum.

## Ograniczenia / do potwierdzenia na sprzęcie

- Nie każdy rejestrator obsługuje równoczesne odtwarzanie 9 lub 16 kanałów. Zacznij od 2 i 4.
- W tej wersji synchronizacja startuje wszystkie kanały z identycznym czasem, a pauza i seek są wysyłane do każdego uchwytu. Rzeczywista synchronizacja klatek zależy od buforowania urządzenia i sieci.
- Oś czasu jest przybliżana lokalnym zegarem aplikacji, a nie pobierana z OSD rejestratora.
- Weryfikacja kompilacji i test fizyczny z NVR muszą zostać wykonane w Visual Studio na komputerze użytkownika. Nie deklaruje się ich jako wykonanych w ramach tej zmiany.

## Informacje o wydaniu 1.5.0

- Ujednolicono wygląd i rozmiar zakładek „Strumień na żywo” oraz „Archiwum”.
- Dodano automatyczne sprawdzanie dostępności nagrań w aktualnym miesiącu i oznaczanie dat w kalendarzu przed szczegółowym wyszukiwaniem.
- Rozróżniono daty zgłoszone przez rejestrator, daty potwierdzone wyszukiwaniem oraz wybraną datę.
- Poprawiono kontrast zaznaczonych pozycji list urządzeń w oknie podglądu i konfiguracji.
- Zastosowano jawny szablon aktywnej zakładki, aby biały tekst był zawsze prezentowany na niebieskim tle; dodano też widoczny stan fokusu klawiatury i przycisku domyślnego.
- Ujednolicono stany najechania, wciśnięcia i fokusu przycisków w oknie konfiguracji.
- Wersję aplikacji ustawiono na 1.5.0 (Assembly/File version 1.5.0.0).

