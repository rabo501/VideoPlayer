using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace VideoPlayer
{
    public partial class MainWindow
    {
        private bool _plateSearchLoadedOnce;

        private async void PlateSearch_Click(object sender, RoutedEventArgs e)
        {
            if (_historyService == null)
            {
                MessageBox.Show(this, "Historia nie została jeszcze zainicjalizowana.", "Wyszukiwarka tablic");
                return;
            }

            SetWorkspaceMode(WorkspaceMode.Player, 1);
            PlateSearchExpander.IsExpanded = true;
            PlateSearchExpander.BringIntoView();
            SearchPlateFilterBox.Focus();
            _plateSearchLoadedOnce = true;
            await ViewModel.PlateSearch.SearchAsync();
        }

        private async void PlateSearchExpander_Expanded(object sender, RoutedEventArgs e)
        {
            if (_historyService != null && !_plateSearchLoadedOnce)
            {
                _plateSearchLoadedOnce = true;
                await ViewModel.PlateSearch.SearchAsync();
            }
        }

        private void PlateSearchOpenSelected_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.PlateSearch.SelectedResult is PlateSearchResult result)
                _ = OpenPlateSearchResultAsync(result);
        }

        private void PlateSearchResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ViewModel.PlateSearch.SelectedResult is PlateSearchResult result)
                _ = OpenPlateSearchResultAsync(result);
        }

        private async Task OpenPlateSearchResultAsync(PlateSearchResult result)
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