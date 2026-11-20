# Etap 3B — wyszukiwanie tablicy w całym nagraniu

## Cel

Umożliwienie wyszukania potwierdzonego numeru tablicy w całym lokalnym nagraniu, bez zapisywania dokumentów ani obrazów poza komputerem. Analiza wykorzystuje FFmpeg i istniejący lokalny silnik Tesseract OCR.

## Sposób działania

1. Otwórz lokalne nagranie.
2. Uruchom moduł OCR i zaznacz cztery narożniki tablicy w obrazie.
3. Rozpoznaj tablicę albo wpisz numer ręcznie.
4. Sprawdź numer i zaznacz potwierdzenie zgodności z obrazem.
5. Wybierz odstęp próbkowania (1, 2, 5 lub 10 sekund).
6. Uruchom „Szukaj w nagraniu”.
7. Dwuklik na wyniku przenosi odtwarzacz do początku znalezionego przedziału.

Program wyodrębnia klatki z nagrania za pomocą jednego procesu FFmpeg, prostuje wskazany obszar perspektywicznie i porównuje wyniki OCR z potwierdzonym numerem. Sąsiednie trafienia są łączone w przedziały czasowe. Dla ochrony czasu i zasobów liczba próbek jest ograniczona do 1200; przy długich nagraniach odstęp jest automatycznie zwiększany. Można anulować analizę.

## Ważne ograniczenie tej części etapu

W tej wersji analizowany jest **ten sam obszar obrazu**, który operator zaznaczył na klatce referencyjnej. Rozwiązanie jest przeznaczone przede wszystkim do nagrań ze statycznej kamery, gdy pojazdy przejeżdżają przez wybrany fragment kadru (np. pas ruchu lub wjazd).

Jeśli pojazd porusza się poza zaznaczonym obszarem, zostanie pominięty. Etap nie wykonuje jeszcze automatycznego wykrywania tablic w dowolnym miejscu kadru ani śledzenia pojazdu. To będzie osobny kolejny podetap, wymagający detektora obszarów tablic i mechanizmu śledzenia.

Wyszukiwanie jest lokalne. Nie wysyła nagrania ani wycinków do Internetu. Wyniki OCR są propozycjami analitycznymi, a nie dowodem identyfikacji — operator powinien sprawdzić wskazane klatki.

## Testy do wykonania

### A. Kompilacja
- [ ] Otworzyć gałąź `wersja-1.7` w Visual Studio 2022.
- [ ] Skompilować konfigurację Debug x64.
- [ ] Skompilować konfigurację Release x64.

### B. Wyszukiwanie podstawowe
- [ ] Otworzyć lokalne nagranie ze statycznej kamery.
- [ ] Zaznaczyć obszar, przez który przejeżdża pojazd.
- [ ] Rozpoznać tablicę, sprawdzić numer i zaznaczyć potwierdzenie.
- [ ] Uruchomić wyszukiwanie z odstępem 5 sekund.
- [ ] Sprawdzić, czy program pokazuje postęp i przedziały czasowe.
- [ ] Dwuklikiem otworzyć znaleziony przedział i zweryfikować klatkę.

### C. Dokładność i próbkowanie
- [ ] Powtórzyć wyszukiwanie z odstępem 1 lub 2 sekundy.
- [ ] Sprawdzić, czy krótkie przejazdy nie są pomijane przy zbyt dużym odstępie.
- [ ] Sprawdzić nagranie, w którym podobne tablice pojawiają się w tym samym obszarze.
- [ ] Sprawdzić zachowanie, gdy tablica jest zasłonięta lub nieczytelna.

### D. Anulowanie i stabilność
- [ ] Anulować analizę podczas przygotowywania klatek przez FFmpeg.
- [ ] Anulować analizę podczas OCR.
- [ ] Sprawdzić, czy katalog tymczasowy jest usuwany po zakończeniu i anulowaniu.
- [ ] Po zakończeniu wyszukiwania ponownie wykonać zwykły OCR i odtworzyć nagranie.

## Zakres zmian

- `MainWindow.PlateOcr.cs` — przygotowanie próbek FFmpeg, prostowanie zaznaczonego obszaru, OCR, grupowanie trafień, postęp i anulowanie.
- `MainWindow.xaml` — panel wyszukiwania, odstęp próbkowania i lista wyników.
- `PlateVideoSearchHit.cs` — model przedziału czasowego wyniku.
