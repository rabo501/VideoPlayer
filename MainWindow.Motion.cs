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
            if (_motionDetectionRunning || !CanOperate() || !PositionSlider.IsEnabled)
            {
                StatusText.Text = "Najpierw otwórz nagranie i zakończ bieżącą operację.";
                return;
            }

            string source = _sourceFilePath!;
            double duration = PositionSlider.Maximum;
            var answer = MessageBox.Show(
                this,
                "Analiza porównuje kolejne klatki nagrania (2 klatki/s).\n" +
                "Domyślna czułość: zmiana jasności co najmniej 22/255 na 1,5% obrazu.\n\n" +
                "Wyniki zostaną dodane jako znaczniki na osi czasu. Kontynuować?",
                "Wykrywanie ruchu",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

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
                    Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe"),
                    source,
                    duration,
                    pixelDifferenceThreshold: 22,
                    changedAreaPercent: 1.5,
                    progress: progress,
                    cancellationToken: _motionDetectionCts.Token);

                if (_closing || !string.Equals(source, _sourceFilePath, StringComparison.OrdinalIgnoreCase)) return;

                foreach (var item in found)
                {
                    _timelineMarkers.Add(new TimelineMarker
                    {
                        Id = Guid.NewGuid(),
                        Seconds = Math.Clamp(item.StartSeconds, 0, PositionSlider.Maximum),
                        Description = $"Ruch ({item.PeakChangedRatio:0.0}% obrazu)"
                    });
                }

                SaveTimelineMarkers();
                RefreshTimelineMarkerList();
                UpdateTimelineOverlay();
                ExportProgressBar.Value = 100;
                ExportProgressText.Text = "Analiza zakończona";
                StatusText.Text = found.Count == 0
                    ? "Analiza zakończona. Nie wykryto ruchu."
                    : $"Wykrywanie ruchu zakończone. Znaleziono {found.Count} zdarzeń.";
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
                _motionDetectionCts?.Dispose();
                _motionDetectionCts = null;
                _motionDetectionRunning = false;
                if (!_closing)
                {
                    MotionDetectButton.IsEnabled = true;
                    MotionCancelButton.Visibility = Visibility.Collapsed;
                    MotionCancelButton.IsEnabled = false;
                }
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
