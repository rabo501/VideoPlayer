using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace VideoPlayer.ViewModels
{
    internal sealed partial class EventCenterViewModel : ObservableObject
    {
        private readonly List<TimelineMarker> _allMarkers = new();

        public ObservableCollection<TimelineMarker> VisibleMarkers { get; } = new();

        public IReadOnlyList<TimelineMarker> AllMarkers => _allMarkers;

        [ObservableProperty]
        private string _filterText = string.Empty;

        [ObservableProperty]
        private string _selectedCategoryTag = "all";

        [ObservableProperty]
        private string _markerDescription = string.Empty;

        [ObservableProperty]
        private TimelineMarker? _selectedMarker;

        [ObservableProperty]
        private string _summaryText = "Brak zdarzeń dla bieżącego nagrania.";

        partial void OnFilterTextChanged(string value) => Refresh();

        partial void OnSelectedCategoryTagChanged(string value) => Refresh();

        partial void OnSelectedMarkerChanged(TimelineMarker? value)
        {
            if (value != null)
                MarkerDescription = value.Description;
        }

        public void SetMarkers(IEnumerable<TimelineMarker> markers)
        {
            _allMarkers.Clear();
            if (markers != null)
                _allMarkers.AddRange(markers);
            Refresh();
        }

        public void AddMarker(TimelineMarker marker)
        {
            ArgumentNullException.ThrowIfNull(marker);
            _allMarkers.Add(marker);
            Refresh();
            SelectedMarker = marker;
        }

        public void AddMarkers(IEnumerable<TimelineMarker> markers)
        {
            ArgumentNullException.ThrowIfNull(markers);
            _allMarkers.AddRange(markers);
            Refresh();
        }

        public bool RemoveMarker(Guid markerId)
        {
            int removed = _allMarkers.RemoveAll(m => m.Id == markerId);
            if (removed == 0) return false;
            if (SelectedMarker?.Id == markerId)
                SelectedMarker = null;
            Refresh();
            return true;
        }

        public TimelineMarker? FindById(Guid markerId) =>
            _allMarkers.FirstOrDefault(m => m.Id == markerId);

        public List<TimelineMarker> BuildSortedSnapshot() =>
            EventCenterService.BuildSortedSnapshot(_allMarkers);

        public void Refresh()
        {
            Guid? selectedId = SelectedMarker?.Id;
            EventCenterSummary summary = EventCenterService.CorrelateAndFilter(
                _allMarkers,
                FilterText,
                SelectedCategoryTag);

            VisibleMarkers.Clear();
            foreach (TimelineMarker marker in summary.VisibleMarkers)
                VisibleMarkers.Add(marker);

            SummaryText = summary.SummaryText;

            if (selectedId.HasValue)
            {
                TimelineMarker? reselected = VisibleMarkers.FirstOrDefault(m => m.Id == selectedId.Value);
                if (!ReferenceEquals(SelectedMarker, reselected))
                    SelectedMarker = reselected;
            }
        }

        [RelayCommand]
        public void ClearFilter()
        {
            FilterText = string.Empty;
            SelectedCategoryTag = "all";
        }
    }
}
