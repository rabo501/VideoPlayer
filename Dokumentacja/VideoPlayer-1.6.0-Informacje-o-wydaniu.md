# VideoPlayer 1.7.0 — informacje o wydaniu

## Najważniejsze zmiany

- Lokalny OCR tablic rejestracyjnych z zaznaczaniem obszaru, korektą czterech narożników i potwierdzeniem wyniku.
- Wykrywanie ruchu z wyborem prostokątnego obszaru analizy (ROI) albo całej klatki.
- Regulacja czułości, minimalnego udziału zmian i minimalnego czasu zdarzenia.
- Zapisywanie początku i końca zdarzenia oraz zakresów na osi czasu.
- Wyznaczanie obszaru zmian i podgląd klatki z zielonym prostokątem.
- Rozszerzenie bazy SQLite z zachowaniem istniejących znaczników przez migracje.

## Wymagania

- Windows x64 i .NET 8 Desktop Runtime.
- Microsoft Visual C++ Redistributable 2015–2022 (x64).
- mpv i FFmpeg wraz z plikami towarzyszącymi.
- Tesseract.dll, x64\\tesseract50.dll, x64\\leptonica-1.82.0.dll oraz tessdata\\eng.traineddata.
- Uruchamianie z katalogu, w którym użytkownik ma prawo zapisu; baza VideoPlayer.db znajduje się obok aplikacji.

Wykrywanie ruchu jest analizą zmian jasności, a nie klasyfikacją obiektów. Wyniki wymagają weryfikacji. OCR również przedstawia propozycję odczytu, którą należy potwierdzić na obrazie.

Przed publikacją wykonaj kompilację i testy na docelowym Windows x64. Szczegóły zawiera VideoPlayer-1.7.0-Dokumentacja.md.
