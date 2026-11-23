# MVVM — etap 6: zdarzenia, historia, wyszukiwanie i analiza ruchu

## Zakres

Etap 6 domyka przeniesienie logiki operacji związanych ze zdarzeniami nagrań do ViewModeli. Nie zmienia układu okien, kontrolek ani formatu lokalnej bazy SQLite.

### Znaczniki i zdarzenia

- `EventCenterViewModel` otrzymuje `HistoryService` przez `AttachHistoryService`.
- Wczytywanie i zapisywanie znaczników dla nagrania odbywa się przez `LoadMarkersForSource` i `SaveMarkersForSource`.
- Widok odpowiada za kontrolki, pozycję odtwarzania, rysowanie znaczników i prezentację błędów.
- Dodawanie, edycja, usuwanie, filtrowanie i korelacja zdarzeń pozostają w EventCenterViewModel / EventCenterService.

### Historia i eksporty

- `HistoryViewModel` udostępnia odczyt/zapis historii: wyszukiwanie tablic w bazie, zapis eksportu i odczyt historii eksportów.
- `MainWindow` nie wykonuje już bezpośrednich zapytań do `HistoryService`.
- Historia ostatnich plików i zrzutów nadal jest przechowywana lokalnie w tym samym pliku SQLite.

### Wyszukiwanie tablic

- `PlateSearchViewModel` nadal zarządza filtrami, wynikami, anulowaniem i stanem wyszukiwania.
- Dostawcą wyników jest metoda `HistoryViewModel.SearchMarkers`, a nie delegat wykonujący zapytanie SQLite w kodzie okna.
- Otwieranie pliku i przewijanie odtwarzacza pozostaje zadaniem widoku.

### Analiza ruchu

- `MotionDetectionViewModel` udostępnia uruchomienie analizy, ekstrakcję klatki podglądu i mapowanie wyników na znaczniki osi czasu.
- `MainWindow` zachowuje elementy specyficzne dla interfejsu: wybór obszaru ROI, prezentację postępu, komunikaty oraz odświeżenie osi czasu.
- Algorytm FFmpeg i obliczenia detekcji pozostają w `MotionDetectionService`.

## Granica odpowiedzialności

| Warstwa | Odpowiedzialność |
|---|---|
| `MainWindow` | Dialogi, kontrolki, nawigacja odtwarzacza, rysowanie osi czasu, komunikaty |
| `EventCenterViewModel` | Stan zdarzeń, filtrowanie, korelacja oraz zapis/odczyt znaczników przez serwis |
| `HistoryViewModel` | Historia plików, wyszukiwanie tablic i historia eksportów |
| `PlateSearchViewModel` | Kryteria, wyniki i anulowanie wyszukiwania tablic |
| `MotionDetectionViewModel` | Cykl analizy ruchu, postęp, mapowanie wyników i ekstrakcja podglądu |
| `HistoryService` | Lokalna baza SQLite |
| `MotionDetectionService` | Analiza klatek i detekcja ruchu |

## Prywatność i zgodność

Operacje historii, znaczników i raportowania pozostają lokalne. Etap nie dodaje komunikacji sieciowej ani przesyłania nagrań. Nie zmienia schematu bazy SQLite i nie wymaga migracji danych użytkownika.

## Testy

Dodano testy:
- zapis i ponowne wczytanie znaczników przez EventCenterViewModel,
- wyszukiwanie tablic i zapis/odczyt historii eksportu przez HistoryViewModel,
- mapowanie wyniku detekcji ruchu na znacznik osi czasu.

Pełną kompilację i testy WPF/FFmpeg należy wykonać w Visual Studio 2022 na komputerze docelowym.
