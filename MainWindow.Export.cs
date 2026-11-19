using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace VideoPlayer
{
    public partial class MainWindow
    {
        private bool CanOperate() => !_exporting && !_opening && !_shutdownRequested && _exports != null && _sourceFilePath != null && File.Exists(_sourceFilePath);
        private bool ValidRange() => _clipStart.HasValue && _clipEnd.HasValue && _clipEnd.Value > _clipStart.Value;
        private string SelectedProfile => (ExportProfileBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "standard";
        private bool ConfirmCopy(bool copy) => !copy || MessageBox.Show(this, "Kopiowanie strumieni może przesunąć granice cięcia. Kodeki muszą być zgodne z MP4. Kontynuować?", "Eksport", MessageBoxButton.YesNo) == MessageBoxResult.Yes;

        private string? ChooseOutput(string title, string proposed, string extension)
        {
            var d = new Microsoft.Win32.SaveFileDialog { Title = title, Filter = extension.ToUpperInvariant() + "|*." + extension, DefaultExt = "." + extension, AddExtension = true, OverwritePrompt = true, FileName = proposed };
            if (d.ShowDialog(this) != true) return null;
            if (File.Exists(d.FileName)) { MessageBox.Show(this, "Dla bezpieczeństwa wybierz nową nazwę. Ta wersja nie nadpisuje istniejących wyników.", "Zapis"); return null; }
            return d.FileName;
        }

        private IProgress<ExportProgress> OperationProgress(CancellationToken token, string prefix = "", int index = 0, int total = 1)
            => new Progress<ExportProgress>(p =>
            {
                if (_closing || token.IsCancellationRequested || _operationCts == null || _operationCts.Token != token) return;
                ExportProgressBar.Value = (index + p.Percent / 100.0) / total * 100;
                ExportProgressText.Text = prefix + p.Detail;
            });

        private async Task RunOperationAsync(string title, Func<CancellationToken, Task<string>> work)
        {
            if (!CanOperate()) return;
            _exporting = true;
            _operationCts = new CancellationTokenSource();
            SetOperationUi(true);
            ExportProgressBar.Value = 0;
            ExportProgressText.Text = "Przygotowanie…";
            ExportOperationText.Text = title;
            try
            {
                Task<string> task = work(_operationCts.Token);
                _operationTask = task;
                string result = await task;
                ExportProgressBar.Value = 100;
                ExportProgressText.Text = "Zakończono";
                StatusText.Text = result;
                _lastExportSummary = result;
                UpdateEstimatedSizeText();
            }
            catch (OperationCanceledException)
            {
                ExportProgressText.Text = "Anulowano";
                StatusText.Text = "Operacja anulowana. Ukończone wcześniej pliki eksportu seryjnego pozostają w folderze wynikowym.";
            }
            catch (Exception ex)
            {
                ExportProgressText.Text = "Błąd operacji";
                StatusText.Text = ex.Message;
                if (!_shutdownRequested) MessageBox.Show(this, ex.Message, title);
            }
            finally
            {
                _operationTask = null;
                _operationCts?.Dispose();
                _operationCts = null;
                _exporting = false;
                SetOperationUi(false);
                SaveSession();
            }
        }

        private void SetOperationUi(bool busy)
        {
            ExportProgressBar.Visibility = Visibility.Visible;
            ExportProgressText.Visibility = Visibility.Visible;
            ExportClipButton.IsEnabled = !busy;
            RemoveClipButton.IsEnabled = !busy;
            ExportAllButton.IsEnabled = !busy;
            CsvReportButton.IsEnabled = !busy;
            PdfReportButton.IsEnabled = !busy;
            ExportProfileBox.IsEnabled = !busy;
            StreamCopyCheckBox.IsEnabled = !busy;
            CancelExportButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            CancelExportButton.IsEnabled = busy;
        }

        private async void ExportClip_Click(object sender, RoutedEventArgs e)
        {
            if (!CanOperate() || !ValidRange()) { StatusText.Text = "Otwórz film i ustaw poprawny zakres."; return; }
            string source = _sourceFilePath!;
            double start = _clipStart!.Value, end = _clipEnd!.Value;
            bool copy = StreamCopyCheckBox.IsChecked == true;
            string profile = SelectedProfile;
            if (!ConfirmCopy(copy)) return;
            string? output = ChooseOutput("Zapisz fragment", Path.GetFileNameWithoutExtension(source) + "_fragment.mp4", "mp4");
            if (output == null) return;
            await RunOperationAsync("Eksport: " + Path.GetFileName(source), async ct =>
            {
                await _exports.ExportAsync(source, output, start, end, profile, copy, OperationProgress(ct), ct);
                long size = new FileInfo(output).Length;
                RecordExport(source, output, "Eksport fragmentu", start, end, $"Profil: {profile}; kopiowanie strumieni: {(copy ? "tak" : "nie")}; rozmiar: {FormatFileSize(size)}");
                return "Zapisano: " + output + " | " + FormatFileSize(size);
            });
        }

        private async void RemoveClip_Click(object sender, RoutedEventArgs e)
        {
            if (!CanOperate() || !ValidRange()) { StatusText.Text = "Otwórz film i ustaw poprawny zakres."; return; }
            string source = _sourceFilePath!;
            double start = _clipStart!.Value, end = _clipEnd!.Value;
            if (MessageBox.Show(this, "Powstanie nowy film bez zaznaczenia. Oryginał pozostanie bez zmian. Kontynuować?", "Usuwanie fragmentu", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            string? output = ChooseOutput("Zapisz film bez zaznaczenia", Path.GetFileNameWithoutExtension(source) + "_po_cieciu.mp4", "mp4");
            if (output == null) return;
            await RunOperationAsync("Usuwanie fragmentu: " + Path.GetFileName(source), async ct =>
            {
                var info = await _exports.ProbeAsync(source, ct);
                await _exports.RemoveAsync(source, output, start, end, info.Duration, info.HasAudio, OperationProgress(ct), ct);
                RecordExport(source, output, "Usunięcie fragmentu", start, end, $"Utworzono nowy plik; oryginał bez zmian; rozmiar: {FormatFileSize(new FileInfo(output).Length)}");
                return "Zapisano: " + output;
            });
        }

        private void CancelExport_Click(object sender, RoutedEventArgs e)
        {
            if (_operationCts == null) return;
            CancelExportButton.IsEnabled = false;
            ExportProgressText.Text = "Anulowanie…";
            _operationCts.Cancel();
        }

        private async void Window_Closing(object? sender, CancelEventArgs e)
        {
            if (_allowClose) return;
            e.Cancel = true;
            if (_shutdownRequested) return;
            if (_opening) { StatusText.Text = "Poczekaj na zakończenie otwierania pliku, a następnie zamknij okno."; return; }
            if (_exporting && MessageBox.Show(this, "Przerwać operację i zamknąć program? Ukończone pliki seryjne pozostaną zapisane.", "Zamykanie", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;

            _shutdownRequested = true;
            _saveTimer.Stop();
            SaveSession();
            _ocrWorkCts?.Cancel();
            _operationCts?.Cancel();
            try { if (_operationTask != null) await _operationTask; } catch { }
            try { await _mpvService.DisposeAsync(); } catch { }
            CleanupDahuaOnClosing();
            _closing = true;
            _allowClose = true;
            Close();
        }
    }
}
