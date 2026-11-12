# Moduł Dahua NetSDK – informacja o aktualnej dokumentacji

Ta instrukcja opisywała wcześniejszą wersję modułu. Część opisanych w niej zachowań (w szczególności fikcyjne presety, automatyczne nazwy kanałów i pobieranie zakresu przez niezweryfikowany adres RTSP) nie odpowiada aktualnej implementacji.

**Aktualna dokumentacja wdrożenia, migracji konfiguracji, połączeń i archiwum znajduje się w pliku:**

[Przebudowa-Archiwum-Dahua.md](Przebudowa-Archiwum-Dahua.md)

Najważniejsze aktualne zasady:
- nazwa operatora (`Name`) jest odrębna od adresu sieciowego (`IpAddress`);
- wszystkie połączenia NetSDK korzystają z `ConnectionAddress`, nigdy z nazwy wyświetlanej;
- starsza konfiguracja jest migrowana z kopią oryginału;
- lista archiwum pochodzi z `CLIENT_QueryRecordFile`;
- odtwarzanie zaznaczonego nagrania wykorzystuje `CLIENT_PlayBackByTimeEx`;
- eksport do głównego MPV pobiera plik DAV przez NetSDK i konwertuje go do MP4 przez lokalny FFmpeg;
- nie jest używany niepotwierdzony adres RTSP `/cam/playback` jako fallback.

Przed wdrożeniem wymagane są testy na rzeczywistym rejestratorze Dahua i kompilacja x64 w Visual Studio 2022.
