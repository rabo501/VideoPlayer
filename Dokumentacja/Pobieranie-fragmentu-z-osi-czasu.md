# Pobieranie fragmentu archiwum z osi czasu

## Obsługa osi czasu

Po poprawkach obsługa nie wymaga przełączania trybu zaznaczania:

- **Kliknięcie zielonego paska** ustawia czas. Jeśli archiwum jest już odtwarzane, program przewija wszystkie aktywne kanały do wskazanego miejsca.
- **Przeciągnięcie po zielonym pasku** zaznacza przedział. Zaznaczenie musi mieścić się w jednym ciągłym rekordzie nagrania.
- **Odtwórz od wskazanego** rozpoczyna odtwarzanie od klikniętego czasu do końca ciągłego nagrania albo odtwarza dokładnie zaznaczony przedział.
- **Pobierz zaznaczony** otwiera wybór pliku i formatu DAV/MP4.
- Kółko myszy przybliża/o oddala oś czasu, prawy przycisk resetuje powiększenie.

Zielone pasy oznaczają dostępne nagrania. Klikanie i przeciąganie poza zielonym pasem jest ignorowane z komunikatem w pasku statusu.

## Pobieranie

1. Wybierz urządzenie, kanał i dzień w panelu Dahua.
2. Wyszukaj nagrania dla wybranego kanału i dnia.
3. Na osi czasu kliknij i przeciągnij po zielonym pasku, aby zaznaczyć interesujący przedział.
4. Kliknij **Pobierz zaznaczony**.
5. W oknie zapisu wybierz format i lokalizację.

Zapisany zostaje tylko jeden plik wynikowy w formacie wybranym przez użytkownika:
- **MP4** – plik DAV jest pobierany do katalogu tymczasowego, konwertowany przez FFmpeg, a następnie usuwany.
- **DAV** – oryginalny kontener archiwum zapisywany bezpośrednio pod wybraną nazwą.

Nie powstaje druga kopia wynikowa. Przy konwersji MP4 plik źródłowy DAV i wynikowy MP4 istnieją równocześnie tylko na czas konwersji.

## Postęp i anulowanie

- Postęp pobierania jest wyświetlany w statusie archiwum, jeśli SDK udostępnia rozmiar całkowity.
- Przycisk **Anuluj pobieranie** przerywa pobieranie lub konwersję.
- Nieukończony plik wynikowy jest usuwany po błędzie/anulowaniu.
- Pobieranie ma limit czasu 30 minut.

## Testy manualne

1. Wyszukaj nagrania na kanale, na którym są dostępne ciągłe fragmenty.
2. Kliknij zielony pasek. Sprawdź ustawienie czasu.
3. Uruchom odtwarzanie przyciskiem **Odtwórz od wskazanego**.
4. Podczas odtwarzania kliknij inne miejsce zielonego paska i sprawdź przewijanie.
5. Przeciągnij po zielonym pasku. Sprawdź podświetlenie i dokładne czasy.
6. Spróbuj przeciągnąć przez przerwę między nagraniami – zaznaczenie powinno zostać odrzucone.
7. Pobierz zaznaczenie jako DAV, a następnie jako MP4.
8. Anuluj pobieranie i sprawdź, czy nie pozostał niekompletny plik.
9. Sprawdź dotychczasowe wyszukiwanie, odtwarzanie wielokanałowe i pobieranie do głównego odtwarzacza.

Kompilację WPF oraz testy sprzętowe należy wykonać lokalnie w Visual Studio na Windows z rejestratorem BCS/Dahua.


## Synchronizacja suwaka odtwarzania z osią godzin Dahua
- Suwak i oś godzin korzystają z jednego punktu odniesienia: pełnej daty i godziny, a nie samej godziny względem różnych dat.
- Zmiana dnia na osi aktualizuje zakres suwaka; przejście odtwarzania przez północ aktualizuje dzień osi.
- Pozycja odtwarzania jest dodatkowo oznaczana pionową linią i etykietą czasu na pasku godzin.
- Podczas przeciągania suwaka etykieta i znacznik na osi pokazują podgląd wybranej pozycji; zatwierdzenie nadal wykonuje zsynchronizowane przewinięcie kanałów.
