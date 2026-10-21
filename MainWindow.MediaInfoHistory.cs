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
                MediaInfoData info = await _mediaInfoService.ReadAsync(sourcePath);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (_closing || !string.Equals(_sourceFilePath, sourcePath, StringComparison.OrdinalIgnoreCase))
                        return;
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
            AppHistoryData history = _historyService.Load();
            _recentFiles.Clear();
            _recentFiles.AddRange(history.RecentFiles);
            _recentScreenshots.Clear();
            _recentScreenshots.AddRange(history.RecentScreenshots);
        }

        private void SaveHistory()
        {
            try
            {
                _historyService.Save(new AppHistoryData
                {
                    RecentFiles = _recentFiles,
                    RecentScreenshots = _recentScreenshots
                });
            }
            catch (Exception ex)
            {
                if (!_closing) StatusText.Text = "Nie udało się zapisać historii: " + ex.Message;
            }
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
            if (RecentFilesBox.SelectedItem is ComboBoxItem item && item.Tag is string path)
                await OpenMediaAsync(path);
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

        private Task<(double Duration, bool HasAudio)> ProbeMediaAsync(
            string sourcePath)
        {
            // Używamy wspólnej usługi FFmpeg, aby ścieżka i obsługa błędów
            // były takie same dla operacji na nagraniu.
            return _ffmpegService.ProbeAsync(sourcePath);
        }

    }
}
