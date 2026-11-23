using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace VideoPlayer
{
    public partial class MainWindow
    {
        private readonly string _windowSettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VideoPlayer", "window-settings.json");
        private readonly DispatcherTimer _windowSettingsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private WindowUiSettings _windowSettings = new();
        private bool _restoringWindowSettings;
        private bool _previewFullscreen;
        private WindowStyle _fullscreenPreviousWindowStyle;
        private ResizeMode _fullscreenPreviousResizeMode;
        private WindowState _fullscreenPreviousWindowState;
        private bool _fullscreenPreviousToolsVisible;
        private Visibility _fullscreenPreviousTimelineVisibility;
        private Visibility _fullscreenPreviousTransportVisibility;

        private void InitializeWindowFeatures()
        {
            try
            {
                if (File.Exists(_windowSettingsPath))
                {
                    var loaded = JsonSerializer.Deserialize<WindowUiSettings>(File.ReadAllText(_windowSettingsPath));
                    if (loaded != null) _windowSettings = loaded;
                }
            }
            catch { _windowSettings = new WindowUiSettings(); }

            _restoringWindowSettings = true;
            try
            {
                Width = Math.Clamp(_windowSettings.Width, MinWidth, 3200);
                Height = Math.Clamp(_windowSettings.Height, MinHeight, 2400);
                Left = _windowSettings.Left;
                Top = _windowSettings.Top;
                WindowStartupLocation = WindowStartupLocation.Manual;
                var bounds = new System.Drawing.Rectangle((int)Left, (int)Top, (int)Width, (int)Height);
                if (!Forms.Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(bounds)))
                    WindowStartupLocation = WindowStartupLocation.CenterScreen;
                if (_windowSettings.IsMaximized) WindowState = WindowState.Maximized;

                // Ogranicz szeroki, wcześniej zapamiętany panel, aby odzyskać miejsce na podgląd.
                _toolsWidth = Math.Clamp(_windowSettings.ToolsWidth, 300, 360);
                if (_windowSettings.ToolsVisible && ToolsColumn != null)
                    ToolsColumn.Width = new GridLength(_toolsWidth);
                if (ToolsTabs != null)
                {
                    int savedTab = Math.Clamp(_windowSettings.SelectedTabIndex, 0, Math.Max(0, ToolsTabs.Items.Count - 1));
                    // Widok Dahua nie jest przywracany automatycznie: po starcie
                    // zawsze pokazujemy Odtwarzacz i jego właściwy panel narzędzi.
                    ToolsTabs.SelectedIndex = savedTab == 3 ? 0 : savedTab;
                }
                if (SpeedBox != null && SpeedBox.Items.Count > 0)
                    SpeedBox.SelectedIndex = Math.Clamp(_windowSettings.SpeedIndex, 0, SpeedBox.Items.Count - 1);
                if (VideoScaleBox != null && VideoScaleBox.Items.Count > 0)
                    VideoScaleBox.SelectedIndex = Math.Clamp(_windowSettings.ScaleIndex, 0, VideoScaleBox.Items.Count - 1);
                if (!_windowSettings.ToolsVisible) SetToolsVisible(false);
            }
            finally { _restoringWindowSettings = false; }

            _windowSettingsTimer.Tick += (_, __) => SaveWindowSettings();
            _windowSettingsTimer.Start();
            SizeChanged += (_, __) => QueueWindowSettingsSave();
            LocationChanged += (_, __) => QueueWindowSettingsSave();
            StateChanged += (_, __) => QueueWindowSettingsSave();
            ToolsSplitter.DragCompleted += (_, __) => SaveWindowSettings();
        }

        private void QueueWindowSettingsSave()
        {
            if (!_restoringWindowSettings && IsLoaded && !_previewFullscreen) SaveWindowSettings();
        }

        private void SaveWindowSettings()
        {
            if (_restoringWindowSettings || !IsInitialized || _previewFullscreen) return;
            try
            {
                if (WindowState == WindowState.Normal)
                {
                    _windowSettings.Width = Width;
                    _windowSettings.Height = Height;
                    _windowSettings.Left = Left;
                    _windowSettings.Top = Top;
                }
                _windowSettings.IsMaximized = WindowState == WindowState.Maximized;
                if (ToolsColumn != null && ToolsColumn.ActualWidth >= 320)
                    _windowSettings.ToolsWidth = ToolsColumn.ActualWidth;
                _windowSettings.ToolsVisible = ToolsPanel?.Visibility == Visibility.Visible;
                if (ToolsTabs != null) _windowSettings.SelectedTabIndex = ToolsTabs.SelectedIndex;
                if (SpeedBox != null) _windowSettings.SpeedIndex = Math.Max(0, SpeedBox.SelectedIndex);
                if (VideoScaleBox != null) _windowSettings.ScaleIndex = Math.Max(0, VideoScaleBox.SelectedIndex);

                string? directory = Path.GetDirectoryName(_windowSettingsPath);
                if (directory == null) return;
                Directory.CreateDirectory(directory);
                string temp = _windowSettingsPath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_windowSettings, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, _windowSettingsPath, true);
            }
            catch (Exception ex)
            {
                if (!_closing) ViewModel.StatusMessage = "Nie zapisano ustawień okna: " + ex.Message;
            }
        }

        private void TogglePreviewWindow_Click(object sender, RoutedEventArgs e) => TogglePreviewWindow();
        private void ReturnPreviewWindow_Click(object sender, RoutedEventArgs e) => ReturnPreviewWindow();

        // Hosty natywne pozostają w tym samym oknie. Ich przenoszenie między HWND
        // zatrzymywało odświeżanie strumienia Dahua Live po powrocie z pełnego ekranu.
        private void TogglePreviewWindow()
        {
            if (_previewFullscreen)
            {
                ReturnPreviewWindow();
                return;
            }
            if (_opening || _shutdownRequested) return;

            _previewFullscreen = true;
            _fullscreenPreviousWindowStyle = WindowStyle;
            _fullscreenPreviousResizeMode = ResizeMode;
            _fullscreenPreviousWindowState = WindowState;
            _fullscreenPreviousToolsVisible = ToolsPanel.Visibility == Visibility.Visible;
            _fullscreenPreviousTimelineVisibility = PlayerTimelineBorder.Visibility;
            _fullscreenPreviousTransportVisibility = PlayerTransportGrid.Visibility;

            SetToolsVisible(false);
            PlayerTimelineBorder.Visibility = Visibility.Collapsed;
            PlayerTransportGrid.Visibility = Visibility.Collapsed;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            Activate();
            Keyboard.ClearFocus();
        }

        private void ReturnPreviewWindow()
        {
            if (!_previewFullscreen) return;

            WindowState = WindowState.Normal;
            WindowStyle = _fullscreenPreviousWindowStyle;
            ResizeMode = _fullscreenPreviousResizeMode;
            WindowState = _fullscreenPreviousWindowState;
            PlayerTimelineBorder.Visibility = _fullscreenPreviousTimelineVisibility;
            PlayerTransportGrid.Visibility = _fullscreenPreviousTransportVisibility;
            SetToolsVisible(_fullscreenPreviousToolsVisible);
            _previewFullscreen = false;
            SaveWindowSettings();
            Activate();
        }

        private void CloseDetachedPreview()
        {
            if (_previewFullscreen) ReturnPreviewWindow();
        }
    }
}