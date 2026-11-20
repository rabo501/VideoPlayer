# VideoPlayer 1.7.0

Windows desktop application (WPF, .NET 8, x64) for reviewing and analyzing video recordings.

## Main features

- Local video playback using mpv.
- Frame navigation, timeline, clip ranges, screenshots and export through FFmpeg.
- Event markers and local SQLite history/session storage.
- Local license-plate OCR using Tesseract; the operator verifies and confirms the suggested plate.
- Motion detection with a selectable region of interest (ROI), configurable sensitivity and event duration, saved time ranges and motion-area preview.
- Dahua recorder live view and archive features available in the project.

## Ongoing development

- [Stage 6 — reporting, local case archive and release 1.7 plan](Dokumentacja/ETAP-6-Raportowanie-i-wydanie-1.7-Wersja-1.7.md)

## Documentation

- [User and deployment manual — 1.7.0](Dokumentacja/VideoPlayer-1.7.0-Dokumentacja.md)
- [Release notes — 1.7.0](Dokumentacja/VideoPlayer-1.7.0-Informacje-o-wydaniu.md)
- [Publication checklist — 1.7.0](Dokumentacja/VideoPlayer-1.7.0-Lista-publikacyjna.md)

Older Dahua development notes and documents labelled 1.3.2 are historical references, not the current user manual.

## Build requirements

- Windows and Visual Studio with .NET 8 desktop/WPF support.
- x64 target platform.
- mpv and FFmpeg runtime folders in the project directory.
- Local OCR runtime files: Tesseract wrapper, native x64 Tesseract/Leptonica libraries, English trained data and Microsoft Visual C++ Redistributable 2015–2022 (x64).

See the deployment manual and publication checklist for the complete runtime layout and pre-release tests.

## Data and privacy

OCR runs locally; the selected frame/crop is processed by the local Tesseract engine. Application history, markers and session data are stored in the local VideoPlayer.db file next to the application. Keep a backup of this database when upgrading.

## Release status

Version metadata is set to 1.7.0. A Windows x64 Release build and final target-machine validation must be completed before distributing a binary package.
