using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace VideoPlayer
{
    public partial class MainWindow
    {
        private enum WorkspaceMode
        {
            Player,
            PlateOcr,
            Dahua
        }

        private double _toolsWidth = 350;
        private WorkspaceMode _workspaceMode = WorkspaceMode.Player;
        private bool _switchingWorkspace;

        private void ConstrainToolsWidth()
        {
            if (ToolsColumn == null || ActualWidth <= 0) return;
            ToolsColumn.MaxWidth = Math.Max(300, (ActualWidth - 40) * 0.55);
        }

        private void ToggleTools_Click(object sender, RoutedEventArgs e)
        {
            SetToolsVisible(ToolsPanel.Visibility != Visibility.Visible);
        }

        private void SetToolsVisible(bool visible)
        {
            if (visible)
            {
                ConstrainToolsWidth();
                ToolsColumn.MinWidth = 300;
                ToolsColumn.Width = new GridLength(Math.Max(300, _toolsWidth));
                ToolsSplitterColumn.Width = new GridLength(7);
                ToolsPanel.Visibility = Visibility.Visible;
                ToolsSplitter.Visibility = Visibility.Visible;
            }
            else
            {
                if (ToolsColumn.ActualWidth >= 320) _toolsWidth = ToolsColumn.ActualWidth;
                ToolsColumn.MinWidth = 0;
                ToolsColumn.Width = new GridLength(0);
                ToolsSplitterColumn.Width = new GridLength(0);
                ToolsPanel.Visibility = Visibility.Collapsed;
                ToolsSplitter.Visibility = Visibility.Collapsed;
            }
            ToolsPanelMenuItem.IsChecked = visible;
            PanelToggleButton.Content = visible ? "Ukryj panel ▶" : "◀ Pokaż panel";
            SaveWindowSettings();
        }

        private void SetWorkspaceMode(WorkspaceMode mode, int? preferredTabIndex = null)
        {
            if (_switchingWorkspace) return;
            _switchingWorkspace = true;
            try
            {
                if (mode != WorkspaceMode.PlateOcr && ViewModel.PlateOcr.IsActive)
                {
                    ResetOcrWorkspaceState(silent: true);
                }

                // Pause mpv when leaving Player viewport so audio/playback does not run invisibly.
                if (mode != WorkspaceMode.Player && _workspaceMode == WorkspaceMode.Player && _ready && !_paused)
                {
                    _ = SendAsync("set_property", "pause", true);
                }

                _workspaceMode = mode;

                PlayerViewportLayer.Visibility = mode == WorkspaceMode.Player ? Visibility.Visible : Visibility.Collapsed;
                PlateOcrViewportLayer.Visibility = mode == WorkspaceMode.PlateOcr ? Visibility.Visible : Visibility.Collapsed;
                DahuaViewportLayer.Visibility = mode == WorkspaceMode.Dahua ? Visibility.Visible : Visibility.Collapsed;

                bool showMpvBottomBar = mode != WorkspaceMode.Dahua;
                PlayerTimelineBorder.Visibility = showMpvBottomBar ? Visibility.Visible : Visibility.Collapsed;
                PlayerTransportGrid.Visibility = showMpvBottomBar ? Visibility.Visible : Visibility.Collapsed;

                OcrInspectorPanel.Visibility = mode == WorkspaceMode.PlateOcr ? Visibility.Visible : Visibility.Collapsed;
                OcrReturnTopButton.Visibility = mode == WorkspaceMode.PlateOcr ? Visibility.Visible : Visibility.Collapsed;

                ModePlayerButton.Background = new SolidColorBrush(mode == WorkspaceMode.Player ? Color.FromRgb(0x34, 0x78, 0xF6) : Colors.Transparent);
                ModePlayerButton.BorderBrush = new SolidColorBrush(mode == WorkspaceMode.Player ? Color.FromRgb(0x34, 0x78, 0xF6) : Colors.Transparent);
                ModePlayerButton.Foreground = new SolidColorBrush(mode == WorkspaceMode.Player ? Colors.White : Color.FromRgb(0xD8, 0xDC, 0xE4));

                ModeDahuaButton.Background = new SolidColorBrush(mode == WorkspaceMode.Dahua ? Color.FromRgb(0x16, 0xA3, 0x4A) : Colors.Transparent);
                ModeDahuaButton.BorderBrush = new SolidColorBrush(mode == WorkspaceMode.Dahua ? Color.FromRgb(0x15, 0x80, 0x3D) : Colors.Transparent);
                ModeDahuaButton.Foreground = new SolidColorBrush(mode == WorkspaceMode.Dahua ? Colors.White : Color.FromRgb(0xD8, 0xDC, 0xE4));

                if (mode == WorkspaceMode.Dahua)
                {
                    EnsureDahuaSdkInitialized();
                }

                if (preferredTabIndex.HasValue && ToolsTabs != null)
                {
                    SetToolsVisible(true);
                    ToolsTabs.SelectedIndex = preferredTabIndex.Value;
                }

                if (mode == WorkspaceMode.Player)
                {
                    Dispatcher.BeginInvoke(new Action(UpdateTimelineOverlay));
                }
            }
            finally
            {
                _switchingWorkspace = false;
            }
        }

        private void SwitchToPlayerMode_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.PlateOcr.IsBusy)
            {
                ViewModel.StatusMessage = "Poczekaj na zatrzymanie bieżącego rozpoznawania OCR.";
                ViewModel.PlateOcr.CancelRecognition();
                return;
            }
            int? targetTab = ToolsTabs.SelectedIndex == 3 ? 0 : null;
            SetWorkspaceMode(WorkspaceMode.Player, targetTab);
        }

        private void ToolsTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_switchingWorkspace || !IsLoaded || !ReferenceEquals(e.Source, ToolsTabs)) return;
            SaveWindowSettings();

            if (ToolsTabs.SelectedIndex == 3)
            {
                SetWorkspaceMode(WorkspaceMode.Dahua);
            }
            else if (ToolsTabs.SelectedIndex is 0 or 1 or 2)
            {
                if (_workspaceMode == WorkspaceMode.Dahua)
                {
                    SetWorkspaceMode(WorkspaceMode.Player);
                }
            }
        }

        private void SelectExportTab_Click(object sender, RoutedEventArgs e)
        {
            SetWorkspaceMode(WorkspaceMode.Player, 2);
        }

        private void MenuScale_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menu || menu.Tag is not string mode) return;
            ApplyScaleFromContextMenu(mode);
        }

        private void VideoPanel_MouseUp(object? sender, System.Windows.Forms.MouseEventArgs e)
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Right && _workspaceMode == WorkspaceMode.Player)
                ShowVideoContextMenu(VideoHost);
        }

        private void DahuaVideoSurface_MouseUp(object? sender, System.Windows.Forms.MouseEventArgs e)
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Right && _workspaceMode == WorkspaceMode.Dahua)
                ShowVideoContextMenu(DahuaPrimaryVideoHost);
        }

        private void ShowVideoContextMenu(FrameworkElement? placementTarget = null)
        {
            var menu = new ContextMenu { PlacementTarget = placementTarget ?? VideoHost, Placement = PlacementMode.MousePoint };
            void Add(string text, RoutedEventHandler action)
            {
                var item = new MenuItem { Header = text };
                item.Click += action;
                menu.Items.Add(item);
            }
            string selectedScale = (VideoScaleBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "fit";
            void AddScale(string label, string mode)
            {
                var scaleItem = new MenuItem
                {
                    Header = label,
                    IsCheckable = true,
                    IsChecked = string.Equals(selectedScale, mode, StringComparison.Ordinal)
                };
                // Opcje są bezpośrednimi pozycjami menu. Unikamy zagnieżdżonego
                // podmenu, które w połączeniu z WindowsFormsHost bywało trudne
                // do kliknięcia i nie przekazywało wyboru do odtwarzacza.
                scaleItem.Click += (_, __) => ApplyScaleFromContextMenu(mode);
                menu.Items.Add(scaleItem);
            }
            AddScale("Dopasuj cały obraz", "fit");
            AddScale("Wypełnij okno", "fill");
            AddScale("Powiększenie 125%", "1.25");
            AddScale("Powiększenie 150%", "1.5");
            AddScale("Powiększenie 200%", "2");

            if (_workspaceMode == WorkspaceMode.Player)
            {
                menu.Items.Add(new Separator());
                Add("Ustaw początek (I)", MarkStart_Click);
                Add("Ustaw koniec (O)", MarkEnd_Click);
                menu.Items.Add(new Separator());
                Add("Dodaj zdarzenie…", ContextAddMarker_Click);
                Add("Rozpoznaj tablicę (OCR)…", PlateOcr_Click);
                Add("Zapisz klatkę (S)", Screenshot_Click);
            }
            menu.Items.Add(new Separator());
            Add("Resetuj widok (R)", ResetView_Click);
            menu.IsOpen = true;
        }

        private void ContextAddMarker_Click(object sender, RoutedEventArgs e)
        {
            SetWorkspaceMode(WorkspaceMode.Player, 1);
            MarkerDescriptionBox.Focus();
        }
    }
}
