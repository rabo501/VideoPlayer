# Dokumentacja techniczna integracji Dahua / BCS-NVR

**Stan dokumentu:** aktualizacja dla VideoPlayer 1.7.0  
**Gałąź kodu:** `wersja-1.7` (bazuje na `studio-net`)  
**Środowisko:** Windows, WPF, .NET, proces x64, Visual Studio 2022

Dokument opisuje aktualny model integracji z rejestratorami Dahua/BCS: konfigurację urządzeń, połączenie NetSDK, podgląd, wyszukiwanie i odtwarzanie archiwum, pobieranie nagrań, obsługę wielu kanałów oraz PTZ. Jest to dokument techniczny dla utrzymania i rozwoju kodu, a nie instrukcja użytkownika krok po kroku.

> **Ważne:** nie pobierać ani nie scalać historycznej gałęzi `dahua-archive-rebuild`. Aktualnym punktem odniesienia jest gałąź `wersja-1.7`. Nazwy kontrolek i szczegóły interfejsu należy weryfikować w aktualnych plikach XAML i kodzie tej gałęzi.

## 1. Model konfiguracji urządzenia

Konfiguracja rozdziela następujące pojęcia:

- `Name` – przyjazna nazwa urządzenia, wyświetlana operatorowi.
- `IpAddress` – adres IP lub nazwa hosta używana do połączenia.
- `Port` – port NetSDK, standardowo 37777.
- `Username`, `Password` – dane logowania.
- Port HTTP – osobny parametr używany przez operacje HTTP CGI, standardowo 80.

Kod sieciowy musi korzystać z adresu połączenia (`ConnectionAddress`), a nie z przyjaznej nazwy `Name`. Nazwy urządzeń nie wolno przekazywać do `CLIENT_Login`, RTSP ani innych funkcji sieciowych.

### Lokalizacja i migracja konfiguracji

Konfiguracja jest przechowywana w:

`%LOCALAPPDATA%\\VideoPlayer\\dahua_devices.json`

Przy pierwszym uruchomieniu nowa wersja kopiuje plik `dahua_devices.json` z katalogu programu do profilu użytkownika. Oryginał pozostaje jako kopia bezpieczeństwa.

Migracja rozpoznaje alternatywne nazwy pól adresu (`IpAddress`, `IPAddress`, `IP`, `Ip`, `Address`, `Host`, `HostAddress`). Jeśli stary plik zawierał adres wyłącznie w polu `Name` albo etykietę w rodzaju `Rejestrator (192.168.1.20:37777)`, adres i ewentualny port są odzyskiwane, a czytelny prefiks nazwy zachowywany.

Uszkodzony plik nie może być po cichu zastępowany konfiguracją demonstracyjną. Błąd powinien być pokazany operatorowi, a plik źródłowy pozostawiony bez zmian. Zapis konfiguracji jest atomowy i tworzy kopię `.bak`.

Dane logowania podlegają zasadom opisanym w osobnym dokumencie `Ochrona-danych-logowania-Dahua.md`. Nie należy umieszczać haseł w logach, raportach, dokumentacji ani kodzie źródłowym.

## 2. Połączenie NetSDK i cykl życia sesji

Przed logowaniem program weryfikuje:
- czy adres hosta/IP nie jest pusty;
- czy host ma poprawny format;
- czy port jest liczbą z zakresu 1–65535.

Po zmianie adresu lub danych logowania aktywna sesja jest rozłączana. Przełączenie urządzenia również zamyka poprzednią sesję. Identyfikator zalogowanego urządzenia musi być sprawdzany przy wyszukiwaniu, odtwarzaniu i pobieraniu archiwum, aby nie użyć uchwytu poprzedniego rejestratora.

Należy zachować prawidłową kolejność inicjalizacji i zamykania SDK oraz zwalniać uchwyty i callbacki zgodnie z kontraktem NetSDK. Nie wolno wykonywać operacji na uchwycie po rozłączeniu lub zmianie urządzenia.

## 3. Podgląd na żywo

Aplikacja obsługuje podgląd wielu kanałów w układach 1, 4, 9 i 16 okien. Operator wybiera kanały, które mają być wyświetlane. Układ podglądu Live i układ odtwarzania archiwum są od siebie niezależne.

Dostępność kanału zależy od konfiguracji i możliwości rejestratora. Nie należy zakładać, że numer kanału widoczny w urządzeniu zawsze odpowiada pozycji na liście bez uwzględnienia mapowania zwracanego przez SDK.

### Sterowanie obszarowe PTZ w podglądzie jednego kanału

W układzie Live 1 (jeden wyświetlany kanał) operator może przeciągnąć lewym przyciskiem myszy prostokąt na obrazie. Zaznaczenie jest używane jako wskazówka dla fizycznego pozycjonowania kamery:
1. środek zaznaczenia względem środka obrazu wyznacza kierunek obrotu i pochylenia;
2. po krótkich impulsach PTZ ruchu poziomego/pionowego wykonywany jest impuls `ZOOM_IN`;
3. długości impulsów są szacowane z proporcji prostokąta do rozmiaru podglądu. Nie jest to kalibracja pola widzenia, dlatego wynik może wymagać ręcznej korekty.

Sterowanie używa `CLIENT_DHPTZControlEx2` i poleceń `LEFT/RIGHT/UP/DOWN/ZOOM_IN`, po każdym impulsie wysyłając odpowiadające mu zatrzymanie (`bStop=true`). Kółko myszy w układzie jednego kanału wykonuje krótki optyczny zoom in/out. W tym trybie nie wolno zmieniać skali ani położenia kontrolki obrazu w celu symulowania przybliżenia. Gesty obszarowe są dostępne tylko po zalogowaniu i w układzie jednego kanału; w układach 4/9/16 dotychczasowa nawigacja kafelków pozostaje odrębna.

Funkcja wymaga kamery PTZ i kanału, na którym NetSDK przyjmuje polecenia PTZ. Nie zadziała na kamerze stałopozycyjnej ani na kanale bez uprawnień PTZ. Czas impulsu jest przybliżony i zależy od modelu, prędkości PTZ, zoomu optycznego i konfiguracji urządzenia. Pozycję należy ocenić na obrazie i w razie potrzeby skorygować ręcznie.

## 4. Wyszukiwanie i odtwarzanie archiwum

W zakładce **Archiwum NVR** operator wybiera:
- zakres daty i czasu;
- układ odtwarzania 1/4/9/16;
- kanały do odtwarzania;
- pojedynczy kanał i zakres do wyszukiwania/pobierania plików, gdy korzysta z funkcji operujących na jednym rekordzie.

Wybór kanałów i układu archiwum znajduje się w tej samej zakładce i nie zależy od wyboru układu Live. Lista kanałów i podsumowanie zaznaczenia powinny odzwierciedlać aktualny wybór operatora.

### Wyszukiwanie plików

Wyszukiwanie archiwum wykorzystuje `CLIENT_QueryRecordFile`. Wyniki są prezentowane w tabeli. Należy kontrolować liczbę zwróconych rekordów względem pojemności bufora natywnego i nie odczytywać elementów poza jego zakresem.

### Odtwarzanie zsynchronizowane

Przycisk uruchomienia archiwum dla wybranych kanałów wywołuje natywne `CLIENT_PlayBackByTimeEx` dla każdego wybranego kanału w tym samym przedziale czasu. Wspólne sterowanie obejmuje start, pauzę, stop, pozycję czasu i prędkość odtwarzania.

Nie należy raportować częściowego uruchomienia jako pełnego sukcesu. Status powinien podawać liczbę kanałów, dla których uzyskano uchwyt odtwarzania, oraz numery kanałów, których nie udało się uruchomić. Jeżeli nie uruchomił się żaden kanał, operacja ma zakończyć się komunikatem błędu.

### Pobieranie nagrania

Pobieranie wybranego przedziału wykorzystuje `CLIENT_DownloadByTimeEx`. Plik źródłowy DAV jest zapisywany lokalnie, a następnie konwertowany przez lokalną kopię FFmpeg do MP4 i może zostać otwarty w głównym odtwarzaczu.

Domyślna lokalizacja pobranych nagrań:

`%LOCALAPPDATA%\\VideoPlayer\\nagrania_dahua\\`

DAV jest materiałem pobranym z rejestratora; MP4 jest plikiem wynikowym. Najpierw wykonywany jest remux bez ponownego kodowania, jeśli kodek i kontener na to pozwalają. Gdy to konieczne, wykonywana jest konwersja obrazu do H.264.

Nie należy traktować niepotwierdzonego adresu RTSP `/cam/playback` jako automatycznego fallbacku. Jeżeli NetSDK nie potrafi pobrać nagrania, aplikacja powinna zgłosić błąd zamiast tworzyć pozornie poprawny plik.

## 5. Szczegóły implementacyjne NetSDK

- `CLIENT_QueryRecordFile` używa bufora natywnego. Rozmiar bufora powinien być wyliczany przez `Marshal.SizeOf<NET_RECORDFILE_INFO>()`, a liczba rekordów ograniczona do liczby elementów, które faktycznie mieszczą się w buforze.
- Struktura `NET_RECORDFILE_INFO` musi zachowywać układ zgodny z natywnym ABI używanej wersji SDK, w tym rozmiary pól tekstowych i pól czasu. Nie zmieniać jej pól bez porównania z nagłówkami SDK.
- Deklaracje P/Invoke muszą zachowywać pełną liczbę argumentów, właściwe typy, konwencję wywołania i rozmiary struktur zgodne z biblioteką x64. Nie skracać sygnatur natywnego API.
- Callback pobierania Dahua sygnalizuje koniec transferu wartością -1 w polu pobranych danych (w typie unsigned widoczne jako `uint.MaxValue`). Obsługa callbacka musi uwzględniać ten warunek i bezpiecznie zakończyć zapis.
- Callbacki nie powinny bezpośrednio modyfikować kontrolek WPF z wątku natywnego. Aktualizacje UI należy przekazywać na wątek interfejsu.
- Przy zatrzymywaniu operacji należy zadbać o anulowanie, zwolnienie uchwytów i niedopuszczenie do użycia nieaktualnego uchwytu po zmianie urządzenia.

## 6. Nazwy kanałów i kodowanie

Nazwy zwracane przez `CLIENT_QueryChannelName` są dekodowane najpierw jako UTF-8 (tryb ścisły), a w razie nieprawidłowej sekwencji jako Windows-1250. Nie używać `Encoding.Default`, ponieważ wynik zależy od ustawień systemu Windows.

Bufor nazw kanałów ma 32 bajty na pozycję; liczba pozycji jest ograniczana do 256. Przy zmianach należy sprawdzić rozmiary buforów względem deklaracji SDK i przetestować polskie znaki: `ą ć ę ł ń ó ś ź ż`.

## 7. Presety PTZ przez HTTP CGI

Odczyt i modyfikacja presetów wykorzystują Dahua HTTP CGI API:

- odczyt: `/cgi-bin/ptz.cgi?action=getPresets&channel=N`;
- zapis bieżącej pozycji: `action=start&code=SetPreset&arg1=0&arg2=N&arg3=0`;
- zmiana nazwy: `action=setPreset&arg1=N&arg2=NAZWA`;
- usunięcie: `action=start&code=ClearPreset&arg1=0&arg2=N&arg3=0`.

CGI używa numeru kanału od 1. Port HTTP jest konfigurowalny dla każdego urządzenia (domyślnie 80), a dane logowania są takie same jak dla NetSDK. `HttpClientHandler` obsługuje uwierzytelnianie Basic/Digest; timeout wynosi 8 sekund. Nazwy są kodowane w URL jako UTF-8 przez `Uri.EscapeDataString`.

Zmiana lokalnej listy następuje dopiero po potwierdzeniu sukcesu przez urządzenie. Przy błędzie lista lokalna pozostaje bez zmian. Odczyt z urządzenia zastępuje lokalną listę, aby usunięte presety nie pozostawały jako nieaktualne wpisy.

Operacje PTZ wymagają aktywnego połączenia i włączonego HTTP CGI na urządzeniu. Port HTTP nie jest portem NetSDK (standardowo 37777).

## 8. Biblioteki i środowisko uruchomieniowe

- Aplikacja i biblioteki natywne Dahua muszą działać w architekturze x64.
- Biblioteki SDK są przechowywane w repozytorium w katalogu `lib` i powinny być kopiowane do katalogu wynikowego aplikacji również pod `lib`, zgodnie z konfiguracją projektu.
- Nie przenosić bibliotek SDK do przypadkowych katalogów ani nie mieszać wersji x86 i x64.
- Przy diagnozowaniu błędów ładowania bibliotek sprawdzić kompletność zależności natywnych oraz wymagany Microsoft Visual C++ Redistributable.
- Nie umieszczać bibliotek SDK ani danych logowania w dokumentach raportowych.

## 9. Ograniczenia i zasady bezpieczeństwa

- Dostępność funkcji zależy od modelu rejestratora, firmware, uprawnień konta i konfiguracji kanałów.
- Porty i funkcje HTTP CGI/NetSDK muszą być dostępne w sieci lokalnej; nie należy zakładać, że HTTPS jest skonfigurowany lub dostępny.
- Dokumentacja nie gwarantuje kompatybilności z każdym modelem Dahua/BCS. Przed wdrożeniem na innym urządzeniu należy przeprowadzić testy zgodności.
- Nagrania pobierane z rejestratora są przetwarzane lokalnie. Nie wysyłać plików wideo ani danych spraw do zewnętrznych usług w ramach diagnostyki.
- Logi diagnostyczne nie mogą zawierać haseł ani tokenów sesji.

## 10. Lista testów przed wydaniem

Poniższa lista jest checklistą do wykonania na docelowym urządzeniu. Sam opis testów nie oznacza, że zostały one wykonane dla konkretnego kompilowanego wydania.

| Test | Oczekiwany wynik |
|---|---|
| Migracja konfiguracji z adresem w `IpAddress` | Zachowane IP i przyjazna nazwa |
| Migracja starej konfiguracji z IP w `Name` | Odzyskany adres i oddzielona nazwa operatora |
| Zapis nazwy bez zmiany IP | Połączenie nadal kieruje na ten sam adres |
| Zmiana IP/portu/danych logowania | Poprzednia sesja rozłączona; kolejne połączenie używa nowych danych |
| Zmiana urządzenia podczas pracy | Brak użycia uchwytów poprzedniego urządzenia |
| Podgląd Live 1/4/9/16 | Wybrane kanały wyświetlają obraz |
| Wyszukiwanie archiwum | Tabela pokazuje rekordy z wybranego kanału i zakresu |
| Archiwum na wielu kanałach | Kanały startują dla wspólnego zakresu czasu |
| Częściowy błąd startu | Status wymienia uruchomione i niedostępne kanały |
| Zmiana układu archiwum | Nie zmienia układu podglądu Live |
| Pauza, stop, zmiana prędkości | Sterowanie obejmuje uruchomione kanały |
| Pobieranie archiwum | Powstaje DAV, poprawny MP4 i możliwe jest odtworzenie w głównym odtwarzaczu |
| Brak nagrania / timeout | Czytelny błąd, brak komunikatu o fałszywym sukcesie |
| Nazwy kanałów z polskimi znakami | Znaki są wyświetlane prawidłowo |
| Odczyt i modyfikacja presetów PTZ | Operacje są potwierdzone przez urządzenie; błąd nie zmienia lokalnej listy |
| Brak zależności x86 | Aplikacja x64 ładuje właściwe biblioteki natywne |

## 11. Procedura pracy z kodem i testowania

1. Pobierz aktualną gałąź `wersja-1.7` repozytorium `rabo501/VideoPlayer`.
2. Otwórz rozwiązanie `VideoPlayer.slnx` w Visual Studio 2022.
3. Ustaw platformę x64 i sprawdź konfigurację kopiowania bibliotek z katalogu `lib`.
4. Zbuduj rozwiązanie w konfiguracji Debug, a przed publikacją również Release.
5. Uruchom aplikację w sieci, w której dostępny jest testowy rejestrator.
6. Sprawdź logowanie i Live, następnie wyszukiwanie archiwum, odtwarzanie pojedynczego kanału i odtwarzanie wielokanałowe.
7. Przetestuj pobieranie krótkiego przedziału, konwersję DAV do MP4 i odtworzenie pliku w głównym odtwarzaczu.
8. Sprawdź presety PTZ oraz nazwy kanałów z polskimi znakami, jeśli urządzenie obsługuje te funkcje.
9. Wykonaj checklistę z sekcji 10 i zapisz wyniki testów dla konkretnego modelu, firmware i kompilacji.

**Nie używać historycznych instrukcji scalania gałęzi `dahua-archive-rebuild` lub `dahua-SDK`.** Dalsze poprawki integracji należy wprowadzać na aktualnej gałęzi rozwojowej `wersja-1.7`.
