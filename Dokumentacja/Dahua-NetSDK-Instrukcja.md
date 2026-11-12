# Moduł Dahua NetSDK dla VideoPlayer (WPF .NET 8.0 Windows)

## Wprowadzenie
W projekcie **VideoPlayer** zaimplementowano moduł obsługi kamer i rejestratorów IP Dahua, wykorzystujący oficjalną bibliotekę **Dahua NetSDK for Windows**.

Dotychczasowa funkcjonalność aplikacji (**odtwarzacz mpv, oś czasu, wycinanie fragmentów, zdarzenia, OCR tablic rejestracyjnych z Tesseract, eksport MP4 i raporty PDF/CSV**) pozostała w 100% nienaruszona, a załadowane nagrania z rejestratorów Dahua korzystają z pełnego pakietu tych narzędzi!

---

## Nowości w tej wersji:

### 1. Zapisywanie kamer i rejestratorów pod dowolną nazwą dla operatora
- **Koniec z automatycznymi nazwami IP:** Dotychczas urządzenia zapisywały się w uproszczeniu jako adresy IP. Teraz operator ma pełną swobodę nadawania dowolnych, zrozumiałych nazw (np. `Rejestrator Główny Magazyn`, `Kamera Obrotowa Brama Północna`, `Monitoring Biurowiec Piętro 1`).
- **Nowy panel „PARAMETRY WYBRANEGO URZĄDZENIA”:**
  - Pole *Nazwa urządzenia (widoczna dla operatora)* pozwala w każdej chwili zmienić etykietę.
  - Przyciski:
    - `💾 Zapisz nazwę / dane`: natychmiastowa aktualizacja wybranego urządzenia w `dahua_devices.json`.
    - `➕ Dodaj jako nowe`: dodanie nowego wpisu do listy z wpisaną nazwą i adresem IP.
    - `🗑 Usuń`: usunięcie urządzenia z listy.
- **Czytelna lista urządzeń:** Każda pozycja na liście operatora wyświetla wytłuszczoną, przyjazną nazwę oraz adres IP i port w podtytule (`Rejestrator NVR Dahua (8 kanałów) — 192.168.1.200:37777`).

### 2. Automatyczny i ręczny odczyt nazw kanałów oraz presetów PTZ bezpośrednio z kamery/NVR
- **Pobieranie nazw kanałów z urządzenia (`CLIENT_QueryChannelName` / `CLIENT_GetNewDevConfig`):**
  Zamiast wpisywać nazwy kamer ręcznie, program odpytuje rejestrator lub kamerę o nazwy zdefiniowane w jej pamięci/konfiguracji (np. `Brama główna`, `Parking VIP`, `Rampa rozładunkowa`).
- **Pobieranie presetów PTZ z kamery (`CLIENT_GetNewDevConfig("Ptz")`):**
  Program odczytuje listę presetów zdefiniowanych bezpośrednio w kamerze obrotowej PTZ (indeks presetu i nazwę).
- **Kiedy dane są pobierane?**
  1. **Automatycznie:** przy każdym pomyślnym połączeniu (`Connect_Click`).
  2. **Na żądanie operatora:** przycisk `🔄 Odczytaj z kamery / NVR` wymusza natychmiastowe odświeżenie nazw kanałów i pozycji PTZ z urządzenia i aktualizuje plik konfiguracyjny.

### 3. Wczytywanie i zarządzanie zapisanymi pozycjami PTZ (Presety)
- **Rozwijana lista zapisanych presetów:** Użytkownik ma do dyspozycji listę predefiniowanych pozycji z nazwami, np.:
  - `[P1] Brama wjazdowa (Szeroki kąt)`
  - `[P2] Szlaban / Tablice rej.`
  - `[P3] Rampa rozładunkowa`
  - `[P4] Parking główny`
- **Szybkie kafelki presetów:** Pod listą znajdują się przyciski `[P1: Brama]`, `[P2: Szlaban]`, itd.
- **Natychmiastowe przejście do presetu (`Goto Preset`):**
  Wywołanie `CLIENT_DHPTZControlEx2` z kodem komendy `POINT_MOVE_CONTROL` (`10` w Dahua NetSDK) natychmiast obraca kamerę PTZ do zapisanej pozycji.
- **Zapisywanie bieżącej pozycji kamery jako nowy preset:**
  Użytkownik ustawia kadr ręcznie (strzałkami i zoomem), wpisuje numer (np. 5) oraz czytelną nazwę (np. "Widok na magazyn"), a funkcja `POINT_SET_CONTROL` (`11`) trwale zapisuje pozycję w kamerze oraz w pliku konfiguracyjnym `dahua_devices.json`.
- **Usuwanie presetów:** Obsługa komendy `POINT_DEL_CONTROL` (`12`).

### 4. Wybór kanału kamery po zapisanej nazwie, a nie numerze
- Każdy kanał rejestratora i kamery posiada czytelną nazwę (np. `Brama główna`, `Parking VIP`, `Magazyn / Rampa`, `Wejście do biura`).
- **Rozwijane listy wyboru:** Zarówno w podglądzie Live (`ChannelComboBox`), jak i w oknie Archiwum (`ArchiveChannelBox`) pozycje wyświetlają się w formacie:
  `Brama główna (CH 1)`, `Parking VIP (CH 2)`, `Magazyn / Rampa (CH 3)`, itp.
- **Kafelki pod ekranem wideo:** Przyciski wyświetlają czytelną nazwę kamery oraz numer kanału:
  `[Brama główna / CH 1]`, `[Parking VIP / CH 2]`, `[Magazyn / CH 3]` itd.
- **Edytor nazwy kanału w locie:** Pole tekstowe *Edytuj nazwę bieżącego kanału* pozwala użytkownikowi w każdej chwili nadać lub zmienić nazwę kamery i zapisać ją jednym kliknięciem do `dahua_devices.json`.
- **Nakładka OSD:** Na obrazie wideo wyświetla się rzeczywista nazwa: np. `BRAMA GŁÓWNA (CH 1)`.

### 5. Wczytywanie nagrań wg daty i nazwy kanału do Głównego Odtwarzacza
- Zakładka **"Archiwum wg Daty (Pełny Odtwarzacz)"**.
- Pobiera wycinek nagrania z rejestratora za pomocą `CLIENT_DownloadByTimeEx` (oraz fallback RTSP przez FFmpeg) do folderu `nagrania_dahua/`.
- Automatycznie ładuje plik do głównego okna programu (`MainWindow.OpenDahuaStreamAsync`).
- **Pełny zestaw funkcji analitycznych:**
  - Oś czasu, suwak i dokładny czas trwania,
  - Klatkowanie strzałkami w przód i w tył (klatka po klatce),
  - Zaznaczanie fragmentów (I / O) i eksport MP4,
  - **Lokalne rozpoznawanie tablic rejestracyjnych OCR (Tesseract)** na dowolnej klatce nagrania,
  - Dodawanie zdarzeń i raporty PDF/CSV z miniaturami.

---

## Wymagane pliki DLL w katalogu aplikacji (.exe)
W folderze wyjściowym (`bin/x64/Debug/net8.0-windows/`) muszą znajdować się 64-bitowe biblioteki Dahua:
- `dhnetsdk.dll`
- `dhplay.dll`
- `avnetsdk.dll`
- `dhconfigsdk.dll`
