using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace VideoPlayer.ViewModels
{
    internal sealed partial class MotionDetectionViewModel : ObservableObject, IDisposable
    {
        private CancellationTokenSource? _cts;

        private readonly MotionDetectionService _service = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanStart))]
        private bool _isRunning;

        [ObservableProperty]
        private double _progressPercent;

        [ObservableProperty]
        private string _progressText = string.Empty;

        public bool CanStart => !IsRunning;

        public CancellationToken BeginRun()
        {
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            IsRunning = true;
            ProgressPercent = 0;
            ProgressText = "Przygotowanie analizy…";
            return _cts.Token;
        }

        public bool IsCurrentToken(CancellationToken token) =>
            _cts != null && _cts.Token == token;

        public Task<List<MotionDetectionService.MotionEvent>> AnalyzeAsync(
            string ffmpegPath, string sourcePath, double durationSeconds,
            double pixelDifferenceThreshold, double changedAreaPercent,
            double minimumEventDurationSeconds, MotionDetectionService.MotionRegion region,
            IProgress<double>? progress, CancellationToken cancellationToken) =>
            _service.AnalyzeAsync(ffmpegPath, sourcePath, durationSeconds,
                pixelDifferenceThreshold, changedAreaPercent, minimumEventDurationSeconds,
                region, progress, cancellationToken);

        public static List<TimelineMarker> CreateMarkers(
            IEnumerable<MotionDetectionService.MotionEvent> events, double durationSeconds)
        {
            ArgumentNullException.ThrowIfNull(events);
            double maximum = double.IsFinite(durationSeconds) ? Math.Max(0, durationSeconds) : 0;
            return events.Select(item =>
            {
                double start = Math.Clamp(item.StartSeconds, 0, maximum);
                double end = Math.Clamp(item.EndSeconds, start, maximum);
                return new TimelineMarker
                {
                    Id = Guid.NewGuid(), Seconds = start, EndSeconds = end,
                    MotionX = item.MotionX, MotionY = item.MotionY,
                    MotionWidth = item.MotionWidth, MotionHeight = item.MotionHeight,
                    Description = $"Ruch ({item.PeakChangedRatio:0.0}% zaznaczonego obszaru)"
                };
            }).ToList();
        }

        public Task<byte[]> ExtractPreviewBmpAsync(string ffmpegPath, string sourcePath, double seconds) =>
            MotionDetectionService.ExtractPreviewBmpAsync(ffmpegPath, sourcePath, seconds);

        public void CompleteRun()
        {
            _cts?.Dispose();
            _cts = null;
            IsRunning = false;
        }

        [RelayCommand(CanExecute = nameof(IsRunning))]
        public void Cancel()
        {
            _cts?.Cancel();
            ProgressText = "Anulowanie analizy ruchu…";
            CancelCommand.NotifyCanExecuteChanged();
        }

        partial void OnIsRunningChanged(bool value)
        {
            CancelCommand.NotifyCanExecuteChanged();
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            IsRunning = false;
        }
    }
}
