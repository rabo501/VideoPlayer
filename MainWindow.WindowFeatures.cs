using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
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
        private Window? _previewWindow;
        private bool _restoringWindowSettings;

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
                {
                    WindowStartupLocation = WindowStartupLocation.CenterScreen;
                }
                if (_windowSettings.IsMaximized) WindowState = WindowState.Maximized;

                _toolsWidth = Math.Clamp(_windowSettings.ToolsWidth, 320, 1100);
                if (ToolsTabs != null)
                    ToolsTabs.SelectedIndex = Math.Clamp(_windowSettings.SelectedTabIndex, 0, Math.Max(0, ToolsTabs.Items.Count - 1));
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
            if (!_restoringWindowSettings && IsLoaded) SaveWindowSettings();
        }

        private void SaveWindowSettings()
        {
            if (_restoringWindowSettings || !IsInitialized || _closing) return;
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

        private void TogglePreviewWindow()
        {
            if (_previewWindow != null)
            {
                ReturnPreviewWindow();
                return;
            }
            if (_opening || _shutdownRequested || VideoHostContainer == null) return;

            SetWorkspaceMode(WorkspaceMode.Player);
            if (!ReferenceEquals(VideoHost.Parent, VideoHostContainer)) return;

            var currentScreen = Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
            var screens = Forms.Screen.AllScreens;
            var target = screens.Length > 1
                ? screens[(Array.IndexOf(screens, currentScreen) + 1) % screens.Length]
                : currentScreen;

            VideoHostContainer.Child = null;
            var previewContent = new Grid { Background = System.Windows.Media.Brushes.Black };
            previewContent.Children.Add(VideoHost);

            var preview = new Window
            {
                Title = "VideoPlayer — podgląd pełnoekranowy",
                Content = previewContent,
                Background = System.Windows.Media.Brushes.Black,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = target.Bounds.Left,
                Top = target.Bounds.Top,
                Width = target.Bounds.Width,
                Height = target.Bounds.Height,
                WindowState = WindowState.Maximized,
                ShowInTaskbar = true
            };
            _previewWindow = preview;
            preview.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape || e.Key == Key.F11)
                {
                    e.Handled = true;
                    ReturnPreviewWindow();
                }
            };
            preview.Closed += (_, __) =>
            {
                if (!ReferenceEquals(_previewWindow, preview)) return;
                if (ReferenceEquals(preview.Content, previewContent))
                {
                    previewContent.Children.Remove(VideoHost);
                    preview.Content = null;
                    VideoHostContainer.Child = VideoHost;
                }
                _previewWindow = null;
            };
            preview.Show();
            preview.Activate();
        }

        private void ReturnPreviewWindow()
        {
            var preview = _previewWindow;
            if (preview == null) return;
            _previewWindow = null;
            if (preview.Content is Grid content && content.Children.Contains(VideoHost))
            {
                content.Children.Remove(VideoHost);
                preview.Content = null;
                VideoHostContainer.Child = VideoHost;
            }
            preview.Close();
        }

        private void CloseDetachedPreview()
        {
            if (_previewWindow == null) return;
            ReturnPreviewWindow();
        }
    }
}
