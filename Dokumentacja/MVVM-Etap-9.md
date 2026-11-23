# MVVM – Etap 9: konfiguracja urządzeń Dahua

## Cel
Przeniesienie walidacji i zapisu konfiguracji urządzeń Dahua z okna WPF do `DahuaViewModel`.

## Zmiany
- `DahuaViewModel.TryValidateDeviceConfiguration` waliduje nazwę, adres hosta, port SDK, port HTTP i dane logowania.
- Parsowanie portów odbywa się z użyciem `InvariantCulture`, niezależnie od ustawień regionalnych Windows.
- `DahuaViewModel.SaveDeviceConfiguration` aktualizuje wybrany wpis, zapisuje konfigurację przez dotychczasowy `DahuaConfigService` i aktualizuje komunikat statusu.
- `MainWindow.Dahua.cs` pozostawia sobie prezentację komunikatów, potwierdzenie usunięcia oraz wymagane czynności UI przy rozłączaniu, ale formularz konfiguracji nie zmienia już bezpośrednio właściwości urządzenia ani nie zapisuje samodzielnie jego danych. Odrębny zapis nazw kanałów i presetów odczytanych z rejestratora pozostaje bez zmian.
- Dodano testy poprawnej konfiguracji i odrzucania nieprawidłowych danych.

## Bezpieczeństwo i zgodność
- Dane logowania nadal są zapisywane przez istniejący mechanizm DPAPI (CurrentUser).
- Format pliku `dahua_devices.json` i lokalizacja konfiguracji nie ulegają zmianie.
- Dodawanie i usuwanie urządzeń nadal korzysta z istniejących metod ViewModelu.
- Nie zmieniono połączeń SDK, podglądu, PTZ, presetów ani odtwarzania archiwum.
- Przed zapisem zmienionej konfiguracji okno nadal rozłącza aktywne urządzenie, aby zatrzymać zależne od niego podglądy i uchwyty.

## Weryfikacja
1. Wykonaj Clean Solution i Rebuild Solution.
2. Uruchom wszystkie testy jednostkowe.
3. W konfiguracji Dahua dodaj urządzenie, wpisz poprawne dane i zapisz.
4. Sprawdź odrzucanie pustej nazwy, błędnego adresu, nieprawidłowych portów i pustych danych logowania.
5. Zmień konfigurację aktualnie połączonego urządzenia i sprawdź, czy rozłączenie oraz ponowne połączenie działają prawidłowo.
6. Usuń urządzenie i sprawdź, czy lista oraz zapis konfiguracji są aktualizowane.
