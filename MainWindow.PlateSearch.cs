using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace VideoPlayer
{
    public partial class MainWindow
    {
        private bool _plateSearchLoadedOnce;

        private void PlateSearch_Click(object sender, RoutedEventArgs e)
        {
            if (_historyService == null)
            {
                MessageBox.Show(this, "Historia nie została jeszcze zainicjalizowana.", "Wyszukiwarka tablic");
                return;
            }

            SetWorkspaceMode(WorkspaceMode.Player, 1);
            PlateSearchExpander.IsExpanded = true;
            PlateSearchExpander.BringIntoView();
            RunPlateSearch();
            SearchPlateFilterBox.Focus();
        }

        private void PlateSearchExpander_Expanded(object sender, RoutedEventArgs e)
        {
            if (_historyService != null && !_plateSearchLoadedOnce)
            {
                _plateSearchLoadedOnce = true;
                RunPlateSearch();
            }
        }

        private void PlateSearchExecute_Click(object sender, RoutedEventArgs e) => RunPlateSearch();

        private void RunPlateSearch()
        {
            if (_historyService == null) return;
            try
            {
                List<PlateSearchResult> results = _historyService.SearchMarkers(
                    SearchPlateFilterBox.Text, SearchMakeFilterBox.Text, SearchSourceFilterBox.Text);
                PlateSearchResultsList.ItemsSource = results;
                SearchResultCountText.Text = results.Count == 1000
                    ? "Znaleziono co najmniej 1000 wyników (limit listy: 1000). Zawęź kryteria."
                    : $"Znaleziono: {results.Count} zdarzeń.";
            }
            catch (Exception ex)
            {
                SearchResultCountText.Text = "Błąd wyszukiwania: " + ex.Message;
            }
        }

        private void PlateSearchOpenSelected_Click(object sender, RoutedEventArgs e)
        {
            if (PlateSearchResultsList.SelectedItem is PlateSearchResult result)
                _ = OpenPlateSearchResultAsync(result);
        }

        private void PlateSearchResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (PlateSearchResultsList.SelectedItem is PlateSearchResult result)
                _ = OpenPlateSearchResultAsync(result);
        }

        private async System.Threading.Tasks.Task OpenPlateSearchResultAsync(PlateSearchResult result)
        {
            if (!File.Exists(result.SourcePath))
            {
                MessageBox.Show(this, "Plik nagrania nie istnieje lub nie jest dostępny:\n" + result.SourcePath,
                    "Wyszukiwarka tablic", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SetWorkspaceMode(WorkspaceMode.Player, 1);
            if (_sourceFilePath == null ||
                !string.Equals(Path.GetFullPath(_sourceFilePath), Path.GetFullPath(result.SourcePath), StringComparison.OrdinalIgnoreCase))
            {
                await OpenMediaAsync(result.SourcePath);
            }

            if (!_opening && _sourceFilePath != null &&
                string.Equals(Path.GetFullPath(_sourceFilePath), Path.GetFullPath(result.SourcePath), StringComparison.OrdinalIgnoreCase))
            {
                await SendAsync("set_property", "pause", true);
                await SeekAsync(result.Seconds);
                StatusText.Text = "Wynik wyszukiwania: " + result.PlateNumber + " — " + result.TimeText;
            }
        }
    }
}