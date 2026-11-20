using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using VideoPlayer.Services;

namespace VideoPlayer
{
    public partial class MainWindow
    {
        private bool CanOperate(bool requireSource = true) =>
            !ViewModel.Export.IsBusy && !ViewModel.Motion.IsRunning && !_opening && !_shutdownRequested &&
            (!requireSource || (_exports != null && _sourceFilePath != null && File.Exists(_sourceFilePath)));

        private bool ValidRange() => ViewModel.Export.HasValidRange;

        private bool ConfirmCopy(bool copy) =>
            !copy || MessageBox.Show(this,
                "Kopiowanie strumieni może przesunąć granice cięcia. Kodeki muszą być zgodne z MP4. Kontynuować?",
                "Eksport", MessageBoxButton.YesNo) == MessageBoxResult.Yes;

        private string? ChooseOutput(string title, string proposed, string extension)
        {
            var d = new Microsoft.Win32.SaveFileDialog
            {
                Title = title,
                Filter = extension.ToUpperInvariant() + "|*." + extension,
                DefaultExt = "." + extension,
                AddExtension = true,
                OverwritePrompt = true,
                FileName = proposed
            };
            if (d.ShowDialog(this) != true) return null;
            if (File.Exists(d.FileName))
            {
                MessageBox.Show(this, "Dla bezpieczeństwa wybierz nową nazwę. Ta wersja nie nadpisuje istniejących wyników.", "Zapis");
                return null;
            }
            return d.FileName;
        }

        private IProgress<ExportProgress> OperationProgress(CancellationToken token, string prefix = "", int index = 0, int total = 1)
            => new Progress<ExportProgress>(p =>
            {
                if (_closing) return;
                ViewModel.Export.ReportProgress(token, (index + p.Percent / 100.0) / total * 100, prefix + p.Detail);
            });

        private async Task RunOperationAsync(string title, Func<CancellationToken, Task<string>> work, bool requireSource = true)
        {
            if (!CanOperate(requireSource)) return;
            CancellationToken token = ViewModel.Export.BeginOperation(title);
            try
            {
                Task<string> task = work(token);
                _operationTask = task;
                string result = await task;
                ViewModel.Export.CompleteOperationSuccess(result);
                StatusText.Text = result;
            }
            catch (OperationCanceledException)
            {
                ViewModel.Export.CompleteOperationCanceled();
                StatusText.Text = "Operacja anulowana. Ukończone wcześniej pliki eksportu seryjnego pozostają w folderze wynikowym.";
            }
            catch (Exception ex)
            {
                ViewModel.Export.CompleteOperationFailed();
                StatusText.Text = ex.Message;
                if (!_shutdownRequested) MessageBox.Show(this, ex.Message, title);
            }
            finally
            {
                _operationTask = null;
                ViewModel.Export.EndOperation();
                SaveSession();
            }
        }

        private async void ExportClip_Click(object sender, RoutedEventArgs e)
        {
            if (!CanOperate() || !ValidRange()) { StatusText.Text = "Otwórz film i ustaw poprawny zakres."; return; }
            string source = _sourceFilePath!;
            double start = ViewModel.Export.ClipStart!.Value, end = ViewModel.Export.ClipEnd!.Value;
            bool copy = ViewModel.Export.UseStreamCopy;
            string profile = ViewModel.Export.SelectedProfileTag;
            if (!ConfirmCopy(copy)) return;
            string? output = ChooseOutput("Zapisz fragment", Path.GetFileNameWithoutExtension(source) + "_fragment.mp4", "mp4");
            if (output == null) return;
            await RunOperationAsync("Eksport: " + Path.GetFileName(source), async ct =>
            {
                await _exports.ExportAsync(source, output, start, end, profile, copy, OperationProgress(ct), ct);
                long size = new FileInfo(output).Length;
                string sizeText = ExportWorkspaceService.FormatFileSize(size);
                RecordExport(source, output, "Eksport fragmentu", start, end, $"Profil: {profile}; kopiowanie strumieni: {(copy ? "tak" : "nie")}; rozmiar: {sizeText}");
                return "Zapisano: " + output + " | " + sizeText;
            });
        }

        private async void RemoveClip_Click(object sender, RoutedEventArgs e)
        {
            if (!CanOperate() || !ValidRange()) { StatusText.Text = "Otwórz film i ustaw poprawny zakres."; return; }
            string source = _sourceFilePath!;
            double start = ViewModel.Export.ClipStart!.Value, end = ViewModel.Export.ClipEnd!.Value;
            if (MessageBox.Show(this, "Powstanie nowy film bez zaznaczenia. Oryginał pozostanie bez zmian. Kontynuować?", "Usuwanie fragmentu", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            string? output = ChooseOutput("Zapisz film bez zaznaczenia", Path.GetFileNameWithoutExtension(source) + "_po_cieciu.mp4", "mp4");
            if (output == null) return;
            await RunOperationAsync("Usuwanie fragmentu: " + Path.GetFileName(source), async ct =>
            {
                var info = await _exports.ProbeAsync(source, ct);
                await _exports.RemoveAsync(source, output, start, end, info.Duration, info.HasAudio, OperationProgress(ct), ct);
                string sizeText = ExportWorkspaceService.FormatFileSize(new FileInfo(output).Length);
                RecordExport(source, output, "Usunięcie fragmentu", start, end, $"Utworzono nowy plik; oryginał bez zmian; rozmiar: {sizeText}");
                return "Zapisano: " + output;
            });
        }

        private async void Window_Closing(object? sender, CancelEventArgs e)
        {
            if (_allowClose) return;
            e.Cancel = true;
            if (_shutdownRequested) return;
            if (_opening) { StatusText.Text = "Poczekaj na zakończenie otwierania pliku, a następnie zamknij okno."; return; }
            if (ViewModel.Export.IsBusy && MessageBox.Show(this, "Przerwać operację i zamknąć program? Ukończone pliki seryjne pozostaną zapisane.", "Zamykanie", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;

            _shutdownRequested = true;
            _saveTimer.Stop();
            SaveSession();
            ViewModel.PlateOcr.CancelRecognition();
            ViewModel.PlateOcr.CancelSearch();
            ViewModel.Export.RequestCancel();
            _mediaInfoCts?.Cancel();
            _mediaInfoCts?.Dispose();
            _mediaInfoCts = null;
            try { if (_operationTask != null) await _operationTask; } catch { }
            try { await _mpvService.DisposeAsync(); } catch { }
            CleanupDahuaOnClosing();
            ViewModel.Dispose();
            _closing = true;
            _allowClose = true;
            Close();
        }
    }
}
