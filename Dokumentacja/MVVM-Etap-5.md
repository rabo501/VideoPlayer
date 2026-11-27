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
