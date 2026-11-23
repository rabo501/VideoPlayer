using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VideoPlayer.ViewModels
{
    internal sealed partial class MainViewModel : ObservableObject, IDisposable
    {
        private bool _disposed;

        public MainViewModel(
            PlateOcrViewModel? plateOcr = null,
            DahuaViewModel? dahua = null,
            EventCenterViewModel? eventCenter = null,
            MotionDetectionViewModel? motion = null,
            ExportViewModel? export = null,
            PlateSearchViewModel? plateSearch = null,
            CaseArchiveViewModel? cases = null,
            HistoryViewModel? history = null,
            ReviewSessionViewModel? reviewSession = null)
        {
            PlateOcr = plateOcr ?? new PlateOcrViewModel();
            Dahua = dahua ?? new DahuaViewModel();
            EventCenter = eventCenter ?? new EventCenterViewModel();
            Motion = motion ?? new MotionDetectionViewModel();
            Export = export ?? new ExportViewModel();
            PlateSearch = plateSearch ?? new PlateSearchViewModel();
            Cases = cases ?? new CaseArchiveViewModel();
            History = history ?? new HistoryViewModel();
            ReviewSession = reviewSession ?? new ReviewSessionViewModel();

            Export.IsMotionRunning = Motion.IsRunning;
            Motion.PropertyChanged += OnMotionPropertyChanged;
        }

        public PlateOcrViewModel PlateOcr { get; }
        public DahuaViewModel Dahua { get; }
        public EventCenterViewModel EventCenter { get; }
        public MotionDetectionViewModel Motion { get; }
        public ExportViewModel Export { get; }
        public PlateSearchViewModel PlateSearch { get; }
        public CaseArchiveViewModel Cases { get; }
        public HistoryViewModel History { get; }
        public ReviewSessionViewModel ReviewSession { get; }

        [ObservableProperty]
        private string _statusMessage = "Uruchamianie odtwarzacza…";

        private void OnMotionPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MotionDetectionViewModel.IsRunning))
                Export.IsMotionRunning = Motion.IsRunning;
        }

        /// <summary>
        /// Rozpoczyna anulowanie wszystkich operacji należących do ViewModeli.
        /// Wywoływane na początku zamykania okna, zanim zostaną zwolnione zasoby.
        /// </summary>
        public void CancelPendingOperations()
        {
            Motion.Cancel();
            PlateOcr.CancelRecognition();
            PlateOcr.CancelSearch();
            Export.RequestCancel();
            Export.CancelMediaInfoLoad();
            PlateSearch.CancelSearch();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            CancelPendingOperations();
            Motion.PropertyChanged -= OnMotionPropertyChanged;

            PlateOcr.Dispose();
            Dahua.Dispose();
            Motion.Dispose();
            Export.Dispose();
            PlateSearch.Dispose();
        }
    }
}
