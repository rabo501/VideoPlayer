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

using Forms = System.Windows.Forms;

namespace VideoPlayer
{
    public partial class MainWindow
    {
        private void MarkStart_Click(
    object sender,
    RoutedEventArgs e)
        {
            if (!_ready || !PositionSlider.IsEnabled)
                return;

            if (!Equals(PlayButton.Content, "Play"))
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
            if (!_ready || !PositionSlider.IsEnabled)
                return;

            if (!Equals(PlayButton.Content, "Play"))
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
    }
}
