using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

using Forms = System.Windows.Forms;

namespace VideoPlayer
{
    public partial class MainWindow
    {
        private async void ExportClip_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (_exporting)
                return;

            if (string.IsNullOrWhiteSpace(_sourceFilePath) ||
                !File.Exists(_sourceFilePath))
            {
                MessageBox.Show(
                    this,
                    "Najpierw otwórz lokalny plik filmu.",
                    "Eksport fragmentu");
                return;
            }

            if (!_clipStart.HasValue ||
                !_clipEnd.HasValue ||
                _clipEnd.Value <= _clipStart.Value)
            {
                MessageBox.Show(
                    this,
                    "Zaznacz początek i koniec fragmentu.",
                    "Eksport fragmentu");
                return;
            }

            string ffmpegPath = _ffmpegService.ExecutablePath;

            if (!File.Exists(ffmpegPath))
            {
                MessageBox.Show(
                    this,
                    "Nie znaleziono ffmpeg.exe w folderze ffmpeg.",
                    "Brak FFmpeg",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            // Zapamiętujemy źródło i zakres na czas eksportu.
            string sourcePath = _sourceFilePath;
            double start = _clipStart.Value;
            double duration = _clipEnd.Value - start;
            bool streamCopyRequested = StreamCopyCheckBox.IsChecked == true;

            if (streamCopyRequested)
            {
                MessageBoxResult confirm = MessageBox.Show(
                    this,
                    "Tryb bez ponownego kodowania jest bardzo szybki, ale początek i koniec mogą zostać dopasowane do klatek kluczowych. Niektóre kodeki mogą nie działać poprawnie w MP4. Kontynuować?",
                    "Eksport bez ponownego kodowania",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes)
                    return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Zapisz zaznaczony fragment",
                Filter = "Film MP4 (*.mp4)|*.mp4",
                DefaultExt = ".mp4",
                AddExtension = true,
                OverwritePrompt = true,
                FileName =
                    Path.GetFileNameWithoutExtension(sourcePath) +
                    "_fragment.mp4"
            };

            if (dialog.ShowDialog(this) != true)
                return;

            string outputPath = Path.GetFullPath(dialog.FileName);

            if (string.Equals(
                Path.GetFullPath(sourcePath),
                outputPath,
                StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    this,
                    "Wybierz inną nazwę niż nazwa filmu źródłowego.",
                    "Eksport fragmentu",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            string temporaryPath = Path.Combine(
                Path.GetDirectoryName(outputPath)!,
                "VideoPlayer_export_" +
                Guid.NewGuid().ToString("N") +
                ".mp4");

            _exporting = true;
            _cancelExportRequested = false;
            _exportStopwatch = Stopwatch.StartNew();
            _activeOperationName = Path.GetFileName(sourcePath);
            _activeOperationDuration = duration;

            ExportClipButton.IsEnabled = false;
            RemoveClipButton.IsEnabled = false;
            ExportProfileBox.IsEnabled = false;
            StreamCopyCheckBox.IsEnabled = false;
            CancelExportButton.Visibility = Visibility.Visible;
            CancelExportButton.IsEnabled = true;
            ExportProgressBar.Value = 0;
            ExportProgressText.Text = "Analiza źródła i przygotowanie eksportu…";
            ExportOperationText.Text = $"Plik: {_activeOperationName} | Zakres: {FormatTime(start)}–{FormatTime(start + duration)} | Długość: {FormatTime(duration)}";

            Process? process = null;

            try
            {
                var startInfo = _ffmpegService.CreateStartInfo(
                    redirectStandardOutput: true,
                    redirectStandardError: true);

                bool streamCopy = StreamCopyCheckBox.IsChecked == true;
                string profile = (ExportProfileBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "standard";

                var arguments = new System.Collections.Generic.List<string>
                {
                    "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                    "-progress", "pipe:1", "-stats_period", "0.25", "-nostats",
                    "-ss", start.ToString("R", CultureInfo.InvariantCulture),
                    "-i", sourcePath,
                    "-t", duration.ToString("R", CultureInfo.InvariantCulture),
                    "-map", "0:v:0", "-map", "0:a:0?"
                };

                if (streamCopy)
                {
                    // Tryb bez ponownego kodowania: szybki, ale cięcie jest ograniczone
                    // do granic pakietów/klatek kluczowych, a kodeki muszą pasować do MP4.
                    arguments.AddRange(new[] { "-c:v", "copy", "-c:a", "copy", "-avoid_negative_ts", "make_zero" });
                }
                else
                {
                    string preset;
                    string crf;
                    switch (profile)
                    {
                        case "fast": preset = "veryfast"; crf = "22"; break;
                        case "small": preset = "slow"; crf = "27"; break;
                        case "quality": preset = "slow"; crf = "16"; break;
                        default: preset = "medium"; crf = "20"; break;
                    }

                    arguments.AddRange(new[]
                    {
                        "-c:v", "libx264", "-preset", preset, "-crf", crf,
                        "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2", "-pix_fmt", "yuv420p",
                        "-c:a", "aac", "-b:a", "192k"
                    });
                }

                arguments.AddRange(new[] { "-sn", "-dn", "-map_chapters", "-1", "-movflags", "+faststart", temporaryPath });

                foreach (string argument in arguments)
                    startInfo.ArgumentList.Add(argument);

                process = new Process
                {
                    StartInfo = startInfo,
                    EnableRaisingEvents = true
                };

                if (!process.Start())
                    throw new InvalidOperationException(
                        "Nie udało się uruchomić FFmpeg.");

                _exportProcess = process;

                // Oba strumienie czytamy równolegle, aby uniknąć
                // zablokowania procesu przez pełny bufor.
                Task progressTask =
                    ReadFfmpegProgressAsync(process, duration);

                Task<string> errorTask =
                    process.StandardError.ReadToEndAsync();

                await process.WaitForExitAsync();
                await progressTask;

                string errors = await errorTask;

                if (_cancelExportRequested)
                    throw new OperationCanceledException(
                        "Eksport został anulowany.");

                if (process.ExitCode != 0)
                {
                    if (errors.Length > 4000)
                        errors = errors.Substring(errors.Length - 4000);

                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(errors)
                            ? "FFmpeg zakończył eksport błędem."
                            : errors.Trim());
                }

                if (!File.Exists(temporaryPath) ||
                    new FileInfo(temporaryPath).Length == 0)
                {
                    throw new IOException(
                        "Nie powstał poprawny plik wynikowy.");
                }

                // Dopiero teraz zastępujemy plik docelowy.
                File.Move(temporaryPath, outputPath, overwrite: true);

                ExportProgressBar.Value = 100;
                ExportProgressText.Text = "Eksport zakończony — 100%";
                long outputSize = new FileInfo(outputPath).Length;
                string profileDescription = streamCopy
                    ? "kopiowanie strumieni bez ponownego kodowania"
                    : profile switch
                    {
                        "fast" => "H.264, profil szybki (veryfast, CRF 22), audio AAC 192 kb/s",
                        "small" => "H.264, profil mniejszego pliku (slow, CRF 27), audio AAC 192 kb/s",
                        "quality" => "H.264, profil wysokiej jakości (slow, CRF 16), audio AAC 192 kb/s",
                        _ => "H.264, profil standardowy (medium, CRF 20), audio AAC 192 kb/s"
                    };
                _lastExportSummary = $"Ostatni eksport: {Path.GetFileName(outputPath)} — {profileDescription}, rzeczywisty rozmiar {FormatFileSize(outputSize)}.";
                ExportOperationText.Text = $"Zakończono: {Path.GetFileName(outputPath)} | Rozmiar: {FormatFileSize(outputSize)} | Czas operacji: {_exportStopwatch?.Elapsed.ToString(@"hh\:mm\:ss") ?? "—"}";
                UpdateEstimatedSizeText();
                StatusText.Text = "Zapisano fragment: " + outputPath;

                MessageBox.Show(
                    this,
                    "Fragment został zapisany:\n" + outputPath,
                    "Eksport zakończony",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
                ExportProgressText.Text = "Eksport anulowany — czyszczenie pliku tymczasowego…";
                ExportOperationText.Text = $"Anulowano operację dla pliku: {_activeOperationName}";
                StatusText.Text =
                    "Eksport anulowano. Plik tymczasowy zostanie usunięty.";
            }
            catch (Exception ex)
            {
                ExportProgressText.Text = "Eksport nie powiódł się.";
                ExportOperationText.Text = $"Błąd podczas przetwarzania: {_activeOperationName}";
                StatusText.Text = "Błąd eksportu.";

                MessageBox.Show(
                    this,
                    ex.Message,
                    "Błąd eksportu",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                _exportProcess = null;

                process?.Dispose();

                try
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
                catch (Exception ex)
                {
                    if (!_closing)
                    {
                        StatusText.Text =
                            "Nie udało się usunąć pliku tymczasowego: " +
                            ex.Message;
                    }
                }

                _exporting = false;
                _exportStopwatch?.Stop();
                _exportStopwatch = null;

                if (!_closing)
                {
                    ExportClipButton.IsEnabled = true;
                    RemoveClipButton.IsEnabled = true;
                    ExportProfileBox.IsEnabled = true;
                    StreamCopyCheckBox.IsEnabled = true;
                    CancelExportButton.IsEnabled = false;
                    CancelExportButton.Visibility = Visibility.Collapsed;
                }
            }
        }

        private async Task ReadFfmpegProgressAsync(
            Process process,
            double duration)
        {
            while (true)
            {
                string? line = await process.StandardOutput.ReadLineAsync();

                if (line == null)
                    break;

                if (line.StartsWith(
                    "out_time_us=",
                    StringComparison.Ordinal))
                {
                    string value = line.Substring("out_time_us=".Length);

                    if (long.TryParse(
                        value,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out long microseconds))
                    {
                        double seconds = microseconds / 1_000_000.0;

                        double percent = duration > 0
                            ? Math.Clamp(seconds / duration * 100.0, 0, 99.9)
                            : 0;

                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (_closing || _cancelExportRequested)
                                return;

                            ExportProgressBar.Value = percent;

                            double elapsed = _exportStopwatch?.Elapsed.TotalSeconds ?? 0;
                            double speed = elapsed > 0 ? seconds / elapsed : 0;
                            double remaining = speed > 0.001 ? Math.Max(0, duration - seconds) / speed : 0;
                            string eta = double.IsFinite(remaining) && remaining < 864000
                                ? TimeSpan.FromSeconds(remaining).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
                                : "obliczanie…";
                            ExportProgressText.Text =
                                $"Kodowanie: {percent:0.0}% ({FormatTime(seconds)} / {FormatTime(duration)}) | Pozostało: {eta}";
                        });
                    }
                }
                else if (line == "progress=end")
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (_closing || _cancelExportRequested)
                            return;

                        ExportProgressBar.Value = 100;
                        ExportProgressText.Text = "Finalizowanie pliku MP4…";
                        ExportOperationText.Text = $"Finalizowanie: {_activeOperationName} | upłynęło: {_exportStopwatch?.Elapsed.ToString(@"hh\:mm\:ss") ?? "—"}";
                    });
                }
            }
        }

        private async void RemoveClip_Click(object sender, RoutedEventArgs e)
        {
            if (_exporting)
            {
                MessageBox.Show("Inna operacja eksportu jest już wykonywana.");
                return;
            }

            if (string.IsNullOrWhiteSpace(_sourceFilePath) ||
                !File.Exists(_sourceFilePath))
            {
                MessageBox.Show("Najpierw otwórz nagranie.");
                return;
            }

            if (!_clipStart.HasValue || !_clipEnd.HasValue)
            {
                MessageBox.Show("Ustaw początek i koniec fragmentu do usunięcia.");
                return;
            }

            double start = _clipStart.Value;
            double end = _clipEnd.Value;

            if (start < 0 || end <= start)
            {
                MessageBox.Show("Nieprawidłowy zakres fragmentu.");
                return;
            }

            string ffmpegPath = _ffmpegService.ExecutablePath;

            if (!File.Exists(ffmpegPath))
            {
                MessageBox.Show("Nie znaleziono pliku:\n" + ffmpegPath);
                return;
            }

            // Odczytaj czas trwania i sprawdź, czy plik ma dźwięk.
            double mediaDuration;
            bool hasAudio;

            try
            {
                var probeInfo = await ProbeMediaAsync(_sourceFilePath);
                mediaDuration = probeInfo.Duration;
                hasAudio = probeInfo.HasAudio;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Nie udało się odczytać informacji o nagraniu:\n" + ex.Message);
                return;
            }

            const double tolerance = 0.15;

            if (mediaDuration <= 0 ||
                start >= mediaDuration ||
                end > mediaDuration + tolerance)
            {
                MessageBox.Show(
                    $"Zakres wykracza poza długość nagrania ({FormatTime(mediaDuration)}).");
                return;
            }

            end = Math.Min(end, mediaDuration);

            bool keepBefore = start > tolerance;
            bool keepAfter = end < mediaDuration - tolerance;

            if (!keepBefore && !keepAfter)
            {
                MessageBox.Show(
                    "Ten zakres obejmuje całe nagranie. " +
                    "Nie można utworzyć pustego pliku.");
                return;
            }

            string description;

            if (!keepBefore)
                description = $"Usunąć początek do {FormatTime(end)}?";
            else if (!keepAfter)
                description = $"Usunąć koniec od {FormatTime(start)}?";
            else
                description =
                    $"Usunąć fragment od {FormatTime(start)} do {FormatTime(end)}?";

            var confirm = MessageBox.Show(
                description +
                "\n\nPowstanie nowy plik. Oryginał pozostanie bez zmian.",
                "Usuwanie fragmentu",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
                return;

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Zapisz nagranie po usunięciu fragmentu",
                Filter = "Plik MP4 (*.mp4)|*.mp4",
                DefaultExt = ".mp4",
                AddExtension = true,
                FileName = System.IO.Path.GetFileNameWithoutExtension(
                    _sourceFilePath) + "_po_cieciu.mp4"
            };

            if (dialog.ShowDialog() != true)
                return;

            string outputPath = System.IO.Path.GetFullPath(dialog.FileName);
            string sourcePath = System.IO.Path.GetFullPath(_sourceFilePath);

            if (string.Equals(
                outputPath, sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Plik wynikowy nie może zastępować oryginału.");
                return;
            }

            string temporaryPath = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(outputPath)!,
                System.IO.Path.GetFileNameWithoutExtension(outputPath) +
                "_" + Guid.NewGuid().ToString("N") + ".tmp.mp4");

            string F(double value) =>
                value.ToString("0.######", CultureInfo.InvariantCulture);

            // Przycinanie obrazu. W razie potrzeby łączymy dwie pozostałe części.
            string filter;

            if (keepBefore && keepAfter)
            {
                filter =
                    $"[0:v:0]trim=start=0:end={F(start)},setpts=PTS-STARTPTS[v0];" +
                    $"[0:v:0]trim=start={F(end)},setpts=PTS-STARTPTS[v1];" +
                    "[v0][v1]concat=n=2:v=1:a=0[v]";
            }
            else if (keepBefore)
            {
                filter =
                    $"[0:v:0]trim=start=0:end={F(start)}," +
                    "setpts=PTS-STARTPTS[v]";
            }
            else
            {
                filter =
                    $"[0:v:0]trim=start={F(end)},setpts=PTS-STARTPTS[v]";
            }

            // Ścieżkę audio przetwarzamy tylko wtedy, gdy rzeczywiście istnieje.
            if (hasAudio)
            {
                if (keepBefore && keepAfter)
                {
                    filter +=
                        $";[0:a:0]atrim=start=0:end={F(start)},asetpts=PTS-STARTPTS[a0]" +
                        $";[0:a:0]atrim=start={F(end)},asetpts=PTS-STARTPTS[a1]" +
                        ";[a0][a1]concat=n=2:v=0:a=1[a]";
                }
                else if (keepBefore)
                {
                    filter +=
                        $";[0:a:0]atrim=start=0:end={F(start)}," +
                        "asetpts=PTS-STARTPTS[a]";
                }
                else
                {
                    filter +=
                        $";[0:a:0]atrim=start={F(end)}," +
                        "asetpts=PTS-STARTPTS[a]";
                }
            }

            var psi = _ffmpegService.CreateStartInfo(
                redirectStandardOutput: true,
                redirectStandardError: true);

            string[] args =
            {
         "-y",
        "-progress", "pipe:1",
        "-stats_period", "0.5",
        "-nostats",
        "-i", sourcePath,
        "-filter_complex", filter,
        "-map", "[v]"
    };

            if (hasAudio)
            {
                args = args.Concat(new[] { "-map", "[a]" }).ToArray();
            }

            args = args.Concat(new[]
            {
        "-c:v", "libx264",
        "-preset", "fast",
        "-crf", "20"
    }).ToArray();

            if (hasAudio)
            {
                args = args.Concat(new[] { "-c:a", "aac", "-b:a", "192k" }).ToArray();
            }

            args = args.Concat(new[]
            {
        "-movflags", "+faststart",
        temporaryPath
    }).ToArray();

            foreach (string arg in args)
                psi.ArgumentList.Add(arg);

            Process? process = null;

            try
            {
                _exporting = true;
                _cancelExportRequested = false;

                RemoveClipButton.IsEnabled = false;
                ExportClipButton.IsEnabled = false;

                ExportProgressBar.Value = 0;
                ExportProgressText.Text = "Przygotowywanie nagrania…";
                ExportProgressBar.Visibility = Visibility.Visible;
                ExportProgressText.Visibility = Visibility.Visible;
                CancelExportButton.Visibility = Visibility.Visible;
                CancelExportButton.IsEnabled = true;

                StatusText.Text = "Usuwanie fragmentu nagrania…";

                process = new Process
                {
                    StartInfo = psi,
                    EnableRaisingEvents = true
                };

                process.Start();
                _exportProcess = process;

                // Czytamy oba strumienie równolegle, aby nie blokować FFmpeg.
                Task progressTask = ReadFfmpegProgressAsync(
                    process,
                    mediaDuration - (end - start));

                Task<string> errorTask =
                    process.StandardError.ReadToEndAsync();

                await process.WaitForExitAsync();
                await progressTask;

                string errors = await errorTask;

                if (_cancelExportRequested)
                    throw new OperationCanceledException("Eksport anulowano.");

                if (process.ExitCode != 0)
                    throw new Exception("FFmpeg zgłosił błąd:\n" + errors);

                if (!File.Exists(temporaryPath) ||
                    new FileInfo(temporaryPath).Length == 0)
                {
                    throw new Exception("Nie utworzono prawidłowego pliku wynikowego.");
                }

                File.Move(temporaryPath, outputPath, overwrite: true);

                StatusText.Text = "Zapisano nagranie po usunięciu fragmentu.";

                MessageBox.Show(
                    $"Operacja zakończona.\n\nPlik:\n{outputPath}\n\n" +
                    (hasAudio ? "Dźwięk został zachowany." :
                        "Nagranie nie zawierało dźwięku — zapisano sam obraz.") +
                    "\nOryginał nie został zmieniony.",
                    "Gotowe",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            catch (OperationCanceledException)
            {
                StatusText.Text = "Operacja anulowana.";
                ExportProgressText.Text = "Anulowano usuwanie fragmentu.";
            }

            catch (Exception ex)
            {
                StatusText.Text = "Błąd usuwania fragmentu.";

                MessageBox.Show(
                    ex.Message,
                    "Błąd",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                process?.Dispose();

                try
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                    // Jeśli plik jest zablokowany, pozostaje do ręcznego usunięcia.
                }

                _exportProcess = null;

                ExportProgressBar.Value = 0;
                ExportProgressBar.Visibility = Visibility.Collapsed;
                ExportProgressText.Visibility = Visibility.Collapsed;
                CancelExportButton.Visibility = Visibility.Collapsed;
                CancelExportButton.IsEnabled = false;

                RemoveClipButton.IsEnabled = true;
                ExportClipButton.IsEnabled = true;
                _exporting = false;
            }
        }

        private void CancelExport_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (!_exporting || _exportProcess == null)
                return;

            _cancelExportRequested = true;
            CancelExportButton.IsEnabled = false;

            ExportProgressText.Text = "Anulowanie eksportu…";
            StatusText.Text = "Zatrzymywanie FFmpeg…";

            try
            {
                if (!_exportProcess.HasExited)
                    _exportProcess.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Proces zakończył się przed anulowaniem.
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                StatusText.Text =
                    "Nie udało się zatrzymać FFmpeg: " + ex.Message;
            }
        }

        private void Window_Closing(
            object? sender,
            CancelEventArgs e)
        {
            if (_exporting)
            {
                MessageBoxResult result = MessageBox.Show(
                    this,
                    "Eksport jest w toku. Zamknięcie aplikacji przerwie operację i usunie nieukończony plik tymczasowy. Czy anulować eksport i zamknąć program?",
                    "Eksport w toku",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                _cancelExportRequested = true;
                try
                {
                    if (_exportProcess != null && !_exportProcess.HasExited)
                        _exportProcess.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }

            _closing = true;
            try
            {
                if (_exportProcess != null && !_exportProcess.HasExited)
                    _exportProcess.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }

            _ready = false;
            _ = _mpvService.DisposeAsync();
        }
    }
}
