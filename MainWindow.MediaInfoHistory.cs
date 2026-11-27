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
        private Task UpdateMediaInfoAsync(string sourcePath) =>
            ViewModel.Export.LoadMediaInfoAsync(sourcePath);

        private void RecordRecentFile(string path)
        {
            string? error = ViewModel.History.AddRecentFile(path);
            if (!string.IsNullOrEmpty(error) && !_closing)
                ViewModel.StatusMessage = "Nie udało się zapisać historii: " + error;
        }

        private void RecordRecentScreenshot(string path)
        {
            string? error = ViewModel.History.AddRecentScreenshot(path);
            if (!string.IsNullOrEmpty(error) && !_closing)
                ViewModel.StatusMessage = "Nie udało się zapisać historii: " + error;
        }

        private async void OpenRecent_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.History.SelectedRecentFile is { } entry)
                await OpenMediaAsync(entry.FullPath);
        }

        private void OpenScreenshot_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.History.SelectedRecentScreenshot is not { } entry || !File.Exists(entry.FullPath))
            { MessageBox.Show(this, "Nie ma dostępnego zrzutu do otwarcia.", "Zrzuty klatek"); return; }
            try { Process.Start(new ProcessStartInfo { FileName = entry.FullPath, UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Nie można otworzyć zrzutu", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void OpenScreenshotsFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string? selected = ViewModel.History.SelectedRecentScreenshot?.FullPath;
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
