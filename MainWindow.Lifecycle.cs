using System;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using VideoPlayer.ViewModels;
using VideoPlayer.Services;

namespace VideoPlayer
{
    public partial class MainWindow
    {
        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var historyService = new HistoryService();
                ViewModel.History.AttachService(historyService);
                ViewModel.EventCenter.AttachHistoryService(historyService);
                ViewModel.PlateSearch.AttachSearchProvider(ViewModel.History.SearchMarkers);
                _cases = new CaseRepository();
                ViewModel.Cases.AttachRepository(_cases);
                ViewModel.ReviewSession.AttachRepository(new ReviewRepository());
                _exports = new ExportService(_ffmpegService);
                _reports = new ReportService(_exports);
                ViewModel.Cases.AttachReportService(new CaseReportService(_cases, historyService, _mediaInfoService, _reports));
                LoadDahuaDeviceList();
                await StartPlayerAsync();
                InitializeVideoMouseInput();
                _saveTimer.Start();
            }
            catch (Exception ex)
            {
                ViewModel.StatusMessage = "Błąd inicjalizacji: " + ex.Message;
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
            ViewModel.StatusMessage = "Gotowy. Otwórz film. Kółko: zoom; przeciąganie: przesuwanie; R: reset widoku.";
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
                ViewModel.StatusMessage = "Utracono mpv: " + reason + " Użyj Uruchom ponownie mpv.";
            });
        }


        private void OnMpvMessageReceived(JsonElement message)
        {
            if (message.TryGetProperty("event", out var ev) && ev.GetString() == "file-loaded") _fileLoaded?.TrySetResult(true);
            if (message.TryGetProperty("event", out ev) && ev.GetString() == "end-file" && message.TryGetProperty("reason", out var reason) && reason.GetString() == "error")
                _fileLoaded?.TrySetException(new IOException("mpv nie otworzył nagrania."));
            if (!_closing) _ = Dispatcher.InvokeAsync(() => HandleMessage(message));
        }


        private async void Window_Closing(object? sender, CancelEventArgs e)
        {
            if (_allowClose) return;
            e.Cancel = true;
            if (_shutdownRequested) return;
            if (_opening) { ViewModel.StatusMessage = "Poczekaj na zakończenie otwierania pliku, a następnie zamknij okno."; return; }
            if (ViewModel.Export.IsBusy && MessageBox.Show(this, "Przerwać operację i zamknąć program? Ukończone pliki seryjne pozostaną zapisane.", "Zamykanie", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;

            _shutdownRequested = true;
            _saveTimer.Stop();
            SaveSession();
            _closing = true;
            ViewModel.CancelPendingOperations();
            _mediaInfoCts?.Cancel();
            _mediaInfoCts?.Dispose();
            _mediaInfoCts = null;
            try { if (_operationTask != null) await _operationTask; } catch { }
            try { if (_motionTask != null) await _motionTask; } catch { }
            try { await _mpvService.DisposeAsync(); } catch { }
            CleanupDahuaOnClosing();
            ViewModel.Dispose();
            _allowClose = true;
            Close();
        }
    }
}
