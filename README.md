# VideoPlayer 1.8.0

Windows desktop application (WPF, .NET 8, x64) for reviewing and analyzing video recordings.

## Main features

- Local video playback using mpv.
- Frame navigation, timeline, clip ranges, screenshots and export through FFmpeg.
- Event markers, local case archive (multi-recording cases) and local SQLite history/session storage.
- Local license-plate OCR using Tesseract (single shared `TesseractEngine` per session, raw BGRA pixel pipeline without intermediate disk/PNG files) and parallel multi-frame video plate search (`ffmpeg -> stdout` + `System.Threading.Channels`); the operator verifies and confirms the suggested plate.
- Motion detection with a selectable region of interest (ROI), configurable sensitivity and event duration, saved time ranges and motion-area preview.
- PDF and CSV event/case reports with parallel PDF thumbnail extraction.
- Dahua recorder live view, multi-channel archive playback and DAV-to-MP4 download via `DahuaSessionService`.
- Modular MVVM architecture (`CommunityToolkit.Mvvm` ViewModels + isolated services + `VideoPlayer.Tests` xUnit suite).

## Documentation

- [Documentation index and obsolete-file review — 1.8.0](Dokumentacja/INDEKS-DOKUMENTACJI-1.8.0.md)
- [Development chronology through 1.8.0](Dokumentacja/HISTORIA-ZMIAN-VideoPlayer-do-1.8.0.md)

- [User and deployment manual — 1.8.0](Dokumentacja/VideoPlayer-1.8.0-Dokumentacja.md)
- [Release notes — 1.8.0](Dokumentacja/VideoPlayer-1.8.0-Informacje-o-wydaniu.md)
- [Publication checklist — 1.8.0](Dokumentacja/VideoPlayer-1.8.0-Lista-publikacyjna.md)
- [Dahua archive technical documentation](Dokumentacja/Przebudowa-Archiwum-Dahua.md)
- [Dahua credentials protection (DPAPI)](Dokumentacja/Ochrona-danych-logowania-Dahua.md)

Older documents labelled 1.3.2 or 1.7 are historical references, not the current user manual.

## Build and test requirements

- Windows and Visual Studio (or .NET 8 SDK) with .NET 8 desktop/WPF support.
- x64 target platform.
- mpv and FFmpeg runtime folders in the project directory.
- Local OCR runtime files: Tesseract wrapper, native x64 Tesseract/Leptonica libraries, English trained data and Microsoft Visual C++ Redistributable 2015–2022 (x64).

Build and test commands:

```bat
dotnet test VideoPlayer.slnx -c Release
dotnet build VideoPlayer.csproj -c Release -p:Platform=x64
```

See the deployment manual and publication checklist for the complete runtime layout and pre-release tests.

## Data and privacy

OCR runs locally; the selected frame/crop is processed by the local Tesseract engine. Application history, markers, case links and session data are stored in the local `VideoPlayer.db` file next to the application. Keep a backup of this database when upgrading.

## Release status

Version metadata is set to **1.8.0**. A Windows x64 Release build, unit test run (`VideoPlayer.Tests`), and final target-machine validation must be completed before distributing a binary package.
