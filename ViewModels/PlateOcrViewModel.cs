using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace VideoPlayer.ViewModels
{
    internal sealed partial class PlateOcrViewModel : ObservableObject, IDisposable
    {
        private readonly VideoPlateSearchService _searchService;
        private CancellationTokenSource? _workCts;
        private CancellationTokenSource? _searchCts;
        private bool _updatingCandidate;

        public PlateOcrViewModel(VideoPlateSearchService? searchService = null)
        {
            _searchService = searchService ?? new VideoPlateSearchService();
        }

        public ObservableCollection<string> Candidates { get; } = new();
        public ObservableCollection<PlateVideoSearchHit> VideoSearchHits { get; } = new();

        public PlatePixels? CurrentRectifiedCrop { get; private set; }

        [ObservableProperty]
        private bool _isActive;

        [ObservableProperty]
        private bool _isBusy;

        [ObservableProperty]
        private bool _isSearchingVideo;

        [ObservableProperty]
        private bool _isFromDahua;

        [ObservableProperty]
        private bool _hasSourceFile;

        [ObservableProperty]
        private bool _hasValidSelection;

        [ObservableProperty]
        private bool _isResultConfirmed;

        [ObservableProperty]
        private string _plateText = string.Empty;

        [ObservableProperty]
        private string _vehicleMake = string.Empty;

        [ObservableProperty]
        private string? _selectedCandidate;

        [ObservableProperty]
        private string _selectionInfo = "Zaznacz tablicę na obrazie po lewej.";

        [ObservableProperty]
        private string _statusText = "Silnik OCR działa lokalnie. Wynik wymaga sprawdzenia — szczególnie O/0, I/1 i B/8.";

        [ObservableProperty]
        private string _frameInfoText = "Rozpoznawanie tablicy — lokalnie";

        [ObservableProperty]
        private string _zoomLabel = "1×";

        [ObservableProperty]
        private string _searchProgressText = "Wpisz numer tablicy, potwierdź go i rozpocznij wyszukiwanie.";

        [ObservableProperty]
        private bool _canRecognize;

        [ObservableProperty]
        private bool _canResetSelection = true;

        [ObservableProperty]
        private bool _canSaveEvent;

        [ObservableProperty]
        private bool _canSearchInRecording;

        [ObservableProperty]
        private bool _canEditResult = true;

        [ObservableProperty]
        private bool _canChangeInterval = true;

        partial void OnIsActiveChanged(bool value) => UpdateActionStates();
        partial void OnIsBusyChanged(bool value) => UpdateActionStates();
        partial void OnIsSearchingVideoChanged(bool value) => UpdateActionStates();
        partial void OnIsFromDahuaChanged(bool value) => UpdateActionStates();
        partial void OnHasSourceFileChanged(bool value) => UpdateActionStates();
        partial void OnHasValidSelectionChanged(bool value) => UpdateActionStates();
        partial void OnIsResultConfirmedChanged(bool value) => UpdateActionStates();

        partial void OnPlateTextChanged(string value)
        {
            if (!_updatingCandidate)
                IsResultConfirmed = false;
            UpdateActionStates();
        }

        partial void OnSelectedCandidateChanged(string? value)
        {
            if (!_updatingCandidate && !string.IsNullOrWhiteSpace(value))
            {
                PlateText = value;
            }
        }

        public void SetRectifiedCrop(PlatePixels? crop)
        {
            CurrentRectifiedCrop = crop;
            HasValidSelection = crop != null;
            UpdateActionStates();
        }

        public void InvalidateResult()
        {
            CurrentRectifiedCrop = null;
            HasValidSelection = false;
            Candidates.Clear();
            _updatingCandidate = true;
            try
            {
                SelectedCandidate = null;
                PlateText = string.Empty;
                IsResultConfirmed = false;
            }
            finally
            {
                _updatingCandidate = false;
            }
            UpdateActionStates();
        }

        public void ResetWorkspace()
        {
            _workCts?.Cancel();
            _searchCts?.Cancel();
            IsActive = false;
            IsFromDahua = false;
            HasSourceFile = false;
            VehicleMake = string.Empty;
            VideoSearchHits.Clear();
            SearchProgressText = "Wpisz numer tablicy, potwierdź go i rozpocznij wyszukiwanie.";
            InvalidateResult();
        }

        public void UpdateActionStates()
        {
            string normalized = (PlateText ?? string.Empty).Trim().Replace(" ", "").Replace("-", "");
            bool validManual = PlateOcrRules.IsValidManual(normalized);

            CanRecognize = !IsBusy && CurrentRectifiedCrop != null;
            CanResetSelection = !IsBusy;
            CanEditResult = !IsBusy;
            CanChangeInterval = !IsSearchingVideo;
            CanSaveEvent = !IsBusy && !IsSearchingVideo && IsResultConfirmed && validManual;
            CanSearchInRecording = !IsSearchingVideo && IsActive && !IsFromDahua &&
                                   HasValidSelection && IsResultConfirmed && validManual && HasSourceFile;
        }

        public async Task RecognizeAcrossFramesAsync(
            string ffmpegPath,
            string? sourcePath,
            PlatePoint[]? points,
            int referenceWidth,
            int referenceHeight,
            double centerSeconds,
            double durationSeconds)
        {
            if (IsBusy || CurrentRectifiedCrop == null) return;

            PlatePixels centerCrop = CurrentRectifiedCrop;
            IsBusy = true;
            _workCts?.Dispose();
            _workCts = new CancellationTokenSource();

            _updatingCandidate = true;
            try { IsResultConfirmed = false; }
            finally { _updatingCandidate = false; }

            StatusText = "Rozpoznawanie lokalne — poczekaj…";

            try
            {
                string? localSource = IsFromDahua ? null : sourcePath;
                PlatePoint[] safePoints = points != null ? (PlatePoint[])points.Clone() : Array.Empty<PlatePoint>();
                var request = new PlateMultiFrameRequest(
                    ffmpegPath,
                    localSource ?? string.Empty,
                    centerCrop,
                    safePoints,
                    referenceWidth,
                    referenceHeight,
                    centerSeconds,
                    durationSeconds);

                IReadOnlyList<PlateOcrConsensusCandidate> results =
                    await _searchService.RecognizeAcrossFramesAsync(request, _workCts.Token);

                if (!IsActive) return;

                Candidates.Clear();
                foreach (var item in results)
                    Candidates.Add(item.Text);

                if (results.Count > 0)
                {
                    _updatingCandidate = true;
                    try
                    {
                        PlateText = results[0].Text;
                        SelectedCandidate = results[0].Text;
                    }
                    finally
                    {
                        _updatingCandidate = false;
                    }

                    StatusText =
                        $"Najczęstszy odczyt: {results[0].Text} — zgodność {results[0].VoteCount}/{results[0].FrameCount} klatek, " +
                        $"średnia pewność OCR {results[0].AverageConfidence:P0}. To propozycja, nie pewny odczyt. Sprawdź numer i potwierdź go ręcznie.";
                }
                else
                {
                    StatusText = "Nie znaleziono czytelnego numeru w analizowanych klatkach. Zaznacz dokładniej albo wpisz numer ręcznie, jeśli jest czytelny.";
                }
            }
            catch (OperationCanceledException)
            {
                StatusText = "Anulowano.";
            }
            finally
            {
                IsBusy = false;
                _workCts?.Dispose();
                _workCts = null;
            }
        }

        public async Task<IReadOnlyList<PlateVideoSearchHit>> SearchInRecordingAsync(
            string ffmpegPath,
            string sourcePath,
            PlatePoint[] points,
            int referenceWidth,
            int referenceHeight,
            double durationSeconds,
            int requestedIntervalSeconds)
        {
            if (IsSearchingVideo) return Array.Empty<PlateVideoSearchHit>();

            string target = PlateOcrRules.Normalize(PlateText);
            var (effectiveInterval, estimatedSamples) = VideoPlateSearchService.ComputeEffectiveSampling(
                durationSeconds, requestedIntervalSeconds);

            IsSearchingVideo = true;
            _searchCts?.Dispose();
            _searchCts = new CancellationTokenSource();
            VideoSearchHits.Clear();

            SearchProgressText = effectiveInterval == requestedIntervalSeconds
                ? $"Przygotowanie analizy: około {estimatedSamples} próbek co {effectiveInterval} s…"
                : $"Dla ograniczenia liczby próbek odstęp zwiększono z {requestedIntervalSeconds} s do {effectiveInterval} s. Przygotowanie analizy…";

            var progress = new Progress<PlateVideoSearchProgress>(update =>
            {
                if (!IsActive) return;
                SearchProgressText = update.StatusMessage;
                if (update.HitsSnapshot.Count > 0)
                {
                    VideoSearchHits.Clear();
                    foreach (PlateVideoSearchHit hit in update.HitsSnapshot)
                        VideoSearchHits.Add(hit);
                }
            });

            try
            {
                var request = new PlateVideoSearchRequest(
                    ffmpegPath,
                    sourcePath,
                    target,
                    (PlatePoint[])points.Clone(),
                    referenceWidth,
                    referenceHeight,
                    durationSeconds,
                    effectiveInterval);

                IReadOnlyList<PlateVideoSearchHit> hits =
                    await _searchService.SearchInRecordingAsync(request, progress, _searchCts.Token);

                if (!IsActive) return Array.Empty<PlateVideoSearchHit>();

                VideoSearchHits.Clear();
                foreach (PlateVideoSearchHit hit in hits)
                    VideoSearchHits.Add(hit);

                SearchProgressText = hits.Count == 0
                    ? $"Zakończono analizę {estimatedSamples} próbek. Nie znaleziono dokładnego odczytu {target} w zaznaczonym obszarze."
                    : $"Zakończono analizę. Znaleziono {hits.Count} przedziałów z odczytem {target}. Dwuklik przenosi do początku wyniku.";

                return hits;
            }
            catch (OperationCanceledException)
            {
                SearchProgressText = "Wyszukiwanie zostało anulowane.";
                throw;
            }
            finally
            {
                IsSearchingVideo = false;
                _searchCts?.Dispose();
                _searchCts = null;
            }
        }

        [RelayCommand]
        public void CancelSearch()
        {
            _searchCts?.Cancel();
            SearchProgressText = "Anulowanie wyszukiwania…";
        }

        public void CancelRecognition()
        {
            _workCts?.Cancel();
        }

        public void Dispose()
        {
            // Operacje zwalniają własne źródła tokenów w blokach finally.
            // Tutaj tylko żądamy anulowania, aby nie ścigać się z ich zakończeniem.
            _workCts?.Cancel();
            _searchCts?.Cancel();
        }
    }
}
