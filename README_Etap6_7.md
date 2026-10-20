# VideoPlayer — Etap 6 i Etap 7

## Zmiany

### Etap 6 — usługi
- `MpvService.cs`: uruchamianie procesu mpv, IPC przez named pipe, wysyłanie poleceń i przekazywanie komunikatów.
- `FfmpegService.cs`: wspólna ścieżka do FFmpeg, konfiguracja procesów oraz odczyt czasu trwania i obecności audio.
- `MediaInfoService.cs`: odczyt kodeków, rozdzielczości, FPS, bitrate'u, czasu i rozmiaru nagrania.
- `HistoryService.cs`: historia ostatnich nagrań/zrzutów oraz trwały zapis znaczników zdarzeń.

`MainWindow` nadal koordynuje interfejs, a eksport zachowuje dotychczasową logikę postępu, anulowania i profili. Wspólne tworzenie procesów FFmpeg i wykrywanie właściwości mediów korzysta już z usług.

### Etap 7 — znaczniki zdarzeń
- dodawanie znacznika w aktualnej pozycji odtwarzania;
- wymagany opis zdarzenia;
- zmiana opisu, usuwanie i przejście do wybranego znacznika;
- dwuklik na pozycji listy lub kliknięcie pomarańczowego punktu na osi czasu przechodzi do czasu zdarzenia;
- znaczniki są zapisywane osobno dla każdego pliku w `%LOCALAPPDATA%\VideoPlayer\timeline-markers.json`.

Historia nagrań i zrzutów pozostaje w `%LOCALAPPDATA%\VideoPlayer\history.json`, w zgodnym formacie JSON.

## Wdrożenie
1. Rozpakuj źródła i otwórz `VideoPlayer.slnx` albo `VideoPlayer.csproj` w Visual Studio.
2. Upewnij się, że w katalogu projektu znajduje się `mpv\mpv.exe`.
3. Upewnij się, że w katalogu projektu znajduje się `ffmpeg\ffmpeg.exe` (oraz pozostałe pliki FFmpeg, jeśli używasz ich w swoim wdrożeniu).
4. Skompiluj projekt dla `net8.0-windows`.
5. Przetestuj otwarcie nagrania, pauzę/przewijanie, zapis klatki, eksport i anulowanie, a następnie dodawanie/edycję/usuwanie znaczników i ponowne otwarcie tego samego nagrania.

## Weryfikacja w tym środowisku
- Plik XAML przeszedł kontrolę poprawności XML.
- Sprawdzono, że nazwy obsługiwanych zdarzeń z XAML mają odpowiadające metody w kodzie.
- Sprawdzono bilans nawiasów klamrowych w plikach C#.
- **Nie wykonano kompilacji WPF**: w środowisku roboczym nie ma `dotnet`/MSBuild ani narzędzi Windows WPF. Dlatego proszę wykonać kompilację i test funkcjonalny w Visual Studio przed użyciem produkcyjnym.

## Dodatkowa funkcja: regulacja obszaru obrazu i skalowania

- Wysokość obszaru wideo można zmieniać przez przeciąganie poziomego separatora między obrazem a panelem sterowania.
- Lista „Skalowanie” w prawym górnym rogu podglądu udostępnia tryby: „Dopasuj cały obraz”, „Wypełnij okno”, 125%, 150% i 200%.
- „Dopasuj cały obraz” pokazuje pełną klatkę bez celowego przycinania; „Wypełnij okno” wypełnia obszar i może przyciąć krawędzie. Powiększenia 125–200% mogą również obciąć fragment obrazu poza obszarem podglądu.
- Rozmiar obszaru zmienia tylko układ aplikacji, a skalowanie jest ustawiane w mpv. Oryginalny plik wideo nie jest modyfikowany.
