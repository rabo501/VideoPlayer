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
    public partial class MainWindow : Window
    {
        private readonly Forms.Panel _videoPanel = new Forms.Panel
        {
            BackColor = System.Drawing.Color.Black,
            Dock = Forms.DockStyle.Fill
        };

        private readonly string _pipeName =
            "VideoPlayer_" + Guid.NewGuid().ToString("N");

        private readonly SemaphoreSlim _sendLock =
            new SemaphoreSlim(1, 1);

        private Process? _mpv;
        private NamedPipeClientStream? _pipe;
        private StreamWriter? _writer;

        private bool _ready;
        private bool _closing;
        private bool _dragging;
        private bool _updatingSlider;
        private bool _eofReached;

        private string _fileName = "";

        private const int ScreenshotRequestId = 10001;
        private string? _pendingScreenshotPath;
        private string? _sourceFilePath;
        private double? _clipStart;
        private double? _clipEnd;
        private bool _exporting;
        private Process? _exportProcess;
        private bool _cancelExportRequested;
        private Stopwatch? _exportStopwatch;
        private string _activeOperationName = "";
        private double _activeOperationDuration;

        private readonly string _historyDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoPlayer");
        private string HistoryFilePath => Path.Combine(_historyDirectory, "history.json");
        private readonly System.Collections.Generic.List<string> _recentFiles = new();
        private readonly System.Collections.Generic.List<string> _recentScreenshots = new();
        private MediaInfo? _currentMediaInfo;
        private string? _lastExportSummary;

        private sealed class MediaInfo
        {
            public double Duration { get; set; }
            public string VideoCodec { get; set; } = "nieznany";
            public string Resolution { get; set; } = "nieznana";
            public string Fps { get; set; } = "nieznane";
            public string Bitrate { get; set; } = "nieznany";
            public string AudioCodec { get; set; } = "brak / nieznany";
            public long FileSize { get; set; }
        }

        private sealed class AppHistory
        {
            public System.Collections.Generic.List<string> RecentFiles { get; set; } = new();
            public System.Collections.Generic.List<string> RecentScreenshots { get; set; } = new();
        }

        public MainWindow()
        {
            InitializeComponent();
            VideoHost.Child = _videoPanel;
        }

        private void ExitMenu_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void About_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                this,
                "Odtwarzacz wideo\n" +
                "Wersja: 1.0\n\n" +
                "Aplikacja do odtwarzania nagrań wideo, przeglądania nagrań z monitoringu, " +
                "zaznaczania i eksportowania fragmentów oraz zapisywania pojedynczych klatek.\n\n" +
                "Silnik odtwarzania: mpv\n" +
                "Przetwarzanie i eksport: FFmpeg\n\n" +
                "Autor: Rafał Bonna\n" +
                "© 2026",
                "Informacje o programie",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void BasicHelp_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                this,
                "PODSTAWOWE FUNKCJE ODTWARZACZA\n\n" +
                "1. Otwieranie nagrania\n" +
                "Wybierz Plik > Otwórz film lub kliknij przycisk „Otwórz film”. Wybierz lokalny plik wideo.\n\n" +
                "2. Odtwarzanie i nawigacja\n" +
                "Użyj przycisku Odtwarzaj / Pauza. Suwak osi czasu pozwala przejść do wybranego momentu. " +
                "Przyciski Poprzednia klatka i Następna klatka umożliwiają dokładniejsze przeglądanie nagrania. " +
                "Listę prędkości można wykorzystać do zwolnienia lub przyspieszenia odtwarzania.\n\n" +
                "3. Zapis klatki JPG\n" +
                "Zatrzymaj nagranie na wybranym obrazie, kliknij Zapisz klatkę JPG i wskaż nazwę oraz folder pliku. " +
                "Zapisane obrazy pojawiają się na liście zrzutów.\n\n" +
                "4. Zaznaczanie fragmentu\n" +
                "Ustaw odtwarzanie w miejscu początku i kliknij ustawienie początku fragmentu. Następnie przejdź do końca " +
                "i ustaw koniec. Jeśli widoczne są uchwyty osi czasu, możesz też przeciągać je myszą.\n\n" +
                "5. Eksport fragmentu\n" +
                "Wybierz profil eksportu i wskaż plik wynikowy. Eksport może potrwać dłużej w zależności od długości nagrania, " +
                "rozdzielczości i wybranego profilu. W trakcie kodowania obserwuj pasek postępu. Jeśli funkcja anulowania jest dostępna, " +
                "możesz przerwać operację.\n\n" +
                "6. Informacje o nagraniu i historia\n" +
                "W panelu sterowania sprawdź parametry pliku oraz szacowany rozmiar zaznaczonego fragmentu. Listy ostatnich nagrań " +
                "i zrzutów ułatwiają ponowne otwieranie wcześniej używanych plików.\n\n" +
                "Uwaga: nazwy przycisków i dostępność poszczególnych opcji mogą zależeć od wersji aplikacji. " +
                "Oryginalne nagranie pozostaje bez zmian; eksport zapisuje nowy plik.",
                "Pomoc — podstawowe funkcje",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }


        private async void Window_Loaded(
            object sender,
            RoutedEventArgs e)
        {
            LoadHistory();
            RefreshHistoryControls();
            try
            {
                string executable = Path.Combine(
                    AppContext.BaseDirectory, "mpv", "mpv.exe");

                if (!File.Exists(executable))
                {
                    throw new FileNotFoundException(
                        "Nie znaleziono mpv.exe w folderze mpv.");
                }

                uint windowHandle = unchecked(
                    (uint)_videoPanel.Handle.ToInt64());

                var startInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                string[] arguments =
                {
                    "--no-config",
                    "--idle=yes",
                    "--keep-open=always",
                    "--keep-open-pause=yes",
                    "--terminal=no",
                    "--osc=no",
                    "--input-default-bindings=no",
                    "--input-vo-keyboard=no",
                    "--wid=" + windowHandle.ToString(
                        CultureInfo.InvariantCulture),
                    "--screenshot-jpeg-quality=95",
                    "--input-ipc-server=\\\\.\\pipe\\" + _pipeName

                };

                foreach (string argument in arguments)
                    startInfo.ArgumentList.Add(argument);

                _mpv = Process.Start(startInfo)
                    ?? throw new InvalidOperationException(
                        "Nie udało się uruchomić mpv.");

                _pipe = new NamedPipeClientStream(
                    ".",
                    _pipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);

                await _pipe.ConnectAsync(10000);

                if (_closing)
                    return;

                _writer = new StreamWriter(
                    _pipe,
                    new UTF8Encoding(false),
                    4096,
                    leaveOpen: true)
                {
                    AutoFlush = true,
                    NewLine = "\n"
                };

                _ready = true;

                // Czytanie odpowiedzi i zdarzeń mpv.
                _ = ReadMessagesAsync(_pipe);

                // mpv będzie przesyłać zmiany tych właściwości.
                await SendAsync("observe_property", 1, "time-pos");
                await SendAsync("observe_property", 2, "duration");
                await SendAsync("observe_property", 3, "eof-reached");
                await SendAsync("observe_property", 4, "pause");

                await SendAsync(
                    "set_property", "speed", GetSelectedSpeed());

                StatusText.Text = "Gotowy. Otwórz film.";
            }
            catch (Exception ex)
            {
                if (!_closing)
                {
                    StatusText.Text =
                        "Nie udało się uruchomić odtwarzacza.";

                    MessageBox.Show(
                        this,
                        ex.Message,
                        "Błąd uruchamiania",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
        }

        private async Task ReadMessagesAsync(
            NamedPipeClientStream pipe)
        {
            try
            {
                using var reader = new StreamReader(
                    pipe,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 4096,
                    leaveOpen: true);

                while (!_closing)
                {
                    string? line = await reader.ReadLineAsync();

                    if (line == null)
                        break;

                    try
                    {
                        using JsonDocument document =
                            JsonDocument.Parse(line);

                        JsonElement message =
                            document.RootElement.Clone();

                        if (_closing)
                            break;

                        await Dispatcher.InvokeAsync(
                            () => HandleMessage(message));
                    }
                    catch (JsonException)
                    {
                        // Pomijamy niepoprawny komunikat.
                    }
                }
            }
            catch (IOException)
            {
                // Zerwane lub zamknięte połączenie.
            }
            catch (ObjectDisposedException)
            {
                // Zamknięcie aplikacji.
            }
            finally
            {
                if (!_closing)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (_closing)
                            return;

                        _ready = false;
                        StatusText.Text =
                            "Połączenie z silnikiem zostało zamknięte.";
                    });
                }
            }
        }

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
            if (!_ready || _closing)
                return;

            await _sendLock.WaitAsync();

            try
            {
                if (_writer == null || _closing)
                    return;

                string message = JsonSerializer.Serialize(
                    new { command });

                await _writer.WriteLineAsync(message);
            }
            catch (Exception ex)
            {
                _ready = false;

                if (!_closing)
                {
                    StatusText.Text =
                        "Błąd połączenia: " + ex.Message;
                }
            }
            finally
            {
                _sendLock.Release();
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

            await _sendLock.WaitAsync();

            try
            {
                if (!_ready || _closing || _writer == null)
                {
                    _pendingScreenshotPath = null;
                    ScreenshotButton.IsEnabled = true;
                    return;
                }

                string message = JsonSerializer.Serialize(new
                {
                    command = new object[]
                    {
                "screenshot-to-file",
                dialog.FileName,
                "video"
                    },
                    request_id = ScreenshotRequestId
                });

                StatusText.Text = "Zapisywanie klatki…";

                await _writer.WriteLineAsync(message);
            }
            catch (Exception ex)
            {
                _pendingScreenshotPath = null;
                ScreenshotButton.IsEnabled = true;

                StatusText.Text = "Nie udało się wysłać polecenia zapisu.";

                MessageBox.Show(
                    this,
                    ex.Message,
                    "Błąd zapisu klatki",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                _sendLock.Release();
            }
        }
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


        private async void ExportClip_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (_exporting)
                return;

            if (string.IsNullOrWhiteSpace(_sourceFilePath) ||
                !File.Exists(_sourceFilePath))
            {
                MessageBox.Show(
                    this,
                    "Najpierw otwórz lokalny plik filmu.",
                    "Eksport fragmentu");
                return;
            }

            if (!_clipStart.HasValue ||
                !_clipEnd.HasValue ||
                _clipEnd.Value <= _clipStart.Value)
            {
                MessageBox.Show(
                    this,
                    "Zaznacz początek i koniec fragmentu.",
                    "Eksport fragmentu");
                return;
            }

            string ffmpegPath = Path.Combine(
                AppContext.BaseDirectory,
                "ffmpeg",
                "ffmpeg.exe");

            if (!File.Exists(ffmpegPath))
            {
                MessageBox.Show(
                    this,
                    "Nie znaleziono ffmpeg.exe w folderze ffmpeg.",
                    "Brak FFmpeg",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            // Zapamiętujemy źródło i zakres na czas eksportu.
            string sourcePath = _sourceFilePath;
            double start = _clipStart.Value;
            double duration = _clipEnd.Value - start;
            bool streamCopyRequested = StreamCopyCheckBox.IsChecked == true;

            if (streamCopyRequested)
            {
                MessageBoxResult confirm = MessageBox.Show(
                    this,
                    "Tryb bez ponownego kodowania jest bardzo szybki, ale początek i koniec mogą zostać dopasowane do klatek kluczowych. Niektóre kodeki mogą nie działać poprawnie w MP4. Kontynuować?",
                    "Eksport bez ponownego kodowania",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes)
                    return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Zapisz zaznaczony fragment",
                Filter = "Film MP4 (*.mp4)|*.mp4",
                DefaultExt = ".mp4",
                AddExtension = true,
                OverwritePrompt = true,
                FileName =
                    Path.GetFileNameWithoutExtension(sourcePath) +
                    "_fragment.mp4"
            };

            if (dialog.ShowDialog(this) != true)
                return;

            string outputPath = Path.GetFullPath(dialog.FileName);

            if (string.Equals(
                Path.GetFullPath(sourcePath),
                outputPath,
                StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    this,
                    "Wybierz inną nazwę niż nazwa filmu źródłowego.",
                    "Eksport fragmentu",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            string temporaryPath = Path.Combine(
                Path.GetDirectoryName(outputPath)!,
                "VideoPlayer_export_" +
                Guid.NewGuid().ToString("N") +
                ".mp4");

            _exporting = true;
            _cancelExportRequested = false;
            _exportStopwatch = Stopwatch.StartNew();
            _activeOperationName = Path.GetFileName(sourcePath);
            _activeOperationDuration = duration;

            ExportClipButton.IsEnabled = false;
            RemoveClipButton.IsEnabled = false;
            ExportProfileBox.IsEnabled = false;
            StreamCopyCheckBox.IsEnabled = false;
            CancelExportButton.Visibility = Visibility.Visible;
            CancelExportButton.IsEnabled = true;
            ExportProgressBar.Value = 0;
            ExportProgressText.Text = "Analiza źródła i przygotowanie eksportu…";
            ExportOperationText.Text = $"Plik: {_activeOperationName} | Zakres: {FormatTime(start)}–{FormatTime(start + duration)} | Długość: {FormatTime(duration)}";

            Process? process = null;

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                bool streamCopy = StreamCopyCheckBox.IsChecked == true;
                string profile = (ExportProfileBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "standard";

                var arguments = new System.Collections.Generic.List<string>
                {
                    "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                    "-progress", "pipe:1", "-stats_period", "0.25", "-nostats",
                    "-ss", start.ToString("R", CultureInfo.InvariantCulture),
                    "-i", sourcePath,
                    "-t", duration.ToString("R", CultureInfo.InvariantCulture),
                    "-map", "0:v:0", "-map", "0:a:0?"
                };

                if (streamCopy)
                {
                    // Tryb bez ponownego kodowania: szybki, ale cięcie jest ograniczone
                    // do granic pakietów/klatek kluczowych, a kodeki muszą pasować do MP4.
                    arguments.AddRange(new[] { "-c:v", "copy", "-c:a", "copy", "-avoid_negative_ts", "make_zero" });
                }
                else
                {
                    string preset;
                    string crf;
                    switch (profile)
                    {
                        case "fast": preset = "veryfast"; crf = "22"; break;
                        case "small": preset = "slow"; crf = "27"; break;
                        case "quality": preset = "slow"; crf = "16"; break;
                        default: preset = "medium"; crf = "20"; break;
                    }

                    arguments.AddRange(new[]
                    {
                        "-c:v", "libx264", "-preset", preset, "-crf", crf,
                        "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2", "-pix_fmt", "yuv420p",
                        "-c:a", "aac", "-b:a", "192k"
                    });
                }

                arguments.AddRange(new[] { "-sn", "-dn", "-map_chapters", "-1", "-movflags", "+faststart", temporaryPath });

                foreach (string argument in arguments)
                    startInfo.ArgumentList.Add(argument);

                process = new Process
                {
                    StartInfo = startInfo,
                    EnableRaisingEvents = true
                };

                if (!process.Start())
                    throw new InvalidOperationException(
                        "Nie udało się uruchomić FFmpeg.");

                _exportProcess = process;

                // Oba strumienie czytamy równolegle, aby uniknąć
                // zablokowania procesu przez pełny bufor.
                Task progressTask =
                    ReadFfmpegProgressAsync(process, duration);

                Task<string> errorTask =
                    process.StandardError.ReadToEndAsync();

                await process.WaitForExitAsync();
                await progressTask;

                string errors = await errorTask;

                if (_cancelExportRequested)
                    throw new OperationCanceledException(
                        "Eksport został anulowany.");

                if (process.ExitCode != 0)
                {
                    if (errors.Length > 4000)
                        errors = errors.Substring(errors.Length - 4000);

                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(errors)
                            ? "FFmpeg zakończył eksport błędem."
                            : errors.Trim());
                }

                if (!File.Exists(temporaryPath) ||
                    new FileInfo(temporaryPath).Length == 0)
                {
                    throw new IOException(
                        "Nie powstał poprawny plik wynikowy.");
                }

                // Dopiero teraz zastępujemy plik docelowy.
                File.Move(temporaryPath, outputPath, overwrite: true);

                ExportProgressBar.Value = 100;
                ExportProgressText.Text = "Eksport zakończony — 100%";
                long outputSize = new FileInfo(outputPath).Length;
                string profileDescription = streamCopy
                    ? "kopiowanie strumieni bez ponownego kodowania"
                    : profile switch
                    {
                        "fast" => "H.264, profil szybki (veryfast, CRF 22), audio AAC 192 kb/s",
                        "small" => "H.264, profil mniejszego pliku (slow, CRF 27), audio AAC 192 kb/s",
                        "quality" => "H.264, profil wysokiej jakości (slow, CRF 16), audio AAC 192 kb/s",
                        _ => "H.264, profil standardowy (medium, CRF 20), audio AAC 192 kb/s"
                    };
                _lastExportSummary = $"Ostatni eksport: {Path.GetFileName(outputPath)} — {profileDescription}, rzeczywisty rozmiar {FormatFileSize(outputSize)}.";
                ExportOperationText.Text = $"Zakończono: {Path.GetFileName(outputPath)} | Rozmiar: {FormatFileSize(outputSize)} | Czas operacji: {_exportStopwatch?.Elapsed.ToString(@"hh\:mm\:ss") ?? "—"}";
                UpdateEstimatedSizeText();
                StatusText.Text = "Zapisano fragment: " + outputPath;

                MessageBox.Show(
                    this,
                    "Fragment został zapisany:\n" + outputPath,
                    "Eksport zakończony",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
                ExportProgressText.Text = "Eksport anulowany — czyszczenie pliku tymczasowego…";
                ExportOperationText.Text = $"Anulowano operację dla pliku: {_activeOperationName}";
                StatusText.Text =
                    "Eksport anulowano. Plik tymczasowy zostanie usunięty.";
            }
            catch (Exception ex)
            {
                ExportProgressText.Text = "Eksport nie powiódł się.";
                ExportOperationText.Text = $"Błąd podczas przetwarzania: {_activeOperationName}";
                StatusText.Text = "Błąd eksportu.";

                MessageBox.Show(
                    this,
                    ex.Message,
                    "Błąd eksportu",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                _exportProcess = null;

                process?.Dispose();

                try
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
                catch (Exception ex)
                {
                    if (!_closing)
                    {
                        StatusText.Text =
                            "Nie udało się usunąć pliku tymczasowego: " +
                            ex.Message;
                    }
                }

                _exporting = false;
                _exportStopwatch?.Stop();
                _exportStopwatch = null;

                if (!_closing)
                {
                    ExportClipButton.IsEnabled = true;
                    RemoveClipButton.IsEnabled = true;
                    ExportProfileBox.IsEnabled = true;
                    StreamCopyCheckBox.IsEnabled = true;
                    CancelExportButton.IsEnabled = false;
                    CancelExportButton.Visibility = Visibility.Collapsed;
                }
            }
        }


        private async Task ReadFfmpegProgressAsync(
            Process process,
            double duration)
        {
            while (true)
            {
                string? line = await process.StandardOutput.ReadLineAsync();

                if (line == null)
                    break;

                if (line.StartsWith(
                    "out_time_us=",
                    StringComparison.Ordinal))
                {
                    string value = line.Substring("out_time_us=".Length);

                    if (long.TryParse(
                        value,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out long microseconds))
                    {
                        double seconds = microseconds / 1_000_000.0;

                        double percent = duration > 0
                            ? Math.Clamp(seconds / duration * 100.0, 0, 99.9)
                            : 0;

                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (_closing || _cancelExportRequested)
                                return;

                            ExportProgressBar.Value = percent;

                            double elapsed = _exportStopwatch?.Elapsed.TotalSeconds ?? 0;
                            double speed = elapsed > 0 ? seconds / elapsed : 0;
                            double remaining = speed > 0.001 ? Math.Max(0, duration - seconds) / speed : 0;
                            string eta = double.IsFinite(remaining) && remaining < 864000
                                ? TimeSpan.FromSeconds(remaining).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
                                : "obliczanie…";
                            ExportProgressText.Text =
                                $"Kodowanie: {percent:0.0}% ({FormatTime(seconds)} / {FormatTime(duration)}) | Pozostało: {eta}";
                        });
                    }
                }
                else if (line == "progress=end")
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (_closing || _cancelExportRequested)
                            return;

                        ExportProgressBar.Value = 100;
                        ExportProgressText.Text = "Finalizowanie pliku MP4…";
                        ExportOperationText.Text = $"Finalizowanie: {_activeOperationName} | upłynęło: {_exportStopwatch?.Elapsed.ToString(@"hh\:mm\:ss") ?? "—"}";
                    });
                }
            }
        }

        private async void RemoveClip_Click(object sender, RoutedEventArgs e)
        {
            if (_exporting)
            {
                MessageBox.Show("Inna operacja eksportu jest już wykonywana.");
                return;
            }

            if (string.IsNullOrWhiteSpace(_sourceFilePath) ||
                !File.Exists(_sourceFilePath))
            {
                MessageBox.Show("Najpierw otwórz nagranie.");
                return;
            }

            if (!_clipStart.HasValue || !_clipEnd.HasValue)
            {
                MessageBox.Show("Ustaw początek i koniec fragmentu do usunięcia.");
                return;
            }

            double start = _clipStart.Value;
            double end = _clipEnd.Value;

            if (start < 0 || end <= start)
            {
                MessageBox.Show("Nieprawidłowy zakres fragmentu.");
                return;
            }

            string ffmpegPath = System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "ffmpeg",
                "ffmpeg.exe");

            if (!File.Exists(ffmpegPath))
            {
                MessageBox.Show("Nie znaleziono pliku:\n" + ffmpegPath);
                return;
            }

            // Odczytaj czas trwania i sprawdź, czy plik ma dźwięk.
            double mediaDuration;
            bool hasAudio;

            try
            {
                var probeInfo = await ProbeMediaAsync(ffmpegPath, _sourceFilePath);
                mediaDuration = probeInfo.Duration;
                hasAudio = probeInfo.HasAudio;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Nie udało się odczytać informacji o nagraniu:\n" + ex.Message);
                return;
            }

            const double tolerance = 0.15;

            if (mediaDuration <= 0 ||
                start >= mediaDuration ||
                end > mediaDuration + tolerance)
            {
                MessageBox.Show(
                    $"Zakres wykracza poza długość nagrania ({FormatTime(mediaDuration)}).");
                return;
            }

            end = Math.Min(end, mediaDuration);

            bool keepBefore = start > tolerance;
            bool keepAfter = end < mediaDuration - tolerance;

            if (!keepBefore && !keepAfter)
            {
                MessageBox.Show(
                    "Ten zakres obejmuje całe nagranie. " +
                    "Nie można utworzyć pustego pliku.");
                return;
            }

            string description;

            if (!keepBefore)
                description = $"Usunąć początek do {FormatTime(end)}?";
            else if (!keepAfter)
                description = $"Usunąć koniec od {FormatTime(start)}?";
            else
                description =
                    $"Usunąć fragment od {FormatTime(start)} do {FormatTime(end)}?";

            var confirm = MessageBox.Show(
                description +
                "\n\nPowstanie nowy plik. Oryginał pozostanie bez zmian.",
                "Usuwanie fragmentu",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
                return;

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Zapisz nagranie po usunięciu fragmentu",
                Filter = "Plik MP4 (*.mp4)|*.mp4",
                DefaultExt = ".mp4",
                AddExtension = true,
                FileName = System.IO.Path.GetFileNameWithoutExtension(
                    _sourceFilePath) + "_po_cieciu.mp4"
            };

            if (dialog.ShowDialog() != true)
                return;

            string outputPath = System.IO.Path.GetFullPath(dialog.FileName);
            string sourcePath = System.IO.Path.GetFullPath(_sourceFilePath);

            if (string.Equals(
                outputPath, sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Plik wynikowy nie może zastępować oryginału.");
                return;
            }

            string temporaryPath = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(outputPath)!,
                System.IO.Path.GetFileNameWithoutExtension(outputPath) +
                "_" + Guid.NewGuid().ToString("N") + ".tmp.mp4");

            string F(double value) =>
                value.ToString("0.######", CultureInfo.InvariantCulture);

            // Przycinanie obrazu. W razie potrzeby łączymy dwie pozostałe części.
            string filter;

            if (keepBefore && keepAfter)
            {
                filter =
                    $"[0:v:0]trim=start=0:end={F(start)},setpts=PTS-STARTPTS[v0];" +
                    $"[0:v:0]trim=start={F(end)},setpts=PTS-STARTPTS[v1];" +
                    "[v0][v1]concat=n=2:v=1:a=0[v]";
            }
            else if (keepBefore)
            {
                filter =
                    $"[0:v:0]trim=start=0:end={F(start)}," +
                    "setpts=PTS-STARTPTS[v]";
            }
            else
            {
                filter =
                    $"[0:v:0]trim=start={F(end)},setpts=PTS-STARTPTS[v]";
            }

            // Ścieżkę audio przetwarzamy tylko wtedy, gdy rzeczywiście istnieje.
            if (hasAudio)
            {
                if (keepBefore && keepAfter)
                {
                    filter +=
                        $";[0:a:0]atrim=start=0:end={F(start)},asetpts=PTS-STARTPTS[a0]" +
                        $";[0:a:0]atrim=start={F(end)},asetpts=PTS-STARTPTS[a1]" +
                        ";[a0][a1]concat=n=2:v=0:a=1[a]";
                }
                else if (keepBefore)
                {
                    filter +=
                        $";[0:a:0]atrim=start=0:end={F(start)}," +
                        "asetpts=PTS-STARTPTS[a]";
                }
                else
                {
                    filter +=
                        $";[0:a:0]atrim=start={F(end)}," +
                        "asetpts=PTS-STARTPTS[a]";
                }
            }

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            string[] args =
            {
         "-y",
        "-progress", "pipe:1",
        "-stats_period", "0.5",
        "-nostats",
        "-i", sourcePath,
        "-filter_complex", filter,
        "-map", "[v]"
    };

            if (hasAudio)
            {
                args = args.Concat(new[] { "-map", "[a]" }).ToArray();
            }

            args = args.Concat(new[]
            {
        "-c:v", "libx264",
        "-preset", "fast",
        "-crf", "20"
    }).ToArray();

            if (hasAudio)
            {
                args = args.Concat(new[] { "-c:a", "aac", "-b:a", "192k" }).ToArray();
            }

            args = args.Concat(new[]
            {
        "-movflags", "+faststart",
        temporaryPath
    }).ToArray();

            foreach (string arg in args)
                psi.ArgumentList.Add(arg);

            Process? process = null;

            try
            {
                _exporting = true;
                _cancelExportRequested = false;

                RemoveClipButton.IsEnabled = false;
                ExportClipButton.IsEnabled = false;

                ExportProgressBar.Value = 0;
                ExportProgressText.Text = "Przygotowywanie nagrania…";
                ExportProgressBar.Visibility = Visibility.Visible;
                ExportProgressText.Visibility = Visibility.Visible;
                CancelExportButton.Visibility = Visibility.Visible;
                CancelExportButton.IsEnabled = true;

                StatusText.Text = "Usuwanie fragmentu nagrania…";

                process = new Process
                {
                    StartInfo = psi,
                    EnableRaisingEvents = true
                };

                process.Start();
                _exportProcess = process;

                // Czytamy oba strumienie równolegle, aby nie blokować FFmpeg.
                Task progressTask = ReadFfmpegProgressAsync(
                    process,
                    mediaDuration - (end - start));

                Task<string> errorTask =
                    process.StandardError.ReadToEndAsync();

                await process.WaitForExitAsync();
                await progressTask;

                string errors = await errorTask;

                if (_cancelExportRequested)
                    throw new OperationCanceledException("Eksport anulowano.");

                if (process.ExitCode != 0)
                    throw new Exception("FFmpeg zgłosił błąd:\n" + errors);

                if (!File.Exists(temporaryPath) ||
                    new FileInfo(temporaryPath).Length == 0)
                {
                    throw new Exception("Nie utworzono prawidłowego pliku wynikowego.");
                }

                File.Move(temporaryPath, outputPath, overwrite: true);

                StatusText.Text = "Zapisano nagranie po usunięciu fragmentu.";

                MessageBox.Show(
                    $"Operacja zakończona.\n\nPlik:\n{outputPath}\n\n" +
                    (hasAudio ? "Dźwięk został zachowany." :
                        "Nagranie nie zawierało dźwięku — zapisano sam obraz.") +
                    "\nOryginał nie został zmieniony.",
                    "Gotowe",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            catch (OperationCanceledException)
            {
                StatusText.Text = "Operacja anulowana.";
                ExportProgressText.Text = "Anulowano usuwanie fragmentu.";
            }

            catch (Exception ex)
            {
                StatusText.Text = "Błąd usuwania fragmentu.";

                MessageBox.Show(
                    ex.Message,
                    "Błąd",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                process?.Dispose();

                try
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                    // Jeśli plik jest zablokowany, pozostaje do ręcznego usunięcia.
                }

                _exportProcess = null;

                ExportProgressBar.Value = 0;
                ExportProgressBar.Visibility = Visibility.Collapsed;
                ExportProgressText.Visibility = Visibility.Collapsed;
                CancelExportButton.Visibility = Visibility.Collapsed;
                CancelExportButton.IsEnabled = false;

                RemoveClipButton.IsEnabled = true;
                ExportClipButton.IsEnabled = true;
                _exporting = false;
            }
        }

        private async Task UpdateMediaInfoAsync(string sourcePath)
        {
            try
            {
                string ffmpegPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
                if (!File.Exists(ffmpegPath))
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (_sourceFilePath == sourcePath)
                            MediaInfoText.Text = "Brak ffmpeg.exe — nie można odczytać parametrów nagrania.";
                    });
                    return;
                }

                var psi = new ProcessStartInfo { FileName = ffmpegPath, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                psi.ArgumentList.Add("-hide_banner");
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(sourcePath);
                using var process = new Process { StartInfo = psi };
                process.Start();
                string output = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                var info = new MediaInfo { FileSize = new FileInfo(sourcePath).Length, Duration = ParseDurationFromFfmpeg(output) };
                var videoLine = System.Text.RegularExpressions.Regex.Match(output, @"Stream #\S+.*?: Video:\s*([^, ]+)(.*)");
                if (videoLine.Success)
                {
                    info.VideoCodec = videoLine.Groups[1].Value;
                    string details = videoLine.Groups[2].Value;
                    var resolution = System.Text.RegularExpressions.Regex.Match(details, @"(\d{2,5})x(\d{2,5})");
                    if (resolution.Success) info.Resolution = resolution.Groups[1].Value + "×" + resolution.Groups[2].Value;
                    var fps = System.Text.RegularExpressions.Regex.Match(details, @"([\d.]+)\s*fps");
                    if (fps.Success) info.Fps = fps.Groups[1].Value;
                }
                var audio = System.Text.RegularExpressions.Regex.Match(output, @"Stream #\S+.*?: Audio:\s*([^, ]+)");
                if (audio.Success) info.AudioCodec = audio.Groups[1].Value;
                var bitrate = System.Text.RegularExpressions.Regex.Match(output, @"bitrate:\s*(\d+)\s*kb/s");
                if (bitrate.Success) info.Bitrate = bitrate.Groups[1].Value + " kb/s";

                await Dispatcher.InvokeAsync(() =>
                {
                    if (_closing || !string.Equals(_sourceFilePath, sourcePath, StringComparison.OrdinalIgnoreCase)) return;
                    _currentMediaInfo = info;
                    UpdateEstimatedSizeText();
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!_closing && string.Equals(_sourceFilePath, sourcePath, StringComparison.OrdinalIgnoreCase))
                        MediaInfoText.Text = "Nie udało się odczytać parametrów: " + ex.Message;
                });
            }
        }

        private static double ParseDurationFromFfmpeg(string output)
        {
            var match = System.Text.RegularExpressions.Regex.Match(output, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
            if (!match.Success) return 0;
            return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 3600 +
                   double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * 60 +
                   double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        }

        private void UpdateEstimatedSizeText()
        {
            if (_currentMediaInfo == null || string.IsNullOrWhiteSpace(_sourceFilePath)) return;
            double fullDuration = _currentMediaInfo.Duration;
            bool hasRange = _clipStart.HasValue && _clipEnd.HasValue && _clipEnd.Value > _clipStart.Value;
            double selectedDuration = hasRange ? _clipEnd!.Value - _clipStart!.Value : fullDuration;
            string estimate = fullDuration > 0
                ? FormatFileSize((long)(_currentMediaInfo.FileSize * Math.Clamp(selectedDuration / fullDuration, 0, 1)))
                : "nie można oszacować";
            string rangeLabel = hasRange ? $"zaznaczenie ({FormatTime(selectedDuration)})" : "całe nagranie";
            MediaInfoText.Text =
                $"Źródło: {_currentMediaInfo.VideoCodec}, {_currentMediaInfo.Resolution}, {_currentMediaInfo.Fps} FPS, bitrate {_currentMediaInfo.Bitrate}, audio: {_currentMediaInfo.AudioCodec}; " +
                $"czas {FormatTime(fullDuration)}, rozmiar {FormatFileSize(_currentMediaInfo.FileSize)}.\n" +
                $"Szacowany rozmiar {rangeLabel} przy podobnym bitrate: około {estimate}. To przybliżenie; rozmiar MP4 po ponownym kodowaniu może się różnić." +
                (string.IsNullOrWhiteSpace(_lastExportSummary) ? "" : "\n" + _lastExportSummary);
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 0) bytes = 0;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
            return $"{size:0.##} {units[unit]}";
        }

        private void LoadHistory()
        {
            try
            {
                if (!File.Exists(HistoryFilePath)) return;
                var history = JsonSerializer.Deserialize<AppHistory>(File.ReadAllText(HistoryFilePath));
                if (history == null) return;
                _recentFiles.Clear();
                _recentFiles.AddRange(history.RecentFiles.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Take(10));
                _recentScreenshots.Clear();
                _recentScreenshots.AddRange(history.RecentScreenshots.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Take(30));
            }
            catch { /* Uszkodzona historia nie blokuje uruchomienia aplikacji. */ }
        }

        private void SaveHistory()
        {
            try
            {
                Directory.CreateDirectory(_historyDirectory);
                File.WriteAllText(HistoryFilePath, JsonSerializer.Serialize(new AppHistory
                {
                    RecentFiles = _recentFiles.Take(10).ToList(),
                    RecentScreenshots = _recentScreenshots.Take(30).ToList()
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { StatusText.Text = "Nie udało się zapisać historii: " + ex.Message; }
        }

        private void AddRecentFile(string path)
        {
            _recentFiles.RemoveAll(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
            _recentFiles.Insert(0, path);
            if (_recentFiles.Count > 10) _recentFiles.RemoveRange(10, _recentFiles.Count - 10);
            SaveHistory();
            RefreshHistoryControls();
        }

        private void AddRecentScreenshot(string path)
        {
            _recentScreenshots.RemoveAll(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
            _recentScreenshots.Insert(0, path);
            if (_recentScreenshots.Count > 30) _recentScreenshots.RemoveRange(30, _recentScreenshots.Count - 30);
            SaveHistory();
            RefreshHistoryControls();
        }

        private void RefreshHistoryControls()
        {
            if (RecentFilesBox == null || ScreenshotsBox == null) return;
            RecentFilesBox.Items.Clear();
            foreach (string path in _recentFiles.Where(File.Exists))
                RecentFilesBox.Items.Add(new ComboBoxItem { Content = Path.GetFileName(path), Tag = path, ToolTip = path });
            if (RecentFilesBox.Items.Count > 0) RecentFilesBox.SelectedIndex = 0;
            ScreenshotsBox.Items.Clear();
            foreach (string path in _recentScreenshots.Where(File.Exists))
                ScreenshotsBox.Items.Add(new ComboBoxItem { Content = Path.GetFileName(path), Tag = path, ToolTip = path });
            if (ScreenshotsBox.Items.Count > 0) ScreenshotsBox.SelectedIndex = 0;
        }

        private async void OpenRecent_Click(object sender, RoutedEventArgs e)
        {
            if (RecentFilesBox.SelectedItem is not ComboBoxItem item || item.Tag is not string path || !File.Exists(path))
            { MessageBox.Show(this, "Wybierz istniejący plik z historii.", "Historia nagrań"); return; }
            if (!_ready) { MessageBox.Show(this, "Silnik odtwarzacza nie jest gotowy.", "Odtwarzacz"); return; }
            _sourceFilePath = path;
            _fileName = Path.GetFileName(path);
            _clipStart = null; _clipEnd = null; _currentMediaInfo = null;
            UpdateClipRangeText();
            _eofReached = false; _dragging = false; _updatingSlider = true;
            try { PositionSlider.Value = 0; PositionSlider.Maximum = 1; PositionSlider.IsEnabled = false; }
            finally { _updatingSlider = false; }
            PositionText.Text = "00:00:00"; DurationText.Text = "00:00:00"; StatusText.Text = _fileName;
            AddRecentFile(path);
            MediaInfoText.Text = "Odczytywanie parametrów nagrania…";
            _ = UpdateMediaInfoAsync(path);
            await SendAsync("loadfile", path, "replace");
            await SendAsync("set_property", "speed", GetSelectedSpeed());
            await SendAsync("set_property", "pause", false);
        }

        private void OpenScreenshot_Click(object sender, RoutedEventArgs e)
        {
            if (ScreenshotsBox.SelectedItem is not ComboBoxItem item || item.Tag is not string path || !File.Exists(path))
            { MessageBox.Show(this, "Nie ma dostępnego zrzutu do otwarcia.", "Zrzuty klatek"); return; }
            try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Nie można otworzyć zrzutu", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void OpenScreenshotsFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string? selected = (ScreenshotsBox.SelectedItem as ComboBoxItem)?.Tag as string;
                string folder = !string.IsNullOrWhiteSpace(selected) && File.Exists(selected)
                    ? Path.GetDirectoryName(selected)!
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "VideoPlayer");
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Nie można otworzyć folderu", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private async Task<(double Duration, bool HasAudio)> ProbeMediaAsync(
            string ffmpegPath,
            string sourcePath)
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(sourcePath);

            using var process = new Process { StartInfo = psi };
            process.Start();

            string output = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            var durationMatch = System.Text.RegularExpressions.Regex.Match(
                output,
                @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");

            if (!durationMatch.Success)
            {
                throw new Exception(
                    "FFmpeg nie rozpoznał czasu trwania pliku. " +
                    "Sprawdź, czy nagranie jest poprawne i obsługiwane.");
            }

            double hours = double.Parse(
                durationMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            double minutes = double.Parse(
                durationMatch.Groups[2].Value, CultureInfo.InvariantCulture);
            double seconds = double.Parse(
                durationMatch.Groups[3].Value, CultureInfo.InvariantCulture);

            double duration = hours * 3600 + minutes * 60 + seconds;

            bool hasAudio = System.Text.RegularExpressions.Regex.IsMatch(
                output,
                @"Stream #\S+.*?: Audio:");

            return (duration, hasAudio);
        }

        private void CancelExport_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (!_exporting || _exportProcess == null)
                return;

            _cancelExportRequested = true;
            CancelExportButton.IsEnabled = false;

            ExportProgressText.Text = "Anulowanie eksportu…";
            StatusText.Text = "Zatrzymywanie FFmpeg…";

            try
            {
                if (!_exportProcess.HasExited)
                    _exportProcess.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Proces zakończył się przed anulowaniem.
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                StatusText.Text =
                    "Nie udało się zatrzymać FFmpeg: " + ex.Message;
            }
        }

        private void Window_Closing(
            object? sender,
            CancelEventArgs e)
        {
            if (_exporting)
            {
                MessageBoxResult result = MessageBox.Show(
                    this,
                    "Eksport jest w toku. Zamknięcie aplikacji przerwie operację i usunie nieukończony plik tymczasowy. Czy anulować eksport i zamknąć program?",
                    "Eksport w toku",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                _cancelExportRequested = true;
                try
                {
                    if (_exportProcess != null && !_exportProcess.HasExited)
                        _exportProcess.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }

            _closing = true;
            try
            {
                if (_exportProcess != null && !_exportProcess.HasExited)
                    _exportProcess.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }

            _ready = false;

            try
            {
                if (_mpv != null && !_mpv.HasExited)
                    _mpv.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            catch (Win32Exception)
            {
            }

            _pipe?.Dispose();
            _mpv?.Dispose();
        }
    }
}