using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;

namespace VideoPlayer
{
    public partial class PlateSearchWindow : Window
    {
        private readonly HistoryService _history;
        public PlateSearchResult? SelectedResult { get; private set; }

        public PlateSearchWindow(HistoryService history)
        {
            InitializeComponent();
            _history = history;
            Loaded += (_, __) => Search();
        }

        private void Search_Click(object sender, RoutedEventArgs e) => Search();

        private void Search()
        {
            try
            {
                List<PlateSearchResult> results = _history.SearchMarkers(
                    PlateFilterBox.Text, MakeFilterBox.Text, SourceFilterBox.Text);
                ResultsList.ItemsSource = results;
                ResultCountText.Text = results.Count == 1000
                    ? "Znaleziono co najmniej 1000 wyników (limit listy: 1000). Zawęź kryteria."
                    : $"Znaleziono: {results.Count} zdarzeń.";
            }
            catch (Exception ex)
            {
                ResultCountText.Text = "Błąd wyszukiwania: " + ex.Message;
            }
        }

        private void OpenSelected()
        {
            if (ResultsList.SelectedItem is not PlateSearchResult result) return;
            SelectedResult = result;
            DialogResult = true;
        }

        private void OpenSelected_Click(object sender, RoutedEventArgs e) => OpenSelected();
        private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected();
        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}