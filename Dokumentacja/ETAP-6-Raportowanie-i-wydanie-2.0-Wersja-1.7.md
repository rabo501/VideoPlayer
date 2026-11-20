# VideoPlayer 1.7 — etap 6: raportowanie i przygotowanie wydania 1.7

## Cel etapu

Przygotowanie aplikacji do kolejnego wydania przez rozwój raportowania, lokalne archiwum spraw, pełną regresję funkcji oraz aktualizację dokumentacji i paczki publikacyjnej.

Prace są prowadzone na gałęzi `wersja-1.7`. Wersja aplikacji zostaje ustawiona na `1.7.0` do czasu zakończenia implementacji i testów akceptacyjnych.

## Podetapy

### 6A — lokalne archiwum spraw

- Zapisywanie numeru, tytułu, miejsca i opisu sprawy w lokalnej bazie SQLite.
- Wczytywanie zapisanych danych sprawy do formularza raportu.
- Powiązanie jednej sprawy z wieloma nagraniami.
- Aktualizacja danych sprawy po ponownym zapisie z tym samym numerem (bez rozróżniania wielkości liter).
- Zachowanie oryginalnych plików bez kopiowania, przenoszenia lub modyfikowania.
- Przechowywanie wyłącznie metadanych i ścieżek do źródeł; brak wysyłania danych poza komputer.
- Usuwanie sprawy z potwierdzeniem; usuwane są wyłącznie rekord sprawy i jej powiązania, nigdy pliki nagrań, znaczniki ani pliki eksportów.

### 6B — raporty

- Uporządkowanie zawartości PDF i CSV.
- Spójne metadane sprawy w raportach.
- Czytelna prezentacja zdarzeń, zakresów czasowych, informacji o źródle i historii eksportu.
- Kontrola poprawności danych i bezpieczny zapis pliku wynikowego.
- Raport musi rozróżniać czas względny nagrania od czasu rzeczywistego rejestratora.

### 6C — testy regresyjne i publikacyjne (w trakcie)

- Czysta kompilacja Release x64 w Visual Studio.
- Weryfikacja kompletności folderu publikacyjnego: .NET, mpv, FFmpeg, Dahua SDK, Tesseract, tessdata i licencje.
- Testy odtwarzania lokalnego, eksportu, OCR, wykrywania ruchu, bazy SQLite oraz integracji Dahua.
- Test migracji i kopii bazy na kopii testowej danych.
- Test na komputerze docelowym bez środowiska programistycznego.
- Usunięcie danych testowych i prywatnych nagrań z paczki.

### 6D — dokumentacja i wydanie 1.7.0 (przygotowane; publikacja po testach 6C)

- Aktualizacja instrukcji użytkownika i wdrożeniowej.
- Aktualizacja informacji o wydaniu oraz listy kontrolnej publikacji.
- Ustalenie numeru 1.7.0 dopiero po zaliczeniu testów.
- Przygotowanie finalnego folderu publikacyjnego x64.

## Ograniczenia

- Wszystkie nagrania, dane spraw i raporty pozostają lokalnie.
- Archiwum spraw przechowuje ścieżki do plików, a nie ich kopie.
- Nie zmieniać oryginalnych nagrań.
- Nie zmieniać zaakceptowanego menu ani funkcji odtwarzania, OCR, detekcji ruchu i Dahua bez potrzeby wynikającej z testów.
- Nie zmieniać numeru wersji przed zakończeniem testów.
- Każdy podetap wdrażać oddzielnie i przekazywać do kompilacji/testów.

## Implementacja 6A — wykonane

Dodano `CaseRepository.cs` oraz tabele `Cases` i `CaseSources` w istniejącej bazie `VideoPlayer.db`.

W zakładce Zdarzenia dodano sekcję „Archiwum spraw”:
- lista spraw zapisanych lokalnie;
- przycisk „Zapisz / aktualizuj sprawę”;
- przycisk „Wczytaj dane sprawy”;
- status informujący o liczbie powiązanych nagrań.

Zapis sprawy wymaga numeru sprawy i otwartego, istniejącego nagrania. Ponowny zapis z tym samym numerem aktualizuje metadane i dopisuje bieżącą ścieżkę nagrania do powiązań. Nie kopiuje pliku źródłowego. Wczytanie sprawy przywraca metadane do formularza raportu. Kolejne nagrania można dołączać do tej samej sprawy przez otwarcie nagrania i ponowny zapis z tym samym numerem; gdy formularz numeru jest pusty, zapis wykorzysta zaznaczoną sprawę z listy.

Użytkownik potwierdził poprawne działanie Etapu 6A.

## Implementacja 6B — wykonane

Dodatkowo poprawiono obsługę raportowania dla spraw wielonagraniowych:
- raport CSV/PDF wybranej sprawy pobiera wszystkie ścieżki zapisane w `CaseSources`;
- dla każdego źródła pobierane są niezależnie znaczniki zdarzeń, historia eksportów i parametry techniczne;
- raport można wygenerować bez otwierania któregokolwiek nagrania;
- raport PDF zawiera wykaz wszystkich nagrań, oznacza niedostępne pliki i przypisuje zdarzenia oraz eksporty do właściwego źródła;
- CSV zawiera osobny wiersz identyfikujący każde nagranie, również gdy nie ma ono zdarzeń;
- usuwanie sprawy wymaga potwierdzenia i nie usuwa plików źródłowych ani danych analizy.


Rozbudowano raport CSV i PDF, zachowując lokalne przetwarzanie.

### CSV

Raport ma jeden wspólny układ kolumn dla dwóch typów wierszy: `Zdarzenie` i `Eksport`. Zawiera:
- numer, tytuł, miejsce i uwagi sprawy;
- ścieżkę źródłową;
- czas początku i końca zdarzenia oraz sekundy od początku nagrania;
- typ zdarzenia, opis, markę pojazdu i liczbę powiązanych zdarzeń ruchu;
- datę, typ i ścieżkę eksportu oraz szczegóły operacji;
- podstawowe parametry nagrania: kodeki, rozdzielczość, FPS, czas trwania i rozmiar pliku.

Dane sprawy są pobierane z aktualnego formularza raportu. Eksporty pochodzą z lokalnej historii danego pliku źródłowego. Plik CSV jest zapisywany w UTF-8 z BOM, a komórki są cytowane i zabezpieczane przed interpretacją jako formuły arkusza.

### PDF

Raport zawiera:
- dane sprawy i źródła;
- parametry techniczne i rozmiar nagrania;
- podsumowanie liczby zdarzeń według kategorii: tablice, ruch i ręczne;
- zakresy czasu zdarzeń, jeśli zdarzenie ma koniec;
- markę pojazdu i liczbę powiązanych zdarzeń ruchu;
- historię eksportów dla danego źródła;
- opcjonalne miniatury.

Czasy są jednoznacznie opisane jako względne względem początku pliku. Nie są przedstawiane jako czas zegara rejestratora.

### Testy akceptacyjne 6B

1. Wybierz sprawę zawierającą co najmniej dwa nagrania; nie otwieraj żadnego z nich.
2. Wygeneruj CSV i sprawdź, czy raport zawiera wiersz identyfikujący każde nagranie oraz zdarzenia z obu źródeł.
3. Wygeneruj PDF z miniaturami i bez miniatur; sprawdź, czy zdarzenia i miniatury są przypisane do właściwych plików.
4. Powtórz generowanie po otwarciu innego nagrania, aby potwierdzić, że zakres raportu nadal wynika z wybranej sprawy.
5. Sprawdź polskie znaki, znaki średnika i cudzysłowy w opisach oraz uwagach.
6. Sprawdź, czy CSV otwiera się prawidłowo w Excelu i ma jednakową liczbę kolumn we wszystkich rodzajach wierszy.
7. Sprawdź, czy zdarzenie ruchu z zakresem ma oba czasy, a znacznik punktowy ma pusty czas końca.
8. Sprawdź klasyfikację tablic, ruchu i zdarzeń ręcznych.
9. Sprawdź, czy historia eksportów jest przypisana do właściwego nagrania.
10. Sprawdź, czy PDF zachowuje czytelność na wielu stronach, a miniatury nie nachodzą na tekst i stopkę.
11. Sprawdź, że brak historii eksportów, niedostępny plik źródłowy lub brak danych technicznych nie przerywa generowania raportu.

## Testy akceptacyjne 6A

1. Otwórz istniejące nagranie.
2. Wpisz numer sprawy, tytuł, miejsce i uwagi.
3. Zapisz sprawę i sprawdź, czy pojawiła się na liście.
4. Zmień nagranie, pozostawiając ten sam numer sprawy, i ponownie zapisz.
5. Sprawdź, czy licznik powiązanych nagrań wzrósł, a metadane zostały zaktualizowane.
6. Wyczyść pola formularza, wybierz sprawę z listy i wczytaj dane.
7. Zamknij i ponownie uruchom program; sprawdź, czy archiwum nadal zawiera zapisane sprawy.
8. Sprawdź, że pliki źródłowe pozostały w swoich pierwotnych lokalizacjach i nie zostały zmodyfikowane.
9. Usuń sprawę z listy i potwierdź operację; sprawdź, że zniknęła z archiwum, ale nagrania, znaczniki i historia eksportów nadal istnieją.
10. Anuluj usuwanie innej sprawy i sprawdź, że pozostaje w archiwum.
11. Wykonaj kopię `VideoPlayer.db` przed testami na danych roboczych.

## Kryterium zakończenia

Etap 6A i 6B zostały wdrożone i zaakceptowane przez użytkownika. Etap 6C obejmuje testy regresyjne oraz publikacyjne na środowisku Windows.
