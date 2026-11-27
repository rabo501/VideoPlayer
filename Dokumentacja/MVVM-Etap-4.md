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
