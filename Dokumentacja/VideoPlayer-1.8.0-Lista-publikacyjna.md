# VideoPlayer 1.8.0 — lista kontrolna publikacji

## Kompilacja i testy automatyczne
- [ ] Wersja projektu: 1.8.0 (`AssemblyVersion` / `FileVersion`: `1.8.0.0`, `InformationalVersion`: `1.8.0`).
- [ ] Konfiguracja Release, platforma x64.
- [ ] Testy jednostkowe xUnit zakończone powodzeniem (`dotnet test VideoPlayer.slnx -c Release`).
- [ ] Czysta kompilacja w Visual Studio bez błędów (wykonać na stanowisku Windows).
- [ ] Publikacja i uruchomienie na komputerze docelowym.

## Zawartość paczki
- [ ] VideoPlayer.exe i zależności .NET (`CommunityToolkit.Mvvm`, `Microsoft.Data.Sqlite`, `PDFsharp-WPF`, `Tesseract`).
- [ ] mpv\\mpv.exe oraz komplet plików mpv.
- [ ] ffmpeg\\ffmpeg.exe oraz wymagane biblioteki FFmpeg.
- [ ] lib\\Tesseract.dll (lub odpowiednik z pakietu NuGet w katalogu uruchomieniowym).
- [ ] x64\\tesseract50.dll i x64\\leptonica-1.82.0.dll.
- [ ] tessdata\\eng.traineddata.
- [ ] Microsoft Visual C++ Redistributable 2015–2022 (x64) — wymaganie instalacyjne.
- [ ] scripts\\review.lua, VideoPlayer.ico i katalog licenses.

## Testy funkcjonalne
- [ ] Otwieranie, odtwarzanie, pauza, przewijanie, historia ostatnich plików/zrzutów i klatka wstecz/w przód.
- [ ] Zoom, przesuwanie obrazu i zapis klatki.
- [ ] OCR: zaznaczenie tablicy, korekta narożników, rozpoznanie (współdzielona sesja), ręczna korekta i potwierdzenie.
- [ ] Wyszukiwanie tablic w całym nagraniu (`ffmpeg -> stdout` i różne odstępy próbkowania, postęp i anulowanie).
- [ ] Detekcja ruchu: ROI, cały obraz, parametry, postęp i anulowanie.
- [ ] Zapis zakresów zdarzeń, migracja bazy, pasy osi czasu i podgląd obszaru ruchu.
- [ ] Eksport MP4, filmu bez fragmentu, eksport seryjny, CSV i PDF z miniaturami.
- [ ] Raport PDF i CSV wybranej sprawy z co najmniej dwoma nagraniami bez otwierania plików MP4.
- [ ] Usuwanie sprawy z potwierdzeniem; weryfikacja, że pliki i dane źródłowe nie są kasowane.
- [ ] Integracja Dahua: logowanie, podgląd live, wybór kanałów archiwum, synchronizacja, częściowa niedostępność kanału, automatyczne łączenie przy wyszukiwaniu/odtwarzaniu i eksport DAV -> MP4.
- [ ] Test kopii bazy i migracji na kopii testowej, bez używania jedynej kopii danych roboczych.

## Przekazanie
- [ ] Wykonać kopię VideoPlayer.db przed aktualizacją.
- [ ] Dołączyć instrukcję, informacje o wydaniu i checklistę 1.8.0.
- [ ] Potwierdzić numer 1.8.0 w metadanych zestawu, tytule okna i oknie Informacje.
- [ ] Usunąć z paczki dane testowe i prywatne nagrania.
