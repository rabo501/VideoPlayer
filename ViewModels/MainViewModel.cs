using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VideoPlayer.ViewModels
{
    internal sealed partial class MainViewModel : ObservableObject, IDisposable
    {
        public MainViewModel(
            PlateOcrViewModel? plateOcr = null,
            DahuaViewModel? dahua = null,
            EventCenterViewModel? eventCenter = null,
            MotionDetectionViewModel? motion = null,
            ExportViewModel? export = null,
            PlateSearchViewModel? plateSearch = null)
        {
            PlateOcr = plateOcr ?? new PlateOcrViewModel();
            Dahua = dahua ?? new DahuaViewModel();
            EventCenter = eventCenter ?? new EventCenterViewModel();
            Motion = motion ?? new MotionDetectionViewModel();
            Export = export ?? new ExportViewModel();
            PlateSearch = plateSearch ?? new PlateSearchViewModel();

            Export.IsMotionRunning = Motion.IsRunning;
            Motion.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MotionDetectionViewModel.IsRunning))
                    Export.IsMotionRunning = Motion.IsRunning;
            };
        }

        public PlateOcrViewModel PlateOcr { get; }
        public DahuaViewModel Dahua { get; }
        public EventCenterViewModel EventCenter { get; }
        public MotionDetectionViewModel Motion { get; }
        public ExportViewModel Export { get; }
        public PlateSearchViewModel PlateSearch { get; }

        [ObservableProperty]
        private string _statusMessage = "Uruchamianie odtwarzacza…";

        public void Dispose()
        {
            PlateOcr.Dispose();
            Dahua.Dispose();
            Motion.Dispose();
            Export.Dispose();
            PlateSearch.Dispose();
        }
    }
}
