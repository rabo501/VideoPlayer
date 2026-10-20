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

        private readonly MpvService _mpvService = new();
        private readonly FfmpegService _ffmpegService = new();
        private readonly MediaInfoService _mediaInfoService = new(
            Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe"));
        private readonly HistoryService _historyService = new();

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

        private readonly System.Collections.Generic.List<string> _recentFiles = new();
        private readonly System.Collections.Generic.List<string> _recentScreenshots = new();
        private MediaInfoData? _currentMediaInfo;
        private string? _lastExportSummary;
        private readonly System.Collections.Generic.List<TimelineMarker> _timelineMarkers = new();
        private readonly System.Collections.Generic.List<System.Windows.Controls.Button> _markerButtons = new();

        public MainWindow()
        {
            InitializeComponent();
            VideoHost.Child = _videoPanel;
            _mpvService.MessageReceived += OnMpvMessageReceived;
        }


        private async void VideoScaleBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready || _closing || VideoScaleBox.SelectedItem is not ComboBoxItem selected)
                return;

            string mode = selected.Tag?.ToString() ?? "fit";
            try
            {
                if (mode == "fill")
                {
                    // mpv panscan=1 skaluje obraz do wypełnienia całego obszaru,
                    // przycinając nadmiar przy zachowaniu proporcji.
                    await SendAsync("set_property", "video-zoom", 0.0);
                    await SendAsync("set_property", "panscan", 1.0);
                }
                else if (mode == "fit")
                {
                    await SendAsync("set_property", "panscan", 0.0);
                    await SendAsync("set_property", "video-zoom", 0.0);
                }
                else if (double.TryParse(mode, NumberStyles.Float, CultureInfo.InvariantCulture, out double factor) && factor >= 1.0)
                {
                    // video-zoom w mpv jest wyrażony w skali logarytmicznej o podstawie 2.
                    await SendAsync("set_property", "panscan", 0.0);
                    await SendAsync("set_property", "video-zoom", Math.Log(factor, 2.0));
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = "Nie udało się zmienić skalowania obrazu: " + ex.Message;
            }
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
                "Obszar obrazu można powiększyć lub zmniejszyć, przeciągając poziomy separator między nagraniem a panelem sterowania. " +
                "Lista „Skalowanie” pozwala dopasować cały obraz, wypełnić obszar albo ustawić powiększenie 125%, 150% lub 200%.\n\n" +
                "7. Punkty zdarzeń na osi czasu\n" +
                "Wpisz opis zdarzenia i kliknij „Dodaj w bieżącej pozycji”. Znaczniki są zapisywane osobno dla każdego nagrania. " +
                "Wybierz znacznik, aby zmienić opis, przejść do jego czasu lub go usunąć. Możesz też dwukrotnie kliknąć pozycję na liście " +
                "albo kliknąć pomarańczowy punkt na osi czasu, aby przejść do zdarzenia.\n\n" +
                "Uwaga: nazwy przycisków i dostępność poszczególnych opcji mogą zależeć od wersji aplikacji. " +
                "Oryginalne nagranie pozostaje bez zmian; eksport zapisuje nowy plik.",
                "Pomoc — podstawowe funkcje",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }


        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            LoadHistory();
            RefreshHistoryControls();
            try
            {
                await _mpvService.StartAsync(
                    Path.Combine(AppContext.BaseDirectory, "mpv", "mpv.exe"),
                    _videoPanel.Handle);
                if (_closing) return;
                _ready = true;

                await SendAsync("observe_property", 1, "time-pos");
                await SendAsync("observe_property", 2, "duration");
                await SendAsync("observe_property", 3, "eof-reached");
                await SendAsync("observe_property", 4, "pause");
                await SendAsync("set_property", "speed", GetSelectedSpeed());
                StatusText.Text = "Gotowy. Otwórz film.";
            }
            catch (Exception ex)
            {
                if (!_closing)
                {
                    StatusText.Text = "Nie udało się uruchomić odtwarzacza.";
                    MessageBox.Show(this, ex.Message, "Błąd uruchamiania",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void OnMpvMessageReceived(JsonElement message)
        {
            if (_closing) return;
            _ = Dispatcher.InvokeAsync(() => HandleMessage(message));
        }

    }
}