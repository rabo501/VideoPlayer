using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace VideoPlayer
{
    public partial class MainWindow : Window
    {
        private readonly Forms.Panel _videoPanel = new() { BackColor = System.Drawing.Color.Black, Dock = Forms.DockStyle.Fill };
        private MpvService _mpvService = new();
        private readonly FfmpegService _ffmpegService = new();
        private readonly MediaInfoService _mediaInfoService = new(Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe"));
        private HistoryService _historyService = null!;
        private ReviewRepository _reviews = null!;
        private ExportService _exports = null!;
        private ReportService _reports = null!;
        private CaseRepository _cases = null!;
        private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(3) };
        private readonly List<string> _recentFiles = new(), _recentScreenshots = new();
        private readonly List<TimelineMarker> _timelineMarkers = new();
        private readonly List<Button> _markerButtons = new();
        private readonly List<Border> _motionRangeBands = new();
        private readonly ObservableCollection<ClipRange> _clips = new();
        private bool _ready, _closing, _dragging, _updatingSlider, _eofReached, _paused = true, _opening, _restoring, _shutdownRequested, _allowClose;
        private bool _exporting, _screenshotBusy, _changingScale;
        private string _fileName = "";
        private string? _sourceFilePath;
        private double? _clipStart, _clipEnd;
        private double _zoom, _panX, _panY, _panscan;
        private MediaInfoData? _currentMediaInfo;
        private string? _lastExportSummary;
        private Task? _operationTask;
        private CancellationTokenSource? _operationCts;
        private TaskCompletionSource<bool>? _fileLoaded;

        public MainWindow()
        {
            InitializeComponent();
            Assembly assembly = Assembly.GetEntryAssembly() ?? typeof(MainWindow).Assembly;
            Version? appVersion = assembly.GetName().Version;
            string displayVersion = appVersion == null ? "1.6.0" : $"{appVersion.Major}.{appVersion.Minor}.{Math.Max(0, appVersion.Build)}";
            AssemblyInformationalVersionAttribute? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            string productVersion = !string.IsNullOrWhiteSpace(informational?.InformationalVersion) ? informational!.InformationalVersion : displayVersion;

            Title = $"VideoPlayer {displayVersion} — analiza nagrań";
            AboutVersionText.Text = $"VideoPlayer — wersja {displayVersion}";
            AboutAuthorVersionText.Text = $"Autor: Rafał Bonna  •  Wydanie: {productVersion}  •  Architektura: {RuntimeInformation.ProcessArchitecture}  •  © {DateTime.Now.Year}";

            VideoHost.Child = _videoPanel;
            InitializeDahuaHost();
            InitializeOcrOverlayHandles();
            ClipRangesListBox.ItemsSource = _clips;
            UpdateRangeHint();
            PreviewKeyDown += OnPreviewKeyDown;
            SizeChanged += (_, __) => ConstrainToolsWidth();
            _saveTimer.Tick += (_, __) => SaveSession();
            Closed += (_, __) => { _motionDetectionCts?.Cancel(); _mouseSendTimer.Stop(); _videoMouse?.Dispose(); };
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _historyService = new HistoryService();
                _cases = new CaseRepository();
                RefreshCaseArchiveList();
                _reviews = new ReviewRepository();
                _exports = new ExportService(_ffmpegService);
                _reports = new ReportService(_exports);
                LoadHistory();
                RefreshHistoryControls();
                LoadDahuaDeviceList();
                await StartPlayerAsync();
                InitializeVideoMouseInput();
                _saveTimer.Start();
            }
            catch (Exception ex)
            {
                StatusText.Text = "Błąd inicjalizacji: " + ex.Message;
                MessageBox.Show(this, ex.Message + "\nSprawdź uprawnienia do katalogu programu i pliki mpv/FFmpeg.", "Błąd uruchamiania");
            }
        }

        private async Task StartPlayerAsync()
        {
            _mpvService.MessageReceived += OnMpvMessageReceived;
            _mpvService.Disconnected += OnDisconnected;
            await _mpvService.StartAsync(Path.Combine(AppContext.BaseDirectory, "mpv", "mpv.exe"), _videoPanel.Handle);
            _ready = true;
            string[] props = { "time-pos", "duration", "eof-reached", "pause", "video-zoom", "video-pan-x", "video-pan-y", "panscan" };
            for (int i = 0; i < props.Length; i++) await _mpvService.SendAsync("observe_property", i + 1, props[i]);
            StatusText.Text = "Gotowy. Otwórz film. Kółko: zoom; przeciąganie: przesuwanie; R: reset widoku.";
        }

        private void OnDisconnected(string reason)
        {
            if (_closing) return;
            _fileLoaded?.TrySetException(new IOException(reason));
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (_closing) return;
                _ready = false;
                _screenshotBusy = false;
                ScreenshotButton.IsEnabled = true;
                StatusText.Text = "Utracono mpv: " + reason + " Użyj Uruchom ponownie mpv.";
            });
        }

        private void OnMpvMessageReceived(JsonElement message)
        {
            if (message.TryGetProperty("event", out var ev) && ev.GetString() == "file-loaded") _fileLoaded?.TrySetResult(true);
            if (message.TryGetProperty("event", out ev) && ev.GetString() == "end-file" && message.TryGetProperty("reason", out var reason) && reason.GetString() == "error")
                _fileLoaded?.TrySetException(new IOException("mpv nie otworzył nagrania."));
            if (!_closing) _ = Dispatcher.InvokeAsync(() => HandleMessage(message));
        }

        private async void RestartMpv_Click(object sender, RoutedEventArgs e)
        {
            if (_exporting || _opening || _shutdownRequested) return;
            SaveSession();
            _ready = false;
            _opening = true;
            try
            {
                await _mpvService.DisposeAsync();
                _mpvService = new MpvService();
                await StartPlayerAsync();
                string? path = _sourceFilePath;
                _opening = false;
                if (path != null && File.Exists(path)) await OpenMediaAsync(path);
            }
            catch (Exception ex) { StatusText.Text = ex.Message; }
            finally { _opening = false; }
        }

        private async void VideoScaleBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready || _closing || _opening || _restoring || _changingScale || VideoScaleBox.SelectedItem is not ComboBoxItem item) return;
            _changingScale = true;
            try
            {
                string mode = item.Tag?.ToString() ?? "fit";
                double z = 0, p = mode == "fill" ? 1 : 0;
                if (double.TryParse(mode, NumberStyles.Float, CultureInfo.InvariantCulture, out double factor) && factor >= 1) z = Math.Log(factor, 2);
                await SetViewAsync(z, 0, 0, p);
                SaveSession();
            }
            catch (Exception ex) { StatusText.Text = ex.Message; }
            finally { _changingScale = false; }
        }

        private async Task SetViewAsync(double zoom, double x, double y, double panscan)
        {
            await _mpvService.SendAsync("set_property", "panscan", panscan);
            await _mpvService.SendAsync("set_property", "video-zoom", zoom);
            await _mpvService.SendAsync("set_property", "video-pan-x", x);
            await _mpvService.SendAsync("set_property", "video-pan-y", y);
            _zoom = zoom;
            _panX = x;
            _panY = y;
            _panscan = panscan;
        }

        private async void ResetView_Click(object sender, RoutedEventArgs e)
        {
            if (!_ready || _opening) return;
            try
            {
                await SetViewAsync(0, 0, 0, 0);
                _restoring = true;
                VideoScaleBox.SelectedIndex = 0;
                _restoring = false;
                SaveSession();
            }
            catch (Exception ex)
            {
                _restoring = false;
                StatusText.Text = ex.Message;
            }
        }

        private void SaveSession_Click(object sender, RoutedEventArgs e) { SaveSession(true); }

        private void SaveSession(bool show = false)
        {
            if (_reviews == null || _opening || _restoring || string.IsNullOrWhiteSpace(_sourceFilePath)) return;
            try
            {
                _reviews.Save(_sourceFilePath, new ReviewSession
                {
                    Position = PositionSlider.Value,
                    Speed = GetSelectedSpeed(),
                    Paused = _paused,
                    Zoom = _zoom,
                    PanX = _panX,
                    PanY = _panY,
                    Panscan = _panscan,
                    ClipStart = _clipStart,
                    ClipEnd = _clipEnd,
                    Clips = new List<ClipRange>(_clips)
                });
                if (show) StatusText.Text = "Sesja została zapisana.";
            }
            catch (Exception ex) { if (!_closing) StatusText.Text = "Błąd zapisu sesji: " + ex.Message; }
        }

        private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_opening || _shutdownRequested) return;
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (ctrl && e.Key == Key.O) { e.Handled = true; Open_Click(this, new RoutedEventArgs()); return; }
            if (ctrl && e.Key == Key.D) { e.Handled = true; DahuaStream_Click(this, new RoutedEventArgs()); return; }
            if (Keyboard.Modifiers != ModifierKeys.None) return;
            if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase ||
                Keyboard.FocusedElement is PasswordBox ||
                Keyboard.FocusedElement is ComboBox ||
                Keyboard.FocusedElement is DatePicker)
                return;

            if (_workspaceMode != WorkspaceMode.Player) return;

            string? action = e.Key switch
            {
                Key.Space => "play",
                Key.Left => "previous",
                Key.Right => "next",
                Key.I => "start",
                Key.O => "end",
                Key.S => "screenshot",
                Key.R => "reset",
                _ => null
            };
            if (action == null) return;
            e.Handled = true;
            await DispatchActionAsync(action);
        }

        private Task DispatchActionAsync(string action)
        {
            if (_opening || _shutdownRequested) return Task.CompletedTask;
            switch (action)
            {
                case "context": ShowVideoContextMenu(); break;
                case "play": PlayPause_Click(this, new RoutedEventArgs()); break;
                case "previous": PreviousFrame_Click(this, new RoutedEventArgs()); break;
                case "next": NextFrame_Click(this, new RoutedEventArgs()); break;
                case "start": MarkStart_Click(this, new RoutedEventArgs()); break;
                case "end": MarkEnd_Click(this, new RoutedEventArgs()); break;
                case "screenshot": Screenshot_Click(this, new RoutedEventArgs()); break;
                case "open": Open_Click(this, new RoutedEventArgs()); break;
                case "reset": ResetView_Click(this, new RoutedEventArgs()); break;
            }
            return Task.CompletedTask;
        }

        private void ExitMenu_Click(object sender, RoutedEventArgs e) => Close();

        private void BasicHelp_Click(object sender, RoutedEventArgs e)
        {
            SetToolsVisible(true);
            ToolsTabs.SelectedIndex = 4;
            HelpExpander.IsExpanded = true;
            AboutExpander.IsExpanded = false;
            HelpExpander.BringIntoView();
        }

        private void About_Click(object sender, RoutedEventArgs e)
        {
            SetToolsVisible(true);
            ToolsTabs.SelectedIndex = 4;
            AboutExpander.IsExpanded = true;
            HelpExpander.IsExpanded = false;
            AboutExpander.BringIntoView();
        }

        private void ProjectLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, "Nie można otworzyć strony projektu.\n" + ex.Message, "VideoPlayer", MessageBoxButton.OK, MessageBoxImage.Information); }
            e.Handled = true;
        }
    }
}
