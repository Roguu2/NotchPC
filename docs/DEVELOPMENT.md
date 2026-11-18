# NotchPC – dla programistów

Nakładka dla Windows 11 inspirowana „wyspą” z iPhone’a (WPF, .NET 10). Kod napisany od zera; nie jest związana z Apple ani z innymi aplikacjami tego typu. Licencje bibliotek: [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md).

## Wymagania

- Windows 11 (działa też na Windows 10 2004+, ale nie było testowane)
- .NET 10 SDK do budowania, a do samego uruchamiania wystarczą .NET 10 Desktop Runtime i ASP.NET Core Runtime 10 (pilot w telefonie)
- Microsoft Edge WebView2 Runtime (panel web; jest w Windows 11)

## Budowanie i uruchamianie

```bash
dotnet build NotchPC.sln
```

```bash
dotnet test tests/NotchPC.Tests
```

```bash
dotnet run --project src/NotchPC.App
```

Plik wykonywalny: `src/NotchPC.App/bin/Debug/net10.0-windows10.0.22621.0/NotchPC.exe`.

### Wersja do rozdania (instalator)

```powershell
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
```

W folderze `publish` powstają:
- `NotchPC-<wersja>-Setup.exe` – instalator: instaluje dla bieżącego użytkownika w `%LocalAppData%\Programs\NotchPC` (bez uprawnień administratora), dodaje skrót w menu Start (i opcjonalnie na pulpicie), opcjonalnie autostart, a w Ustawieniach → Aplikacje pozycję do odinstalowania. Program ma w sobie .NET, więc nic więcej nie trzeba instalować. Aktualizacja = uruchomienie nowszego instalatora (ustawienia zostają).
- `NotchPC-<wersja>-win-x64-portable.zip` – to samo bez instalacji (rozpakuj i uruchom `NotchPC.exe`).

Plik nie ma podpisu cyfrowego (to płatny certyfikat), więc Windows przy pierwszym uruchomieniu pokaże „System Windows chronił ten komputer” → „Więcej informacji” → „Uruchom mimo to”. Powiadomienia Windows na żywo wymagają włączonego Trybu dewelopera (Ustawienia → Powiadomienia w NotchPC); bez niego reszta działa normalnie.

## Gdzie co jest

| Ścieżka | Zawartość |
|---|---|
| `src/NotchPC.Core` | logika bez UI: ustawienia (JSON), języki (`Localization/Strings*.resx`), pozycjonowanie |
| `src/NotchPC.Platform` | Win32: monitory i DPI, wykrywanie pełnego ekranu, styl okna nakładki, autostart |
| `src/NotchPC.App` | WPF: notch, widgety na pulpicie, okno ustawień (WPF-UI), ikona w trayu |
| `src/NotchPC.Sensors` | osobny proces czujników (LibreHardwareMonitor), kopiowany przy budowaniu do `sensors\` obok `NotchPC.exe` |
| `tests/NotchPC.Tests` | testy xUnit dla Core |
| `%AppData%\NotchPC\settings.json` | ustawienia użytkownika |
| `%AppData%\NotchPC\logs\notchpc.log` | log (błędy i zmiany stanu pełnego ekranu) |
| `%LocalAppData%\NotchPC\lyrics-cache` | pobrane teksty piosenek z LRCLIB (można bezpiecznie usunąć) |
| `%AppData%\NotchPC\clipboard` | historia schowka, szyfrowana DPAPI (usunięcie folderu = pusta historia) |
| `Obrazy\Screenshots` | zrzuty ekranu z notcha (folder do zmiany w ustawieniach) |
| `%LocalAppData%\NotchPC\WebView2` | dane panelu web (logowania, ciasteczka); usunięcie = wylogowanie ze stron w panelu |
| `%LocalAppData%\Programs\NotchPC\identity` | manifest tożsamości pakietu (powiadomienia Windows na żywo) |
| `scripts/publish.ps1` | budowanie instalatora i wersji przenośnej |
| `src/NotchPC.Setup` | instalator (.NET Framework 4.7.2, wbudowany w Windows) |

## Nowy język

1. Skopiuj `src/NotchPC.Core/Localization/Strings.pl.resx` jako `Strings.<kod>.resx` i przetłumacz wartości.
2. Dopisz kulturę w `Loc.SupportedCultures` i w `SatelliteResourceLanguages` w `Directory.Build.props`.
3. Test `LocalizationTests` sprawdzi, czy nie brakuje kluczy ani `{0}`.
