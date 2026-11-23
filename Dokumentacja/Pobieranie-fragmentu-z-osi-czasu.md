# Pobieranie fragmentu archiwum z osi czasu

## Zakres funkcji

W wersji 1.8 dodano możliwość zaznaczenia fragmentu archiwum NVR bezpośrednio na osi czasu w panelu Dahua.

1. Wybierz urządzenie, kanał i dzień.
2. Wyszukaj nagrania na rejestratorze.
3. Kliknij **Zaznacz fragment**.
4. Przeciągnij myszą po zielonym obszarze nagrania na osi czasu.
5. Program pokaże wybrany przedział i przepisze początek oraz koniec do pól czasu.
6. Kliknij **Pobierz zaznaczony**.
7. W oknie zapisu wybierz format i lokalizację.

Zaznaczenie musi mieścić się w całości w jednym ciągłym rekordzie archiwum. Zaznaczenie obejmujące przerwę pomiędzy nagraniami jest odrzucane. Kliknięcie osi poza trybem zaznaczania nadal służy do wskazania czasu/przewinięcia odtwarzania.

## Dostępne formaty

- **MP4** – domyślny format, konwertowany przez dołączony FFmpeg.
- **DAV** – oryginalny kontener pobierany z rejestratora przez Dahua NetSDK.

Zapisany zostaje tylko jeden plik wynikowy w formacie wybranym przez użytkownika. Przy wyborze MP4 NetSDK pobiera plik DAV do katalogu tymczasowego, FFmpeg konwertuje go do wskazanego MP4, a plik DAV jest usuwany w bloku `finally`, również po błędzie lub anulowaniu. Przy wyborze DAV plik jest zapisywany bezpośrednio pod wskazaną nazwą i nie powstaje dodatkowy MP4.

Istniejąca funkcja **Pobierz zakres do Odtwarzacza** nadal tworzy MP4 i automatycznie otwiera go w głównym odtwarzaczu.

## Postęp i anulowanie

- Pasek statusu osi archiwum pokazuje postęp pobierania, jeśli NetSDK udostępnia rozmiar całkowity.
- Przycisk **Anuluj pobieranie** zatrzymuje pobieranie lub konwersję.
- Nieukończony plik wynikowy jest usuwany po błędzie/anulowaniu.
- Pobieranie ma limit czasu 30 minut.

## Testy manualne po kompilacji

1. Wyszukaj nagrania na kanale, na którym są dostępne ciągłe fragmenty.
2. Włącz zaznaczanie i przeciągnij od wcześniejszego do późniejszego czasu; sprawdź wyświetlony przedział.
3. Powtórz zaznaczenie w przeciwnym kierunku.
4. Spróbuj zaznaczyć przedział obejmujący przerwę pomiędzy rekordami – program powinien odmówić pobrania.
5. Pobierz jako DAV i sprawdź, czy powstał tylko plik DAV.
6. Pobierz jako MP4 i sprawdź, czy powstał tylko plik MP4, a plik roboczy DAV został usunięty.
7. Anuluj pobieranie w trakcie i sprawdź, czy nie pozostał niekompletny plik.
8. Sprawdź, czy kliknięcie osi poza trybem zaznaczania nadal przewija odtwarzanie.
9. Sprawdź dotychczasowe wyszukiwanie, odtwarzanie wielokanałowe i przycisk **Pobierz zakres do Odtwarzacza**.

Testy sprzętowe z rejestratorem oraz kompilację rozwiązania należy wykonać lokalnie w Visual Studio na Windows.
