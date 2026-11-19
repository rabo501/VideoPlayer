using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

using Forms = System.Windows.Forms;

namespace VideoPlayer
{
    public partial class MainWindow
    {
        private void MarkStart_Click(
    object sender,
    RoutedEventArgs e)
        {
            if (!_ready || _opening || !PositionSlider.IsEnabled)
                return;

            if (!_paused)
            {
                MessageBox.Show(
                    this,
                    "Najpierw zatrzymaj film przyciskiem Play / Pauza " +
                    "lub ustaw klatkę przyciskami klatkowymi.",
                    "Zaznaczanie fragmentu",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                return;
            }

            _clipStart = PositionSlider.Value;

            // Nowy początek nie może znajdować się za końcem.
            if (_clipEnd.HasValue &&
                _clipEnd.Value <= _clipStart.Value)
            {
                _clipEnd = null;
            }

            UpdateClipRangeText();
        }

        private void MarkEnd_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (!_ready || _opening || !PositionSlider.IsEnabled)
                return;

            if (!_paused)
            {
                MessageBox.Show(
                    this,
                    "Najpierw zatrzymaj film przyciskiem Play / Pauza " +
                    "lub ustaw klatkę przyciskami klatkowymi.",
                    "Zaznaczanie fragmentu",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                return;
            }

            double position = PositionSlider.Value;

            if (!_clipStart.HasValue)
            {
                MessageBox.Show(
                    this,
                    "Najpierw zaznacz początek fragmentu.",
                    "Zaznaczanie fragmentu");

                return;
            }

            if (position <= _clipStart.Value)
            {
                MessageBox.Show(
                    this,
                    "Koniec musi znajdować się za początkiem fragmentu.",
                    "Zaznaczanie fragmentu");

                return;
            }

            _clipEnd = position;
            UpdateClipRangeText();
        }

        private static string FormatClipTime(double seconds)
        {
            return TimeSpan
                .FromSeconds(Math.Max(0, seconds))
                .ToString(
                    @"d\.hh\:mm\:ss\.fff",
                    CultureInfo.InvariantCulture);
        }

        private void UpdateClipRangeText()
        {
            UpdateRangeHint();
            string start = _clipStart.HasValue
                ? FormatClipTime(_clipStart.Value)
                : "—";

            string end = _clipEnd.HasValue
                ? FormatClipTime(_clipEnd.Value)
                : "—";

            string duration = "—";

            if (_clipStart.HasValue &&
                _clipEnd.HasValue &&
                _clipEnd.Value > _clipStart.Value)
            {
                duration = FormatClipTime(_clipEnd.Value - _clipStart.Value);
            }

            ClipRangeText.Text =
                $"Początek: {start}\n" +
                $"Koniec: {end}\n" +
                $"Długość: {duration}";

            UpdateTimelineOverlay();
            UpdateEstimatedSizeText();
        }

        private void TimelineOverlay_SizeChanged(
            object sender,
            SizeChangedEventArgs e)
        {
            UpdateTimelineOverlay();
        }

        private void UpdateTimelineOverlay()
        {
            if (TimelineOverlay == null ||
                ClipStartHandle == null ||
                ClipEndHandle == null ||
                ClipSelectionRect == null)
            {
                return;
            }

            double width = TimelineOverlay.ActualWidth;
            double duration = PositionSlider.Maximum - PositionSlider.Minimum;

            if (width <= 0 || duration <= 0 || !double.IsFinite(width))
                return;

            bool hasStart = _clipStart.HasValue;
            bool hasEnd = _clipEnd.HasValue;
            bool hasRange = hasStart && hasEnd && _clipEnd!.Value > _clipStart!.Value;

            ClipStartHandle.Visibility = hasStart ? Visibility.Visible : Visibility.Collapsed;
            ClipEndHandle.Visibility = hasEnd ? Visibility.Visible : Visibility.Collapsed;
            ClipSelectionRect.Visibility = hasRange ? Visibility.Visible : Visibility.Collapsed;

            double startX = hasStart
                ? Math.Clamp((_clipStart!.Value - PositionSlider.Minimum) / duration * width, 0, width)
                : 0;
            double endX = hasEnd
                ? Math.Clamp((_clipEnd!.Value - PositionSlider.Minimum) / duration * width, 0, width)
                : 0;

            if (hasStart)
                Canvas.SetLeft(ClipStartHandle, Math.Clamp(startX - ClipStartHandle.Width / 2, -1, Math.Max(-1, width - ClipStartHandle.Width + 1)));

            if (hasEnd)
                Canvas.SetLeft(ClipEndHandle, Math.Clamp(endX - ClipEndHandle.Width / 2, -1, Math.Max(-1, width - ClipEndHandle.Width + 1)));

            if (hasRange)
            {
                Canvas.SetLeft(ClipSelectionRect, startX);
                ClipSelectionRect.Width = Math.Max(0, endX - startX);
                Canvas.SetTop(ClipSelectionRect, (TimelineOverlay.ActualHeight - ClipSelectionRect.Height) / 2);
            }

            DrawTimelineMarkers();
        }

        private void ClipStartHandle_DragDelta(
            object sender,
            DragDeltaEventArgs e)
        {
            if (_clipStart == null || TimelineOverlay.ActualWidth <= 0)
                return;

            double width = TimelineOverlay.ActualWidth;
            double left = Canvas.GetLeft(ClipStartHandle);
            if (!double.IsFinite(left)) left = 0;

            double center = Math.Clamp(
                left + ClipStartHandle.Width / 2 + e.HorizontalChange,
                0,
                width);

            double newTime = PositionSlider.Minimum +
                center / width * (PositionSlider.Maximum - PositionSlider.Minimum);

            if (_clipEnd.HasValue)
                newTime = Math.Min(newTime, _clipEnd.Value - 0.001);

            _clipStart = Math.Clamp(newTime, PositionSlider.Minimum, PositionSlider.Maximum);
            UpdateClipRangeText();
        }

        private void ClipEndHandle_DragDelta(
            object sender,
            DragDeltaEventArgs e)
        {
            if (_clipEnd == null || TimelineOverlay.ActualWidth <= 0)
                return;

            double width = TimelineOverlay.ActualWidth;
            double left = Canvas.GetLeft(ClipEndHandle);
            if (!double.IsFinite(left)) left = 0;

            double center = Math.Clamp(
                left + ClipEndHandle.Width / 2 + e.HorizontalChange,
                0,
                width);

            double newTime = PositionSlider.Minimum +
                center / width * (PositionSlider.Maximum - PositionSlider.Minimum);

            if (_clipStart.HasValue)
                newTime = Math.Max(newTime, _clipStart.Value + 0.001);

            _clipEnd = Math.Clamp(newTime, PositionSlider.Minimum, PositionSlider.Maximum);
            UpdateClipRangeText();
        }

        private void ClipHandle_DragCompleted(
            object sender,
            DragCompletedEventArgs e)
        {
            UpdateClipRangeText();
        }

        private void LoadTimelineMarkers()
        {
            _timelineMarkers.Clear();
            if (!string.IsNullOrWhiteSpace(_sourceFilePath) && File.Exists(_sourceFilePath))
                _timelineMarkers.AddRange(_historyService.LoadMarkers(_sourceFilePath));
            RefreshTimelineMarkerList();
            UpdateTimelineOverlay();
        }

        private void SaveTimelineMarkers()
        {
            if (string.IsNullOrWhiteSpace(_sourceFilePath) || !File.Exists(_sourceFilePath))
                return;
            try
            {
                _historyService.SaveMarkers(_sourceFilePath, _timelineMarkers);
            }
            catch (Exception ex)
            {
                StatusText.Text = "Nie udało się zapisać znaczników: " + ex.Message;
            }
        }

        private void RefreshTimelineMarkerList()
        {
            if (TimelineMarkersListBox == null) return;
            TimelineMarkersListBox.ItemsSource = null;
            TimelineMarkersListBox.ItemsSource = _timelineMarkers.OrderBy(m => m.Seconds).ToList();
        }

        private void AddMarker_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_sourceFilePath) || !File.Exists(_sourceFilePath) ||
                !PositionSlider.IsEnabled)
            {
                MessageBox.Show(this, "Najpierw otwórz nagranie.", "Znacznik zdarzenia",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string description = MarkerDescriptionBox.Text.Trim();
            if (description.Length == 0)
            {
                MessageBox.Show(this, "Wpisz opis zdarzenia przed dodaniem znacznika.",
                    "Znacznik zdarzenia", MessageBoxButton.OK, MessageBoxImage.Information);
                MarkerDescriptionBox.Focus();
                return;
            }

            _timelineMarkers.Add(new TimelineMarker
            {
                Id = Guid.NewGuid(),
                Seconds = Math.Clamp(PositionSlider.Value, 0, PositionSlider.Maximum),
                Description = description
            });
            SaveTimelineMarkers();
            RefreshTimelineMarkerList();
            UpdateTimelineOverlay();
            StatusText.Text = "Dodano znacznik: " + description;
        }

        private void UpdateMarker_Click(object sender, RoutedEventArgs e)
        {
            if (TimelineMarkersListBox.SelectedItem is not TimelineMarker selected)
            {
                MessageBox.Show(this, "Wybierz znacznik z listy.", "Znaczniki zdarzeń",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            string description = MarkerDescriptionBox.Text.Trim();
            if (description.Length == 0)
            {
                MessageBox.Show(this, "Opis nie może być pusty.", "Znaczniki zdarzeń",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            selected.Description = description;
            SaveTimelineMarkers();
            RefreshTimelineMarkerList();
            TimelineMarkersListBox.SelectedItem = _timelineMarkers.FirstOrDefault(m => m.Id == selected.Id);
            UpdateTimelineOverlay();
        }

        private async void GoToMarker_Click(object sender, RoutedEventArgs e)
        {
            if (TimelineMarkersListBox.SelectedItem is not TimelineMarker selected) return;
            await SeekAsync(selected.Seconds);
            StatusText.Text = "Przejście do zdarzenia: " + selected.Description;
        }

        private void DeleteMarker_Click(object sender, RoutedEventArgs e)
        {
            if (TimelineMarkersListBox.SelectedItem is not TimelineMarker selected) return;
            _timelineMarkers.RemoveAll(m => m.Id == selected.Id);
            SaveTimelineMarkers();
            RefreshTimelineMarkerList();
            MarkerDescriptionBox.Clear();
            UpdateTimelineOverlay();
        }

        private void TimelineMarkersListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TimelineMarkersListBox.SelectedItem is TimelineMarker marker)
                MarkerDescriptionBox.Text = marker.Description;
        }

        private async void MarkerListItem_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListBoxItem item && item.DataContext is TimelineMarker marker)
            {
                TimelineMarkersListBox.SelectedItem = marker;
                await SeekAsync(marker.Seconds);
                StatusText.Text = "Przejście do zdarzenia: " + marker.Description;
            }
        }

        private void DrawTimelineMarkers()
        {
            if (TimelineOverlay == null) return;
            foreach (var button in _markerButtons)
                TimelineOverlay.Children.Remove(button);
            _markerButtons.Clear();
            foreach (var band in _motionRangeBands) TimelineOverlay.Children.Remove(band);
            _motionRangeBands.Clear();

            double width = TimelineOverlay.ActualWidth;
            double duration = PositionSlider.Maximum - PositionSlider.Minimum;
            if (width <= 0 || duration <= 0 || !PositionSlider.IsEnabled) return;

            foreach (TimelineMarker marker in _timelineMarkers.Where(m => m.EndSeconds.HasValue && m.EndSeconds.Value > m.Seconds))
            {
                double startX = Math.Clamp((marker.Seconds - PositionSlider.Minimum) / duration * width, 0, width);
                double endX = Math.Clamp((marker.EndSeconds!.Value - PositionSlider.Minimum) / duration * width, 0, width);
                var band = new Border
                {
                    Height = 14,
                    Width = Math.Max(2, endX - startX),
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(105, 255, 145, 0)),
                    BorderBrush = System.Windows.Media.Brushes.DarkOrange,
                    BorderThickness = new Thickness(0, 1, 0, 1),
                    CornerRadius = new CornerRadius(2),
                    ToolTip = $"{FormatClipTime(marker.Seconds)} – {FormatClipTime(marker.EndSeconds.Value)} — {marker.Description}",
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(band, startX);
                Canvas.SetTop(band, Math.Max(0, (TimelineOverlay.ActualHeight - band.Height) / 2));
                Canvas.SetZIndex(band, 2);
                TimelineOverlay.Children.Add(band);
                _motionRangeBands.Add(band);
            }

            foreach (TimelineMarker marker in _timelineMarkers)
            {
                double x = Math.Clamp((marker.Seconds - PositionSlider.Minimum) / duration * width, 0, width);
                var button = new System.Windows.Controls.Button
                {
                    Width = 12,
                    Height = 12,
                    Padding = new Thickness(0),
                    BorderThickness = new Thickness(1),
                    BorderBrush = System.Windows.Media.Brushes.White,
                    Background = System.Windows.Media.Brushes.DarkOrange,
                    ToolTip = marker.EndSeconds is double end && end > marker.Seconds
                        ? $"{FormatClipTime(marker.Seconds)} – {FormatClipTime(end)} — {marker.Description}"
                        : $"{FormatClipTime(marker.Seconds)} — {marker.Description}",
                    Cursor = Cursors.Hand,
                    Focusable = true
                };
                button.Click += async (_, __) =>
                {
                    TimelineMarkersListBox.SelectedItem = marker;
                    await SeekAsync(marker.Seconds);
                    StatusText.Text = "Przejście do zdarzenia: " + marker.Description;
                };
                Canvas.SetLeft(button, Math.Clamp(x - button.Width / 2, 0, Math.Max(0, width - button.Width)));
                Canvas.SetTop(button, Math.Max(0, (TimelineOverlay.ActualHeight - button.Height) / 2));
                Canvas.SetZIndex(button, 20);
                TimelineOverlay.Children.Add(button);
                _markerButtons.Add(button);
            }
        }
    }
}
