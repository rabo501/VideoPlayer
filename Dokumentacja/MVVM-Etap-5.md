# MVVM — etap 4: sesje podglądu i archiwum Dahua

## Zakres zmian

Etap 4 przenosi odpowiedzialność za cykl życia natywnych uchwytów NetSDK z okna głównego do warstwy serwisowej dostępnej przez `DahuaViewModel`.

### Podgląd na żywo

- `DahuaSessionService` rejestruje każdy uchwyt uruchomionego strumienia.
- `DahuaViewModel` udostępnia operacje uruchamiania, zatrzymywania pojedynczego strumienia i zatrzymywania wszystkich strumieni.
- `MainWindow.Dahua.cs` nadal tworzy kontrolki `PictureBox`, `Panel` i `WindowsFormsHost`, ponieważ są elementami widoku. Nie przekazuje ich do ViewModel; przekazuje wyłącznie uchwyt okna.
- Przełączenie urządzenia, zatrzymanie podglądu i rozłączenie kończą sesje przez ViewModel/serwis.

### Odtwarzanie archiwalne

- Serwis rejestruje uchwyty odtwarzania archiwalnego i odpowiada za ich zwalnianie.
- Operacje zmiany prędkości, pauzy/wznowienia i przewijania są wykonywane przez serwis, a ViewModel udostępnia je widokowi.
- Przy niepełnym powodzeniu zmiany prędkości serwis przywraca normalne tempo na wszystkich aktywnych kanałach.
- Przy niepełnym powodzeniu pauzy serwis wycofuje zmianę na kanałach, które ją przyjęły.
- Okno główne zachowuje stan prezentacyjny: wybrane kanały, pozycję suwaka, czas odtwarzania i przypisanie uchwytów do kafelków.

### Zamykanie i rozłączanie

`DahuaSessionService.Disconnect()` zatrzymuje najpierw wszystkie zarejestrowane strumienie i odtwarzania, a dopiero potem wylogowuje użytkownika z rejestratora. Dzięki temu zwalnianie uchwytów nie zależy wyłącznie od ścieżki zamykania okna.

## Granica odpowiedzialności

| Warstwa | Odpowiedzialność |
|---|---|
| `MainWindow` | Kontrolki, układ kafelków, gesty myszy, etykiety kanałów, suwak i timer prezentacji |
| `DahuaViewModel` | Interfejs operacji sesji dla widoku |
| `DahuaSessionService` | Natywne uchwyty NetSDK, start/stop strumieni i archiwum, sterowanie zsynchronizowanym odtwarzaniem |

Nie przenoszono kontrolek WPF/WinForms do ViewModel. Zachowano istniejący interfejs i mechanizm renderowania obrazu.

## Testy i weryfikacja

Dodano test bez sprzętu, który sprawdza bezpieczne zachowanie operacji zarządzania archiwum przy braku aktywnych uchwytów. Pełną weryfikację należy wykonać w Visual Studio 2022 na urządzeniu Dahua: podgląd 1/4/9/16 kanałów, zatrzymanie i ponowne uruchomienie, archiwum wielokanałowe, pauza, przewijanie, zmiana prędkości, przełączenie urządzenia i zamknięcie programu.


# MVVM — etap 5: sprawy, nagrania i raporty

## Zakres zmian

- `CaseArchiveViewModel` zarządza wyborem sprawy, jej metadanymi oraz uruchamianiem raportu dla sprawy.
- Dodano `CaseReportService`, który pobiera listę nagrań powiązanych ze sprawą, znaczniki zdarzeń, historię eksportów i dostępne parametry techniczne nagrań.
- `MainWindow.Review.cs` pozostaje odpowiedzialny za interakcje widoku (wybór nazwy pliku, potwierdzenia i prezentację postępu), ale nie pobiera już danych sprawy ani nie składa źródeł raportu.
- Generowanie raportu sprawy nie wymaga aktywnego pliku w odtwarzaczu. Źródła są pobierane z lokalnego archiwum SQLite na podstawie identyfikatora sprawy.
- Raport obejmuje wszystkie nagrania przypisane do sprawy, wraz ze znacznikami i historią eksportów każdego z nich. Brak pliku źródłowego lub błąd odczytu parametrów technicznych jednego nagrania nie blokuje raportu; raport oznacza niedostępny plik i kontynuuje pracę.
- Pliki źródłowe nie są kopiowane, modyfikowane ani przesyłane do sieci.

## Granica odpowiedzialności

| Warstwa | Odpowiedzialność |
|---|---|
| `MainWindow` | Dialog zapisu, potwierdzenia, zdarzenia UI i przekazanie postępu |
| `CaseArchiveViewModel` | Wybór sprawy, metadane, status oraz zlecenie raportu |
| `CaseReportService` | Odczyt sprawy, wszystkich powiązanych ścieżek, znaczników, eksportów i parametrów nagrań |
| `CaseRepository` / `HistoryService` | Lokalny zapis i odczyt danych SQLite |
| `ReportService` | Renderowanie CSV/PDF |

## Weryfikacja

Dodano test integracyjny, który tworzy sprawę z dwoma nagraniami i generuje CSV bez otwierania pliku w odtwarzaczu. Test sprawdza obecność obu ścieżek źródłowych i danych sprawy w raporcie. Pełny test PDF z miniaturami oraz testy na rzeczywistych plikach należy wykonać w Visual Studio 2022.
