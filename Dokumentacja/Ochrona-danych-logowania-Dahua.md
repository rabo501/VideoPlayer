# Ochrona danych logowania Dahua

## Mechanizm

Dane logowania kamer i rejestratorów są chronione przez Windows DPAPI (ProtectedData) w zakresie CurrentUser. JSON nadal przechowuje pola Username i Password, ale ich wartości mają postać dpapi:v1:<Base64>; jawne wartości nie są zapisywane.

Klucz ochrony jest zarządzany przez Windows i związany z profilem użytkownika. Aplikacja nie zapisuje własnego klucza ani hasła głównego.

## Migracja ze starszych wersji

Przy pierwszym odczycie starszego dahua_devices.json aplikacja rozpoznaje wartości bez prefiksu DPAPI jako dane w starym formacie. Po wczytaniu urządzeń zapisuje je ponownie w formie zaszyfrowanej. Podczas migracji nie tworzy kopii .bak zawierającej jawne hasła i usuwa starą przenośną kopię pliku po udanym zapisie.

Plik konfiguracyjny jest przechowywany w:
%LOCALAPPDATA%\VideoPlayer\dahua_devices.json

## Zmiana komputera lub konta Windows

DPAPI w zakresie CurrentUser celowo nie pozwala odszyfrować danych z innego konta Windows lub innego komputera. Po przeniesieniu konfiguracji aplikacja wyświetli ostrzeżenie. Operator musi ponownie wpisać nazwę użytkownika i hasło w edytorze urządzenia i zapisać konfigurację.

Jeżeli odszyfrowanie się nie powiedzie, aplikacja nie traktuje zaszyfrowanego tekstu jako hasła i nie nadpisuje go pustą wartością podczas automatycznej migracji.

## Zalecenia operacyjne

- Ogranicz dostęp do konta Windows i profilu użytkownika.
- Nie umieszczaj pliku dahua_devices.json w repozytorium ani publicznych kopiach zapasowych.
- Stare jawne kopie dahua_devices.json należy usunąć również z ręcznych archiwów, nośników i historii repozytorium.
- Jeżeli hasła były wcześniej przechowywane jawnie w pliku, rozważ ich zmianę w kamerach i rejestratorach. Szyfrowanie nowego pliku nie usuwa kopii, które mogły już zostać skopiowane.

## Test odbiorczy

1. Uruchom aplikację na tym samym koncie Windows, na którym zapisano urządzenia.
2. Sprawdź, czy urządzenia są widoczne i można połączyć się z rejestratorem oraz kamerą.
3. Otwórz %LOCALAPPDATA%\VideoPlayer\dahua_devices.json w edytorze tekstu. Wartości Username i Password powinny zaczynać się od dpapi:v1:; nie powinny zawierać haseł.
4. Zmień nazwę lub hasło urządzenia, zapisz i ponownie uruchom aplikację.
5. Wykonaj kopię pliku na testowe konto Windows lub komputer. Oczekiwany wynik: ostrzeżenie o niedostępnych danych logowania; po ponownym wpisaniu danych na docelowym koncie połączenie powinno działać.
6. Sprawdź, czy migracja starszego pliku nie pozostawiła jawnego .bak ani starego pliku w katalogu aplikacji.

DPAPI chroni sekrety zapisane na dysku przed odczytem przez inne konta/komputery, ale nie zabezpiecza przed złośliwym kodem działającym w sesji tego samego użytkownika ani przed odczytem pamięci działającej aplikacji.