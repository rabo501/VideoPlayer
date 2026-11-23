# MVVM – Etap 8: odczyt parametrów nagrania

## Cel
Przeniesienie obsługi asynchronicznego odczytu informacji o pliku z warstwy okna do `ExportViewModel`.

## Zmiany
- Dodano interfejs `IMediaInfoReader`, dzięki któremu ViewModel nie zależy bezpośrednio od konkretnej implementacji FFmpeg.
- `ExportViewModel` przejmuje uruchamianie odczytu parametrów, anulowanie poprzedniego żądania oraz obsługę błędów.
- Wynik jest stosowany wyłącznie wtedy, gdy nadal dotyczy aktualnie wybranego nagrania.
- Otworzenie kolejnego pliku i zamykanie aplikacji anuluje trwający odczyt.
- `MainWindow` przekazuje żądanie do ViewModelu i nie zarządza już tokenem anulowania odczytu informacji o pliku.
- Dodano test symulujący sytuację, w której wolniejszy odczyt poprzedniego nagrania kończy się po odczycie nowego pliku.

## Zachowanie i zgodność
- Nadal używany jest ten sam `MediaInfoService` i ten sam plik `ffmpeg/ffmpeg.exe`.
- Nie zmieniono formatu ani treści prezentowanych parametrów nagrania.
- Nie zmieniono eksportowania, raportowania ani układu interfejsu.
- Wstrzyknięta usługa jest współdzielona z generowaniem raportów spraw.

## Weryfikacja
1. Wykonaj Clean Solution i Rebuild Solution.
2. Uruchom wszystkie testy jednostkowe.
3. Otwórz kolejno dwa różne nagrania i sprawdź, czy parametry w panelu dotyczą zawsze ostatnio otwartego pliku.
4. Szybko przełącz kilka nagrań jedno po drugim i sprawdź, czy nie pojawiają się błędy anulowania.
5. Zamknij program podczas odczytu parametrów i sprawdź, czy zamykanie przebiega prawidłowo.
