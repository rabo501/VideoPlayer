# VideoPlayer 1.7.0 — informacje o wydaniu

## Najważniejsze zmiany

- Ujednolicone okno główne do pracy z nagraniami, zdarzeniami, analizą i podglądem kamer.
- Lokalny OCR tablic rejestracyjnych z korektą perspektywy, analizą wielu klatek i wyszukiwaniem numeru w nagraniu.
- Wykrywanie ruchu z wyborem ROI, filtrowaniem szumu, łączeniem krótkich przerw i prezentacją zakresów na osi czasu.
- Wielokanałowy podgląd i zsynchronizowane odtwarzanie archiwum Dahua.
- Lokalne archiwum spraw z możliwością powiązania wielu nagrań, aktualizacji i usuwania spraw.
- Raporty PDF i CSV obejmujące wiele nagrań, zdarzenia, parametry źródeł i historię eksportów.
- Naprawa aktualizacji postępu podczas generowania raportu PDF sprawy bez otwierania pliku MP4.
- Stabilizacja komunikacji IPC mpv i zabezpieczenie migracji bazy SQLite kopią zapasową.

## Wersja i środowisko

- Wersja aplikacji: **1.7.0**.
- Windows x64, .NET 8 Desktop Runtime.
- Microsoft Visual C++ Redistributable 2015–2022 (x64).
- mpv i FFmpeg wraz z plikami towarzyszącymi.
- Biblioteki Dahua SDK w katalogu `lib`.
- Tesseract, biblioteki natywne x64 oraz `tessdata\eng.traineddata`.

## Ważne informacje

Wszystkie nagrania, dane spraw, znaczniki, eksporty i raporty pozostają lokalnie. Archiwum spraw przechowuje ścieżki do nagrań, nie kopiuje ich ani nie modyfikuje. Raporty opisują czasy względne od początku pliku; nie należy ich utożsamiać z czasem zegara rejestratora.

Wyniki OCR i wykrywania ruchu wymagają weryfikacji przez operatora.

## Status publikacji

Kod i dokumentacja oznaczają wersję 1.7.0. Przed dystrybucją wymagane są: czysta kompilacja Release x64, kompletna publikacja, testy na komputerze docelowym oraz sprawdzenie migracji kopii bazy danych.