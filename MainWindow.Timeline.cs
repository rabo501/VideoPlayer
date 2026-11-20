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
using VideoPlayer.Services;

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

            double start = PositionSlider.Value;
            double? end = ViewModel.Export.ClipEnd;

            // Nowy początek nie może znajdować się za końcem.
            if (end.HasValue && end.Value <= start)
            {
                end = null;
            }

            ViewModel.Export.SetClipRange(start, end);
            UpdateTimelineOverlay();
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

            if (!ViewModel.Export.ClipStart.HasValue)
            {
                MessageBox.Show(
                    this,
                    "Najpierw zaznacz początek fragmentu.",
                    "Zaznaczanie fragmentu");

                return;
            }

            if (position <= ViewModel.Export.ClipStart.Value)
            {
                MessageBox.Show(
                    this,
                    "Koniec musi znajdować się za początkiem fragmentu.",
                    "Zaznaczanie fragmentu");

                return;
            }

            ViewModel.Export.SetClipRange(ViewModel.Export.ClipStart, position);
            UpdateTimelineOverlay();
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

            double? clipStart = ViewModel.Export.ClipStart;
            double? clipEnd = ViewModel.Export.ClipEnd;
            bool hasStart = clipStart.HasValue;
            bool hasEnd = clipEnd.HasValue;
            bool hasRange = hasStart && hasEnd && clipEnd!.Value > clipStart!.Value;

            ClipStartHandle.Visibility = hasStart ? Visibility.Visible : Visibility.Collapsed;
            ClipEndHandle.Visibility = hasEnd ? Visibility.Visible : Visibility.Collapsed;
            ClipSelectionRect.Visibility = hasRange ? Visibility.Visible : Visibility.Collapsed;

            double startX = hasStart
                ? Math.Clamp((clipStart!.Value - PositionSlider.Minimum) / duration * width, 0, width)
                : 0;
            double endX = hasEnd
                ? Math.Clamp((clipEnd!.Value - PositionSlider.Minimum) / duration * width, 0, width)
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
            if (ViewModel.Export.ClipStart == null || TimelineOverlay.ActualWidth <= 0)
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

            if (ViewModel.Export.ClipEnd.HasValue)
                newTime = Math.Min(newTime, ViewModel.Export.ClipEnd.Value - 0.001);

            double clamped = Math.Clamp(newTime, PositionSlider.Minimum, PositionSlider.Maximum);
            ViewModel.Export.SetClipRange(clamped, ViewModel.Export.ClipEnd);
            UpdateTimelineOverlay();
        }

        private void ClipEndHandle_DragDelta(
            object sender,
            DragDeltaEventArgs e)
        {
            if (ViewModel.Export.ClipEnd == null || TimelineOverlay.ActualWidth <= 0)
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

            if (ViewModel.Export.ClipStart.HasValue)
                newTime = Math.Max(newTime, ViewModel.Export.ClipStart.Value + 0.001);

            double clamped = Math.Clamp(newTime, PositionSlider.Minimum, PositionSlider.Maximum);
            ViewModel.Export.SetClipRange(ViewModel.Export.ClipStart, clamped);
            UpdateTimelineOverlay();
        }

        private void ClipHandle_DragCompleted(
            object sender,
            DragCompletedEventArgs e)
        {
            ViewModel.Export.RefreshClipTexts();
            UpdateTimelineOverlay();
        }

        private void LoadTimelineMarkers()
        {
            if (!string.IsNullOrWhiteSpace(_sourceFilePath) && File.Exists(_sourceFilePath))
                ViewModel.EventCenter.SetMarkers(_historyService.LoadMarkers(_sourceFilePath));
            else
                ViewModel.EventCenter.SetMarkers(Array.Empty<TimelineMarker>());
            UpdateTimelineOverlay();
        }

        private void SaveTimelineMarkers()
        {
            if (string.IsNullOrWhiteSpace(_sourceFilePath) || !File.Exists(_sourceFilePath))
                return;
            try
            {
                _historyService.SaveMarkers(_sourceFilePath, ViewModel.EventCenter.AllMarkers);
            }
            catch (Exception ex)
            {
                StatusText.Text = "Nie udało się zapisać znaczników: " + ex.Message;
            }
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

            string description = ViewModel.EventCenter.MarkerDescription.Trim();
            if (description.Length == 0)
            {
                MessageBox.Show(this, "Wpisz opis zdarzenia przed dodaniem znacznika.",
                    "Znacznik zdarzenia", MessageBoxButton.OK, MessageBoxImage.Information);
                MarkerDescriptionBox.Focus();
                return;
            }

            var marker = new TimelineMarker
            {
                Id = Guid.NewGuid(),
                Seconds = Math.Clamp(PositionSlider.Value, 0, PositionSlider.Maximum),
                Description = description
            };
            ViewModel.EventCenter.AddMarker(marker);
            SaveTimelineMarkers();
            UpdateTimelineOverlay();
            StatusText.Text = "Dodano znacznik: " + description;
        }

        private void UpdateMarker_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.EventCenter.SelectedMarker is not TimelineMarker selected)
            {
                MessageBox.Show(this, "Wybierz znacznik z listy.", "Znaczniki zdarzeń",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            string description = ViewModel.EventCenter.MarkerDescription.Trim();
            if (description.Length == 0)
            {
                MessageBox.Show(this, "Opis nie może być pusty.", "Znaczniki zdarzeń",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            selected.Description = description;
            SaveTimelineMarkers();
            ViewModel.EventCenter.Refresh();
            UpdateTimelineOverlay();
        }

        private async void GoToMarker_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.EventCenter.SelectedMarker is not TimelineMarker selected) return;
            await SeekAsync(selected.Seconds);
            StatusText.Text = "Przejście do zdarzenia: " + selected.Description;
        }

        private void DeleteMarker_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.EventCenter.SelectedMarker is not TimelineMarker selected) return;
            ViewModel.EventCenter.RemoveMarker(selected.Id);
            SaveTimelineMarkers();
            ViewModel.EventCenter.MarkerDescription = string.Empty;
            UpdateTimelineOverlay();
        }

        private async void MarkerListItem_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListBoxItem item && item.DataContext is TimelineMarker marker)
            {
                ViewModel.EventCenter.SelectedMarker = marker;
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

            var markers = ViewModel.EventCenter.AllMarkers;

            foreach (TimelineMarker marker in markers.Where(m => m.EndSeconds.HasValue && m.EndSeconds.Value > m.Seconds))
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
                    ToolTip = $"{ExportWorkspaceService.FormatClipTime(marker.Seconds)} – {ExportWorkspaceService.FormatClipTime(marker.EndSeconds.Value)} — {marker.Description}",
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(band, startX);
                Canvas.SetTop(band, Math.Max(0, (TimelineOverlay.ActualHeight - band.Height) / 2));
                Canvas.SetZIndex(band, 2);
                TimelineOverlay.Children.Add(band);
                _motionRangeBands.Add(band);
            }

            foreach (TimelineMarker marker in markers)
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
                        ? $"{ExportWorkspaceService.FormatClipTime(marker.Seconds)} – {ExportWorkspaceService.FormatClipTime(end)} — {marker.Description}"
                        : $"{ExportWorkspaceService.FormatClipTime(marker.Seconds)} — {marker.Description}",
                    Cursor = Cursors.Hand,
                    Focusable = true
                };
                button.Click += async (_, __) =>
                {
                    ViewModel.EventCenter.SelectedMarker = marker;
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
