using System;
using System.IO;
using System.Windows;

namespace VideoPlayer
{
    public partial class MainWindow
    {
        private void PlateSearch_Click(object sender, RoutedEventArgs e)
        {
            if (_historyService == null)
            {
                MessageBox.Show(this, "Historia nie została jeszcze zainicjalizowana.", "Wyszukiwarka tablic");
                return;
            }

            var dialog = new PlateSearchWindow(_historyService) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.SelectedResult is not PlateSearchResult result)
                return;

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

            await OpenMediaAsync(result.SourcePath);
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