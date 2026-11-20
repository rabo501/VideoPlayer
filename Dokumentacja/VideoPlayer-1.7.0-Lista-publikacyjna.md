# VideoPlayer 1.7.0 — lista kontrolna publikacji

## Kompilacja
- [ ] Wersja projektu: 1.7.0.
- [ ] Konfiguracja Release, platforma x64.
- [ ] Czysta kompilacja w Visual Studio bez błędów (wykonać na stanowisku Windows).
- [ ] Publikacja i uruchomienie na komputerze docelowym.

## Zawartość paczki
- [ ] VideoPlayer.exe i zależności .NET.
- [ ] mpv\\mpv.exe oraz komplet plików mpv.
- [ ] ffmpeg\\ffmpeg.exe oraz wymagane biblioteki FFmpeg.
- [ ] lib\\Tesseract.dll.
- [ ] x64\\tesseract50.dll i x64\\leptonica-1.82.0.dll.
- [ ] tessdata\\eng.traineddata.
- [ ] Microsoft Visual C++ Redistributable 2015–2022 (x64) — wymaganie instalacyjne.
- [ ] scripts\\review.lua, VideoPlayer.ico i katalog licenses.

## Testy funkcjonalne
- [ ] Otwieranie, odtwarzanie, pauza, przewijanie i klatka wstecz/w przód.
- [ ] Zoom, przesuwanie obrazu i zapis klatki.
- [ ] OCR: zaznaczenie tablicy, korekta narożników, rozpoznanie, ręczna korekta i potwierdzenie.
- [ ] Detekcja ruchu: ROI, cały obraz, parametry, postęp i anulowanie.
- [ ] Zapis zakresów zdarzeń, migracja bazy, pasy osi czasu i podgląd obszaru ruchu.
- [ ] Eksport MP4, filmu bez fragmentu, CSV i PDF z miniaturami.
- [ ] Raport PDF wybranej sprawy z co najmniej dwoma nagraniami bez otwierania plików MP4.
- [ ] Raport CSV wybranej sprawy z co najmniej dwoma nagraniami bez otwierania plików MP4.
- [ ] Usuwanie sprawy z potwierdzeniem; weryfikacja, że pliki i dane źródłowe nie są kasowane.
- [ ] Integracja Dahua: podgląd live, wybór kanałów archiwum, synchronizacja, częściowa niedostępność kanału i eksport (jeśli jest używana w danym wdrożeniu).
- [ ] OCR wieloklatkowy oraz wyszukiwanie tablic z różnymi odstępami próbkowania.
- [ ] Wykrywanie ruchu w ROI i na całym obrazie, filtrowanie szumu, łączenie zdarzeń, postęp i anulowanie.
- [ ] Test kopii bazy i migracji na kopii testowej, bez używania jedynej kopii danych roboczych.

## Przekazanie
- [ ] Wykonać kopię VideoPlayer.db przed aktualizacją.
- [ ] Dołączyć instrukcję, informacje o wydaniu i checklistę 1.7.0.
- [ ] Potwierdzić numer 1.7.0 w metadanych zestawu, tytule okna i oknie Informacje.
- [ ] Usunąć z paczki dane testowe i prywatne nagrania.
