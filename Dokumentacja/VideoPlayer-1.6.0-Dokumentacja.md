# VideoPlayer 1.6.0
## Dokumentacja użytkownika i uruchomienia

Stan dokumentacji: 19.11.2026. Wydanie: 1.6.0. Dokument opisuje aktualny zakres aplikacji, lokalny OCR, wykrywanie ruchu, integrację Dahua, eksport, raporty oraz przechowywanie danych.

## 1. Przeznaczenie i najważniejsze ograniczenia

VideoPlayer jest aplikacją Windows WPF do przeglądania i analizy nagrań. Odtwarzanie realizuje mpv, przetwarzanie nagrań i miniatur FFmpeg, a lokalny odczyt tablic Tesseract. Dane analizy są zapisywane w SQLite. Nie jest to narzędzie do automatycznego wykrywania wszystkich tablic w całym filmie: użytkownik wskazuje klatkę i obszar, sprawdza wynik oraz potwierdza numer.

Eksport fragmentu i zapis filmu bez fragmentu tworzą nowe pliki; nie edytują oryginalnego nagrania. Zoom jest zmianą widoku, a nie podniesieniem rzeczywistej rozdzielczości materiału. Skuteczność odczytu i precyzję eksportu trzeba oceniać na konkretnych nagraniach.

## 2. Wymagania i uruchomienie

Paczka zawiera projekt źródłowy, nie instalator. Do budowania potrzebny jest Windows, Visual Studio z obsługą aplikacji desktopowych .NET oraz SDK .NET 8. Projekt korzysta z WPF i integracji Windows Forms. Silniki oraz biblioteki natywne muszą odpowiadać architekturze x64.

1. Przed aktualizacją zamknij aplikację i wykonaj kopię całego projektu oraz dotychczasowej bazy VideoPlayer.db.
2. Rozpakuj nową paczkę do osobnego katalogu. Nie usuwaj starego środowiska przed sprawdzeniem nowej wersji.
3. Do katalogu projektu VideoPlayer skopiuj własne katalogi mpv i ffmpeg z działającej instalacji. Zachowaj także pliki towarzyszące silnikom.
4. Otwórz projekt VideoPlayer/VideoPlayer.csproj w Visual Studio, przywróć zależności i zbuduj dla x64.
5. W katalogu uruchomieniowym sprawdź obecność mpv/mpv.exe, ffmpeg/ffmpeg.exe, scripts/review.lua oraz katalogów x64 i tessdata. Samo istnienie plików obok źródeł nie wystarczy, jeśli nie trafiły do wyniku budowania.
6. Przy problemie uruchomienia OCR sprawdź Microsoft Visual C++ Redistributable 2015–2022 x64, biblioteki x64\\tesseract50.dll i x64\\leptonica-1.82.0.dll oraz model tessdata\\eng.traineddata. Wszystkie muszą znaleźć się w katalogu wynikowym. W paczce pozostaw pliki licencji.
7. Uruchom program z katalogu, w którym masz prawo zapisu: baza SQLite powstaje obok uruchomionej aplikacji.

Wersja 1.6.0 zawiera lokalny OCR oraz narzędzia analizy nagrań. Nie wymaga przesyłania klatki do internetowego serwisu rozpoznawania. Pierwsze budowanie projektu może wymagać pobrania zależności.

Numer wydania jest ustawiany centralnie w pliku VideoPlayer.csproj. Tytuł okna i okno Informacje pokazują wersję zestawu aplikacji.

## 3. Interfejs

Główne okno zawiera menu, obszar wideo, panel narzędzi, oś czasu, przyciski odtwarzania oraz pasek stanu i postępu operacji. Panel narzędzi można pokazać lub ukryć i zmienić jego szerokość separatorem.

| Zakładka | Zastosowanie |
|---|---|
| Fragmenty | Ustawienie początku i końca, nazwy oraz listy zakresów |
| Zdarzenia | Opisane punkty nagrania, lokalny OCR, raporty CSV i PDF |
| Eksport | Profile, eksport zaznaczenia i listy, film bez zaznaczonego fragmentu |
| Pliki | Ostatnie nagrania, zapisane klatki, parametry materiału, widok i sesja |

Pasek stanu pokazuje komunikaty, błędy i wynik operacji. Podczas dłuższych zadań dostępny jest postęp i przycisk Anuluj. Część przycisków jest wtedy blokowana, żeby nie mieszać operacji.

## 4. Otwieranie i odtwarzanie

Wybierz otwieranie pliku z menu albo Ctrl+O. Filtr okna wyboru obejmuje MP4, MKV, AVI, MOV, WEBM, M4V, WMV i TS; opcja wszystkich plików pozwala wybrać inny plik. Faktyczna możliwość odczytu zależy od materiału i silnika mpv. Ostatnie dostępne nagrania można ponownie otworzyć z zakładki Pliki.

Przycisk Play/Pauza i spacja przełączają odtwarzanie. Suwak osi czasu służy do zmiany pozycji i pokazuje czas bieżący oraz długość nagrania. Przyciski Klatka wstecz i Klatka w przód oraz strzałki klawiatury służą do analizy klatkowej. Cofanie klatek może być wolniejsze dla trudnych kodeków i długich odstępów między klatkami kluczowymi.

Dostępne prędkości odtwarzania: 0,25×, 0,5×, 1×, 2×, 4×, 8×, 16× i 32×. Jest to prędkość oglądania; jej zmiana sama w sobie nie tworzy przyspieszonego eksportu. Przy bardzo szybkim odtwarzaniu płynność zależy od sprzętu i nagrania.

## 5. Widok filmu i obsługa myszy

W głównym odtwarzaczu kółko myszy zmienia powiększenie z uwzględnieniem położenia kursora. Przytrzymanie lewego przycisku i przeciąganie przesuwa kadr. Działa to nad obrazem w aktywnym oknie, bez wciśniętych klawiszy modyfikujących. Klawisz R lub Resetuj widok przywraca ustawienia widoku.

Z listy widoku lub menu wybierzesz Dopasuj cały obraz, Wypełnij okno oraz powiększenie 125%, 150% albo 200%. Dopasowanie pokazuje pełny obraz z ewentualnymi pasami; wypełnienie może odciąć część brzegów, zachowując proporcje.

Prawy przycisk nad głównym obrazem otwiera menu podręczne: ustawienie początku i końca zakresu, przejście do dodawania zdarzenia, zapis klatki i reset widoku. Wybranie dodawania zdarzenia ustawia fokus w polu opisu; zdarzenie zapisujesz odpowiednim przyciskiem.

Sterowanie w oddzielnym oknie OCR jest inne: lewy przycisk zaznacza, a prawy przesuwa klatkę. Nie należy przenosić gestów z jednego okna do drugiego.

## 6. Zapis klatki i informacje o pliku

Ustaw interesującą pozycję i wybierz Zapisz klatkę lub naciśnij S. Program zatrzymuje nagranie i pyta o nazwę oraz miejsce zapisu JPG. Proponowana nazwa zawiera nazwę nagrania i oznaczenie czasu. Jeśli anulujesz okno zapisu, nie powstanie nowy plik.

Zapis wykorzystuje klatkę wideo, a nie zrzut całego interfejsu z przyciskami czy ramką zaznaczenia OCR. Zapisane klatki trafiają do historii w zakładce Pliki. Pokaż klatkę otwiera wskazany plik w aplikacji systemowej, a Folder klatek otwiera jego katalog. Gdy nie ma wskazanej klatki, program korzysta z katalogu Obrazy/VideoPlayer.

W zakładce Pliki odczytywane są informacje o materiale: kodek, rozdzielczość, FPS, bitrate i rozmiar. Niedostępne metadane mogą być opisane jako nieznane. Odczyt parametrów korzysta z FFmpeg; problem z tym silnikiem nie musi oznaczać braku możliwości samego odtwarzania przez mpv.

## 7. Zaznaczanie i lista fragmentów

1. Przejdź do początku fragmentu i wybierz Początek lub I.
2. Przejdź dalej i wybierz Koniec lub O.
3. Sprawdź prezentowane granice zaznaczenia i podaj nazwę w polu Nazwa fragmentu.
4. Wybierz Dodaj zaznaczenie, żeby zachować zakres na liście.

Koniec musi być późniejszy niż początek, a cały zakres musi mieścić się w długości filmu. Brak granicy lub nieprawidłowa kolejność powoduje komunikat zamiast poprawnego eksportu. Zakres jest również zaznaczany na osi czasu.

Zaktualizuj zmienia wybrany wpis zgodnie z bieżącym zakresem i nazwą. Wczytaj zakres przywraca granice wskazanego wpisu do dalszej pracy. Usuń z listy usuwa wpis, nie kasuje materiału źródłowego. Lista pozwala przygotować wiele fragmentów przed uruchomieniem eksportu seryjnego. Przejdź do eksportu otwiera właściwą zakładkę.

## 8. Zdarzenia na osi czasu

W zakładce Zdarzenia wpisz opis i wybierz Dodaj zdarzenie przy interesującej pozycji filmu. Każdy punkt ma czas nagrania i opis. Po wybraniu wpisu możesz zmienić opis, przejść do jego czasu albo go usunąć. Kliknięcie znacznika na osi czasu służy do nawigacji do punktu.

Zdarzenia są przypisane do konkretnego pliku i przechowywane w bazie. Raporty korzystają z aktualnej listy zdarzeń uporządkowanej według czasu. Czas oznacza pozycję względem początku filmu; nie należy traktować go jako automatycznie ustalonej daty i godziny rzeczywistego zdarzenia.

## 9. Lokalny odczyt tablic rejestracyjnych

### 9.1. Przebieg pracy

1. Otwórz film i przejdź do klatki, na której tablica jest możliwie czytelna.
2. W zakładce Zdarzenia wybierz Rozpoznaj tablicę — lokalnie. Program zatrzyma nagranie, pobierze klatkę i otworzy okno OCR.
3. Jeśli potrzebujesz większej precyzji, powiększ obraz kółkiem i przesuń prawym przyciskiem jeszcze przed pierwszym zaznaczeniem.
4. Przeciągnij lewym przyciskiem, żeby zaznaczyć obszar tablicy. Dostosuj każdy z czterech ponumerowanych narożników niezależnie.
5. Ustaw narożniki zgodnie z perspektywą tablicy, bez krzyżowania boków. Obejmij czytelny numer, unikając zbędnego tła, ramki oraz niebieskiego paska, jeśli pogarszają wycinek.
6. Po puszczeniu narożnika sprawdź podgląd wyprostowanego obrazu przekazywanego do OCR. Nieprawidłowa geometria jest sygnalizowana kolorem ramki i nie daje poprawnego wycinka.
7. Uruchom rozpoznawanie. Sprawdź propozycję i listę kandydatów. Wybierz wynik albo popraw numer ręcznie; przy braku odczytu możesz wpisać numer, jeśli rzeczywiście jest czytelny na obrazie.
8. Potwierdź zgodność numeru z obrazem, a następnie zapisz. Program doda zdarzenie z czasem klatki i opisem Tablica rejestracyjna: [numer] (numer potwierdzony ręcznie).

Zdarzenie jest widoczne na liście i osi czasu oraz trafia do raportów. Anulowanie okna nie dodaje zdarzenia; film pozostaje zatrzymany. Zmiana wpisanego numeru usuwa potwierdzenie i wymaga ponownego sprawdzenia.

### 9.2. Powiększenie, przesuwanie i narożniki

| Sterowanie w oknie OCR | Działanie |
|---|---|
| Kółko myszy | Powiększenie lub pomniejszenie pod kursorem |
| Prawy przycisk i przeciąganie | Przesunięcie powiększonej klatki |
| Lewy przycisk i przeciąganie | Utworzenie zaznaczenia lub przesunięcie narożnika |
| + i − | Zmiana powiększenia względem środka widoku |
| Dopasuj | Pokazanie całej klatki bez usuwania narożników |
| Reset zaznaczenia | Usunięcie obszaru i wyników do ponownego wskazania |

Powiększenie wynosi od 1× do 16× względem dopasowania całej klatki do okna. Nie jest to stosunek do natywnej liczby pikseli filmu. Przesuwanie jest ograniczone tak, żeby obraz nie znikał poza widokiem. Podczas przeciągania narożnika lub przesuwania obrazu najpierw puść przycisk, a dopiero potem zmień zoom.

Narożniki są zapisane w oryginalnych współrzędnych klatki. Zmiana zoomu, przesunięcie lub rozmiar okna nie zmieniają wskazanego obszaru i nie wymagają ponownego OCR. Nowe zaznaczenie lub przesunięcie narożnika unieważnia poprzedni wynik i potwierdzenie; rozpoznawanie trzeba uruchomić ponownie po sprawdzeniu wyprostowanego podglądu.

### 9.3. Ograniczenia OCR

OCR działa lokalnie. Model, silnik i biblioteki muszą znajdować się w przewidzianych katalogach. Numer jest propozycją, nie gwarantowanym odczytem ani identyfikacją pojazdu. Program nie powinien zastępować wizualnego sprawdzenia znaków podobnych, np. O/0 czy I/1. Rozmycie, mała liczba pikseli, prześwietlenie, brud i silna perspektywa mogą uniemożliwić poprawny wynik mimo prostowania. Powiększenie ułatwia zaznaczenie, ale nie odzyskuje brakujących szczegółów.

## 10. Wykrywanie ruchu

### 10.1. Uruchomienie analizy

1. Otwórz nagranie i wybierz przycisk Wykryj ruch na pasku odtwarzacza.
2. W oknie ustawień zobaczysz klatkę z aktualnej pozycji nagrania.
3. Przeciągnij lewym przyciskiem myszy po obrazie, aby zaznaczyć prostokątny obszar analizy. Zaznaczenie jest pokazane zieloną ramką.
4. Jeśli nie zaznaczysz obszaru albo wybierzesz Cały obraz, analizowana będzie cała klatka.
5. Ustaw czułość, minimalny obszar zmiany i minimalny czas zdarzenia, a następnie wybierz Rozpocznij analizę.
6. Postęp jest pokazywany na pasku postępu. Anuluj analizę przerywa bieżące zadanie.
7. Po zakończeniu wykryte zdarzenia zostaną dodane do listy zdarzeń i oznaczone zakresami na osi czasu.

### 10.2. Parametry

| Parametr | Zakres | Znaczenie |
|---|---:|---|
| Czułość — próg różnicy jasności | 1–100 | Próg różnicy jasności pikseli. Niższa wartość oznacza większą czułość. |
| Minimalny obszar zmiany | 0,5–10% | Minimalny udział zmienionych pikseli w analizowanym obszarze. |
| Minimalny czas zdarzenia | 0,5–5 s | Krótsze wykrycia są odrzucane. |

Analiza jest wykonywana przez FFmpeg na próbkach 2 klatek/s, przeskalowanych do 320×180 i obrazu monochromatycznego. Zmiany pikseli i procent zmienionego obszaru są liczone tylko wewnątrz wskazanego prostokąta. Przy analizie całego obrazu obszarem odniesienia jest cała klatka.

### 10.3. Zdarzenia i podgląd

Każde wykryte zdarzenie zapisuje czas początku i końca, maksymalny udział zmienionego obszaru oraz prostokąt obejmujący wykryte zmiany. Zakres zdarzenia jest prezentowany na osi czasu jako pomarańczowy pas. Dane są przechowywane w lokalnej bazie VideoPlayer.db.

Aby obejrzeć obszar, zaznacz zdarzenie na liście i wybierz Podgląd obszaru ruchu. Program pobiera klatkę z czasu zdarzenia i pokazuje ją w osobnym oknie z zielonym prostokątem. Starsze zdarzenia, zapisane przed wprowadzeniem współrzędnych obszaru, nie mają danych do takiego podglądu.

### 10.4. Ograniczenia

Wykrywanie porównuje jasność kolejnych próbek, nie rozpoznaje obiektów ani osób i nie określa rodzaju ruchu. Zmiana oświetlenia, szum, kompresja, migotanie obrazu lub ruch kamery mogą powodować fałszywe wykrycia. Ruch krótszy niż odstęp między próbkami może zostać pominięty. Wyniki należy zweryfikować na nagraniu źródłowym.
## 11. Eksport wideo

### 10.1. Pojedynczy fragment

Ustaw prawidłowy początek i koniec, wybierz profil w zakładce Eksport, a następnie Zapisz zaznaczenie jako MP4. Wskaż nową nazwę i miejsce wyniku. Źródło pozostaje bez zmian.

### 10.2. Profile i kopiowanie strumieni

| Opcja | Zastosowanie |
|---|---|
| Szybki eksport | Priorytet szybkości kodowania |
| Standardowy | Domyślny kompromis jakości i czasu |
| Mniejszy plik | Mocniejsza kompresja, możliwa utrata szczegółów |
| Wysoka jakość | Mniejsza kompresja, zwykle większy plik i dłuższa praca |
| Kopiuj strumienie bez rekompresji | Bez ponownego kodowania przy zgodnych strumieniach i kontenerze |

Przy kodowaniu eksportu fragmentów używane są H.264 oraz AAC, jeśli źródło zawiera mapowaną ścieżkę audio. Profile nie są zmianą prędkości odtwarzania ani nakładaniem powiększenia z podglądu na wynik.

Kopiowanie strumieni wymaga zgodności kodeków z MP4. Granice cięcia mogą przesunąć się względem zaznaczenia, szczególnie przy klatkach kluczowych. Program wyświetla ostrzeżenie przed takim eksportem. Gdy istotne są granice fragmentu, wybierz ponowne kodowanie i sprawdź wynik.

### 10.3. Cała lista fragmentów

Dodaj zakresy do listy i wybierz Eksportuj całą listę fragmentów. Po wskazaniu folderu nadrzędnego program utworzy nowy podfolder eksportu oraz ponumerowane pliki MP4 z nazwami fragmentów. Kolejne pozycje są przetwarzane w jednej operacji z informacją o aktualnym pliku. Anulowanie nie usuwa wcześniej ukończonych plików seryjnych.

### 10.4. Film bez zaznaczonego fragmentu

Wybierz Zapisz film bez zaznaczonego fragmentu i potwierdź operację. Powstaje nowy film z pominięciem wskazanego zakresu; źródło pozostaje nienaruszone. To osobna operacja łączenia pozostawionych części, a nie fizyczne usunięcie danych z oryginału. Ustawienia profilu eksportu fragmentu i opcja kopiowania strumieni nie są obietnicą identycznego trybu tego zadania.

### 10.5. Postęp i ochrona plików

Obserwuj opis operacji, pasek postępu i komunikat końcowy. Anuluj przerywa bieżące zadanie. Przy zamykaniu podczas operacji program pyta o jej przerwanie. Dla eksportowanych wyników wybieraj nowe nazwy: aplikacja odmawia nadpisania istniejącego wyniku w swoim oknie wyboru ścieżki. Zapis JPG ma oddzielne systemowe okno potwierdzania nadpisania.

## 12. Raporty zdarzeń

### 11.1. CSV

W zakładce Zdarzenia wybierz Raport CSV i wskaż plik wynikowy. Raport zawiera kolumny Plik, Czas, Sekundy i Opis, rozdzielane średnikiem. Obejmuje także zapisane i potwierdzone odczyty tablic, ponieważ są zdarzeniami. CSV nie zawiera miniatur; jego eksport nie został zmieniony przez poprawkę autora PDF.

### 11.2. PDF

Wybierz Raport PDF. Opcja Miniatury w PDF pozwala dołączyć obrazy dla czasów zdarzeń; ich generowanie wymaga FFmpeg i może wydłużyć operację. Raport przedstawia opisane punkty nagrania w kolejności czasu.

Stopka i metadane autora PDF korzystają z pełnej nazwy użytkownika Windows. Gdy nie uda się ustalić imienia i nazwiska, wpisywane jest dokładnie: „Nie ustalono imienia i nazwiska”. Cała stopka ma czcionkę Arial 8, krój zwykły i kolor szary. Odczyt danych konta wymaga sprawdzenia w docelowym środowisku Windows; nie należy zakładać, że każde konto udostępnia pełną nazwę.

Raport nie stanowi niezależnego potwierdzenia prawdziwości opisu albo poprawności OCR: zapisuje wynik pracy użytkownika z materiałem.

## 13. Sesje, historia i baza danych

Zapisz sesję zachowuje pozycję filmu, wybraną prędkość, stan pauzy, zoom, przesunięcie, tryb wypełnienia, bieżące granice i listę fragmentów. Zapis sesji jest wykonywany także przy zmianie materiału; przy prawidłowym zamknięciu zachowywane są dane pracy. Opcja Przywracaj pozycję i widok decyduje o odtwarzaniu poprzednich ustawień podczas otwierania pliku. Zakresy i zdarzenia należy odróżnić od samego przywracania widoku.

Historia obejmuje ostatnie nagrania i klatki. Usunięte albo niedostępne pliki nie są prezentowane jako dostępne do otwarcia. Baza przechowuje ścieżki i dane analizy, nie kopie wszystkich nagrań i zrzutów.

VideoPlayer.db znajduje się w katalogu uruchomionej aplikacji. Repozytorium sesji i usługa historii korzystają z tego samego pliku. Program zawiera import starszych danych JSON z lokalnego katalogu użytkownika VideoPlayer; nie traktuj tego jako zamiennika kopii bezpieczeństwa.

Powiązanie analizy opiera się na ścieżce pliku. Przeniesienie lub zmiana nazwy nagrania może spowodować, że dotychczasowe dane nie zostaną odnalezione dla nowej ścieżki. Dwie kopie programu uruchomione z różnych katalogów mogą korzystać z różnych baz.

Kopię bazy wykonuj po zamknięciu programu. Jeśli obok bazy pozostają pliki SQLite -wal lub -shm, nie usuwaj ich arbitralnie i zachowaj spójną kopię katalogu. Zabezpieczaj oddzielnie nagrania źródłowe, wyeksportowane filmy, zapisane klatki i raporty. Po aktualizacji sprawdź, czy używana jest właściwa baza w aktualnym katalogu uruchomieniowym.

## 14. Skróty i gesty — zestawienie

| Skrót lub gest | Okno / działanie |
|---|---|
| Ctrl+O | Główne: otwarcie filmu |
| Spacja | Główne: Play/Pauza |
| Strzałka w lewo | Główne: poprzednia klatka |
| Strzałka w prawo | Główne: następna klatka |
| I | Główne: początek zakresu |
| O | Główne: koniec zakresu |
| S | Główne: zapis klatki JPG |
| R | Główne: reset widoku |
| Kółko myszy | Główne i OCR: powiększanie widoku |
| Lewy przycisk i przeciąganie | Główne: przesuwanie kadru; OCR: zaznaczenie lub narożnik |
| Prawy przycisk | Główne: menu podręczne; OCR z przeciąganiem: przesuwanie |

Skróty analizy nie powinny przejmować liter podczas wpisywania tekstu w polach edycyjnych i pracy z listami rozwijanymi. Nie wszystkie skróty okna głównego obowiązują w oknie OCR. R jest skrótem głównego widoku; w OCR do przywrócenia pełnej klatki służy Dopasuj.

## 15. Rozwiązywanie problemów

| Objaw | Co sprawdzić |
|---|---|
| Brak odtwarzania lub połączenia mpv | Obecność mpv.exe w katalogu uruchomieniowym; Pomoc → Uruchom ponownie mpv; komunikat stanu |
| Brak sterowania myszą nad filmem | Aktywność okna, kursor nad obrazem, brak Ctrl/Shift/Alt, obecność scripts/review.lua |
| Nie można wyeksportować zakresu | Ustawienie obu granic, koniec późniejszy od początku, granice w długości filmu, dostępność źródła i FFmpeg |
| Błąd przy kopiowaniu strumieni | Zgodność kodeków z MP4; zamiast kopiowania użyj ponownego kodowania |
| Brak parametrów materiału lub miniatur PDF | Dostępność FFmpeg i prawidłowy plik źródłowy |
| Nie działa lokalny OCR | x64\\tesseract50.dll, x64\\leptonica-1.82.0.dll, tessdata\\eng.traineddata oraz Microsoft Visual C++ Redistributable 2015–2022 (x64) |
| OCR nie odczytuje czytelnej tablicy | Dokładniejsze narożniki, kontrola wyprostowanego podglądu, inna klatka, ograniczenie tła |
| Nieaktywny zapis numeru | Poprawność numeru i zaznaczenie potwierdzenia zgodności z obrazem |
| Zniknęły dawne sesje lub zdarzenia | Właściwa baza VideoPlayer.db i ta sama ścieżka nagrania |
| Nie można zapisać bazy albo wyniku | Uprawnienia zapisu, wolne miejsce i nowa nazwa pliku wynikowego |
| Autor PDF nie jest imieniem i nazwiskiem | Dostępność pełnej nazwy konta Windows; przy braku danych stosowany jest jawny tekst zastępczy |

Menu Pomoc udostępnia pomoc podstawową, ponowne uruchomienie mpv i informacje o programie. Niniejsza dokumentacja jest pełniejsza niż starsze teksty pomocy wbudowanej.

## 16. Weryfikacja i zakres wydania 1.6.0

Wydanie 1.6.0 obejmuje lokalny OCR, wykrywanie ruchu z obszarem ROI, zapis przedziałów czasowych zdarzeń i podgląd obszaru ruchu oraz istniejące funkcje odtwarzania, eksportu, raportowania i integracji Dahua. Kompilację x64 oraz testy na docelowym komputerze Windows należy wykonać przed opublikowaniem paczki.

Ten dokument jest główną instrukcją użytkownika i wdrożenia dla wydania 1.6.0. Starsze instrukcje cząstkowe oraz dokument DOCX/ZIP oznaczone 1.3.2 mają charakter archiwalny; w razie różnic korzystaj z dokumentacji 1.6.0.

### Lista kontroli po instalacji

- Otwórz nagranie; sprawdź Play/Pauza, przewijanie, klatki i prędkości.
- Sprawdź zoom, przesunięcie i R w głównym oknie, a oddzielnie gesty OCR.
- Zapisz JPG, zakres i zdarzenie; zamknij program i sprawdź przywracanie danych.
- W OCR zaznacz tablicę, przesuń narożniki, zmień zoom i rozmiar okna, a następnie potwierdź numer.
- Wyeksportuj pojedynczy zakres, listę i nowy film bez fragmentu; obejrzyj wyniki.
- Wygeneruj CSV oraz PDF z miniaturami i bez nich. Sprawdź autora, stopkę, czasy i opisy.
- Potwierdź, że oryginalny film pozostał bez zmian i że znasz lokalizację właściwej bazy.

## 17. Przygotowanie publikacji 1.6.0

- [ ] Ustawiono konfigurację Release i platformę x64.
- [ ] Wykonano czystą kompilację oraz publikację na docelowym Windows z .NET 8 Desktop Runtime.
- [ ] W katalogu wynikowym są mpv\\mpv.exe i pliki towarzyszące, ffmpeg\\ffmpeg.exe oraz wymagane biblioteki FFmpeg.
- [ ] W katalogu wynikowym są x64\\tesseract50.dll, x64\\leptonica-1.82.0.dll, tessdata\\eng.traineddata i Tesseract.dll.
- [ ] Na komputerze docelowym jest Microsoft Visual C++ Redistributable 2015–2022 (x64).
- [ ] W paczce są scripts\\review.lua, VideoPlayer.ico oraz pliki licencji.
- [ ] Sprawdzono OCR, detekcję ruchu na zaznaczonym obszarze i całym obrazie, anulowanie, zapis zakresów i podgląd obszaru.
- [ ] Sprawdzono eksport MP4, JPG, CSV i PDF oraz integrację Dahua, jeśli jest używana.
- [ ] Wykonano kopię bazy danych i przetestowano aktualizację istniejącej bazy.
- [ ] Wydanie zawiera numer 1.6.0 w nazwie paczki i informacjach o programie.

**Ważne:** pliki natywne OCR muszą być dołączone do paczki wydania, nawet jeśli na komputerze deweloperskim OCR działa dzięki bibliotekom już zainstalowanym w systemie. Instalacja Visual C++ Redistributable nie zastępuje bibliotek Tesseract i Leptonica.
