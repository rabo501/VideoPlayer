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
        private async Task UpdateMediaInfoAsync(string sourcePath)
        {
            try
            {
                string ffmpegPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
                if (!File.Exists(ffmpegPath))
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (_sourceFilePath == sourcePath)
                            MediaInfoText.Text = "Brak ffmpeg.exe — nie można odczytać parametrów nagrania.";
                    });
                    return;
                }

                var psi = new ProcessStartInfo { FileName = ffmpegPath, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                psi.ArgumentList.Add("-hide_banner");
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(sourcePath);
                using var process = new Process { StartInfo = psi };
                process.Start();
                string output = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                var info = new MediaInfo { FileSize = new FileInfo(sourcePath).Length, Duration = ParseDurationFromFfmpeg(output) };
                var videoLine = System.Text.RegularExpressions.Regex.Match(output, @"Stream #\S+.*?: Video:\s*([^, ]+)(.*)");
                if (videoLine.Success)
                {
                    info.VideoCodec = videoLine.Groups[1].Value;
                    string details = videoLine.Groups[2].Value;
                    var resolution = System.Text.RegularExpressions.Regex.Match(details, @"(\d{2,5})x(\d{2,5})");
                    if (resolution.Success) info.Resolution = resolution.Groups[1].Value + "×" + resolution.Groups[2].Value;
                    var fps = System.Text.RegularExpressions.Regex.Match(details, @"([\d.]+)\s*fps");
                    if (fps.Success) info.Fps = fps.Groups[1].Value;
                }
                var audio = System.Text.RegularExpressions.Regex.Match(output, @"Stream #\S+.*?: Audio:\s*([^, ]+)");
                if (audio.Success) info.AudioCodec = audio.Groups[1].Value;
                var bitrate = System.Text.RegularExpressions.Regex.Match(output, @"bitrate:\s*(\d+)\s*kb/s");
                if (bitrate.Success) info.Bitrate = bitrate.Groups[1].Value + " kb/s";

                await Dispatcher.InvokeAsync(() =>
                {
                    if (_closing || !string.Equals(_sourceFilePath, sourcePath, StringComparison.OrdinalIgnoreCase)) return;
                    _currentMediaInfo = info;
                    UpdateEstimatedSizeText();
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!_closing && string.Equals(_sourceFilePath, sourcePath, StringComparison.OrdinalIgnoreCase))
                        MediaInfoText.Text = "Nie udało się odczytać parametrów: " + ex.Message;
                });
            }
        }

        private static double ParseDurationFromFfmpeg(string output)
        {
            var match = System.Text.RegularExpressions.Regex.Match(output, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
            if (!match.Success) return 0;
            return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 3600 +
                   double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * 60 +
                   double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        }

        private void UpdateEstimatedSizeText()
        {
            if (_currentMediaInfo == null || string.IsNullOrWhiteSpace(_sourceFilePath)) return;
            double fullDuration = _currentMediaInfo.Duration;
            bool hasRange = _clipStart.HasValue && _clipEnd.HasValue && _clipEnd.Value > _clipStart.Value;
            double selectedDuration = hasRange ? _clipEnd!.Value - _clipStart!.Value : fullDuration;
            string estimate = fullDuration > 0
                ? FormatFileSize((long)(_currentMediaInfo.FileSize * Math.Clamp(selectedDuration / fullDuration, 0, 1)))
                : "nie można oszacować";
            string rangeLabel = hasRange ? $"zaznaczenie ({FormatTime(selectedDuration)})" : "całe nagranie";
            MediaInfoText.Text =
                $"Źródło: {_currentMediaInfo.VideoCodec}, {_currentMediaInfo.Resolution}, {_currentMediaInfo.Fps} FPS, bitrate {_currentMediaInfo.Bitrate}, audio: {_currentMediaInfo.AudioCodec}; " +
                $"czas {FormatTime(fullDuration)}, rozmiar {FormatFileSize(_currentMediaInfo.FileSize)}.\n" +
                $"Szacowany rozmiar {rangeLabel} przy podobnym bitrate: około {estimate}. To przybliżenie; rozmiar MP4 po ponownym kodowaniu może się różnić." +
                (string.IsNullOrWhiteSpace(_lastExportSummary) ? "" : "\n" + _lastExportSummary);
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 0) bytes = 0;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
            return $"{size:0.##} {units[unit]}";
        }

        private void LoadHistory()
        {
            try
            {
                if (!File.Exists(HistoryFilePath)) return;
                var history = JsonSerializer.Deserialize<AppHistory>(File.ReadAllText(HistoryFilePath));
                if (history == null) return;
                _recentFiles.Clear();
                _recentFiles.AddRange(history.RecentFiles.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Take(10));
                _recentScreenshots.Clear();
                _recentScreenshots.AddRange(history.RecentScreenshots.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Take(30));
            }
            catch { /* Uszkodzona historia nie blokuje uruchomienia aplikacji. */ }
        }

        private void SaveHistory()
        {
            try
            {
                Directory.CreateDirectory(_historyDirectory);
                File.WriteAllText(HistoryFilePath, JsonSerializer.Serialize(new AppHistory
                {
                    RecentFiles = _recentFiles.Take(10).ToList(),
                    RecentScreenshots = _recentScreenshots.Take(30).ToList()
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { StatusText.Text = "Nie udało się zapisać historii: " + ex.Message; }
        }

        private void AddRecentFile(string path)
        {
            _recentFiles.RemoveAll(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
            _recentFiles.Insert(0, path);
            if (_recentFiles.Count > 10) _recentFiles.RemoveRange(10, _recentFiles.Count - 10);
            SaveHistory();
            RefreshHistoryControls();
        }

        private void AddRecentScreenshot(string path)
        {
            _recentScreenshots.RemoveAll(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
            _recentScreenshots.Insert(0, path);
            if (_recentScreenshots.Count > 30) _recentScreenshots.RemoveRange(30, _recentScreenshots.Count - 30);
            SaveHistory();
            RefreshHistoryControls();
        }

        private void RefreshHistoryControls()
        {
            if (RecentFilesBox == null || ScreenshotsBox == null) return;
            RecentFilesBox.Items.Clear();
            foreach (string path in _recentFiles.Where(File.Exists))
                RecentFilesBox.Items.Add(new ComboBoxItem { Content = Path.GetFileName(path), Tag = path, ToolTip = path });
            if (RecentFilesBox.Items.Count > 0) RecentFilesBox.SelectedIndex = 0;
            ScreenshotsBox.Items.Clear();
            foreach (string path in _recentScreenshots.Where(File.Exists))
                ScreenshotsBox.Items.Add(new ComboBoxItem { Content = Path.GetFileName(path), Tag = path, ToolTip = path });
            if (ScreenshotsBox.Items.Count > 0) ScreenshotsBox.SelectedIndex = 0;
        }

        private async void OpenRecent_Click(object sender, RoutedEventArgs e)
        {
            if (RecentFilesBox.SelectedItem is not ComboBoxItem item || item.Tag is not string path || !File.Exists(path))
            { MessageBox.Show(this, "Wybierz istniejący plik z historii.", "Historia nagrań"); return; }
            if (!_ready) { MessageBox.Show(this, "Silnik odtwarzacza nie jest gotowy.", "Odtwarzacz"); return; }
            _sourceFilePath = path;
            _fileName = Path.GetFileName(path);
            _clipStart = null; _clipEnd = null; _currentMediaInfo = null;
            UpdateClipRangeText();
            _eofReached = false; _dragging = false; _updatingSlider = true;
            try { PositionSlider.Value = 0; PositionSlider.Maximum = 1; PositionSlider.IsEnabled = false; }
            finally { _updatingSlider = false; }
            PositionText.Text = "00:00:00"; DurationText.Text = "00:00:00"; StatusText.Text = _fileName;
            AddRecentFile(path);
            MediaInfoText.Text = "Odczytywanie parametrów nagrania…";
            _ = UpdateMediaInfoAsync(path);
            await SendAsync("loadfile", path, "replace");
            await SendAsync("set_property", "speed", GetSelectedSpeed());
            await SendAsync("set_property", "pause", false);
        }

        private void OpenScreenshot_Click(object sender, RoutedEventArgs e)
        {
            if (ScreenshotsBox.SelectedItem is not ComboBoxItem item || item.Tag is not string path || !File.Exists(path))
            { MessageBox.Show(this, "Nie ma dostępnego zrzutu do otwarcia.", "Zrzuty klatek"); return; }
            try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Nie można otworzyć zrzutu", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void OpenScreenshotsFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string? selected = (ScreenshotsBox.SelectedItem as ComboBoxItem)?.Tag as string;
                string folder = !string.IsNullOrWhiteSpace(selected) && File.Exists(selected)
                    ? Path.GetDirectoryName(selected)!
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "VideoPlayer");
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Nie można otworzyć folderu", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private async Task<(double Duration, bool HasAudio)> ProbeMediaAsync(
            string ffmpegPath,
            string sourcePath)
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(sourcePath);

            using var process = new Process { StartInfo = psi };
            process.Start();

            string output = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            var durationMatch = System.Text.RegularExpressions.Regex.Match(
                output,
                @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");

            if (!durationMatch.Success)
            {
                throw new Exception(
                    "FFmpeg nie rozpoznał czasu trwania pliku. " +
                    "Sprawdź, czy nagranie jest poprawne i obsługiwane.");
            }

            double hours = double.Parse(
                durationMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            double minutes = double.Parse(
                durationMatch.Groups[2].Value, CultureInfo.InvariantCulture);
            double seconds = double.Parse(
                durationMatch.Groups[3].Value, CultureInfo.InvariantCulture);

            double duration = hours * 3600 + minutes * 60 + seconds;

            bool hasAudio = System.Text.RegularExpressions.Regex.IsMatch(
                output,
                @"Stream #\S+.*?: Audio:");

            return (duration, hasAudio);
        }
    }
}
