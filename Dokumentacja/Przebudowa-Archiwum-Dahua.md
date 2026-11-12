# Przebudowa integracji Dahua – konfiguracja, połączenie i archiwum

## 1. Model urządzenia

Konfiguracja rozdziela dwa niezależne pojęcia:

- `Name` – nazwa opisowa operatora, używana wyłącznie w interfejsie.
- `IpAddress` – adres IP lub nazwa hosta, używana przez NetSDK do połączenia.
- `Port`, `Username`, `Password` – parametry logowania.

Kod sieciowy pobiera adres wyłącznie z `ConnectionAddress`. Nie wolno przekazywać `Name` do `CLIENT_Login`, RTSP ani innych funkcji sieciowych.

## 2. Migracja starszej konfiguracji

Konfiguracja jest przechowywana w:

`%LOCALAPPDATA%\VideoPlayer\dahua_devices.json`

Przy pierwszym uruchomieniu nowa wersja kopiuje plik `dahua_devices.json` z katalogu programu do profilu użytkownika. Oryginał pozostaje jako kopia bezpieczeństwa.

Migracja rozpoznaje alternatywne nazwy pól adresu (`IpAddress`, `IPAddress`, `IP`, `Ip`, `Address`, `Host`, `HostAddress`). Jeśli stary plik zawierał adres wyłącznie w polu `Name` albo etykietę w rodzaju `Rejestrator (192.168.1.20:37777)`, adres i ewentualny port są odzyskiwane, a czytelny prefiks nazwy jest zachowywany.

Uszkodzony plik nie jest zastępowany konfiguracją demonstracyjną. Błąd jest pokazywany operatorowi, a plik źródłowy pozostaje nienaruszony. Zapis jest atomowy i tworzy kopię `.bak`.

## 3. Połączenie

Przed logowaniem program weryfikuje:
- czy adres hosta/IP nie jest pusty;
- czy host ma poprawny format;
- czy port jest liczbą 1–65535.

Po zmianie adresu lub danych logowania aktywna sesja jest rozłączana. Przełączenie urządzenia również zamyka poprzednią sesję. Identyfikator połączonego urządzenia jest kontrolowany przy wyszukiwaniu, odtwarzaniu i pobieraniu archiwum, aby nie użyć uchwytu poprzedniego rejestratora.

## 4. Archiwum

1. Wybierz urządzenie i kanał.
2. Ustaw początek i koniec zakresu.
3. Wybierz **Wyszukaj nagrania na rejestratorze**.
4. Program wywołuje `CLIENT_QueryRecordFile` i prezentuje zwrócone pliki w tabeli.
5. **Odtwórz zaznaczone** uruchamia natywne `CLIENT_PlayBackByTimeEx` w oknie Dahua.
6. **Wczytaj zaznaczone do głównego odtwarzacza** pobiera wybrany przedział przez `CLIENT_DownloadByTimeEx`, zapisuje plik źródłowy DAV, konwertuje go lokalnym FFmpeg do MP4 i otwiera MP4 w głównym odtwarzaczu.

Wyniki pobierania są zapisywane w:

`%LOCALAPPDATA%\VideoPlayer\nagrania_dahua\`

Nie jest używany niepotwierdzony adres RTSP `/cam/playback` jako automatyczny fallback. Jeśli NetSDK nie potrafi pobrać nagrania, program zgłasza błąd zamiast tworzyć pozornie poprawny plik.

## 5. Ważne uwagi techniczne

- `CLIENT_QueryRecordFile` używa bufora natywnego; rozmiar jest liczony przez `Marshal.SizeOf<NET_RECORDFILE_INFO>()`, a liczba rekordów ograniczana do rozmiaru bufora.
- Struktura `NET_RECORDFILE_INFO` jest deklarowana z układem natywnym, polem nazwy pliku o rozmiarze 124 bajtów i polami czasu.
- `CLIENT_PlayBackByTimeEx` ma pełną liczbę argumentów natywnego API. Nie wolno skracać deklaracji P/Invoke – błędna liczba argumentów może uszkodzić stos/ABI i zakończyć proces.
- Callback pobierania Dahua sygnalizuje koniec transferu wartością -1 w polu pobranych danych (w typie unsigned widoczne jako `uint.MaxValue`).
- Plik DAV jest źródłem pobranym z rejestratora. MP4 jest kontenerem wynikowym utworzonym przez FFmpeg. Najpierw wykonywany jest szybki remux z zachowaniem obrazu; jeśli kodek nie pasuje do MP4, program ponawia konwersję obrazu do H.264.

## 6. Testy wymagane przed wdrożeniem

Kompilacja i testy z fizycznym urządzeniem są wymagane. W tym środowisku nie wykonano kompilacji WPF ani połączenia z rejestratorem.

| Test | Oczekiwany wynik |
|---|---|
| Migracja pliku z adresem w `IpAddress` | Zachowane IP i nazwa |
| Migracja starego pliku z IP w `Name` | Odzyskane IP; nazwa operatora ustawiona oddzielnie |
| Zapis nazwy operatora bez zmiany IP | Połączenie nadal kieruje na to samo IP |
| Zapis zmienionego IP/portu | Poprzednia sesja rozłączona; kolejne połączenie na nowy endpoint |
| Zmiana urządzenia na liście | Brak użycia uchwytu poprzedniego urządzenia |
| Wyszukiwanie plików archiwum | Tabela pokazuje nagrania z wybranego kanału i czasu |
| Odtwarzanie zaznaczonego | Obraz archiwum w oknie Dahua |
| Eksport zaznaczonego | Plik DAV, poprawna konwersja MP4 i odtworzenie w MPV |
| Brak nagrania / timeout | Czytelny błąd; brak fałszywego sukcesu |

## 7. Procedura testu w Visual Studio

1. Pobierz gałąź `dahua-archive-rebuild`.
2. Otwórz `VideoPlayer.slnx` w Visual Studio 2022.
3. Ustaw konfigurację x64.
4. Zbuduj rozwiązanie.
5. Upewnij się, że 64-bitowe biblioteki Dahua znajdują się w katalogu uruchomieniowym zgodnie z instrukcją SDK.
6. Uruchom program i sprawdź najpierw samo logowanie oraz Live.
7. Wyszukaj nagrania na znanym kanale i krótkim zakresie (np. 2–3 minuty).
8. Odtwórz wybrany rekord bez pobierania.
9. Wczytaj ten sam rekord do głównego MPV i sprawdź MP4.
10. Dopiero po tych testach scal gałąź z `dahua-SDK`.

