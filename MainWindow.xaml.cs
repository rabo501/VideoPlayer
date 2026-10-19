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


    }
}