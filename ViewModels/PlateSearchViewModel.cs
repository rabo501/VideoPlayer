using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace VideoPlayer.ViewModels
{
    internal sealed partial class PlateSearchViewModel : ObservableObject, IDisposable
    {
        private Func<string, string, string, List<PlateSearchResult>>? _searchProvider;
        private CancellationTokenSource? _searchCts;
        private long _searchSequence;

        public PlateSearchViewModel(Func<string, string, string, List<PlateSearchResult>>? searchProvider = null)
        {
            _searchProvider = searchProvider;
        }

        public ObservableCollection<PlateSearchResult> Results { get; } = new();

        [ObservableProperty]
        private string _plateFilter = string.Empty;

        [ObservableProperty]
        private string _makeFilter = string.Empty;

        [ObservableProperty]
        private string _sourceFilter = string.Empty;

        [ObservableProperty]
        private bool _isSearching;

        [ObservableProperty]
        private string _resultCountText = "Wpisz kryteria i kliknij Szukaj.";

        [ObservableProperty]
        private PlateSearchResult? _selectedResult;

        public void AttachSearchProvider(Func<string, string, string, List<PlateSearchResult>> searchProvider)
        {
            _searchProvider = searchProvider ?? throw new ArgumentNullException(nameof(searchProvider));
        }

        [RelayCommand]
        public async Task SearchAsync()
        {
            if (_searchProvider == null)
            {
                ResultCountText = "Historia nie została jeszcze zainicjalizowana.";
                return;
            }

            long seq = Interlocked.Increment(ref _searchSequence);
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            var cts = new CancellationTokenSource();
            _searchCts = cts;

            string plate = PlateFilter ?? string.Empty;
            string make = MakeFilter ?? string.Empty;
            string source = SourceFilter ?? string.Empty;
            Func<string, string, string, List<PlateSearchResult>> provider = _searchProvider;

            IsSearching = true;
            ResultCountText = "Wyszukiwanie…";
            try
            {
                List<PlateSearchResult> found = await Task.Run(() =>
                {
                    cts.Token.ThrowIfCancellationRequested();
                    return provider(plate, make, source);
                }, cts.Token);

                if (cts.IsCancellationRequested || seq != Interlocked.Read(ref _searchSequence))
                    return;

                Results.Clear();
                foreach (PlateSearchResult item in found)
                    Results.Add(item);

                ResultCountText = FormatResultCount(found.Count);
            }
            catch (OperationCanceledException)
            {
                // Replaced by a newer query or canceled on shutdown.
            }
            catch (Exception ex)
            {
                if (seq == Interlocked.Read(ref _searchSequence))
                    ResultCountText = "Błąd wyszukiwania: " + ex.Message;
            }
            finally
            {
                if (ReferenceEquals(_searchCts, cts))
                {
                    _searchCts.Dispose();
                    _searchCts = null;
                }

                if (seq == Interlocked.Read(ref _searchSequence))
                    IsSearching = false;
            }
        }

        public static string FormatResultCount(int count) =>
            count >= 1000
                ? "Znaleziono co najmniej 1000 wyników (limit listy: 1000). Zawęź kryteria."
                : $"Znaleziono: {count} zdarzeń.";

        public void CancelSearch()
        {
            Interlocked.Increment(ref _searchSequence);
            _searchCts?.Cancel();
            IsSearching = false;
            ResultCountText = "Anulowano wyszukiwanie.";
        }

        public void Dispose()
        {
            CancelSearch();
            _searchProvider = null;
            // Aktywne zadanie zwalnia własny CancellationTokenSource w bloku finally.
        }
    }
}
