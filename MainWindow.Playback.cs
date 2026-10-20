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
        private void HandleMessage(JsonElement message)
        {
            if (_closing)
                return;

            // Odpowiedź na nasze polecenie zapisu klatki.
            if (message.TryGetProperty("request_id", out JsonElement requestId) &&
                requestId.ValueKind == JsonValueKind.Number &&
                requestId.TryGetInt32(out int id) &&
                id == ScreenshotRequestId)
            {
                string? savedPath = _pendingScreenshotPath;

                _pendingScreenshotPath = null;
                ScreenshotButton.IsEnabled = true;

                bool success =
                    message.TryGetProperty("error", out JsonElement result) &&
                    result.ValueKind == JsonValueKind.String &&
                    result.GetString() == "success";

                if (success)
                {
                    if (!string.IsNullOrWhiteSpace(savedPath))
                        AddRecentScreenshot(savedPath);
                    StatusText.Text = "Zapisano klatkę: " + savedPath;
                }
                else
                {
                    string errorText =
                        message.TryGetProperty("error", out JsonElement errorValue)
                            ? errorValue.ToString()
                            : "Brak informacji o błędzie.";

                    StatusText.Text = "Nie udało się zapisać klatki.";

                    MessageBox.Show(
                        this,
                        "Silnik mpv nie zapisał obrazu:\n" + errorText,
                        "Błąd zapisu klatki",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }

                return;
            }

            // Odpowiedź na polecenie.
            if (!message.TryGetProperty("event", out JsonElement ev))
            {
                if (message.TryGetProperty(
                        "error", out JsonElement error) &&
                    error.ValueKind == JsonValueKind.String &&
                    error.GetString() != "success")
                {
                    StatusText.Text =
                        "mpv: " + error.GetString();
                }

                return;
            }

            if (ev.GetString() != "property-change")
                return;

            if (!message.TryGetProperty("name", out JsonElement name) ||
                !message.TryGetProperty("data", out JsonElement data))
            {
                return;
            }

            switch (name.GetString())
            {
                case "duration":
                    if (data.ValueKind == JsonValueKind.Number &&
                        data.TryGetDouble(out double duration) &&
                        double.IsFinite(duration) &&
                        duration > 0)
                    {
                        SetDuration(duration);
                    }
                    break;

                case "time-pos":
                    if (data.ValueKind == JsonValueKind.Number &&
                        data.TryGetDouble(out double position) &&
                        double.IsFinite(position) &&
                        !_dragging)
                    {
                        _updatingSlider = true;

                        try
                        {
                            PositionSlider.Value = Math.Clamp(
                                position,
                                PositionSlider.Minimum,
                                PositionSlider.Maximum);
                        }
                        finally
                        {
                            _updatingSlider = false;
                        }

                        PositionText.Text = FormatTime(position);
                    }
                    break;

                case "eof-reached":
                    _eofReached =
                        data.ValueKind == JsonValueKind.True;

                    StatusText.Text = _eofReached
                        ? "Koniec filmu. Przewiń suwakiem albo naciśnij Play."
                        : _fileName;
                    break;

                case "pause":
                    if (data.ValueKind == JsonValueKind.True)
                        PlayButton.Content = "Play";
                    else if (data.ValueKind == JsonValueKind.False)
                        PlayButton.Content = "Pauza";
                    break;
            }
        }

        private void SetDuration(double duration)
        {
            _updatingSlider = true;

            try
            {
                PositionSlider.Maximum = duration;
                PositionSlider.IsEnabled = true;
            }
            finally
            {
                _updatingSlider = false;
            }

            DurationText.Text = FormatTime(duration);
            UpdateTimelineOverlay();
            UpdateTimelineOverlay();
        }

        private static string FormatTime(double seconds)
        {
            TimeSpan time = TimeSpan.FromSeconds(
                Math.Max(0, seconds));

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0:00}:{1:00}:{2:00}",
                (int)time.TotalHours,
                time.Minutes,
                time.Seconds);
        }

        private async Task SendAsync(params object[] command)
        {
            if (!_ready || _closing) return;
            try
            {
                await _mpvService.SendAsync(command);
            }
            catch (Exception ex)
            {
                _ready = false;
                if (!_closing) StatusText.Text = "Błąd połączenia: " + ex.Message;
            }
        }

        private async void Open_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (!_ready)
            {
                MessageBox.Show(
                    this,
                    "Silnik nie jest gotowy.",
                    "Odtwarzacz");
                return;
            }

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Wybierz film",
                Filter =
                    "Pliki wideo|*.mp4;*.mkv;*.avi;*.mov;*.webm;" +
                    "*.m4v;*.wmv;*.ts|Wszystkie pliki|*.*"
            };

            if (dialog.ShowDialog(this) != true)
                return;
            _sourceFilePath = dialog.FileName;
            LoadTimelineMarkers();
            AddRecentFile(dialog.FileName);
            _currentMediaInfo = null;
            MediaInfoText.Text = "Odczytywanie parametrów nagrania…";
            _ = UpdateMediaInfoAsync(dialog.FileName);

            _clipStart = null;
            _clipEnd = null;

            UpdateClipRangeText();

            _fileName = Path.GetFileName(dialog.FileName);
            _eofReached = false;
            _dragging = false;

            _updatingSlider = true;

            try
            {
                PositionSlider.Value = 0;
                PositionSlider.Maximum = 1;
                PositionSlider.IsEnabled = false;
            }
            finally
            {
                _updatingSlider = false;
            }

            PositionText.Text = "00:00:00";
            DurationText.Text = "00:00:00";
            StatusText.Text = _fileName;

            await SendAsync("loadfile", dialog.FileName, "replace");
            await SendAsync(
                "set_property", "speed", GetSelectedSpeed());
            await SendAsync("set_property", "pause", false);
        }

        private async void PlayPause_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (_eofReached)
            {
                // Play na końcu filmu rozpoczyna go od nowa.
                await SeekAsync(0);
                await SendAsync("set_property", "pause", false);
            }
            else
            {
                await SendAsync("cycle", "pause");
            }
        }

        private async void PreviousFrame_Click(
            object sender,
            RoutedEventArgs e)
        {
            await SendAsync("set_property", "pause", true);
            await SendAsync("frame-back-step");
        }

        private async void NextFrame_Click(
            object sender,
            RoutedEventArgs e)
        {
            await SendAsync("set_property", "pause", true);
            await SendAsync("frame-step");
        }

        private async void Speed_Changed(
            object sender,
            SelectionChangedEventArgs e)
        {
            if (!_ready)
                return;

            await SendAsync(
                "set_property", "speed", GetSelectedSpeed());
        }

        private double GetSelectedSpeed()
        {
            if (SpeedBox.SelectedItem is ComboBoxItem item &&
                item.Tag is string value &&
                double.TryParse(
                    value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double speed))
            {
                return speed;
            }

            return 1.0;
        }

        private void PositionSlider_DragStarted(
            object sender,
            DragStartedEventArgs e)
        {
            _dragging = true;
        }

        private async void PositionSlider_DragCompleted(
            object sender,
            DragCompletedEventArgs e)
        {
            double target = PositionSlider.Value;

            // Aktualizacja pozycji filmu dopiero po puszczeniu suwaka.
            await SeekAsync(target);

            _dragging = false;
        }

        private async void PositionSlider_ValueChanged(
            object sender,
            RoutedPropertyChangedEventArgs<double> e)
        {
            // Ignorujemy aktualizacje przychodzące z silnika.
            if (_updatingSlider || !_ready ||
                !PositionSlider.IsEnabled)
            {
                return;
            }

            PositionText.Text = FormatTime(e.NewValue);

            // Podczas przeciągania pokazujemy wybrany czas.
            // Kliknięcie toru lub użycie klawiatury przewija od razu.
            if (!_dragging)
                await SeekAsync(e.NewValue);
        }

        private async Task SeekAsync(double seconds)
        {
            if (!_ready || !PositionSlider.IsEnabled)
                return;

            double target = Math.Clamp(
                seconds, 0, PositionSlider.Maximum);

            await SendAsync("seek", target, "absolute+exact");

            // Nie zmieniamy pauzy: przewinięty, zatrzymany film
            // pozostaje zatrzymany. Play wznowi go w nowym miejscu.
        }

        private async void Screenshot_Click(
    object sender,
    RoutedEventArgs e)
        {
            if (!_ready || !PositionSlider.IsEnabled)
            {
                MessageBox.Show(
                    this,
                    "Najpierw otwórz film.",
                    "Zapis klatki",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                return;
            }

            if (_pendingScreenshotPath != null)
                return;

            // Zatrzymujemy film, żeby podczas wybierania nazwy pliku
            // wyświetlana klatka się nie zmieniła.
            await SendAsync("set_property", "pause", true);

            if (!_ready)
                return;

            string baseName = Path.GetFileNameWithoutExtension(_fileName);

            string position = TimeSpan
                .FromSeconds(Math.Max(0, PositionSlider.Value))
                .ToString(
                    @"hh\-mm\-ss\-fff",
                    CultureInfo.InvariantCulture);

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Zapisz aktualną klatkę",
                Filter = "Obraz JPEG (*.jpg)|*.jpg",
                DefaultExt = ".jpg",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = $"{baseName}_{position}.jpg"
            };

            if (dialog.ShowDialog(this) != true)
                return;

            ScreenshotButton.IsEnabled = false;
            _pendingScreenshotPath = dialog.FileName;

            try
            {
                StatusText.Text = "Zapisywanie klatki…";
                await _mpvService.SendRequestAsync(
                    ScreenshotRequestId,
                    "screenshot-to-file",
                    dialog.FileName,
                    "video");
            }
            catch (Exception ex)
            {
                _pendingScreenshotPath = null;
                ScreenshotButton.IsEnabled = true;
                StatusText.Text = "Nie udało się wysłać polecenia zapisu klatki.";
                MessageBox.Show(this, ex.Message, "Błąd zapisu klatki",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
