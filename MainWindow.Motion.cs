using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace VideoPlayer
{
    public partial class MainWindow
    {
        private readonly MotionDetectionService _motionDetectionService = new();
        private CancellationTokenSource? _motionDetectionCts;
        private bool _motionDetectionRunning;

        private async void MotionDetect_Click(object sender, RoutedEventArgs e)
        {
            if (_motionDetectionRunning || !CanOperate() || !PositionSlider.IsEnabled || string.IsNullOrWhiteSpace(_sourceFilePath))
            {
                StatusText.Text = "Najpierw otwórz nagranie i zakończ bieżącą operację.";
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

            _motionDetectionRunning = true;
            _motionDetectionCts = new CancellationTokenSource();
            MotionDetectButton.IsEnabled = false;
            MotionCancelButton.Visibility = Visibility.Visible;
            MotionCancelButton.IsEnabled = true;
            ExportProgressBar.Visibility = Visibility.Visible;
            ExportProgressBar.Value = 0;
            ExportProgressText.Visibility = Visibility.Visible;
            ExportProgressText.Text = "Przygotowanie analizy…";
            var progress = new Progress<double>(value =>
            {
                if (_closing || !_motionDetectionRunning) return;
                ExportProgressBar.Value = value;
                ExportProgressText.Text = $"Analiza ruchu: {value:0}%";
            });

            try
            {
                List<MotionDetectionService.MotionEvent> found = await _motionDetectionService.AnalyzeAsync(
                    Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe"), source, duration,
                    settings!.PixelThreshold, settings.ChangedAreaPercent, settings.MinimumDuration, settings.Region,
                    progress, _motionDetectionCts.Token);
                if (_closing || !string.Equals(source, _sourceFilePath, StringComparison.OrdinalIgnoreCase)) return;
                foreach (var item in found)
                    _timelineMarkers.Add(new TimelineMarker
                    {
                        Id = Guid.NewGuid(),
                        Seconds = Math.Clamp(item.StartSeconds, 0, PositionSlider.Maximum),
                        Description = $"Ruch ({item.PeakChangedRatio:0.0}% obszaru)"
                    });
                SaveTimelineMarkers();
                RefreshTimelineMarkerList();
                UpdateTimelineOverlay();
                ExportProgressBar.Value = 100;
                ExportProgressText.Text = "Analiza zakończona";
                StatusText.Text = found.Count == 0 ? "Analiza zakończona. Nie wykryto ruchu." : $"Wykrywanie ruchu zakończone. Znaleziono {found.Count} zdarzeń.";
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "Analiza ruchu została anulowana.";
                ExportProgressText.Text = "Anulowano";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Błąd analizy ruchu: " + ex.Message;
                MessageBox.Show(this, ex.Message, "Wykrywanie ruchu", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _motionDetectionCts?.Dispose(); _motionDetectionCts = null; _motionDetectionRunning = false;
                if (!_closing) { MotionDetectButton.IsEnabled = true; MotionCancelButton.Visibility = Visibility.Collapsed; MotionCancelButton.IsEnabled = false; }
            }
        }

        private void MotionCancel_Click(object sender, RoutedEventArgs e)
        {
            _motionDetectionCts?.Cancel();
            MotionCancelButton.IsEnabled = false;
            StatusText.Text = "Anulowanie analizy ruchu…";
        }
    }
}