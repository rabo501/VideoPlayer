# VideoPlayer 1.8.0 — informacje o wydaniu

## Najważniejsze zmiany w wersji 1.8.0

- **Przebudowa architektury na MVVM (`CommunityToolkit.Mvvm`)**: wydzielono `MainViewModel` oraz wyspecjalizowane ViewModele (`PlateOcrViewModel`, `DahuaViewModel`, `EventCenterViewModel`, `MotionDetectionViewModel`, `ExportViewModel`, `PlateSearchViewModel`, `CaseArchiveViewModel`, `HistoryViewModel`), odciążając warstwę code-behind `MainWindow.*.cs`.
- **Wydzielenie niezależnych serwisów**: utworzono `DahuaSessionService`, `VideoPlateSearchService`, `EventCenterService` oraz `ExportWorkspaceService`, umożliwiając czyste testy bez zależności od WPF.
- **Przyspieszenie lokalnego OCR i wyszukiwania tablic w nagraniu**:
  - jedna współdzielona instancja `TesseractEngine` na całą sesję rozpoznawania/wyszukiwania zamiast inicjalizacji dla każdej klatki;
  - przekazywanie surowego bufora pikseli BGRA bez zapisu setek plików tymczasowych JPG i podwójnej kompresji PNG w pamięci;
  - potok `ffmpeg -> stdout` z równoległym prostowaniem perspektywy i kanałem producent–konsument (`System.Threading.Channels`).
- **Równoległe generowanie miniatur w raportach PDF** (`Parallel.ForEachAsync` z zachowaniem kolejności wpisów i anulowaniem).
- **Poprawki asynchroniczności w module Dahua**: prawidłowe oczekiwanie na automatyczne logowanie (`await DahuaConnectAsync()`) przed wyszukiwaniem archiwum, startem zsynchronizowanego odtwarzania oraz podglądu wielokanałowego.
- **Zestaw testów jednostkowych xUnit (`VideoPlayer.Tests`)**: testy dla serwisów, OCR, detekcji ruchu, centrum zdarzeń, wyszukiwania tablic, eksportu, historii i archiwum spraw.

## Zachowane funkcje z wcześniejszych wydań

- Ujednolicone okno główne do pracy z nagraniami, zdarzeniami, analizą i podglądem kamer.
- Lokalny OCR tablic rejestracyjnych z korektą perspektywy, analizą wielu klatek i wyszukiwaniem numeru w nagraniu.
- Wykrywanie ruchu z wyborem ROI, filtrowaniem szumu, łączeniem krótkich przerw i prezentacją zakresów na osi czasu.
- Wielokanałowy podgląd, presety PTZ oraz zsynchronizowane odtwarzanie archiwum Dahua.
- Lokalne archiwum spraw z możliwością powiązania wielu nagrań, aktualizacji i usuwania spraw oraz raporty PDF/CSV.
- Stabilizacja komunikacji IPC mpv i zabezpieczenie migracji bazy SQLite kopią zapasową.

## Wersja i środowisko

- Wersja aplikacji: **1.8.0** (linia **1.8**).
- Windows x64, .NET 8 Desktop Runtime.
- Microsoft Visual C++ Redistributable 2015–2022 (x64).
- mpv i FFmpeg wraz z plikami towarzyszącymi.
- Biblioteki Dahua SDK w katalogu `lib`.
- Tesseract, biblioteki natywne x64 oraz `tessdata\eng.traineddata`.

## Ważne informacje

Wszystkie nagrania, dane spraw, znaczniki, eksporty i raporty pozostają lokalnie. Archiwum spraw przechowuje ścieżki do nagrań, nie kopiuje ich ani nie modyfikuje. Raporty opisują czasy względne od początku pliku; nie należy ich utożsamiać z czasem zegara rejestratora.

Wyniki OCR i wykrywania ruchu wymagają weryfikacji przez operatora.

## Status publikacji

Kod i dokumentacja oznaczają wersję **1.8.0**. Przed dystrybucją wymagane są: uruchomienie testów (`dotnet test VideoPlayer.slnx -c Release`), czysta kompilacja Release x64, kompletna publikacja, testy na komputerze docelowym oraz sprawdzenie migracji kopii bazy danych.