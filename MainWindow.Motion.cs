using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace VideoPlayer
{
    public partial class MainWindow
    {
        private async void MotionDetect_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.Motion.IsRunning || !CanOperate() || !PositionSlider.IsEnabled || string.IsNullOrWhiteSpace(_sourceFilePath))
            {
                ViewModel.StatusMessage = "Najpierw otwórz nagranie i zakończ bieżącą operację.";
                return;
            }

            string source = _sourceFilePath!;
            double duration = PositionSlider.Maximum;
            string preview = Path.Combine(Path.GetTempPath(), "VideoPlayer_motion_" + Guid.NewGuid().ToString("N") + ".jpg");
            MotionRegionWindow? settings = null;
            try
            {
                await _mpvService.RequestAsync(new object[] { "screenshot-to-file", preview, "video" });
                if (!File.Exists(preview)) throw new IOException("Nie udało się pobrać klatki do ustawienia obszaru detekcji.");
                settings = new MotionRegionWindow(preview) { Owner = this };
                if (settings.ShowDialog() != true) return;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Ustawienia wykrywania ruchu", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            finally { try { if (File.Exists(preview)) File.Delete(preview); } catch { } }

            CancellationToken token = ViewModel.Motion.BeginRun();
            ViewModel.Export.SetExternalProgress("Wykrywanie ruchu", 0, ViewModel.Motion.ProgressText);

            var progress = new Progress<double>(value =>
            {
                if (_closing || !ViewModel.Motion.IsRunning || !ViewModel.Motion.IsCurrentToken(token)) return;
                ViewModel.Motion.ProgressPercent = value;
                ViewModel.Motion.ProgressText = $"Analiza ruchu: {value:0}%";
                ViewModel.Export.SetExternalProgress("Wykrywanie ruchu", value, ViewModel.Motion.ProgressText);
            });

            try
            {
                List<MotionDetectionService.MotionEvent> found = await ViewModel.Motion.Service.AnalyzeAsync(
                    Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe"),
                    source,
                    duration,
                    settings!.PixelThreshold,
                    settings.ChangedAreaPercent,
                    settings.MinimumDuration,
                    settings.Region,
                    progress,
                    token);

                if (_closing || !string.Equals(source, _sourceFilePath, StringComparison.OrdinalIgnoreCase)) return;

                var addedMarkers = found.Select(item => new TimelineMarker
                {
                    Id = Guid.NewGuid(),
                    Seconds = Math.Clamp(item.StartSeconds, 0, PositionSlider.Maximum),
                    EndSeconds = Math.Clamp(item.EndSeconds, 0, PositionSlider.Maximum),
                    MotionX = item.MotionX,
                    MotionY = item.MotionY,
                    MotionWidth = item.MotionWidth,
                    MotionHeight = item.MotionHeight,
                    Description = $"Ruch ({item.PeakChangedRatio:0.0}% zaznaczonego obszaru)"
                }).ToList();

                ViewModel.EventCenter.AddMarkers(addedMarkers);
                SaveTimelineMarkers();
                UpdateTimelineOverlay();

                ViewModel.Export.SetExternalProgress("Wykrywanie ruchu", 100, "Analiza zakończona");
                ViewModel.StatusMessage = found.Count == 0
                    ? "Analiza zakończona. Nie wykryto ruchu."
                    : $"Wykrywanie ruchu zakończone. Znaleziono {found.Count} zdarzeń.";
            }
            catch (OperationCanceledException)
            {
                ViewModel.StatusMessage = "Analiza ruchu została anulowana.";
                ViewModel.Export.SetExternalProgress("Wykrywanie ruchu", ViewModel.Motion.ProgressPercent, "Anulowano");
            }
            catch (Exception ex)
            {
                ViewModel.StatusMessage = "Błąd analizy ruchu: " + ex.Message;
                MessageBox.Show(this, ex.Message, "Wykrywanie ruchu", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ViewModel.Motion.CompleteRun();
            }
        }

        private async void MotionPreview_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.EventCenter.SelectedMarker is not TimelineMarker marker)
            {
                MessageBox.Show(this, "Wybierz zdarzenie z listy.", "Podgląd ruchu", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (marker.MotionX is not double x || marker.MotionY is not double y ||
                marker.MotionWidth is not double w || marker.MotionHeight is not double h)
            {
                MessageBox.Show(this, "To zdarzenie nie ma zapisanego obszaru ruchu. Podgląd jest dostępny dla zdarzeń wykrytych po tej aktualizacji.", "Podgląd ruchu", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (string.IsNullOrWhiteSpace(_sourceFilePath) || !File.Exists(_sourceFilePath))
            {
                MessageBox.Show(this, "Najpierw otwórz nagranie.", "Podgląd ruchu", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                string ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
                byte[] bmpBytes = await MotionDetectionService.ExtractPreviewBmpAsync(ffmpeg, _sourceFilePath, marker.Seconds);
                if (_closing) return;
                new MotionEventPreviewWindow(bmpBytes, x, y, w, h) { Owner = this }.ShowDialog();
            }
            catch (Exception ex)
            {
                if (!_closing)
                    MessageBox.Show(this, ex.Message, "Podgląd ruchu", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}