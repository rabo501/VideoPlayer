using System;
using System.Collections.ObjectModel;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoPlayer.Services;

namespace VideoPlayer.ViewModels
{
    internal sealed partial class ExportViewModel : ObservableObject, IDisposable
    {
        private CancellationTokenSource? _operationCts;
        private CancellationTokenSource? _mediaInfoCts;
        private IMediaInfoReader? _mediaInfoReader;

        public ObservableCollection<ClipRange> Clips { get; } = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanStartOperation))]
        private bool _isBusy;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanStartOperation))]
        private bool _isMotionRunning;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
        private bool _canCancel;

        [ObservableProperty]
        private double _progressPercent;

        [ObservableProperty]
        private string _operationTitle = "Brak aktywnej operacji";

        [ObservableProperty]
        private string _progressText = "Gotowy do eksportu";

        [ObservableProperty]
        private string _selectedProfileTag = "standard";

        [ObservableProperty]
        private bool _useStreamCopy;

        [ObservableProperty]
        private double? _clipStart;

        [ObservableProperty]
        private double? _clipEnd;

        [ObservableProperty]
        private string _clipName = "Fragment";

        [ObservableProperty]
        private ClipRange? _selectedClip;

        [ObservableProperty]
        private string _rangeFeedbackText = ExportWorkspaceService.BuildRangeFeedbackText(null, null, 0);

        [ObservableProperty]
        private string _clipRangeText = "Nie zaznaczono fragmentu.";

        [ObservableProperty]
        private string _mediaInfoText = "Otwórz nagranie, aby odczytać kodek, rozdzielczość, FPS, bitrate i rozmiar.";

        private MediaInfoData? _currentMediaInfo;
        private string? _currentSourcePath;
        private string? _lastExportSummary;

        public bool CanStartOperation => !IsBusy && !IsMotionRunning;

        public void AttachMediaInfoReader(IMediaInfoReader reader)
        {
            _mediaInfoReader = reader ?? throw new ArgumentNullException(nameof(reader));
        }

        public async Task LoadMediaInfoAsync(string sourcePath)
        {
            if (_mediaInfoReader == null)
            {
                SetMediaInfoError("Odczyt parametrów nagrania nie został zainicjalizowany.");
                return;
            }

            CancelMediaInfoLoad();
            var cts = new CancellationTokenSource();
            _mediaInfoCts = cts;
            _currentSourcePath = sourcePath;
            _currentMediaInfo = null;
            MediaInfoText = "Odczytywanie parametrów…";

            try
            {
                MediaInfoData info = await _mediaInfoReader.ReadAsync(sourcePath, cts.Token);
                if (!IsCurrentMediaInfoRequest(cts, sourcePath))
                    return;

                SetMediaInfo(info, sourcePath);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // A newer recording was opened or the application is closing.
            }
            catch (Exception ex)
            {
                if (IsCurrentMediaInfoRequest(cts, sourcePath))
                    SetMediaInfoError(ex.Message);
            }
            finally
            {
                if (ReferenceEquals(_mediaInfoCts, cts))
                    _mediaInfoCts = null;
                cts.Dispose();
            }
        }

        public void CancelMediaInfoLoad()
        {
            CancellationTokenSource? cts = _mediaInfoCts;
            _mediaInfoCts = null;
            cts?.Cancel();
        }

        private bool IsCurrentMediaInfoRequest(CancellationTokenSource cts, string sourcePath) =>
            ReferenceEquals(_mediaInfoCts, cts) &&
            !cts.IsCancellationRequested &&
            string.Equals(_currentSourcePath, sourcePath, StringComparison.OrdinalIgnoreCase);

        public bool HasValidRange => ExportWorkspaceService.HasValidRange(ClipStart, ClipEnd);

        public MediaInfoData? CurrentMediaInfo => _currentMediaInfo;

        public CancellationToken BeginOperation(string title)
        {
            _operationCts?.Dispose();
            _operationCts = new CancellationTokenSource();
            IsBusy = true;
            CanCancel = true;
            ProgressPercent = 0;
            ProgressText = "Przygotowanie…";
            OperationTitle = title;
            return _operationCts.Token;
        }

        public bool IsCurrentOperationToken(CancellationToken token) =>
            _operationCts != null && _operationCts.Token == token;

        public void ReportProgress(CancellationToken token, double percent, string detail)
        {
            if (token.IsCancellationRequested || !IsCurrentOperationToken(token))
                return;
            ProgressPercent = Math.Clamp(percent, 0, 100);
            ProgressText = detail;
        }

        public void CompleteOperationSuccess(string? exportSummary = null)
        {
            ProgressPercent = 100;
            ProgressText = "Zakończono";
            if (!string.IsNullOrWhiteSpace(exportSummary))
            {
                _lastExportSummary = exportSummary;
                RefreshMediaInfoSummary();
            }
        }

        public void CompleteOperationCanceled()
        {
            ProgressText = "Anulowano";
        }

        public void CompleteOperationFailed()
        {
            ProgressText = "Błąd operacji";
        }

        public void EndOperation()
        {
            _operationCts?.Dispose();
            _operationCts = null;
            IsBusy = false;
            CanCancel = false;
        }

        public void RequestCancel()
        {
            _operationCts?.Cancel();
        }

        [RelayCommand(CanExecute = nameof(CanCancel))]
        public void Cancel()
        {
            if (_operationCts == null) return;
            CanCancel = false;
            ProgressText = "Anulowanie…";
            _operationCts.Cancel();
        }

        public void SetExternalProgress(string title, double percent, string text)
        {
            OperationTitle = title;
            ProgressPercent = Math.Clamp(percent, 0, 100);
            ProgressText = text;
        }

        public void ResetForNewMedia(string? sourcePath)
        {
            CancelMediaInfoLoad();
            _currentSourcePath = sourcePath;
            _currentMediaInfo = null;
            _lastExportSummary = null;
            ClipStart = null;
            ClipEnd = null;
            Clips.Clear();
            SelectedClip = null;
            MediaInfoText = string.IsNullOrWhiteSpace(sourcePath)
                ? "Otwórz nagranie, aby odczytać kodek, rozdzielczość, FPS, bitrate i rozmiar."
                : "Odczytywanie parametrów…";
            RefreshClipTexts();
        }

        public void SetMediaInfo(MediaInfoData? info, string? sourcePath)
        {
            _currentMediaInfo = info;
            _currentSourcePath = sourcePath;
            RefreshMediaInfoSummary();
        }

        public void SetMediaInfoError(string errorMessage)
        {
            MediaInfoText = "Nie udało się odczytać parametrów: " + errorMessage;
        }

        public void SetClipRange(double? start, double? end)
        {
            ClipStart = start;
            ClipEnd = end;
            RefreshClipTexts();
        }

        public void SetRangeErrorText(string text)
        {
            RangeFeedbackText = text;
        }

        public void RefreshClipTexts()
        {
            RangeFeedbackText = ExportWorkspaceService.BuildRangeFeedbackText(ClipStart, ClipEnd, Clips.Count);
            ClipRangeText = ExportWorkspaceService.BuildClipRangeSummary(ClipStart, ClipEnd);
            RefreshMediaInfoSummary();
        }

        public void RefreshRangeHintOnly()
        {
            RangeFeedbackText = ExportWorkspaceService.BuildRangeFeedbackText(ClipStart, ClipEnd, Clips.Count);
        }

        public void RefreshMediaInfoSummary()
        {
            if (_currentMediaInfo == null || string.IsNullOrWhiteSpace(_currentSourcePath))
                return;
            MediaInfoText = ExportWorkspaceService.BuildMediaInfoSummary(
                _currentMediaInfo, _currentSourcePath, ClipStart, ClipEnd, _lastExportSummary);
        }

        public void AddClip(ClipRange range)
        {
            Clips.Add(range);
            SelectedClip = range;
            RefreshRangeHintOnly();
        }

        public bool ReplaceClip(ClipRange existing, ClipRange updated)
        {
            int index = Clips.IndexOf(existing);
            if (index < 0) return false;
            Clips[index] = updated;
            SelectedClip = updated;
            RefreshRangeHintOnly();
            return true;
        }

        public bool RemoveClip(ClipRange clip)
        {
            bool removed = Clips.Remove(clip);
            if (removed)
                RefreshRangeHintOnly();
            return removed;
        }

        public void Dispose()
        {
            CancelMediaInfoLoad();
            _operationCts?.Cancel();
            _operationCts?.Dispose();
            _operationCts = null;
            IsBusy = false;
            CanCancel = false;
        }
    }
}
