# VideoPlayer – etap 1 stabilizacji (gałąź wersja-1.7)

## Zakres zmian

### 1. Komunikacja z mpv
- `SendRequestAsync(requestId, ...)` przekazuje teraz podany identyfikator do IPC mpv zamiast ignorować go i generować nowy.
- Identyfikatory muszą być dodatnie.
- Próba użycia identyfikatora, który już oczekuje na odpowiedź, kończy się czytelnym wyjątkiem zamiast nadpisania oczekującego żądania.
- Generator automatycznych identyfikatorów jest przesuwany za jawnie podany identyfikator, aby uniknąć kolizji.

### 2. Ochrona bazy SQLite przed migracją
- Przed migracją istniejącej bazy sprawdzane są wymagane kolumny tabeli `TimelineMarkers`.
- Jeśli migracja jest potrzebna, tworzona jest kopia w katalogu `backups` obok bazy.
- Kopia wykonywana jest przez mechanizm SQLite Backup API. Nie jest to zwykłe kopiowanie pliku `.db`, więc uwzględnia zatwierdzone dane z dziennika WAL.
- Kopia nie jest tworzona przy każdym uruchomieniu ani wtedy, gdy schemat jest już aktualny.
- Jeśli wykonanie kopii się nie powiedzie, inicjalizacja i migracja nie są kontynuowane.

## Ważne informacje
- Nie zmieniono wyglądu interfejsu ani menu.
- Nie zmieniono formatu nagrań, algorytmów OCR ani detekcji ruchu.
- Nie zmieniono ustawień Dahua SDK.
- Numer wydania aplikacji pozostaje 1.6.0 na czas prac rozwojowych. Nazwa gałęzi `wersja-1.7` oznacza linię rozwojową, a nie gotowe wydanie 1.7.
- Zmiany nie były kompilowane w Visual Studio ani testowane na urządzeniu Dahua w ramach tej aktualizacji.

## Lista testów regresyjnych

### A. Kompilacja i uruchomienie
- [ ] Otworzyć rozwiązanie w Visual Studio 2022.
- [ ] Skompilować konfigurację Debug x64.
- [ ] Skompilować konfigurację Release x64.
- [ ] Uruchomić aplikację i sprawdzić, czy mpv startuje bez komunikatu o błędzie.
- [ ] Otworzyć kilka formatów nagrań używanych w codziennej pracy.

### B. Odtwarzanie i IPC mpv
- [ ] Play/Pause, przewijanie, klatka poprzednia/następna.
- [ ] Zmiana prędkości odtwarzania.
- [ ] Zrzut ekranu.
- [ ] Wielokrotne otwieranie różnych nagrań.
- [ ] Zamknięcie aplikacji podczas odtwarzania.
- [ ] Sprawdzić, czy po utracie procesu mpv aplikacja pokazuje czytelny stan rozłączenia.

### C. Baza danych i migracja
- [ ] Uruchomić program na kopii starszej bazy, która nie ma wszystkich kolumn `TimelineMarkers`.
- [ ] Potwierdzić utworzenie kopii w `backups`.
- [ ] Potwierdzić, że po migracji zachowały się wcześniejsze znaczniki, zakresy i dane OCR.
- [ ] Uruchomić program ponownie i sprawdzić, że nie powstaje kolejna kopia bez potrzeby.
- [ ] Dodać znacznik, zamknąć program, uruchomić ponownie i sprawdzić zapis.
- [ ] Sprawdzić działanie na katalogu, w którym użytkownik ma prawo zapisu.

### D. FFmpeg i eksport
- [ ] Eksport pojedynczego fragmentu z ponownym kodowaniem.
- [ ] Eksport z kopiowaniem strumieni.
- [ ] Usunięcie fragmentu z nagrania.
- [ ] Anulowanie eksportu.
- [ ] Sprawdzić, że anulowanie nie pozostawia niekompletnego pliku pod docelową nazwą.

### E. Dahua
- [ ] Logowanie do rejestratora.
- [ ] Podgląd na żywo.
- [ ] Odtwarzanie archiwum.
- [ ] Zatrzymanie odtwarzania i rozłączenie.
- [ ] Zamknięcie aplikacji przy aktywnym połączeniu.

### F. OCR i detekcja ruchu
- [ ] OCR na wybranym fragmencie obrazu.
- [ ] Potwierdzanie i zapis rozpoznanej tablicy.
- [ ] Wykrywanie ruchu na całym obrazie.
- [ ] Wykrywanie ruchu w wybranym ROI.
- [ ] Podgląd prostokąta wykrytego ruchu.
- [ ] Ponowne otwarcie nagrania i sprawdzenie zapisanych znaczników.

## Kryterium zakończenia etapu
Etap 1 uznajemy za zakończony po kompilacji Debug/Release x64 i przejściu testów A–F. W razie problemów należy zgłosić dokładny komunikat błędu oraz wskazać punkt listy testowej.
