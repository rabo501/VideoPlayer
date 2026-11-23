using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using FormsIntegration = System.Windows.Forms.Integration;
using Calendar = System.Windows.Controls.Calendar;
using VideoPlayer.ViewModels;

namespace VideoPlayer
{
    public partial class MainWindow
    {
        // Panel przycina obraz. PictureBox zachowuje ten sam HWND przekazany do SDK.
        private readonly Forms.Panel _dahuaPrimaryViewport = new()
        {
            BackColor = System.Drawing.Color.Black,
            Dock = Forms.DockStyle.Fill
        };
        private readonly Forms.PictureBox _dahuaPrimaryPictureBox = new()
        {
            BackColor = System.Drawing.Color.Black,
            SizeMode = Forms.PictureBoxSizeMode.Normal
        };
        private string _dahuaViewScaleMode = "fit";
        private double _dahuaViewScaleFactor = 1.0;

        private DahuaDeviceItem? _currentDahuaDevice;
        private DahuaDeviceItem? _configSelectedDevice;
        private bool _configLoading;
        private IntPtr _dahuaRealPlayHandle = IntPtr.Zero;
        private IntPtr _dahuaPlaybackHandle = IntPtr.Zero;
        private int _dahuaCurrentChannel = 1;
        private bool _dahuaPtzAreaZoomRunning;
        private readonly List<Button> _dahuaChannelButtons = new();
        private readonly HashSet<DateTime> _dahuaArchiveAvailableDays = new();
        private readonly HashSet<DateTime> _dahuaRecorderAvailableDays = new();
        private readonly HashSet<Calendar> _dahuaHookedArchiveCalendars = new();
        private readonly List<DahuaMultiViewSlot> _dahuaMultiViewSlots = new();
        private int _dahuaLiveStartRequestId;
        private IReadOnlyList<IntPtr> DahuaMultiPlaybackHandles => ViewModel.Dahua.GetArchivePlaybackHandles();
        private readonly HashSet<int> _dahuaArchiveSelectedChannels = new();
        private bool _dahuaUpdatingArchiveChannelChecks;
        private readonly DispatcherTimer _dahuaPlaybackUiTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private DateTime _dahuaPlaybackRangeStart;
        private DateTime _dahuaPlaybackRangeEnd;
        private DateTime _dahuaPlaybackSegmentStartedAt;
        private double _dahuaPlaybackElapsedBeforePause;
        private bool _dahuaSynchronizedPlaybackPaused;
        private bool _dahuaIsSeekingPlayback;
        private bool _dahuaUpdatingChannelChecks;
        private bool _dahuaConnectForCalendarAvailability;
        private double _dahuaArchivePlaybackSpeed = 1.0;
        private bool _dahuaUpdatingArchiveSpeed;
        private double _dahuaRecordingTimelineZoom = 1.0;
        private double _dahuaRecordingTimelineStartHour;
        private bool _dahuaSyncingDeviceLists;
        private bool _dahuaEventsHooked;
        private bool _dahuaTimelineIsSelecting;
        private bool _dahuaTimelineDragStarted;
        private Point _dahuaTimelinePointerDown;
        private DateTime? _dahuaTimelineSelectionStart;
        private DateTime? _dahuaTimelineSelectionEnd;
        private CancellationTokenSource? _dahuaArchiveDownloadCts;

        private DahuaSessionService DahuaSession => ViewModel.Dahua.Session;

        private sealed class DahuaMultiViewSlot
        {
            public int Channel { get; init; }
            public Forms.PictureBox PictureBox { get; init; } = null!;
            public FormsIntegration.WindowsFormsHost Host { get; init; } = null!;
            public Forms.Panel Viewport { get; init; } = null!;
            public double Zoom { get; set; } = 1.0;
            public int SourceWidth { get; set; }
            public int SourceHeight { get; set; }
            public double SourceAspect { get; set; } = 16.0 / 9.0;
            public DahuaNetSdk.fRealDataCallBackEx2? FrameInfoCallback { get; set; }
            public int FrameInfoUpdatePending;
            public int PanX { get; set; }
            public int PanY { get; set; }
            public bool IsDragging { get; set; }
            public System.Drawing.Point DragStart { get; set; }
            public System.Drawing.Point PanStart { get; set; }
            public bool IsSelectingArea { get; set; }
            public System.Drawing.Point SelectionStart { get; set; }
            public System.Drawing.Rectangle? SelectionFrameScreen { get; set; }
            public IntPtr StreamHandle { get; set; }
            public IntPtr PlaybackHandle { get; set; }
        }

        private void InitializeDahuaHost()
        {
            _dahuaPrimaryViewport.Controls.Add(_dahuaPrimaryPictureBox);
            _dahuaPrimaryPictureBox.CreateControl();
            DahuaPrimaryVideoHost.Child = _dahuaPrimaryViewport;
            _dahuaPrimaryViewport.Resize += (_, __) => ApplyDahuaPrimaryScale();
            ApplyDahuaPrimaryScale();
            InitializeDahuaDatePickers();
            _dahuaPlaybackUiTimer.Tick += DahuaPlaybackUiTimer_Tick;

            if (!_dahuaEventsHooked)
            {
                _dahuaEventsHooked = true;
                DahuaSession.SdkDisconnected += (ip, port) =>
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        ViewModel.Dahua.StatusText = $"Utracono połączenie z {ip}:{port}. Trwa automatyczne wznawianie…";
                    });
                };
                DahuaSession.SdkReconnected += (ip, port) =>
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        ViewModel.Dahua.StatusText = $"Przywrócono połączenie z {ip}:{port}.";
                        _ = StartDahuaMultiLiveForSelectedChannelsAsync();
                    });
                };
            }
        }

        private void SetDahuaPrimaryScale(string mode)
        {
            _dahuaViewScaleMode = mode;
            _dahuaViewScaleFactor = 1.0;
            if (double.TryParse(mode, NumberStyles.Float, CultureInfo.InvariantCulture, out double factor) && factor >= 1.0)
                _dahuaViewScaleFactor = factor;

            // Właściwy podgląd Live jest uruchamiany w slotach wielokanałowych:
            // SDK otrzymuje HWND slot.PictureBox, a nie główny _dahuaPrimaryPictureBox.
            // Dlatego skalę trzeba zastosować do wszystkich aktualnych slotów.
            if (_dahuaMultiViewSlots.Count > 0)
            {
                foreach (DahuaMultiViewSlot slot in _dahuaMultiViewSlots)
                {
                    // Fit/fill jest realizowane przez bazowy rozmiar obrazu z jego
                    // rzeczywistym aspect ratio; Zoom pozostaje dodatkowym powiększeniem.
                    slot.Zoom = Math.Max(1.0, _dahuaViewScaleFactor);
                    slot.PanX = 0;
                    slot.PanY = 0;
                    ApplyDahuaSlotViewport(slot);
                }
                return;
            }

            // Zapasowa ścieżka dla pojedynczego hosta, jeśli zostanie użyty bez slotów.
            ApplyDahuaPrimaryScale();
        }

        private void ApplyDahuaPrimaryScale()
        {
            if (_dahuaPrimaryViewport.ClientSize.Width <= 0 || _dahuaPrimaryViewport.ClientSize.Height <= 0) return;
            double vw = _dahuaPrimaryViewport.ClientSize.Width;
            double vh = _dahuaPrimaryViewport.ClientSize.Height;
            const double aspect = 16.0 / 9.0;
            double bw, bh;
            if (string.Equals(_dahuaViewScaleMode, "fill", StringComparison.OrdinalIgnoreCase))
            {
                bw = Math.Max(vw, vh * aspect);
                bh = bw / aspect;
                if (bh < vh) { bh = vh; bw = bh * aspect; }
            }
            else
            {
                bw = Math.Min(vw, vh * aspect);
                bh = bw / aspect;
            }
            int width = Math.Max(1, (int)Math.Round(bw * _dahuaViewScaleFactor));
            int height = Math.Max(1, (int)Math.Round(bh * _dahuaViewScaleFactor));
            _dahuaPrimaryPictureBox.Bounds = new System.Drawing.Rectangle(
                (int)Math.Round((vw - width) / 2.0),
                (int)Math.Round((vh - height) / 2.0), width, height);
        }

        private void InitializeDahuaDatePickers()
        {
            DateTime now = DateTime.Now;
            DahuaStartDatePicker.SelectedDate = now;
            DahuaEndDatePicker.SelectedDate = now;
            DahuaStartTimeBox.Text = now.AddHours(-1).ToString("HH:mm:ss");
            DahuaEndTimeBox.Text = now.ToString("HH:mm:ss");
        }

        private void DahuaStream_Click(object sender, RoutedEventArgs e)
        {
            SetWorkspaceMode(WorkspaceMode.Dahua, 3);
        }

        public async Task OpenDahuaStreamAsync(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                ViewModel.StatusMessage = "Plik nagrania Dahua nie istnieje.";
                return;
            }

            try
            {
                SetWorkspaceMode(WorkspaceMode.Player, 0);
                await OpenMediaAsync(filePath);
                Activate();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Błąd wczytywania strumienia do odtwarzacza: " + ex.Message, "Dahua VideoPlayer");
            }
        }

        private void EnsureDahuaSdkInitialized()
        {
            DahuaSdkInitResult result = ViewModel.Dahua.EnsureSdkInitialized();
            if (DahuaSdkStatusBadge != null)
                DahuaSdkStatusBadge.Foreground = result.Success ? Brushes.LightGreen : Brushes.Orange;
        }

        private void CleanupDahuaOnClosing()
        {
            _dahuaPlaybackUiTimer.Stop();
            try { _dahuaArchiveDownloadCts?.Cancel(); } catch { }
            DahuaDisconnect();
            DisposeDahuaMultiViewSlots();
        }

        private void LoadDahuaDeviceList()
        {
            try
            {
                ViewModel.Dahua.LoadDevices();
                RefreshDahuaDeviceBindings(selectDevice: ViewModel.Dahua.Devices.FirstOrDefault());
            }
            catch (Exception ex)
            {
                ViewModel.Dahua.Devices.Clear();
                ViewModel.Dahua.StatusText = "Nie można wczytać konfiguracji urządzeń. Oryginalny plik nie został nadpisany.";
                MessageBox.Show(this, ex.Message, "Błąd konfiguracji Dahua", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RefreshDahuaDeviceBindings(DahuaDeviceItem? selectDevice)
        {
            _dahuaSyncingDeviceLists = true;
            try
            {
                DahuaDevicesListBox.Items.Refresh();
                DahuaConfigDevicesList.Items.Refresh();
                var devices = ViewModel.Dahua.Devices;
                if (selectDevice != null && devices.Contains(selectDevice))
                {
                    DahuaDevicesListBox.SelectedItem = selectDevice;
                    DahuaConfigDevicesList.SelectedItem = selectDevice;
                }
                else if (devices.Count > 0)
                {
                    DahuaDevicesListBox.SelectedIndex = 0;
                    DahuaConfigDevicesList.SelectedIndex = 0;
                }
                else
                {
                    ClearDahuaConfigEditor();
                }
            }
            finally
            {
                _dahuaSyncingDeviceLists = false;
            }

            if (DahuaDevicesListBox.SelectedItem is DahuaDeviceItem dev)
                ApplySelectedDahuaDevice(dev);
            if (DahuaConfigDevicesList.SelectedItem is DahuaDeviceItem cfgDev)
                LoadDahuaConfigEditor(cfgDev);
        }

        private void DahuaDevicesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_dahuaSyncingDeviceLists) return;
            if (DahuaDevicesListBox.SelectedItem is DahuaDeviceItem dev)
                ApplySelectedDahuaDevice(dev);
        }

        private void ApplySelectedDahuaDevice(DahuaDeviceItem dev)
        {
            if (_currentDahuaDevice != null && !string.Equals(_currentDahuaDevice.Id, dev.Id, StringComparison.Ordinal))
                _dahuaArchiveSelectedChannels.Clear();

            if (DahuaSession.LoginId != IntPtr.Zero && !DahuaSession.IsConnectedTo(dev))
                DahuaDisconnect();

            ClearDahuaTimelineSelection();
            _currentDahuaDevice = dev;
            ViewModel.Dahua.SelectedDevice = dev;
            DahuaStopMultiLiveStreams();
            DahuaStopSynchronizedPlayback();
            DisposeDahuaMultiViewSlots();
            BuildDahuaModernChannelChecks();

            _dahuaArchiveAvailableDays.Clear();
            _dahuaRecorderAvailableDays.Clear();
            foreach (DateTime day in DahuaSession.GetCachedAvailabilityForChannel(dev.Id, GetSelectedDahuaArchiveChannel()))
                _dahuaRecorderAvailableDays.Add(day);

            RefreshOpenDahuaArchiveCalendars();
            ViewModel.Dahua.ArchiveRecords.Clear();
            _currentDahuaDevice.EnsureDefaultChannels(16);
            _currentDahuaDevice.EnsureDefaultPresets();

            _dahuaCurrentChannel = dev.Channel;
            ViewModel.Dahua.CurrentChannel = dev.Channel;
            RefreshDahuaChannelLists();
            RefreshDahuaPresetsList();
            BuildDahuaArchiveChannelChecks();
            RefreshDahuaArchiveSelectedChannelsSummary();

            DahuaChannelNameEditBox.Text = dev.GetChannelName(_dahuaCurrentChannel);
            DahuaStreamTypeBox.SelectedIndex = dev.StreamType == DahuaNetSdk.EM_RealPlayType.Realplay ? 0 : 1;
            HighlightActiveDahuaChannelButton(_dahuaCurrentChannel);

            if (!ReferenceEquals(DahuaConfigDevicesList.SelectedItem, dev))
            {
                _dahuaSyncingDeviceLists = true;
                DahuaConfigDevicesList.SelectedItem = dev;
                _dahuaSyncingDeviceLists = false;
                LoadDahuaConfigEditor(dev);
            }
        }

        #region Dahua Device Configuration Sub-tab

        private void DahuaConfigDevicesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_dahuaSyncingDeviceLists || _configLoading) return;
            if (DahuaConfigDevicesList.SelectedItem is DahuaDeviceItem device)
                LoadDahuaConfigEditor(device);
        }

        private void LoadDahuaConfigEditor(DahuaDeviceItem device)
        {
            _configLoading = true;
            _configSelectedDevice = device;
            DahuaConfigNameBox.Text = device.Name;
            DahuaConfigAddressBox.Text = device.IpAddress;
            DahuaConfigPortBox.Text = device.Port.ToString(CultureInfo.InvariantCulture);
            DahuaConfigHttpPortBox.Text = device.HttpPort.ToString(CultureInfo.InvariantCulture);
            DahuaConfigUsernameBox.Text = device.CredentialsUnavailable ? string.Empty : device.Username;
            DahuaConfigPasswordBox.Password = device.CredentialsUnavailable ? string.Empty : device.Password;
            DahuaConfigCredentialInfo.Text = device.CredentialsUnavailable
                ? "Nie można odszyfrować danych logowania dla tego konta Windows. Wpisz ponownie użytkownika i hasło."
                : "Dane logowania są chronione przez Windows DPAPI (CurrentUser).";
            DahuaConfigCredentialInfo.Foreground = device.CredentialsUnavailable ? Brushes.Orange : Brushes.LightGreen;
            _configLoading = false;
        }

        private void ClearDahuaConfigEditor()
        {
            _configLoading = true;
            _configSelectedDevice = null;
            DahuaConfigNameBox.Text = string.Empty;
            DahuaConfigAddressBox.Text = string.Empty;
            DahuaConfigPortBox.Text = "37777";
            DahuaConfigHttpPortBox.Text = "80";
            DahuaConfigUsernameBox.Text = "admin";
            DahuaConfigPasswordBox.Password = string.Empty;
            DahuaConfigCredentialInfo.Text = "Nowe urządzenie. Podaj dane logowania i zapisz konfigurację.";
            DahuaConfigCredentialInfo.Foreground = Brushes.LightSkyBlue;
            _configLoading = false;
        }

        private bool TryReadDahuaConfigEditor(out string name, out string address, out ushort port, out ushort httpPort)
        {
            name = DahuaConfigNameBox.Text.Trim();
            address = DahuaConfigAddressBox.Text.Trim();
            if (!DahuaViewModel.TryValidateDeviceConfiguration(
                name,
                address,
                DahuaConfigPortBox.Text,
                DahuaConfigHttpPortBox.Text,
                DahuaConfigUsernameBox.Text,
                DahuaConfigPasswordBox.Password,
                out port,
                out httpPort,
                out string error))
            {
                MessageBox.Show(this, error, "Konfiguracja", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            return true;
        }

        private void DahuaConfigSave_Click(object sender, RoutedEventArgs e)
        {
            if (!TryReadDahuaConfigEditor(out string name, out string address, out ushort port, out ushort httpPort))
                return;

            if (_configSelectedDevice == null)
            {
                MessageBox.Show(this, "Najpierw wybierz urządzenie lub kliknij Dodaj nowe urządzenie.", "Konfiguracja", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (DahuaSession.IsConnectedTo(_configSelectedDevice))
                DahuaDisconnect();

            try
            {
                ViewModel.Dahua.SaveDeviceConfiguration(
                    _configSelectedDevice,
                    name,
                    address,
                    port,
                    httpPort,
                    DahuaConfigUsernameBox.Text,
                    DahuaConfigPasswordBox.Password);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Błąd konfiguracji", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            DahuaConfigCredentialInfo.Text = "Zapisano. Dane logowania są chronione przez Windows DPAPI.";
            DahuaConfigCredentialInfo.Foreground = Brushes.LightGreen;
            RefreshDahuaDeviceBindings(_configSelectedDevice);
        }

        private void DahuaConfigAdd_Click(object sender, RoutedEventArgs e)
        {
            DahuaDeviceItem device = ViewModel.Dahua.AddNewDevice();
            RefreshDahuaDeviceBindings(device);
            LoadDahuaConfigEditor(device);
            DahuaConfigNameBox.Text = "Nowe urządzenie Dahua";
            DahuaConfigAddressBox.Focus();
        }

        private void DahuaConfigDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_configSelectedDevice == null) return;
            if (MessageBox.Show(this, $"Usunąć urządzenie „{_configSelectedDevice.Name}”?", "Potwierdzenie",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            var removed = _configSelectedDevice;
            if (DahuaSession.IsConnectedTo(removed))
                DahuaDisconnect();

            ViewModel.Dahua.RemoveDevice(removed);
            RefreshDahuaDeviceBindings(ViewModel.Dahua.Devices.FirstOrDefault());
        }

        #endregion

        #region Channels & Device State Queries

        private async void DahuaQueryFromDevice_Click(object sender, RoutedEventArgs e)
        {
            if (DahuaSession.LoginId == IntPtr.Zero)
            {
                if (!await DahuaConnectAsync()) return;
            }
            await QueryDahuaDeviceStateAsync(showDialog: true);
        }

        private async Task QueryDahuaDeviceStateAsync(bool showDialog = false)
        {
            if (_currentDahuaDevice == null || DahuaSession.LoginId == IntPtr.Zero) return;

            try
            {
                DahuaDeviceItem device = _currentDahuaDevice;
                int channel = _dahuaCurrentChannel;
                DahuaDeviceStateReadResult result =
                    await ViewModel.Dahua.ReadDeviceStateAsync(device, channel);

                if (_closing || !ReferenceEquals(_currentDahuaDevice, device)) return;

                List<DahuaChannelNameChange> changes =
                    DahuaViewModel.GetChannelNameChanges(device, result.RemoteChannels);
                int appliedNames = 0;
                if (changes.Count > 0)
                {
                    string preview = string.Join(Environment.NewLine,
                        changes.Take(20).Select(change =>
                            $"CH {change.Number}: \"{change.LocalName}\"  →  \"{change.RemoteName}\""));
                    if (changes.Count > 20)
                        preview += Environment.NewLine + $"… oraz {changes.Count - 20} kolejnych zmian.";

                    var decision = MessageBox.Show(this,
                        $"Rejestrator zwrócił {result.RemoteChannels.Count} nazw kanałów. Wykryto {changes.Count} różnic względem konfiguracji lokalnej.\n\n" +
                        preview + "\n\nCzy zastosować nazwy z rejestratora?\nIdentyfikatory kanałów pozostaną bez zmian.",
                        "Porównanie nazw kanałów Dahua",
                        MessageBoxButton.YesNo, MessageBoxImage.Question);

                    if (decision == MessageBoxResult.Yes)
                        appliedNames = ViewModel.Dahua.ApplyDeviceChannelNames(device, result.RemoteChannels);
                }

                ViewModel.Dahua.SaveDevices();

                RefreshDahuaChannelLists();
                RefreshDahuaPresetsList();

                string channelMessage = result.RemoteChannels.Count > 0
                    ? $"Nazwy kanałów: odczytano {result.RemoteChannels.Count}, zastosowano {appliedNames} zmian."
                    : $"Nazwy kanałów: odczyt nieudany; zachowano lokalne etykiety (kanałów: {result.ChannelCount}).";
                string presetMessage = result.PresetCount.HasValue
                    ? $"Presety PTZ: odczytano {result.PresetCount.Value} pozycji."
                    : "Presety PTZ: odczyt nieudany; zachowano dotychczasową listę.";
                string msg = channelMessage + Environment.NewLine + presetMessage;

                ViewModel.Dahua.StatusText = msg;
                if (showDialog)
                    MessageBox.Show(this, msg, "Odczyt z urządzenia Dahua", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                DahuaSessionService.WriteDiagnostic($"Manual device read managed exception: {ex}");
                ViewModel.Dahua.StatusText = $"Błąd odczytu danych z urządzenia: {ex.Message}";
            }
        }

        private void RefreshDahuaChannelLists()
        {
            if (_currentDahuaDevice == null) return;

            DahuaChannelComboBox.SelectionChanged -= DahuaChannelComboBox_SelectionChanged;
            DahuaChannelComboBox.Items.Clear();
            DahuaArchiveChannelBox.Items.Clear();

            var visibleChannels = GetVisibleDahuaChannels().OrderBy(c => c.Number).ToList();
            if (visibleChannels.Count > 0 && !visibleChannels.Any(c => c.Number == _dahuaCurrentChannel))
                _dahuaCurrentChannel = visibleChannels[0].Number;

            foreach (var chInfo in visibleChannels)
            {
                string text = chInfo.DisplayName;
                DahuaChannelComboBox.Items.Add(new ComboBoxItem { Content = text, Tag = chInfo.Number });
                DahuaArchiveChannelBox.Items.Add(new ComboBoxItem { Content = text, Tag = chInfo.Number });
            }

            for (int i = 0; i < DahuaChannelComboBox.Items.Count; i++)
            {
                if ((DahuaChannelComboBox.Items[i] as ComboBoxItem)?.Tag is int num && num == _dahuaCurrentChannel)
                {
                    DahuaChannelComboBox.SelectedIndex = i;
                    DahuaArchiveChannelBox.SelectedIndex = i;
                    break;
                }
            }
            DahuaChannelComboBox.SelectionChanged += DahuaChannelComboBox_SelectionChanged;

            BuildDahuaChannelButtons();
        }

        private void BuildDahuaChannelButtons()
        {
            if (_currentDahuaDevice == null) return;

            DahuaChannelButtonsPanel.Children.Clear();
            _dahuaChannelButtons.Clear();

            foreach (var chInfo in GetVisibleDahuaChannels().OrderBy(c => c.Number))
            {
                int ch = chInfo.Number;
                string displayName = string.IsNullOrWhiteSpace(chInfo.Name) ? $"CH {ch}" : $"{chInfo.Name}\n[CH {ch}]";
                var btn = new Button
                {
                    Content = displayName,
                    Tag = ch,
                    Style = (Style)FindResource("DahuaChannelButton"),
                    ToolTip = $"Kanał {ch}: {chInfo.Name}"
                };
                btn.Click += (_, _) => SwitchDahuaChannel(ch);
                _dahuaChannelButtons.Add(btn);
                DahuaChannelButtonsPanel.Children.Add(btn);
            }
            HighlightActiveDahuaChannelButton(_dahuaCurrentChannel);
        }

        private void HighlightActiveDahuaChannelButton(int channelNumber)
        {
            string channelName = _currentDahuaDevice?.GetChannelName(channelNumber) ?? $"Kanał {channelNumber}";
            foreach (var btn in _dahuaChannelButtons)
            {
                if (btn.Tag is int ch && ch == channelNumber)
                {
                    btn.Background = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
                    btn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
                    btn.Foreground = Brushes.White;
                }
                else
                {
                    btn.Background = new SolidColorBrush(Color.FromRgb(0x25, 0x2A, 0x36));
                    btn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x52, 0x5C, 0x6C));
                    btn.Foreground = Brushes.White;
                }
            }
            DahuaActiveChannelOverlayText.Text = $"{channelName.ToUpper()} (CH {channelNumber})";
        }

        public void SwitchDahuaChannel(int channelNumber)
        {
            _dahuaCurrentChannel = channelNumber;
            ViewModel.Dahua.CurrentChannel = channelNumber;
            HighlightActiveDahuaChannelButton(channelNumber);
            if (_currentDahuaDevice != null)
                DahuaChannelNameEditBox.Text = _currentDahuaDevice.GetChannelName(channelNumber);

            for (int i = 0; i < DahuaChannelComboBox.Items.Count; i++)
            {
                if ((DahuaChannelComboBox.Items[i] as ComboBoxItem)?.Tag is int num && num == channelNumber)
                {
                    DahuaChannelComboBox.SelectedIndex = i;
                    DahuaArchiveChannelBox.SelectedIndex = i;
                    break;
                }
            }

            SelectDahuaModernChannel(channelNumber);
            if (DahuaSession.LoginId != IntPtr.Zero)
            {
                _ = StartDahuaMultiLiveForSelectedChannelsAsync();
            }
            else
            {
                string name = _currentDahuaDevice?.GetChannelName(channelNumber) ?? $"Kanał {channelNumber}";
                ViewModel.Dahua.StatusText = $"Wybrano: {name} (CH {channelNumber}). Kliknij „Podgląd zaznaczonych” lub „Połącz”.";
            }
        }

        private void DahuaChannelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DahuaChannelComboBox.SelectedItem is ComboBoxItem item && item.Tag is int ch && ch != _dahuaCurrentChannel)
                SwitchDahuaChannel(ch);
        }

        private void DahuaSaveChannelName_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDahuaDevice == null) return;
            string newName = DahuaChannelNameEditBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(newName)) return;

            ViewModel.Dahua.SaveChannelName(_currentDahuaDevice, _dahuaCurrentChannel, newName);
            RefreshDahuaChannelLists();
            BuildDahuaModernChannelChecks();

            ViewModel.Dahua.StatusText = $"Zapisano nową nazwę dla Kanału {_dahuaCurrentChannel}: '{newName}'";
            DahuaActiveChannelOverlayText.Text = $"{newName.ToUpper()} (CH {_dahuaCurrentChannel})";
        }

        private void DahuaCustomChannel_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(DahuaCustomChannelBox.Text.Trim(), out int ch) && ch >= 1)
            {
                if (_currentDahuaDevice != null &&
                    ViewModel.Dahua.EnsureChannelExists(_currentDahuaDevice, ch))
                {
                    RefreshDahuaChannelLists();
                }
                SwitchDahuaChannel(ch);
            }
        }

        #endregion

        #region PTZ Presets & Manual Controls

        private void RefreshDahuaPresetsList()
        {
            DahuaPresetsComboBox.Items.Clear();
            DahuaQuickPresetsPanel.Children.Clear();

            if (_currentDahuaDevice == null || _currentDahuaDevice.Presets == null) return;

            foreach (var p in _currentDahuaDevice.Presets.OrderBy(x => x.PresetNumber))
            {
                DahuaPresetsComboBox.Items.Add(new ComboBoxItem { Content = $"[P{p.PresetNumber}] {p.Name}", Tag = p.PresetNumber });
                int pNum = p.PresetNumber;
                var quickBtn = new Button
                {
                    Content = $"P{pNum}: {p.Name}",
                    Tag = pNum,
                    Style = (Style)FindResource("DahuaPresetTileButton"),
                    ToolTip = $"Obróć do: {p.Name} (Preset {pNum})"
                };
                quickBtn.Click += (_, _) => GotoDahuaPreset(pNum);
                DahuaQuickPresetsPanel.Children.Add(quickBtn);
            }

            if (DahuaPresetsComboBox.Items.Count > 0)
                DahuaPresetsComboBox.SelectedIndex = 0;
        }

        private void DahuaPresetsComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DahuaPresetsComboBox.SelectedItem is ComboBoxItem item && item.Tag is int presetNumber)
            {
                var preset = _currentDahuaDevice?.Presets.FirstOrDefault(p => p.PresetNumber == presetNumber);
                if (preset != null)
                {
                    DahuaPresetNumberBox.Text = preset.PresetNumber.ToString(CultureInfo.InvariantCulture);
                    DahuaPresetNameBox.Text = preset.Name;
                }
            }
        }

        private void GotoDahuaPreset(int presetNumber)
        {
            if (DahuaSession.LoginId == IntPtr.Zero)
            {
                MessageBox.Show(this, "Najpierw połącz ze strumieniem wideo kamery.", "Brak połączenia", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var match = _currentDahuaDevice?.Presets.FirstOrDefault(p => p.PresetNumber == presetNumber);
            ViewModel.Dahua.GotoPreset(_dahuaCurrentChannel, presetNumber);
            if (match != null && ViewModel.Dahua.StatusText.StartsWith("Kamera PTZ:", StringComparison.Ordinal))
                ViewModel.Dahua.StatusText = $"Kamera PTZ: Przechodzenie do '{match.Name}' (Preset {presetNumber})…";
        }

        private void DahuaGotoPreset_Click(object sender, RoutedEventArgs e)
        {
            if (DahuaPresetsComboBox.SelectedItem is ComboBoxItem item && item.Tag is int pNum)
                GotoDahuaPreset(pNum);
        }

        private async void DahuaSaveCurrentPositionAsPreset_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDahuaDevice == null) return;
            if (!DahuaSession.IsConnectedTo(_currentDahuaDevice))
            {
                MessageBox.Show(this, "Najpierw połącz się z rejestratorem/kamerą.", "Brak połączenia", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!int.TryParse(DahuaPresetNumberBox.Text.Trim(), out int presetNumber) || presetNumber < 1 || presetNumber > 255)
            {
                MessageBox.Show(this, "Podaj prawidłowy numer presetu (1-255).", "Nieprawidłowy numer", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string presetName = DahuaPresetNameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(presetName)) presetName = $"Preset {presetNumber}";

            try
            {
                DahuaDeviceItem device = _currentDahuaDevice;
                int channel = _dahuaCurrentChannel;
                DahuaPresetPositionSaveResult saveResult =
                    await ViewModel.Dahua.SavePresetPositionAsync(device, channel, presetNumber, presetName);

                RefreshDahuaPresetsList();
                DahuaPresetsComboBox.SelectedItem = DahuaPresetsComboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is int n && n == presetNumber);

                if (!saveResult.NameSaved)
                {
                    MessageBox.Show(this, $"Pozycja PTZ została zapisana, ale zmiana nazwy nie powiodła się.\n\n{saveResult.NameError}", "Częściowy zapis presetu", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Nie udało się zapisać pozycji presetu.\n\n{ex.Message}", "Błąd zapisu presetu PTZ", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void DahuaRenameSelectedPreset_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDahuaDevice == null || DahuaPresetsComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not int presetNumber) return;
            string newName = DahuaPresetNameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(newName))
            {
                MessageBox.Show(this, "Wpisz nową nazwę presetu.", "Brak nazwy", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!DahuaSession.IsConnectedTo(_currentDahuaDevice))
            {
                MessageBox.Show(this, "Najpierw połącz się z urządzeniem.", "Brak połączenia", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                DahuaDeviceItem device = _currentDahuaDevice;
                await ViewModel.Dahua.RenamePresetAsync(device, _dahuaCurrentChannel, presetNumber, newName);
                RefreshDahuaPresetsList();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Błąd zmiany nazwy presetu", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void DahuaRefreshPresetsFromDevice_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDahuaDevice == null) return;
            if (!DahuaSession.IsConnectedTo(_currentDahuaDevice))
            {
                MessageBox.Show(this, "Najpierw połącz się z urządzeniem.", "Brak połączenia", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                DahuaDeviceItem device = _currentDahuaDevice;
                await ViewModel.Dahua.RefreshPresetsFromDeviceAsync(device, _dahuaCurrentChannel);
                RefreshDahuaPresetsList();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Nie udało się odczytać presetów.\n\n{ex.Message}", "Błąd odczytu presetów PTZ", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void DahuaDeletePreset_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDahuaDevice == null || DahuaPresetsComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not int presetNumber) return;
            if (!DahuaSession.IsConnectedTo(_currentDahuaDevice))
            {
                MessageBox.Show(this, "Najpierw połącz się z urządzeniem.", "Brak połączenia", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show(this, $"Czy na pewno usunąć preset {presetNumber} z urządzenia?", "Potwierdź usunięcie presetu",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            try
            {
                DahuaDeviceItem device = _currentDahuaDevice;
                await ViewModel.Dahua.DeletePresetAsync(device, _dahuaCurrentChannel, presetNumber);
                RefreshDahuaPresetsList();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Nie udało się usunąć presetu.\n\n{ex.Message}", "Błąd usuwania presetu PTZ", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SendDahuaPtz(DahuaNetSdk.EM_PTZ_COMMAND command, bool stop)
        {
            ViewModel.Dahua.SendPtz(_dahuaCurrentChannel, command, stop);
        }

        private void DahuaPtzUp_Down(object sender, MouseButtonEventArgs e) => SendDahuaPtz(DahuaNetSdk.EM_PTZ_COMMAND.UP, false);
        private void DahuaPtzDown_Down(object sender, MouseButtonEventArgs e) => SendDahuaPtz(DahuaNetSdk.EM_PTZ_COMMAND.DOWN, false);
        private void DahuaPtzLeft_Down(object sender, MouseButtonEventArgs e) => SendDahuaPtz(DahuaNetSdk.EM_PTZ_COMMAND.LEFT, false);
        private void DahuaPtzRight_Down(object sender, MouseButtonEventArgs e) => SendDahuaPtz(DahuaNetSdk.EM_PTZ_COMMAND.RIGHT, false);

        private void SendDahuaPtzDiagonal(string? direction, bool stop)
        {
            ViewModel.Dahua.SendPtzDiagonal(_dahuaCurrentChannel, direction, stop);
        }

        private void DahuaPtzDiagonal_Down(object sender, MouseButtonEventArgs e)
        {
            if (sender is Button button) SendDahuaPtzDiagonal(button.Tag?.ToString(), false);
        }

        private void DahuaPtzDiagonal_Stop(object sender, MouseButtonEventArgs e)
        {
            if (sender is Button button) SendDahuaPtzDiagonal(button.Tag?.ToString(), true);
        }

        private void DahuaPtzZoomIn_Down(object sender, MouseButtonEventArgs e) => SendDahuaPtz(DahuaNetSdk.EM_PTZ_COMMAND.ZOOM_IN, false);
        private void DahuaPtzZoomOut_Down(object sender, MouseButtonEventArgs e) => SendDahuaPtz(DahuaNetSdk.EM_PTZ_COMMAND.ZOOM_OUT, false);
        private void DahuaPtzFocusIn_Down(object sender, MouseButtonEventArgs e) => SendDahuaPtz(DahuaNetSdk.EM_PTZ_COMMAND.FOCUS_IN, false);
        private void DahuaPtzFocusOut_Down(object sender, MouseButtonEventArgs e) => SendDahuaPtz(DahuaNetSdk.EM_PTZ_COMMAND.FOCUS_OUT, false);

        private void DahuaPtz_Stop(object sender, MouseButtonEventArgs e)
        {
            if (sender is Button button && Enum.TryParse(button.Tag?.ToString(), true, out DahuaNetSdk.EM_PTZ_COMMAND command))
                SendDahuaPtz(command, true);
        }

        #endregion

        #region Live Stream & Connection

        private async void DahuaStreamTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DahuaSession.LoginId == IntPtr.Zero) return;
            if (_dahuaMultiViewSlots.Any(slot => slot.StreamHandle != IntPtr.Zero))
                await StartDahuaMultiLiveForSelectedChannelsAsync();
            else if (_dahuaRealPlayHandle != IntPtr.Zero)
                StartDahuaLiveStreamForChannel(_dahuaCurrentChannel);
        }

        private async void DahuaConnect_Click(object sender, RoutedEventArgs e)
        {
            await DahuaConnectAsync();
        }

        private async Task<bool> DahuaConnectAsync()
        {
            if (_currentDahuaDevice == null) return false;
            EnsureDahuaSdkInitialized();
            if (!DahuaSession.IsSdkInitialized)
            {
                MessageBox.Show(this, "Nie zainicjalizowano Dahua NetSDK. Sprawdź pliki dhnetsdk.dll i dhplay.dll (x64) w katalogu lib.", "Błąd SDK", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            if (_currentDahuaDevice.CredentialsUnavailable)
            {
                DahuaSubTabs.SelectedIndex = 2;
                MessageBox.Show(this, "Zapisane dane logowania są niedostępne. Wpisz je ponownie w zakładce Urządzenia.", "Dane logowania", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            string ip = _currentDahuaDevice.ConnectionAddress;
            if (!DahuaSessionService.IsValidEndpoint(ip, _currentDahuaDevice.Port.ToString(CultureInfo.InvariantCulture), out _, out string validationError))
            {
                ViewModel.Dahua.StatusText = validationError;
                MessageBox.Show(this, validationError, "Nieprawidłowa konfiguracja urządzenia", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            DahuaDisconnect();
            string user = _currentDahuaDevice.Username ?? string.Empty;
            string pass = _currentDahuaDevice.Password ?? string.Empty;
            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(pass))
            {
                DahuaSubTabs.SelectedIndex = 2;
                ViewModel.Dahua.StatusText = "Brak danych logowania. Wpisz użytkownika i hasło w zakładce Urządzenia.";
                MessageBox.Show(this, ViewModel.Dahua.StatusText, "Brak danych logowania", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            try
            {
                DahuaConnectionResult result = await ViewModel.Dahua.ConnectAsync(_currentDahuaDevice);
                if (!result.Success || _closing)
                    return false;

                if (result.ActiveChannels != null)
                {
                    if (result.ActiveChannels.Count > 0)
                    {
                        RefreshDahuaChannelLists();
                        BuildDahuaModernChannelChecks();
                    }
                    else
                    {
                        ViewModel.Dahua.StatusText = "Rejestrator nie zgłosił aktywnych kanałów wejściowych.";
                    }
                }

                ViewModel.Dahua.IsStreamOverlayVisible = false;

                if (!_dahuaConnectForCalendarAvailability)
                    await StartDahuaMultiLiveForSelectedChannelsAsync();
                else
                    ViewModel.Dahua.StatusText = $"Połączono z {ip}. Sprawdzanie dostępności nagrań w kalendarzu…";

                return true;
            }
            catch (Exception ex)
            {
                DahuaSessionService.WriteDiagnostic($"Connect exception: {ex}");
                ViewModel.Dahua.StatusText = $"Błąd połączenia: {ex.Message}";
                return false;
            }
        }

        private void StartDahuaLiveStreamForChannel(int channelNumber)
        {
            if (DahuaSession.LoginId == IntPtr.Zero) return;
            DahuaStopMultiLiveStreams();
            DahuaStopSynchronizedPlayback();
            try
            {
                if (_dahuaRealPlayHandle != IntPtr.Zero)
                {
                    ViewModel.Dahua.StopLiveStream(_dahuaRealPlayHandle);
                    _dahuaRealPlayHandle = IntPtr.Zero;
                }
                if (_dahuaPlaybackHandle != IntPtr.Zero)
                {
                    ViewModel.Dahua.StopArchivePlayback(_dahuaPlaybackHandle);
                    _dahuaPlaybackHandle = IntPtr.Zero;
                }
                if (!_dahuaPrimaryPictureBox.IsHandleCreated) _dahuaPrimaryPictureBox.CreateControl();

                var streamType = DahuaStreamTypeBox.SelectedIndex == 1
                    ? DahuaNetSdk.EM_RealPlayType.Realplay_1
                    : DahuaNetSdk.EM_RealPlayType.Realplay;
                _dahuaRealPlayHandle = ViewModel.Dahua.StartLiveStream(channelNumber, _dahuaPrimaryPictureBox.Handle, streamType);

                string chName = _currentDahuaDevice?.GetChannelName(channelNumber) ?? $"Kanał {channelNumber}";
                if (_dahuaRealPlayHandle == IntPtr.Zero)
                {
                    uint lastError = DahuaNetSdk.CLIENT_GetLastError();
                    ViewModel.Dahua.StatusText = $"Błąd uruchomienia strumienia na kanale '{chName}'. {DahuaNetSdk.GetErrorDescription(lastError)}";
                }
                else
                {
                    ViewModel.Dahua.StatusText = $"Na żywo: {chName} (CH {channelNumber})";
                    HighlightActiveDahuaChannelButton(channelNumber);
                }
            }
            catch (Exception ex)
            {
                ViewModel.Dahua.StatusText = $"Błąd uruchamiania kanału {channelNumber}: {ex.Message}";
            }
        }

        private void DahuaDisconnect()
        {
            DahuaStopMultiLiveStreams();
            DahuaStopSynchronizedPlayback();
            try
            {
                if (_dahuaRealPlayHandle != IntPtr.Zero)
                {
                    ViewModel.Dahua.StopLiveStream(_dahuaRealPlayHandle);
                    _dahuaRealPlayHandle = IntPtr.Zero;
                }
                if (_dahuaPlaybackHandle != IntPtr.Zero)
                {
                    ViewModel.Dahua.StopArchivePlayback(_dahuaPlaybackHandle);
                    _dahuaPlaybackHandle = IntPtr.Zero;
                }
                ViewModel.Dahua.Disconnect();
            }
            catch { }

            ViewModel.Dahua.IsStreamOverlayVisible = true;
            _dahuaPrimaryPictureBox.Invalidate();
        }

        private void DahuaDisconnect_Click(object sender, RoutedEventArgs e) => DahuaDisconnect();

        #endregion

        #region Multi-View & Synchronized Archive Playback

        private IEnumerable<DahuaChannelInfo> GetVisibleDahuaChannels()
        {
            if (_currentDahuaDevice == null) return Enumerable.Empty<DahuaChannelInfo>();
            if (DahuaSession.ActiveChannelNumbers == null) return _currentDahuaDevice.Channels;
            return _currentDahuaDevice.Channels.Where(channel => DahuaSession.ActiveChannelNumbers.Contains(channel.Number));
        }

        private void BuildDahuaModernChannelChecks()
        {
            DahuaModernChannelChecksPanel.Children.Clear();
            if (_currentDahuaDevice == null) return;

            int firstChannel = GetVisibleDahuaChannels().OrderBy(ch => ch.Number).Select(ch => ch.Number).FirstOrDefault();
            foreach (var channel in GetVisibleDahuaChannels().OrderBy(ch => ch.Number))
            {
                var check = new CheckBox
                {
                    Content = $"{channel.Name}  (CH {channel.Number})",
                    Tag = channel.Number,
                    IsChecked = channel.Number == firstChannel,
                    Foreground = Brushes.White,
                    FontSize = 11,
                    Margin = new Thickness(2, 3, 2, 3),
                    ToolTip = channel.Name
                };
                check.Checked += DahuaModernChannelCheck_Changed;
                check.Unchecked += DahuaModernChannelCheck_Changed;
                DahuaModernChannelChecksPanel.Children.Add(check);
            }
            int capacity = GetDahuaMultiViewCapacity();
            int selectedCount = GetSelectedDahuaModernChannels().Count;
            foreach (CheckBox check in DahuaModernChannelChecksPanel.Children.OfType<CheckBox>())
                check.IsEnabled = check.IsChecked == true || selectedCount < capacity;
        }

        private void SelectDahuaModernChannel(int channelNumber)
        {
            foreach (CheckBox check in DahuaModernChannelChecksPanel.Children.OfType<CheckBox>())
                check.IsChecked = check.Tag is int value && value == channelNumber;
        }

        private List<int> GetSelectedDahuaModernChannels()
        {
            return DahuaModernChannelChecksPanel.Children.OfType<CheckBox>()
                .Where(ch => ch.IsChecked == true && ch.Tag is int)
                .Select(ch => (int)ch.Tag).ToList();
        }

        private int GetDahuaMultiViewCapacity() => DahuaModernGridModeBox.SelectedIndex switch
        {
            0 => 1,
            2 => 9,
            3 => 16,
            _ => 4
        };

        private int GetDahuaArchiveMultiViewCapacity() => DahuaArchiveGridModeBox.SelectedIndex switch
        {
            0 => 1,
            2 => 9,
            3 => 16,
            _ => 4
        };

        private void BuildDahuaArchiveChannelChecks()
        {
            if (DahuaArchiveChannelChecksPanel == null) return;
            DahuaArchiveChannelChecksPanel.Children.Clear();
            if (_currentDahuaDevice == null) return;

            var channels = _currentDahuaDevice.Channels.OrderBy(channel => channel.Number).ToList();
            if (channels.Count == 0) return;

            var availableNumbers = channels.Select(channel => channel.Number).ToHashSet();
            _dahuaArchiveSelectedChannels.RemoveWhere(number => !availableNumbers.Contains(number));

            if (_dahuaArchiveSelectedChannels.Count == 0)
            {
                int initial = GetSelectedDahuaArchiveChannel();
                if (!channels.Any(channel => channel.Number == initial))
                    initial = channels[0].Number;
                _dahuaArchiveSelectedChannels.Add(initial);
            }

            int capacity = GetDahuaArchiveMultiViewCapacity();
            _dahuaUpdatingArchiveChannelChecks = true;
            foreach (var channel in channels)
            {
                var check = new CheckBox
                {
                    Content = $"{channel.Name}  (CH {channel.Number})",
                    Tag = channel.Number,
                    IsChecked = _dahuaArchiveSelectedChannels.Contains(channel.Number),
                    Foreground = Brushes.White,
                    FontSize = 11,
                    Margin = new Thickness(3, 5, 3, 5),
                    ToolTip = channel.Name
                };
                check.Checked += DahuaArchiveChannelCheck_Changed;
                check.Unchecked += DahuaArchiveChannelCheck_Changed;
                DahuaArchiveChannelChecksPanel.Children.Add(check);
            }

            var selected = DahuaArchiveChannelChecksPanel.Children.OfType<CheckBox>()
                .Where(check => check.IsChecked == true).ToList();
            foreach (var check in selected.Skip(capacity))
            {
                check.IsChecked = false;
                if (check.Tag is int number) _dahuaArchiveSelectedChannels.Remove(number);
            }
            _dahuaUpdatingArchiveChannelChecks = false;
            UpdateDahuaArchiveChannelCheckStates();
            RefreshDahuaArchiveSelectedChannelsSummary();
        }

        private void UpdateDahuaArchiveChannelCheckStates()
        {
            if (DahuaArchiveChannelChecksPanel == null) return;
            int capacity = GetDahuaArchiveMultiViewCapacity();
            var checks = DahuaArchiveChannelChecksPanel.Children.OfType<CheckBox>().ToList();
            int selectedCount = checks.Count(check => check.IsChecked == true);
            foreach (var check in checks)
                check.IsEnabled = check.IsChecked == true || selectedCount < capacity;
        }

        private void DahuaArchiveChannelCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_dahuaUpdatingArchiveChannelChecks || sender is not CheckBox changed || changed.Tag is not int)
                return;

            int capacity = GetDahuaArchiveMultiViewCapacity();
            var selected = DahuaArchiveChannelChecksPanel.Children.OfType<CheckBox>()
                .Where(check => check.IsChecked == true).ToList();
            if (selected.Count > capacity)
            {
                _dahuaUpdatingArchiveChannelChecks = true;
                changed.IsChecked = false;
                _dahuaUpdatingArchiveChannelChecks = false;
                ViewModel.Dahua.PlaybackSyncStatusText = $"Układ obsługuje maksymalnie {capacity} kanałów.";
            }

            _dahuaArchiveSelectedChannels.Clear();
            foreach (var check in DahuaArchiveChannelChecksPanel.Children.OfType<CheckBox>().Where(check => check.IsChecked == true))
                if (check.Tag is int number) _dahuaArchiveSelectedChannels.Add(number);

            UpdateDahuaArchiveChannelCheckStates();
            RefreshDahuaArchiveSelectedChannelsSummary();
        }

        private void DahuaArchiveGridModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || DahuaArchiveChannelChecksPanel == null) return;
            int capacity = GetDahuaArchiveMultiViewCapacity();
            _dahuaUpdatingArchiveChannelChecks = true;
            var selected = DahuaArchiveChannelChecksPanel.Children.OfType<CheckBox>()
                .Where(check => check.IsChecked == true).ToList();
            foreach (var check in selected.Skip(capacity))
            {
                check.IsChecked = false;
                if (check.Tag is int number) _dahuaArchiveSelectedChannels.Remove(number);
            }
            _dahuaUpdatingArchiveChannelChecks = false;
            UpdateDahuaArchiveChannelCheckStates();
            RefreshDahuaArchiveSelectedChannelsSummary();
        }

        private void DahuaModernGridModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || DahuaModernChannelChecksPanel == null) return;
            int capacity = GetDahuaMultiViewCapacity();
            _dahuaUpdatingChannelChecks = true;
            var selected = DahuaModernChannelChecksPanel.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).ToList();
            foreach (CheckBox check in selected.Skip(capacity)) check.IsChecked = false;
            _dahuaUpdatingChannelChecks = false;

            int selectedCount = GetSelectedDahuaModernChannels().Count;
            foreach (CheckBox check in DahuaModernChannelChecksPanel.Children.OfType<CheckBox>())
                check.IsEnabled = check.IsChecked == true || selectedCount < capacity;
        }

        private void DahuaModernChannelCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_dahuaUpdatingChannelChecks) return;
            int capacity = GetDahuaMultiViewCapacity();
            var selected = DahuaModernChannelChecksPanel.Children.OfType<CheckBox>().Where(ch => ch.IsChecked == true).ToList();
            if (selected.Count > capacity && sender is CheckBox changed)
            {
                changed.IsChecked = false;
                ViewModel.Dahua.StatusText = $"Wybrany układ obsługuje maksymalnie {capacity} kanał(ów).";
                selected = DahuaModernChannelChecksPanel.Children.OfType<CheckBox>().Where(ch => ch.IsChecked == true).ToList();
            }
            foreach (CheckBox check in DahuaModernChannelChecksPanel.Children.OfType<CheckBox>())
                check.IsEnabled = check.IsChecked == true || selected.Count < capacity;
        }

        private void DisposeDahuaMultiViewSlots()
        {
            foreach (var oldSlot in _dahuaMultiViewSlots)
            {
                try { oldSlot.Host.Child = null; } catch { }
                try { oldSlot.PictureBox.Dispose(); } catch { }
                try { oldSlot.Host.Dispose(); } catch { }
            }
            _dahuaMultiViewSlots.Clear();
            if (DahuaMultiVideoGrid != null) DahuaMultiVideoGrid.Children.Clear();
        }

        private void BuildDahuaMultiViewSlots(IReadOnlyList<int> channels, int? requestedCapacity = null)
        {
            DahuaStopMultiLiveStreams();
            DahuaStopPlaybackHandlesOnly();
            DisposeDahuaMultiViewSlots();

            int capacity = requestedCapacity ?? GetDahuaMultiViewCapacity();
            (int columns, int rows) = DahuaSessionService.GetGridDimensions(capacity);
            DahuaMultiVideoGrid.Columns = columns;
            DahuaMultiVideoGrid.Rows = rows;

            foreach (int channel in channels.Take(capacity))
            {
                DahuaMultiViewSlot? slotRef = null;
                var viewport = new Forms.Panel { BackColor = System.Drawing.Color.Black, Dock = Forms.DockStyle.Fill, TabStop = true };
                var picture = new Forms.PictureBox
                {
                    BackColor = System.Drawing.Color.Black,
                    SizeMode = Forms.PictureBoxSizeMode.Normal,
                    Location = new System.Drawing.Point(0, 0),
                    Size = new System.Drawing.Size(640, 360)
                };
                viewport.Controls.Add(picture);
                // HWND PictureBox powstaje dopiero po osadzeniu WindowsFormsHost
                // w widocznym drzewie WPF — wcześniej nie tworzymy natywnego uchwytu.
                viewport.MouseWheel += (_, args) => HandleDahuaSlotZoomWheel(slotRef!, args, viewport.PointToClient(Forms.Control.MousePosition));
                picture.MouseWheel += (_, args) => HandleDahuaSlotZoomWheel(slotRef!, args, viewport.PointToClient(Forms.Control.MousePosition));
                viewport.MouseDown += (_, args) => BeginDahuaSlotSelectionOrPan(slotRef!, viewport.PointToClient(Forms.Control.MousePosition), args);
                picture.MouseDown += (_, args) => BeginDahuaSlotSelectionOrPan(slotRef!, viewport.PointToClient(Forms.Control.MousePosition), args);
                viewport.MouseMove += (_, args) => UpdateDahuaSlotSelectionOrPan(slotRef!, viewport.PointToClient(Forms.Control.MousePosition), args);
                picture.MouseMove += (_, args) => UpdateDahuaSlotSelectionOrPan(slotRef!, viewport.PointToClient(Forms.Control.MousePosition), args);
                viewport.MouseUp += (_, args) => EndDahuaSlotSelectionOrPan(slotRef!, args);
                picture.MouseUp += (_, args) => EndDahuaSlotSelectionOrPan(slotRef!, args);

                var host = new FormsIntegration.WindowsFormsHost { Child = viewport, Background = Brushes.Black };
                var tile = new Grid { Background = Brushes.Black, ClipToBounds = true, Margin = new Thickness(2) };
                tile.Children.Add(host);
                var channelLabel = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(220, 15, 23, 42)),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(7),
                    Padding = new Thickness(7, 3, 7, 3),
                    CornerRadius = new CornerRadius(3),
                    Cursor = Cursors.Hand,
                    Child = new TextBlock
                    {
                        Text = $"{_currentDahuaDevice?.GetChannelName(channel) ?? $"Kanał {channel}"} | CH {channel}",
                        Foreground = Brushes.White,
                        FontWeight = FontWeights.Bold,
                        FontSize = 11
                    }
                };
                channelLabel.MouseLeftButtonDown += (_, args) =>
                {
                    _dahuaCurrentChannel = channel;
                    ViewModel.Dahua.CurrentChannel = channel;
                    HighlightActiveDahuaChannelButton(channel);
                    DahuaChannelNameEditBox.Text = _currentDahuaDevice?.GetChannelName(channel) ?? $"Kanał {channel}";
                    args.Handled = true;
                };
                tile.Children.Add(channelLabel);
                DahuaMultiVideoGrid.Children.Add(tile);
                slotRef = new DahuaMultiViewSlot { Channel = channel, PictureBox = picture, Host = host, Viewport = viewport };
                _dahuaMultiViewSlots.Add(slotRef);
                viewport.Resize += (_, _) => { if (slotRef != null) ApplyDahuaSlotViewport(slotRef); };
                ApplyDahuaSlotViewport(slotRef);
            }
        }

        private void ApplyDahuaSlotViewport(DahuaMultiViewSlot? slot)
        {
            if (slot == null || slot.Viewport.IsDisposed || slot.PictureBox.IsDisposed) return;
            int viewportWidth = Math.Max(1, slot.Viewport.ClientSize.Width);
            int viewportHeight = Math.Max(1, slot.Viewport.ClientSize.Height);
            double aspect = slot.SourceAspect > 0.01 && double.IsFinite(slot.SourceAspect)
                ? slot.SourceAspect : 16.0 / 9.0;
            double viewportAspect = (double)viewportWidth / viewportHeight;
            bool fill = string.Equals(_dahuaViewScaleMode, "fill", StringComparison.OrdinalIgnoreCase);

            // Dopasuj cały obraz (Fit) albo wypełnij viewport i przytnij nadmiar (Fill),
            // zawsze zachowując proporcje rzeczywistej klatki źródłowej.
            double baseWidth = fill
                ? Math.Max(viewportWidth, viewportHeight * aspect)
                : Math.Min(viewportWidth, viewportHeight * aspect);
            double baseHeight = baseWidth / aspect;
            if (!fill && baseHeight > viewportHeight)
            {
                baseHeight = viewportHeight;
                baseWidth = baseHeight * aspect;
            }
            else if (fill && baseHeight < viewportHeight)
            {
                baseHeight = viewportHeight;
                baseWidth = baseHeight * aspect;
            }

            int width = Math.Max(1, (int)Math.Round(baseWidth * slot.Zoom));
            int height = Math.Max(1, (int)Math.Round(baseHeight * slot.Zoom));

            // Przy pierwszym dopasowaniu wyśrodkuj obraz (zarówno Fit, jak i Fill).
            if (slot.PanX == 0 && slot.PanY == 0)
            {
                slot.PanX = (int)Math.Round((viewportWidth - width) / 2.0);
                slot.PanY = (int)Math.Round((viewportHeight - height) / 2.0);
            }
            slot.PanX = Math.Min(0, Math.Max(viewportWidth - width, slot.PanX));
            slot.PanY = Math.Min(0, Math.Max(viewportHeight - height, slot.PanY));
            if (slot.Zoom <= 1.001) slot.Zoom = 1.0;

            slot.PictureBox.Size = new System.Drawing.Size(width, height);
            slot.PictureBox.Location = new System.Drawing.Point(slot.PanX, slot.PanY);
        }

        private void HandleDahuaSlotZoomWheel(DahuaMultiViewSlot slot, Forms.MouseEventArgs args, System.Drawing.Point cursor)
        {
            if (args.Delta == 0) return;

            if (_dahuaMultiViewSlots.Count == 1)
            {
                if (DahuaSession.LoginId == IntPtr.Zero || _dahuaPtzAreaZoomRunning) return;
                _ = RunDahuaPtzWheelZoomAsync(args.Delta > 0);
                return;
            }

            double oldZoom = slot.Zoom;
            double newZoom = Math.Max(1.0, Math.Min(5.0, oldZoom * (args.Delta > 0 ? 1.2 : 1.0 / 1.2)));
            if (Math.Abs(newZoom - oldZoom) < 0.001) return;
            double contentX = (cursor.X - slot.PanX) / oldZoom;
            double contentY = (cursor.Y - slot.PanY) / oldZoom;
            slot.Zoom = newZoom;
            slot.PanX = (int)Math.Round(cursor.X - contentX * newZoom);
            slot.PanY = (int)Math.Round(cursor.Y - contentY * newZoom);
            ApplyDahuaSlotViewport(slot);
            slot.Viewport.Focus();
        }

        private async Task RunDahuaPtzWheelZoomAsync(bool zoomIn)
        {
            if (_dahuaPtzAreaZoomRunning) return;
            _dahuaPtzAreaZoomRunning = true;
            try
            {
                await ViewModel.Dahua.RunPtzPulseAsync(
                    _dahuaCurrentChannel,
                    zoomIn ? DahuaNetSdk.EM_PTZ_COMMAND.ZOOM_IN : DahuaNetSdk.EM_PTZ_COMMAND.ZOOM_OUT,
                    180);
            }
            finally { _dahuaPtzAreaZoomRunning = false; }
        }

        private void BeginDahuaSlotSelectionOrPan(DahuaMultiViewSlot slot, System.Drawing.Point cursor, Forms.MouseEventArgs args)
        {
            if (args.Button != Forms.MouseButtons.Left) return;

            if (_dahuaMultiViewSlots.Count == 1 && DahuaSession.LoginId != IntPtr.Zero)
            {
                slot.IsSelectingArea = true;
                slot.SelectionStart = cursor;
                slot.Viewport.Capture = true;
                slot.Viewport.Cursor = Forms.Cursors.Cross;
                return;
            }

            if (slot.Zoom <= 1.001) return;
            slot.IsDragging = true;
            slot.DragStart = cursor;
            slot.PanStart = new System.Drawing.Point(slot.PanX, slot.PanY);
            slot.Viewport.Capture = true;
            slot.Viewport.Cursor = Forms.Cursors.SizeAll;
        }

        private void UpdateDahuaSlotSelectionOrPan(DahuaMultiViewSlot slot, System.Drawing.Point cursor, Forms.MouseEventArgs args)
        {
            if (slot.IsSelectingArea)
            {
                if (slot.SelectionFrameScreen.HasValue)
                    Forms.ControlPaint.DrawReversibleFrame(slot.SelectionFrameScreen.Value, System.Drawing.Color.Lime, Forms.FrameStyle.Dashed);

                var start = slot.Viewport.PointToScreen(slot.SelectionStart);
                var end = slot.Viewport.PointToScreen(cursor);
                var frame = System.Drawing.Rectangle.FromLTRB(
                    Math.Min(start.X, end.X), Math.Min(start.Y, end.Y),
                    Math.Max(start.X, end.X), Math.Max(start.Y, end.Y));
                if (frame.Width > 0 && frame.Height > 0)
                {
                    Forms.ControlPaint.DrawReversibleFrame(frame, System.Drawing.Color.Lime, Forms.FrameStyle.Dashed);
                    slot.SelectionFrameScreen = frame;
                }
                else slot.SelectionFrameScreen = null;
                return;
            }

            if (!slot.IsDragging) return;
            slot.PanX = slot.PanStart.X + cursor.X - slot.DragStart.X;
            slot.PanY = slot.PanStart.Y + cursor.Y - slot.DragStart.Y;
            ApplyDahuaSlotViewport(slot);
        }

        private void EndDahuaSlotSelectionOrPan(DahuaMultiViewSlot slot, Forms.MouseEventArgs args)
        {
            if (args.Button != Forms.MouseButtons.Left) return;

            if (slot.IsSelectingArea)
            {
                if (slot.SelectionFrameScreen.HasValue)
                    Forms.ControlPaint.DrawReversibleFrame(slot.SelectionFrameScreen.Value, System.Drawing.Color.Lime, Forms.FrameStyle.Dashed);

                var start = slot.SelectionStart;
                var end = slot.Viewport.PointToClient(Forms.Control.MousePosition);
                slot.IsSelectingArea = false;
                slot.SelectionFrameScreen = null;
                slot.Viewport.Capture = false;
                slot.Viewport.Cursor = Forms.Cursors.Default;

                var area = System.Drawing.Rectangle.FromLTRB(
                    Math.Min(start.X, end.X), Math.Min(start.Y, end.Y),
                    Math.Max(start.X, end.X), Math.Max(start.Y, end.Y));
                if (area.Width >= 18 && area.Height >= 18)
                    _ = ApplyDahuaAreaZoomAsync(slot, area);
                else
                    ViewModel.Dahua.StatusText = "Zaznacz większy obszar obrazu, aby przybliżyć kamerę.";
                return;
            }

            slot.IsDragging = false;
            slot.Viewport.Capture = false;
            slot.Viewport.Cursor = Forms.Cursors.Default;
        }

        private async Task ApplyDahuaAreaZoomAsync(DahuaMultiViewSlot slot, System.Drawing.Rectangle area)
        {
            if (_dahuaPtzAreaZoomRunning || DahuaSession.LoginId == IntPtr.Zero || _dahuaMultiViewSlots.Count != 1)
                return;

            _dahuaPtzAreaZoomRunning = true;
            try
            {
                int viewWidth = Math.Max(1, slot.Viewport.ClientSize.Width);
                int viewHeight = Math.Max(1, slot.Viewport.ClientSize.Height);
                ViewModel.Dahua.StatusText = "PTZ: ustawianie kierunku na zaznaczony obszar…";

                await ViewModel.Dahua.ApplyAreaZoomAsync(
                    slot.Channel, viewWidth, viewHeight, area.Left, area.Top, area.Width, area.Height);

                ViewModel.Dahua.StatusText = "PTZ: zakończono przybliżanie zaznaczonego obszaru. W razie potrzeby skoryguj ręcznie kierunek lub zoom.";
            }
            catch (Exception ex)
            {
                ViewModel.Dahua.StatusText = $"Błąd przybliżania obszaru PTZ: {ex.Message}";
            }
            finally { _dahuaPtzAreaZoomRunning = false; }
        }

        private async void DahuaStartMultiLive_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDahuaDevice == null) return;
            if (!DahuaSession.IsConnectedTo(_currentDahuaDevice))
            {
                if (!await DahuaConnectAsync() || !DahuaSession.IsConnectedTo(_currentDahuaDevice))
                    return;
                if (_dahuaMultiViewSlots.Any(slot => slot.StreamHandle != IntPtr.Zero))
                    return;
            }
            await StartDahuaMultiLiveForSelectedChannelsAsync();
        }

        private async Task StartDahuaMultiLiveForSelectedChannelsAsync()
        {
            if (DahuaSession.LoginId == IntPtr.Zero || _currentDahuaDevice == null) return;

            // Każde nowe żądanie unieważnia oczekujące uruchomienie poprzedniego układu.
            int requestId = ++_dahuaLiveStartRequestId;
            var channels = GetSelectedDahuaModernChannels();
            if (channels.Count == 0) channels.Add(_dahuaCurrentChannel);
            channels = channels.Take(GetDahuaMultiViewCapacity()).ToList();
            if (channels.Count == 0) return;

            _dahuaCurrentChannel = channels[0];
            ViewModel.Dahua.CurrentChannel = _dahuaCurrentChannel;
            HighlightActiveDahuaChannelButton(_dahuaCurrentChannel);

            DahuaStopSynchronizedPlayback();
            if (_dahuaRealPlayHandle != IntPtr.Zero)
            {
                ViewModel.Dahua.StopLiveStream(_dahuaRealPlayHandle);
                _dahuaRealPlayHandle = IntPtr.Zero;
            }

            BuildDahuaMultiViewSlots(channels);

            // Nie przekazujemy SDK uchwytu kontrolki utworzonej przed dołączeniem
            // WindowsFormsHost do widocznego drzewa WPF. Najpierw wymuszamy layout,
            // a dopiero po jego zakończeniu tworzymy/odczytujemy końcowy HWND.
            await Dispatcher.InvokeAsync(() =>
            {
                if (_closing || requestId != _dahuaLiveStartRequestId) return;

                DahuaViewportLayer.UpdateLayout();
                DahuaMultiVideoGrid.UpdateLayout();

                foreach (DahuaMultiViewSlot slot in _dahuaMultiViewSlots)
                {
                    slot.Host.UpdateLayout();
                    if (!slot.Viewport.IsHandleCreated)
                        slot.Viewport.CreateControl();
                    if (!slot.PictureBox.IsHandleCreated)
                        slot.PictureBox.CreateControl();

                    ApplyDahuaSlotViewport(slot);
                }
            }, DispatcherPriority.Render);

            if (_closing || requestId != _dahuaLiveStartRequestId ||
                DahuaSession.LoginId == IntPtr.Zero)
                return;

            var streamType = DahuaStreamTypeBox.SelectedIndex == 1
                ? DahuaNetSdk.EM_RealPlayType.Realplay_1
                : DahuaNetSdk.EM_RealPlayType.Realplay;
            int started = 0;
            uint lastError = 0;
            foreach (DahuaMultiViewSlot slot in _dahuaMultiViewSlots)
            {
                if (!slot.PictureBox.IsHandleCreated)
                    slot.PictureBox.CreateControl();

                slot.StreamHandle = ViewModel.Dahua.StartLiveStream(
                    slot.Channel, slot.PictureBox.Handle, streamType);

                if (slot.StreamHandle != IntPtr.Zero)
                {
                    started++;
                    // Callback przekazuje metadane klatki; przechowujemy delegata w slocie,
                    // aby GC nie zwolnił go, gdy natywny SDK nadal go wywołuje.
                    slot.FrameInfoCallback = (handle, dataType, buffer, bufferSize, param, user) =>
                    {
                        if (dataType != 1 || param == IntPtr.Zero) return;
                        try
                        {
                            DahuaNetSdk.tagVideoFrameParam frame =
                                System.Runtime.InteropServices.Marshal.PtrToStructure<DahuaNetSdk.tagVideoFrameParam>(param);
                            int sourceWidth = frame.size == 255 ? frame.width : frame.size switch
                            {
                                18 => 1920, // 1080
                                17 => 1280, // 720
                                _ => 0
                            };
                            int sourceHeight = frame.size == 255 ? frame.height : frame.size switch
                            {
                                18 => 1080,
                                17 => 720,
                                _ => 0
                            };
                            if (sourceWidth <= 0 || sourceHeight <= 0) return;

                            double aspect = (double)sourceWidth / sourceHeight;
                            if (!double.IsFinite(aspect) || aspect <= 0.01) return;

                            // Nie wykonujemy operacji UI na wątku NetSDK. Ograniczamy
                            // liczbę oczekujących aktualizacji do jednej na slot.
                            if (slot.SourceWidth == sourceWidth && slot.SourceHeight == sourceHeight)
                                return;
                            if (System.Threading.Interlocked.CompareExchange(
                                ref slot.FrameInfoUpdatePending, 1, 0) != 0)
                                return;

                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                try
                                {
                                    if (_closing || slot.StreamHandle != handle ||
                                        !_dahuaMultiViewSlots.Contains(slot) ||
                                        slot.SourceWidth == sourceWidth && slot.SourceHeight == sourceHeight)
                                        return;

                                    slot.SourceWidth = sourceWidth;
                                    slot.SourceHeight = sourceHeight;
                                    slot.SourceAspect = aspect;
                                    ApplyDahuaSlotViewport(slot);
                                }
                                finally
                                {
                                    System.Threading.Interlocked.Exchange(ref slot.FrameInfoUpdatePending, 0);
                                }
                            }), DispatcherPriority.Render);
                        }
                        catch
                        {
                            // Nie wolno dopuścić, aby wyjątek zarządzany opuścił callback natywny.
                        }
                    };

                    // 0x2 = REALDATA_FLAG_DATA_WITH_FRAME_INFO.
                    // Jeśli firmware/SDK nie obsługuje callbacku, Live pozostaje aktywny
                    // i zachowuje bezpieczne proporcje domyślne do czasu kolejnego odczytu.
                    if (!DahuaNetSdk.CLIENT_SetRealDataCallBackEx2(
                        slot.StreamHandle, slot.FrameInfoCallback, IntPtr.Zero, 0x00000002))
                    {
                        slot.FrameInfoCallback = null;
                    }
                }
                else
                    lastError = DahuaNetSdk.CLIENT_GetLastError();
            }

            ViewModel.Dahua.StatusText = started == channels.Count
                ? $"Podgląd wielokanałowy: uruchomiono {started}/{channels.Count} kanałów."
                : started > 0
                    ? $"Podgląd Live: uruchomiono {started}/{channels.Count} kanałów. Błąd pozostałych: {DahuaNetSdk.GetErrorDescription(lastError)}"
                    : $"Nie udało się uruchomić podglądu Live. {DahuaNetSdk.GetErrorDescription(lastError)}";

            ViewModel.Dahua.PlaybackSyncStatusText = "Podgląd na żywo";
            ViewModel.Dahua.IsStreamOverlayVisible = started == 0;
        }

        private void DahuaStopMultiLiveStreams()
        {
            foreach (var slot in _dahuaMultiViewSlots)
            {
                if (slot.StreamHandle == IntPtr.Zero) continue;
                ViewModel.Dahua.StopLiveStream(slot.StreamHandle);
                slot.StreamHandle = IntPtr.Zero;
                slot.FrameInfoCallback = null;
            }
        }

        private double GetDahuaPlaybackDurationSeconds() => Math.Max(1, (_dahuaPlaybackRangeEnd - _dahuaPlaybackRangeStart).TotalSeconds);

        private double GetDahuaPlaybackElapsedSeconds()
        {
            double elapsed = _dahuaPlaybackElapsedBeforePause;
            if (!_dahuaSynchronizedPlaybackPaused)
                elapsed += (DateTime.Now - _dahuaPlaybackSegmentStartedAt).TotalSeconds * _dahuaArchivePlaybackSpeed;
            return Math.Clamp(elapsed, 0, GetDahuaPlaybackDurationSeconds());
        }

        private void UpdateDahuaPlaybackSliderRange(bool keepPlayheadVisible)
        {
            if (DahuaPlaybackPositionSlider == null || DahuaMultiPlaybackHandles.Count == 0) return;

            double visibleHours = 24.0 / _dahuaRecordingTimelineZoom;
            DateTime selectedDay = (DahuaStartDatePicker.SelectedDate ?? _dahuaPlaybackRangeStart.Date).Date;
            double viewStartHour = Math.Clamp(_dahuaRecordingTimelineStartHour, 0, 24.0 - visibleHours);
            double viewEndHour = viewStartHour + visibleHours;
            double elapsed = GetDahuaPlaybackElapsedSeconds();
            DateTime playheadTime = _dahuaPlaybackRangeStart.AddSeconds(elapsed);

            // Oś godzin i suwak muszą korzystać z tej samej daty kalendarzowej.
            if (keepPlayheadVisible && playheadTime.Date != selectedDay)
            {
                selectedDay = playheadTime.Date;
                DahuaStartDatePicker.SelectedDate = selectedDay;
            }

            if (keepPlayheadVisible)
            {
                double playheadHour = (playheadTime - selectedDay).TotalHours;
                if (playheadHour < viewStartHour || playheadHour > viewEndHour)
                {
                    _dahuaRecordingTimelineStartHour = Math.Clamp(
                        playheadHour - visibleHours * 0.15, 0, 24.0 - visibleHours);
                    viewStartHour = _dahuaRecordingTimelineStartHour;
                    viewEndHour = viewStartHour + visibleHours;
                    RefreshDahuaRecordingTimeline();
                }
            }

            // Wartość suwaka to sekundy od początku odtwarzania, a jego zakres
            // odpowiada dokładnie widocznemu przedziałowi osi godzin.
            DateTime visibleStart = selectedDay.AddHours(viewStartHour);
            DateTime visibleEnd = selectedDay.AddHours(viewEndHour);
            double minOffset = (visibleStart - _dahuaPlaybackRangeStart).TotalSeconds;
            double maxOffset = (visibleEnd - _dahuaPlaybackRangeStart).TotalSeconds;
            if (maxOffset <= minOffset) maxOffset = minOffset + 1;

            DahuaPlaybackPositionSlider.Minimum = minOffset;
            DahuaPlaybackPositionSlider.Maximum = maxOffset;
            DahuaPlaybackPositionSlider.Value = Math.Clamp(elapsed, minOffset, maxOffset);
        }

        private void RefreshDahuaArchiveSelectedChannelsSummary()
        {
            if (_currentDahuaDevice == null)
            {
                DahuaArchiveSelectedChannelsText.Text = "Nie wybrano urządzenia Dahua.";
                return;
            }

            var channels = _dahuaArchiveSelectedChannels.OrderBy(channel => channel).ToList();
            if (channels.Count == 0)
            {
                int defaultChannel = GetSelectedDahuaArchiveChannel();
                DahuaArchiveSelectedChannelsText.Text =
                    $"Domyślnie: CH {defaultChannel} — kanał wybrany w panelu Archiwum NVR.";
                return;
            }

            string summary = string.Join(", ", channels.Select(channel =>
                $"{_currentDahuaDevice.GetChannelName(channel)} (CH {channel})"));
            DahuaArchiveSelectedChannelsText.Text = summary;
        }

        private async void DahuaStartSynchronizedPlayback_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDahuaDevice == null) return;
            DateTime? start = GetDahuaStartDateTime();
            DateTime? end = GetDahuaEndDateTime();
            if (!DahuaSessionService.ValidateArchiveRange(start, end, out string rangeError))
            {
                MessageBox.Show(this, rangeError, "Błędny zakres", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!DahuaSession.IsConnectedTo(_currentDahuaDevice))
            {
                if (!await DahuaConnectAsync() || !DahuaSession.IsConnectedTo(_currentDahuaDevice))
                    return;
            }

            var channels = _dahuaArchiveSelectedChannels.OrderBy(channel => channel).ToList();
            if (channels.Count == 0)
            {
                int defaultChannel = GetSelectedDahuaArchiveChannel();
                channels.Add(defaultChannel);
                _dahuaArchiveSelectedChannels.Add(defaultChannel);
            }
            RefreshDahuaArchiveSelectedChannelsSummary();
            int capacity = GetDahuaArchiveMultiViewCapacity();
            if (channels.Count > capacity)
            {
                MessageBox.Show(this, $"Wybrano {channels.Count} kanałów, ale aktualny układ obsługuje maksymalnie {capacity}. Zmień układ lub wybierz mniej kanałów.", "Za dużo kanałów", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _dahuaCurrentChannel = channels[0];
            ViewModel.Dahua.CurrentChannel = _dahuaCurrentChannel;
            HighlightActiveDahuaChannelButton(_dahuaCurrentChannel);
            DahuaStopMultiLiveStreams();
            if (_dahuaRealPlayHandle != IntPtr.Zero)
            {
                ViewModel.Dahua.StopLiveStream(_dahuaRealPlayHandle);
                _dahuaRealPlayHandle = IntPtr.Zero;
            }
            DahuaStopSynchronizedPlayback();
            BuildDahuaMultiViewSlots(channels, capacity);

            _dahuaPlaybackRangeStart = start!.Value;
            _dahuaPlaybackRangeEnd = end!.Value;
            DahuaStartDatePicker.SelectedDate = _dahuaPlaybackRangeStart.Date;
            _dahuaPlaybackElapsedBeforePause = 0;
            _dahuaPlaybackSegmentStartedAt = DateTime.Now;
            _dahuaSynchronizedPlaybackPaused = false;
            _dahuaArchivePlaybackSpeed = 1.0;
            _dahuaUpdatingArchiveSpeed = true;
            DahuaArchiveSpeedBox.SelectedIndex = 2;
            _dahuaUpdatingArchiveSpeed = false;
            DahuaPlaybackPositionSlider.Minimum = 0;
            DahuaPlaybackPositionSlider.Maximum = Math.Max(1, (end.Value - start.Value).TotalSeconds);
            DahuaPlaybackPositionSlider.Value = 0;
            DahuaPlaybackPositionSlider.IsEnabled = true;

            try
            {
                DahuaSession.ConfigureCurrentRecordModes();
                int started = 0;
                var failedChannels = new List<int>();
                foreach (var slot in _dahuaMultiViewSlots)
                {
                    slot.PlaybackHandle = ViewModel.Dahua.StartArchivePlayback(slot.Channel, start.Value, end.Value, slot.PictureBox.Handle);
                    if (slot.PlaybackHandle != IntPtr.Zero)
                    {
                        started++;
                    }
                    else
                    {
                        failedChannels.Add(slot.Channel);
                    }
                }
                if (started == 0)
                    throw new InvalidOperationException("Rejestrator nie uruchomił odtwarzania na żadnym z wybranych kanałów.");

                // Dopiero teraz istnieją uchwyty archiwum; można zsynchronizować zakres suwaka z widocznym zakresem osi godzin.
                UpdateDahuaPlaybackSliderRange(keepPlayheadVisible: true);
                RefreshDahuaRecordingTimeline();
                _dahuaPlaybackUiTimer.Start();
                ViewModel.Dahua.IsStreamOverlayVisible = false;
                string failedSummary = failedChannels.Count == 0
                    ? "Wszystkie wybrane kanały uruchomione."
                    : $"Nie uruchomiono kanałów: {string.Join(", ", failedChannels.Select(channel => $"CH {channel}"))}.";
                ViewModel.Dahua.PlaybackSyncStatusText = $"Odtwarzanie archiwum: {started}/{channels.Count} kanałów. {failedSummary}";
                ViewModel.Dahua.StatusText = failedChannels.Count == 0
                    ? $"Odtwarzanie wielokanałowe uruchomione: {started} kanałów od {start.Value:HH:mm:ss}."
                    : $"Odtwarzanie częściowe: {started}/{channels.Count} kanałów od {start.Value:HH:mm:ss}. Brak: {string.Join(", ", failedChannels.Select(channel => $"CH {channel}"))}.";
            }
            catch (Exception ex)
            {
                DahuaStopSynchronizedPlayback();
                ViewModel.Dahua.StatusText = $"Błąd zsynchronizowanego odtwarzania: {ex.Message}";
                MessageBox.Show(this, ex.Message, "Archiwum Dahua", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void DahuaRecordingTimelineCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RefreshDahuaRecordingTimeline();

        private void RefreshDahuaRecordingTimeline()
        {
            if (DahuaRecordingTimelineCanvas == null) return;
            DahuaRecordingTimelineCanvas.Children.Clear();
            double width = DahuaRecordingTimelineCanvas.ActualWidth;
            if (width <= 0) return;

            DateTime selectedDay = (DahuaStartDatePicker.SelectedDate ?? DateTime.Today).Date;
            var records = ViewModel.Dahua.ArchiveRecords.ToList();
            const double left = 24, right = 8;
            double usableWidth = Math.Max(1, width - left - right);
            double visibleHours = 24.0 / _dahuaRecordingTimelineZoom;
            double viewStartHour = Math.Clamp(_dahuaRecordingTimelineStartHour, 0, 24.0 - visibleHours);
            double viewEndHour = viewStartHour + visibleHours;
            double xForHour(double hour) => left + usableWidth * (hour - viewStartHour) / visibleHours;

            int tickMinutes = visibleHours <= 0.5 ? 1 : visibleHours <= 1.0 ? 2 : visibleHours <= 2.5 ? 5 : visibleHours <= 4.0 ? 10 : visibleHours <= 8.0 ? 15 : visibleHours <= 12.0 ? 30 : 120;
            int firstTick = (int)(Math.Ceiling(viewStartHour * 60.0 / tickMinutes) * tickMinutes);
            for (int minuteOfDay = firstTick; minuteOfDay <= Math.Min(1440, viewEndHour * 60.0); minuteOfDay += tickMinutes)
            {
                double xPos = xForHour(minuteOfDay / 60.0);
                bool majorTick = minuteOfDay % 60 == 0;
                DahuaRecordingTimelineCanvas.Children.Add(new System.Windows.Shapes.Line
                {
                    X1 = xPos, X2 = xPos, Y1 = majorTick ? 0 : 13, Y2 = 22,
                    Stroke = new SolidColorBrush(majorTick ? Color.FromRgb(100, 116, 139) : Color.FromRgb(55, 65, 81)),
                    StrokeThickness = majorTick ? 1.0 : 0.7,
                    IsHitTestVisible = false
                });

                var label = new TextBlock
                {
                    Text = $"{minuteOfDay / 60:00}:{minuteOfDay % 60:00}",
                    Foreground = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                    FontSize = majorTick ? 9 : 8,
                    Width = 34,
                    TextAlignment = TextAlignment.Center,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(label, Math.Clamp(xPos - 17, 0, Math.Max(0, width - 34)));
                Canvas.SetTop(label, 0);
                DahuaRecordingTimelineCanvas.Children.Add(label);
            }

            foreach (DahuaRecordItem record in records)
            {
                DateTime segmentStart = record.StartTime < selectedDay ? selectedDay : record.StartTime;
                DateTime dayEnd = selectedDay.AddDays(1);
                DateTime segmentEnd = record.EndTime > dayEnd ? dayEnd : record.EndTime;
                if (segmentEnd <= segmentStart || record.StartTime.Date > selectedDay || record.EndTime.Date < selectedDay) continue;
                double recordStartHour = (segmentStart - selectedDay).TotalHours;
                double recordEndHour = (segmentEnd - selectedDay).TotalHours;
                if (recordEndHour < viewStartHour || recordStartHour > viewEndHour) continue;
                double x1 = xForHour(Math.Max(recordStartHour, viewStartHour));
                double x2 = xForHour(Math.Min(recordEndHour, viewEndHour));
                var block = new System.Windows.Shapes.Rectangle
                {
                    Width = Math.Max(2, x2 - x1), Height = 10, RadiusX = 2, RadiusY = 2,
                    Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94)),
                    Stroke = new SolidColorBrush(Color.FromRgb(74, 222, 128)),
                    StrokeThickness = 0.5,
                    ToolTip = $"{record.StartTime:HH:mm:ss} – {record.EndTime:HH:mm:ss}",
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(block, x1);
                Canvas.SetTop(block, 25);
                DahuaRecordingTimelineCanvas.Children.Add(block);
            }

            if (records.Count == 0)
            {
                var hint = new TextBlock
                {
                    Text = $"Widok {viewStartHour:00}:00–{viewEndHour:00}:00 | Kółko: zoom | Prawy przycisk: reset",
                    Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                    FontSize = 9,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(hint, left);
                Canvas.SetTop(hint, 27);
                DahuaRecordingTimelineCanvas.Children.Add(hint);
            }

            DrawDahuaTimelineSelection(selectedDay, viewStartHour, viewEndHour, xForHour);
            DrawDahuaPlaybackPlayhead(selectedDay, viewStartHour, viewEndHour, xForHour);
        }

        private void DrawDahuaPlaybackPlayhead(
            DateTime selectedDay,
            double viewStartHour,
            double viewEndHour,
            Func<double, double> xForHour)
        {
            if (DahuaMultiPlaybackHandles.Count == 0) return;

            DateTime playheadTime = _dahuaPlaybackRangeStart.AddSeconds(GetDahuaPlaybackElapsedSeconds());
            if (playheadTime.Date != selectedDay) return;

            double hour = playheadTime.TimeOfDay.TotalHours;
            if (hour < viewStartHour || hour > viewEndHour) return;

            double x = xForHour(hour);
            DahuaRecordingTimelineCanvas.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = x, X2 = x,
                Y1 = 0, Y2 = Math.Max(30, DahuaRecordingTimelineCanvas.ActualHeight),
                Stroke = new SolidColorBrush(Color.FromRgb(248, 113, 113)),
                StrokeThickness = 2,
                IsHitTestVisible = false
            });

            var label = new TextBlock
            {
                Text = $"▶ {playheadTime:HH:mm:ss}",
                Foreground = new SolidColorBrush(Color.FromRgb(254, 202, 202)),
                Background = new SolidColorBrush(Color.FromArgb(220, 127, 29, 29)),
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(3, 1, 3, 1),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(label, Math.Clamp(x + 4, 0, Math.Max(0, DahuaRecordingTimelineCanvas.ActualWidth - 72)));
            Canvas.SetTop(label, 22);
            DahuaRecordingTimelineCanvas.Children.Add(label);
        }

        private void DrawDahuaTimelineSelection(
            DateTime selectedDay,
            double viewStartHour,
            double viewEndHour,
            Func<double, double> xForHour)
        {
            if (_dahuaTimelineSelectionStart == null || _dahuaTimelineSelectionEnd == null)
                return;

            DateTime start = _dahuaTimelineSelectionStart.Value <= _dahuaTimelineSelectionEnd.Value
                ? _dahuaTimelineSelectionStart.Value : _dahuaTimelineSelectionEnd.Value;
            DateTime end = _dahuaTimelineSelectionStart.Value <= _dahuaTimelineSelectionEnd.Value
                ? _dahuaTimelineSelectionEnd.Value : _dahuaTimelineSelectionStart.Value;
            DateTime dayEnd = selectedDay.AddDays(1);
            if (end < selectedDay || start > dayEnd) return;

            double startHour = Math.Clamp((start - selectedDay).TotalHours, viewStartHour, viewEndHour);
            double endHour = Math.Clamp((end - selectedDay).TotalHours, viewStartHour, viewEndHour);
            double x1 = xForHour(startHour);
            double x2 = xForHour(endHour);

            var selection = new System.Windows.Shapes.Rectangle
            {
                Width = Math.Max(2, x2 - x1),
                Height = 12,
                RadiusX = 2,
                RadiusY = 2,
                Fill = new SolidColorBrush(Color.FromArgb(85, 59, 130, 246)),
                Stroke = new SolidColorBrush(Color.FromRgb(96, 165, 250)),
                StrokeThickness = 1,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(selection, Math.Min(x1, x2));
            Canvas.SetTop(selection, 24);
            DahuaRecordingTimelineCanvas.Children.Add(selection);

            var label = new TextBlock
            {
                Text = $"{start:HH:mm:ss} – {end:HH:mm:ss}",
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromArgb(220, 30, 64, 110)),
                FontSize = 9,
                Padding = new Thickness(3, 1, 3, 1),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(label, Math.Clamp(Math.Min(x1, x2), 0, Math.Max(0, DahuaRecordingTimelineCanvas.ActualWidth - 120)));
            Canvas.SetTop(label, 25);
            DahuaRecordingTimelineCanvas.Children.Add(label);
        }

        private DateTime GetDahuaTimelineTime(Point point)
        {
            const double left = 24;
            double usableWidth = Math.Max(1, DahuaRecordingTimelineCanvas.ActualWidth - left - 8);
            double visibleHours = 24.0 / _dahuaRecordingTimelineZoom;
            double viewStartHour = Math.Clamp(_dahuaRecordingTimelineStartHour, 0, 24.0 - visibleHours);
            double pointerRatio = Math.Clamp((point.X - left) / usableWidth, 0, 1);
            double hour = Math.Clamp(viewStartHour + pointerRatio * visibleHours, 0, 24);
            double seconds = Math.Clamp(Math.Round(hour * 3600), 0, 86399);
            return (DahuaStartDatePicker.SelectedDate ?? DateTime.Today).Date.AddSeconds(seconds);
        }

        private void ClearDahuaTimelineSelection()
        {
            _dahuaTimelineSelectionStart = null;
            _dahuaTimelineSelectionEnd = null;
            if (_dahuaTimelineIsSelecting)
            {
                _dahuaTimelineIsSelecting = false;
                if (DahuaRecordingTimelineCanvas?.IsMouseCaptured == true)
                    DahuaRecordingTimelineCanvas.ReleaseMouseCapture();
            }
            if (DahuaRecordingTimelineCanvas != null)
                RefreshDahuaRecordingTimeline();
        }

        private DahuaRecordItem? FindDahuaRecordAt(DateTime time, int channel)
        {
            return ViewModel.Dahua.ArchiveRecords.FirstOrDefault(record =>
                record.Channel == channel && time >= record.StartTime && time <= record.EndTime);
        }

        private void DahuaRecordingTimelineCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dahuaTimelineIsSelecting) return;
            Point current = e.GetPosition(DahuaRecordingTimelineCanvas);
            if (!_dahuaTimelineDragStarted)
            {
                double dx = current.X - _dahuaTimelinePointerDown.X;
                double dy = current.Y - _dahuaTimelinePointerDown.Y;
                if (Math.Abs(dx) < 4 && Math.Abs(dy) < 4) return;
                _dahuaTimelineDragStarted = true;
            }
            _dahuaTimelineSelectionEnd = GetDahuaTimelineTime(current);
            RefreshDahuaRecordingTimeline();
        }

        private void DahuaRecordingTimelineCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dahuaTimelineIsSelecting) return;
            DateTime releaseTime = GetDahuaTimelineTime(e.GetPosition(DahuaRecordingTimelineCanvas));
            bool wasDrag = _dahuaTimelineDragStarted;
            _dahuaTimelineIsSelecting = false;
            _dahuaTimelineDragStarted = false;
            DahuaRecordingTimelineCanvas.ReleaseMouseCapture();

            if (!wasDrag)
            {
                DahuaStartDatePicker.SelectedDate = releaseTime.Date;
                DahuaStartTimeBox.Text = releaseTime.ToString("HH:mm:ss");
                UpdateDahuaPlaybackSliderRange(keepPlayheadVisible: false);
                _dahuaTimelineSelectionStart = null;
                _dahuaTimelineSelectionEnd = null;
                RefreshDahuaRecordingTimeline();
                if (DahuaMultiPlaybackHandles.Count > 0 &&
                    releaseTime >= _dahuaPlaybackRangeStart && releaseTime <= _dahuaPlaybackRangeEnd)
                {
                    int offset = DahuaSessionService.ComputeArchiveSeekOffsetSeconds(releaseTime, _dahuaPlaybackRangeStart, _dahuaPlaybackRangeEnd);
                    SeekDahuaSynchronizedPlayback(offset);
                    ViewModel.Dahua.PlaybackSyncStatusText = $"Przewinięto do {releaseTime:HH:mm:ss}.";
                }
                else if (FindDahuaRecordAt(releaseTime, GetSelectedDahuaArchiveChannel()) != null)
                    ViewModel.Dahua.PlaybackSyncStatusText = $"Wybrano {releaseTime:HH:mm:ss}. Naciśnij „Odtwórz od wskazanego”, aby rozpocząć.";
                else
                    ViewModel.Dahua.PlaybackSyncStatusText = $"O {releaseTime:HH:mm:ss} nie ma nagrania na wybranym kanale.";
                e.Handled = true;
                return;
            }

            if (_dahuaTimelineSelectionStart == null || _dahuaTimelineSelectionEnd == null) return;
            DateTime start = _dahuaTimelineSelectionStart.Value <= _dahuaTimelineSelectionEnd.Value ? _dahuaTimelineSelectionStart.Value : _dahuaTimelineSelectionEnd.Value;
            DateTime finish = _dahuaTimelineSelectionStart.Value <= _dahuaTimelineSelectionEnd.Value ? _dahuaTimelineSelectionEnd.Value : _dahuaTimelineSelectionStart.Value;
            DahuaRecordItem? startRecord = FindDahuaRecordAt(start, GetSelectedDahuaArchiveChannel());
            DahuaRecordItem? endRecord = FindDahuaRecordAt(finish, GetSelectedDahuaArchiveChannel());
            if (finish <= start || startRecord == null || endRecord == null || !ReferenceEquals(startRecord, endRecord))
            {
                _dahuaTimelineSelectionStart = null;
                _dahuaTimelineSelectionEnd = null;
                ViewModel.Dahua.PlaybackSyncStatusText = "Zaznaczenie musi mieścić się w jednym zielonym, ciągłym fragmencie nagrania. Spróbuj ponownie.";
                RefreshDahuaRecordingTimeline();
                e.Handled = true;
                return;
            }
            DahuaStartDatePicker.SelectedDate = start.Date;
            DahuaStartTimeBox.Text = start.ToString("HH:mm:ss");
            DahuaEndDatePicker.SelectedDate = finish.Date;
            UpdateDahuaPlaybackSliderRange(keepPlayheadVisible: false);
            DahuaEndTimeBox.Text = finish.ToString("HH:mm:ss");
            ViewModel.Dahua.PlaybackSyncStatusText = $"Zaznaczono {start:HH:mm:ss} – {finish:HH:mm:ss} ({(finish-start).TotalMinutes:0.0} min). Możesz odtworzyć lub pobrać fragment.";
            RefreshDahuaRecordingTimeline();
            e.Handled = true;
        }

        private void DahuaRecordingTimelineCanvas_LostMouseCapture(object sender, MouseEventArgs e)
        {
            _dahuaTimelineIsSelecting = false;
            _dahuaTimelineDragStarted = false;
        }

        private void DahuaPlayFromTimeline_Click(object sender, RoutedEventArgs e)
        {
            DateTime? start = _dahuaTimelineSelectionStart.HasValue && _dahuaTimelineSelectionEnd.HasValue
                ? (_dahuaTimelineSelectionStart.Value <= _dahuaTimelineSelectionEnd.Value ? _dahuaTimelineSelectionStart : _dahuaTimelineSelectionEnd)
                : GetDahuaStartDateTime();
            DateTime? end = _dahuaTimelineSelectionStart.HasValue && _dahuaTimelineSelectionEnd.HasValue
                ? (_dahuaTimelineSelectionStart.Value <= _dahuaTimelineSelectionEnd.Value ? _dahuaTimelineSelectionEnd : _dahuaTimelineSelectionStart)
                : null;
            if (start == null) return;
            DahuaRecordItem? record = FindDahuaRecordAt(start.Value, GetSelectedDahuaArchiveChannel());
            if (record == null)
            {
                MessageBox.Show(this, "Wskaż kliknięciem zielony fragment nagrania albo zaznacz go przeciągnięciem.", "Brak nagrania", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (end == null || end.Value <= start.Value) end = record.EndTime;
            DahuaStartDatePicker.SelectedDate = start.Value.Date;
            DahuaStartTimeBox.Text = start.Value.ToString("HH:mm:ss");
            DahuaEndDatePicker.SelectedDate = end.Value.Date;
            DahuaEndTimeBox.Text = end.Value.ToString("HH:mm:ss");
            DahuaStartSynchronizedPlayback_Click(sender, e);
        }

        private void DahuaRecordingTimelineCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (DahuaRecordingTimelineCanvas.ActualWidth <= 0) return;
            const double left = 24;
            double usableWidth = Math.Max(1, DahuaRecordingTimelineCanvas.ActualWidth - left - 8);
            Point pointer = e.GetPosition(DahuaRecordingTimelineCanvas);
            double pointerRatio = Math.Clamp((pointer.X - left) / usableWidth, 0, 1);
            double oldVisibleHours = 24.0 / _dahuaRecordingTimelineZoom;
            double oldStart = Math.Clamp(_dahuaRecordingTimelineStartHour, 0, 24.0 - oldVisibleHours);
            double anchorHour = oldStart + pointerRatio * oldVisibleHours;

            double factor = e.Delta > 0 ? 1.25 : 1.0 / 1.25;
            _dahuaRecordingTimelineZoom = Math.Clamp(_dahuaRecordingTimelineZoom * factor, 1.0, 12.0);
            double newVisibleHours = 24.0 / _dahuaRecordingTimelineZoom;
            _dahuaRecordingTimelineStartHour = Math.Clamp(anchorHour - pointerRatio * newVisibleHours, 0, 24.0 - newVisibleHours);

            RefreshDahuaRecordingTimeline();
            UpdateDahuaPlaybackSliderRange(keepPlayheadVisible: false);
            e.Handled = true;
        }

        private void DahuaRecordingTimelineCanvas_ResetZoom(object sender, MouseButtonEventArgs e)
        {
            _dahuaRecordingTimelineZoom = 1.0;
            _dahuaRecordingTimelineStartHour = 0.0;
            RefreshDahuaRecordingTimeline();
            UpdateDahuaPlaybackSliderRange(keepPlayheadVisible: false);
            e.Handled = true;
        }

        private void DahuaRecordingTimelineCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_currentDahuaDevice == null || DahuaRecordingTimelineCanvas.ActualWidth <= 0) return;
            DateTime pressedTime = GetDahuaTimelineTime(e.GetPosition(DahuaRecordingTimelineCanvas));
            int channel = GetSelectedDahuaArchiveChannel();
            if (FindDahuaRecordAt(pressedTime, channel) == null)
            {
                ViewModel.Dahua.PlaybackSyncStatusText = ViewModel.Dahua.ArchiveRecords.Count == 0
                    ? "Najpierw wyszukaj nagrania dla wybranego kanału i dnia. Zielone pasy oznaczają dostępne nagrania."
                    : $"W tym miejscu ({pressedTime:HH:mm:ss}) nie ma nagrania. Kliknij lub przeciągnij po zielonym pasie.";
                return;
            }
            _dahuaTimelinePointerDown = e.GetPosition(DahuaRecordingTimelineCanvas);
            _dahuaTimelineSelectionStart = pressedTime;
            _dahuaTimelineSelectionEnd = pressedTime;
            _dahuaTimelineIsSelecting = true;
            _dahuaTimelineDragStarted = false;
            DahuaRecordingTimelineCanvas.CaptureMouse();
            e.Handled = true;
        }

        private void DahuaArchiveSpeedBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_dahuaUpdatingArchiveSpeed || !IsLoaded || DahuaMultiPlaybackHandles.Count == 0 ||
                DahuaArchiveSpeedBox.SelectedItem is not ComboBoxItem item ||
                !double.TryParse(item.Tag?.ToString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double requestedSpeed))
                return;

            double elapsed = _dahuaPlaybackElapsedBeforePause;
            if (!_dahuaSynchronizedPlaybackPaused)
                elapsed += (DateTime.Now - _dahuaPlaybackSegmentStartedAt).TotalSeconds * _dahuaArchivePlaybackSpeed;

            int succeeded = ViewModel.Dahua.SetArchivePlaybackSpeed(requestedSpeed);

            if (succeeded != DahuaMultiPlaybackHandles.Count)
            {
                _dahuaArchivePlaybackSpeed = 1.0;
                _dahuaUpdatingArchiveSpeed = true;
                DahuaArchiveSpeedBox.SelectedIndex = 2;
                _dahuaUpdatingArchiveSpeed = false;
                ViewModel.Dahua.PlaybackSyncStatusText = "Rejestrator nie przyjął zmiany tempa. Przywrócono 1×.";
                return;
            }

            _dahuaPlaybackElapsedBeforePause = Math.Clamp(elapsed, 0, DahuaPlaybackPositionSlider.Maximum);
            _dahuaPlaybackSegmentStartedAt = DateTime.Now;
            _dahuaArchivePlaybackSpeed = requestedSpeed;
            ViewModel.Dahua.PlaybackSyncStatusText = $"Tempo odtwarzania: {requestedSpeed.ToString("0.##", CultureInfo.InvariantCulture)}× ({succeeded} kanałów)";
        }

        private void SeekDahuaSynchronizedPlayback(int offsetSeconds)
        {
            offsetSeconds = (int)Math.Clamp(offsetSeconds, 0, GetDahuaPlaybackDurationSeconds());
            int succeeded = ViewModel.Dahua.SeekArchivePlayback(offsetSeconds);
            if (succeeded != DahuaMultiPlaybackHandles.Count)
            {
                ViewModel.Dahua.PlaybackSyncStatusText = $"Przewijanie nie powiodło się ({succeeded}/{DahuaMultiPlaybackHandles.Count}).";
                return;
            }
            _dahuaPlaybackElapsedBeforePause = offsetSeconds;
            _dahuaPlaybackSegmentStartedAt = DateTime.Now;
            UpdateDahuaPlaybackSliderRange(keepPlayheadVisible: true);
            DahuaPlaybackPositionSlider.Value = Math.Clamp(offsetSeconds, DahuaPlaybackPositionSlider.Minimum, DahuaPlaybackPositionSlider.Maximum);
            ViewModel.Dahua.PlaybackPositionText = $"{_dahuaPlaybackRangeStart.AddSeconds(offsetSeconds):HH:mm:ss} / {_dahuaPlaybackRangeEnd:HH:mm:ss}";
            RefreshDahuaRecordingTimeline();
            ViewModel.Dahua.PlaybackSyncStatusText = $"Przewinięto kanały: {succeeded}/{DahuaMultiPlaybackHandles.Count}";
        }

        private void DahuaPlaybackPositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_dahuaIsSeekingPlayback || DahuaMultiPlaybackHandles.Count == 0) return;

            double offset = Math.Clamp(e.NewValue, 0, GetDahuaPlaybackDurationSeconds());
            DateTime previewTime = _dahuaPlaybackRangeStart.AddSeconds(offset);
            ViewModel.Dahua.PlaybackPositionText = $"{previewTime:HH:mm:ss} / {_dahuaPlaybackRangeEnd:HH:mm:ss}";

            DateTime selectedDay = (DahuaStartDatePicker.SelectedDate ?? previewTime.Date).Date;
            if (previewTime.Date != selectedDay)
                DahuaStartDatePicker.SelectedDate = previewTime.Date;

            double visibleHours = 24.0 / _dahuaRecordingTimelineZoom;
            double hour = previewTime.TimeOfDay.TotalHours;
            double viewStart = Math.Clamp(_dahuaRecordingTimelineStartHour, 0, 24.0 - visibleHours);
            if (hour < viewStart || hour > viewStart + visibleHours)
                _dahuaRecordingTimelineStartHour = Math.Clamp(
                    hour - visibleHours * 0.15, 0, 24.0 - visibleHours);

            RefreshDahuaRecordingTimeline();
        }

        private void DahuaPlaybackSlider_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (DahuaMultiPlaybackHandles.Count > 0) _dahuaIsSeekingPlayback = true;
        }

        private void DahuaPlaybackSlider_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (_dahuaIsSeekingPlayback && DahuaMultiPlaybackHandles.Count > 0) CommitDahuaPlaybackSliderSeek();
        }

        private void DahuaPlaybackSlider_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => CommitDahuaPlaybackSliderSeek();

        private void CommitDahuaPlaybackSliderSeek()
        {
            if (!_dahuaIsSeekingPlayback) return;
            _dahuaIsSeekingPlayback = false;
            if (DahuaMultiPlaybackHandles.Count == 0) return;
            int offsetSeconds = (int)Math.Clamp(Math.Round(DahuaPlaybackPositionSlider.Value), 0, GetDahuaPlaybackDurationSeconds());
            SeekDahuaSynchronizedPlayback(offsetSeconds);
        }

        private void DahuaPlaybackUiTimer_Tick(object? sender, EventArgs e)
        {
            if (_dahuaIsSeekingPlayback || _dahuaSynchronizedPlaybackPaused || DahuaMultiPlaybackHandles.Count == 0) return;
            double elapsed = GetDahuaPlaybackElapsedSeconds();
            DateTime playheadTime = _dahuaPlaybackRangeStart.AddSeconds(elapsed);
            DateTime selectedDay = (DahuaStartDatePicker.SelectedDate ?? _dahuaPlaybackRangeStart.Date).Date;
            if (playheadTime.Date != selectedDay)
            {
                selectedDay = playheadTime.Date;
                DahuaStartDatePicker.SelectedDate = selectedDay;
            }

            double visibleHours = 24.0 / _dahuaRecordingTimelineZoom;
            double playheadHour = (playheadTime - selectedDay).TotalHours;
            double viewStart = Math.Clamp(_dahuaRecordingTimelineStartHour, 0, 24.0 - visibleHours);
            if (playheadHour < viewStart || playheadHour > viewStart + visibleHours)
                _dahuaRecordingTimelineStartHour = Math.Clamp(
                    playheadHour - visibleHours * 0.15, 0, 24.0 - visibleHours);

            UpdateDahuaPlaybackSliderRange(keepPlayheadVisible: false);
            DahuaPlaybackPositionSlider.Value = Math.Clamp(elapsed, DahuaPlaybackPositionSlider.Minimum, DahuaPlaybackPositionSlider.Maximum);
            ViewModel.Dahua.PlaybackPositionText = $"{playheadTime:HH:mm:ss} / {_dahuaPlaybackRangeEnd:HH:mm:ss}";
            RefreshDahuaRecordingTimeline();
            if (elapsed >= GetDahuaPlaybackDurationSeconds())
            {
                _dahuaPlaybackUiTimer.Stop();
                ViewModel.Dahua.PlaybackSyncStatusText = "Koniec zakresu archiwum";
            }
        }

        private void DahuaToggleSynchronizedPlayback_Click(object sender, RoutedEventArgs e)
        {
            if (DahuaMultiPlaybackHandles.Count == 0) return;
            bool pause = !_dahuaSynchronizedPlaybackPaused;
            bool synchronized = ViewModel.Dahua.SetArchivePlaybackPaused(pause);

            if (!synchronized)
            {
                ViewModel.Dahua.PlaybackSyncStatusText = "Nie udało się zsynchronizować sterowania";
                return;
            }
            if (pause)
            {
                _dahuaPlaybackElapsedBeforePause += (DateTime.Now - _dahuaPlaybackSegmentStartedAt).TotalSeconds * _dahuaArchivePlaybackSpeed;
                _dahuaSynchronizedPlaybackPaused = true;
                _dahuaPlaybackUiTimer.Stop();
            }
            else
            {
                _dahuaPlaybackSegmentStartedAt = DateTime.Now;
                _dahuaSynchronizedPlaybackPaused = false;
                _dahuaPlaybackUiTimer.Start();
            }
            ViewModel.Dahua.PlaybackSyncStatusText = pause ? "Wstrzymano wszystkie kanały" : "Wznowiono wszystkie kanały";
        }

        private void DahuaStopPlaybackHandlesOnly()
        {
            foreach (IntPtr handle in DahuaMultiPlaybackHandles)
                ViewModel.Dahua.StopArchivePlayback(handle);
            foreach (var slot in _dahuaMultiViewSlots) slot.PlaybackHandle = IntPtr.Zero;
        }

        private void DahuaStopSynchronizedPlayback()
        {
            _dahuaPlaybackUiTimer.Stop();
            DahuaStopPlaybackHandlesOnly();
            _dahuaSynchronizedPlaybackPaused = false;
            _dahuaPlaybackElapsedBeforePause = 0;
            _dahuaArchivePlaybackSpeed = 1.0;
            if (DahuaArchiveSpeedBox != null)
            {
                _dahuaUpdatingArchiveSpeed = true;
                DahuaArchiveSpeedBox.SelectedIndex = 2;
                _dahuaUpdatingArchiveSpeed = false;
            }
            if (DahuaPlaybackPositionSlider != null)
            {
                DahuaPlaybackPositionSlider.IsEnabled = false;
                DahuaPlaybackPositionSlider.Value = 0;
            }
            ViewModel.Dahua.PlaybackPositionText = "00:00:00 / 00:00:00";
            RefreshDahuaRecordingTimeline();
        }

        private void DahuaStopSynchronizedPlayback_Click(object sender, RoutedEventArgs e)
        {
            DahuaStopSynchronizedPlayback();
            ViewModel.Dahua.PlaybackSyncStatusText = "Odtwarzanie zatrzymane";
            ViewModel.Dahua.StatusText = "Zatrzymano zsynchronizowane odtwarzanie archiwum.";
        }

        #endregion

        #region Snapshot, OCR & Archive Download to Main Player

        private string? CaptureDahuaFrameToFile()
        {
            DahuaMultiViewSlot? activeSlot = _dahuaMultiViewSlots.FirstOrDefault(slot =>
                slot.Channel == _dahuaCurrentChannel && (slot.StreamHandle != IntPtr.Zero || slot.PlaybackHandle != IntPtr.Zero));
            activeSlot ??= _dahuaMultiViewSlots.FirstOrDefault(slot =>
                slot.StreamHandle != IntPtr.Zero || slot.PlaybackHandle != IntPtr.Zero);

            if (activeSlot == null && _dahuaRealPlayHandle == IntPtr.Zero && _dahuaPlaybackHandle == IntPtr.Zero)
            {
                MessageBox.Show(this, "Najpierw uruchom strumień wideo.", "Brak strumienia", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }

            IntPtr handle = activeSlot != null
                ? (activeSlot.StreamHandle != IntPtr.Zero ? activeSlot.StreamHandle : activeSlot.PlaybackHandle)
                : (_dahuaRealPlayHandle != IntPtr.Zero ? _dahuaRealPlayHandle : _dahuaPlaybackHandle);
            int captureChannel = activeSlot?.Channel ?? _dahuaCurrentChannel;

            try
            {
                string folder = Path.Combine(AppContext.BaseDirectory, "zrzuty");
                Directory.CreateDirectory(folder);
                string filename = $"Dahua_CH{captureChannel}_{DateTime.Now:yyyyMMdd_HHmmssfff}.jpg";
                string fullPath = Path.Combine(folder, filename);

                if (DahuaSessionService.CapturePicture(handle, fullPath))
                {
                    ViewModel.Dahua.StatusText = $"Zapisano zrzut: {filename}";
                    RecordRecentScreenshot(fullPath);
                    return fullPath;
                }
                ViewModel.Dahua.StatusText = "Błąd zapisu zrzutu z biblioteki Dahua NetSDK.";
                return null;
            }
            catch (Exception ex)
            {
                ViewModel.Dahua.StatusText = $"Wyjątek podczas zapisu klatki: {ex.Message}";
                return null;
            }
        }

        private void DahuaSnapshot_Click(object sender, RoutedEventArgs e) => CaptureDahuaFrameToFile();

        private void DahuaOcrFromLive_Click(object sender, RoutedEventArgs e)
        {
            string? framePath = CaptureDahuaFrameToFile();
            if (string.IsNullOrEmpty(framePath)) return;
            try
            {
                var frame = new BitmapImage();
                frame.BeginInit();
                frame.CacheOption = BitmapCacheOption.OnLoad;
                frame.UriSource = new Uri(framePath);
                frame.EndInit();
                frame.Freeze();
                StartInlinePlateOcr(frame, 0, fromDahua: true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Błąd otwarcia modułu OCR: {ex.Message}", "Błąd OCR", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void DahuaArchiveChannelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ClearDahuaTimelineSelection();
            _dahuaArchiveAvailableDays.Clear();
            _dahuaRecorderAvailableDays.Clear();
            if (_currentDahuaDevice != null)
            {
                foreach (DateTime day in DahuaSession.GetCachedAvailabilityForChannel(_currentDahuaDevice.Id, GetSelectedDahuaArchiveChannel()))
                    _dahuaRecorderAvailableDays.Add(day);
            }
            RefreshOpenDahuaArchiveCalendars();
            RefreshDahuaArchiveSelectedChannelsSummary();
            foreach (DatePicker picker in new[] { DahuaStartDatePicker, DahuaEndDatePicker })
                LoadDahuaRecorderAvailabilityForVisibleMonth(picker);
        }

        private void DahuaArchiveDatePicker_CalendarOpened(object sender, RoutedEventArgs e)
        {
            if (sender is not DatePicker picker) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                picker.ApplyTemplate();
                if (picker.Template.FindName("PART_Popup", picker) is not Popup popup || popup.Child is not DependencyObject popupRoot)
                    return;
                Calendar? calendar = popupRoot as Calendar ?? FindDahuaVisualChild<Calendar>(popupRoot);
                if (calendar == null) return;

                if (_dahuaHookedArchiveCalendars.Add(calendar))
                {
                    calendar.DisplayDateChanged += (_, _) =>
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            RefreshDahuaCalendarButtons(calendar);
                            _ = LoadDahuaRecorderAvailabilityForMonthAsync(calendar.DisplayDate);
                        }));
                    };
                }
                RefreshDahuaCalendarButtons(calendar);
                _ = LoadDahuaRecorderAvailabilityForMonthAsync(calendar.DisplayDate);
            }));
        }

        private void RefreshOpenDahuaArchiveCalendars()
        {
            RefreshDahuaDatePickerCalendar(DahuaStartDatePicker);
            RefreshDahuaDatePickerCalendar(DahuaEndDatePicker);
        }

        private void LoadDahuaRecorderAvailabilityForVisibleMonth(DatePicker picker)
        {
            if (picker.Template.FindName("PART_Popup", picker) is Popup popup && popup.IsOpen && popup.Child is DependencyObject popupRoot)
            {
                Calendar? calendar = popupRoot as Calendar ?? FindDahuaVisualChild<Calendar>(popupRoot);
                if (calendar != null) _ = LoadDahuaRecorderAvailabilityForMonthAsync(calendar.DisplayDate);
            }
        }

        private async Task LoadDahuaRecorderAvailabilityForMonthAsync(DateTime displayedDate)
        {
            if (_currentDahuaDevice == null || DahuaArchiveChannelBox.SelectedItem == null) return;
            int channel = GetSelectedDahuaArchiveChannel();
            DateTime monthStart = new(displayedDate.Year, displayedDate.Month, 1);
            string deviceId = _currentDahuaDevice.Id;

            if (DahuaSession.TryGetCachedMonthAvailability(deviceId, channel, monthStart, out var cachedDays) && cachedDays != null)
            {
                _dahuaRecorderAvailableDays.UnionWith(cachedDays);
                RefreshOpenDahuaArchiveCalendars();
                ViewModel.Dahua.CalendarAvailabilityStatusText = $"Dostępność pobrana z pamięci: {cachedDays.Count} dni w {monthStart:MM.yyyy}.";
                return;
            }
            if (!DahuaSession.TryBeginMonthAvailabilityQuery(deviceId, channel, monthStart)) return;

            ViewModel.Dahua.CalendarAvailabilityStatusText = $"Sprawdzanie dostępności nagrań: {monthStart:MM.yyyy}, kanał {channel}…";

            try
            {
                if (!DahuaSession.IsConnectedTo(_currentDahuaDevice))
                {
                    _dahuaConnectForCalendarAvailability = true;
                    try { await DahuaConnectAsync(); }
                    finally { _dahuaConnectForCalendarAvailability = false; }
                    if (!DahuaSession.IsConnectedTo(_currentDahuaDevice))
                        throw new InvalidOperationException("Nie udało się połączyć z wybranym rejestratorem.");
                }

                HashSet<DateTime> days = await DahuaSession.QueryMonthAvailabilityAsync(deviceId, channel, monthStart);
                if (_currentDahuaDevice?.Id != deviceId || GetSelectedDahuaArchiveChannel() != channel) return;

                _dahuaRecorderAvailableDays.UnionWith(days);
                RefreshOpenDahuaArchiveCalendars();
                ViewModel.Dahua.CalendarAvailabilityStatusText = days.Count == 0
                    ? $"Brak nagrań w {monthStart:MM.yyyy} (kanał {channel})."
                    : $"Nagrania w {days.Count} dniach miesiąca {monthStart:MM.yyyy} (kanał {channel}).";
            }
            catch (Exception ex)
            {
                if (_currentDahuaDevice?.Id == deviceId && GetSelectedDahuaArchiveChannel() == channel)
                    ViewModel.Dahua.CalendarAvailabilityStatusText = $"Nie udało się pobrać dostępności: {ex.Message}";
            }
            finally
            {
                DahuaSession.EndMonthAvailabilityQuery(deviceId, channel, monthStart);
            }
        }

        private void RefreshDahuaDatePickerCalendar(DatePicker picker)
        {
            if (picker.Template.FindName("PART_Popup", picker) is Popup popup && popup.IsOpen && popup.Child is DependencyObject popupRoot)
            {
                Calendar? calendar = popupRoot as Calendar ?? FindDahuaVisualChild<Calendar>(popupRoot);
                if (calendar != null) RefreshDahuaCalendarButtons(calendar);
            }
        }

        private void RefreshDahuaCalendarButtons(Calendar calendar)
        {
            foreach (CalendarDayButton dayButton in FindDahuaVisualChildren<CalendarDayButton>(calendar))
            {
                dayButton.ClearValue(Control.BackgroundProperty);
                dayButton.ClearValue(Control.ForegroundProperty);
                dayButton.ClearValue(Control.BorderBrushProperty);
                dayButton.ClearValue(Control.BorderThicknessProperty);
                dayButton.ClearValue(Control.FontWeightProperty);
                dayButton.ClearValue(ToolTipService.ToolTipProperty);

                if (dayButton.DataContext is DateTime date)
                {
                    bool foundInLastSearch = _dahuaArchiveAvailableDays.Contains(date.Date);
                    bool availableOnRecorder = _dahuaRecorderAvailableDays.Contains(date.Date);
                    if ((foundInLastSearch || availableOnRecorder) && calendar.SelectedDate?.Date != date.Date)
                    {
                        dayButton.ToolTip = foundInLastSearch ? $"Znaleziono nagrania: {date:dd.MM.yyyy}" : $"Rejestrator zgłasza nagrania: {date:dd.MM.yyyy}";
                        dayButton.Background = foundInLastSearch ? new SolidColorBrush(Color.FromRgb(22, 163, 74)) : new SolidColorBrush(Color.FromRgb(20, 83, 45));
                        dayButton.Foreground = Brushes.White;
                        dayButton.BorderBrush = foundInLastSearch ? new SolidColorBrush(Color.FromRgb(134, 239, 172)) : new SolidColorBrush(Color.FromRgb(74, 222, 128));
                        dayButton.BorderThickness = new Thickness(1);
                        dayButton.FontWeight = FontWeights.Bold;
                    }
                }
            }
        }

        private static T? FindDahuaVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match) return match;
                T? nested = FindDahuaVisualChild<T>(child);
                if (nested != null) return nested;
            }
            return null;
        }

        private static IEnumerable<T> FindDahuaVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match) yield return match;
                foreach (T nested in FindDahuaVisualChildren<T>(child)) yield return nested;
            }
        }

        private DateTime? GetDahuaStartDateTime()
        {
            if (DahuaStartDatePicker.SelectedDate == null) return null;
            if (!TimeSpan.TryParse(DahuaStartTimeBox.Text.Trim(), out var time) || time < TimeSpan.Zero || time >= TimeSpan.FromDays(1)) return null;
            return DahuaStartDatePicker.SelectedDate.Value.Date + time;
        }

        private DateTime? GetDahuaEndDateTime()
        {
            if (DahuaEndDatePicker.SelectedDate == null) return null;
            if (!TimeSpan.TryParse(DahuaEndTimeBox.Text.Trim(), out var time) || time < TimeSpan.Zero || time >= TimeSpan.FromDays(1)) return null;
            return DahuaEndDatePicker.SelectedDate.Value.Date + time;
        }

        private int GetSelectedDahuaArchiveChannel() =>
            DahuaArchiveChannelBox.SelectedItem is ComboBoxItem item && item.Tag is int ch ? ch : _dahuaCurrentChannel;

        private async void DahuaSearchArchive_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDahuaDevice == null) return;
            var start = GetDahuaStartDateTime();
            var end = GetDahuaEndDateTime();
            if (!DahuaSessionService.ValidateArchiveRange(start, end, out string error))
            {
                MessageBox.Show(this, error, "Błędny zakres dat", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!DahuaSession.IsConnectedTo(_currentDahuaDevice))
            {
                if (!await DahuaConnectAsync() || !DahuaSession.IsConnectedTo(_currentDahuaDevice))
                    return;
            }

            int channel = GetSelectedDahuaArchiveChannel();

            ClearDahuaTimelineSelection();
            ViewModel.Dahua.DownloadStatusText = $"Wyszukiwanie nagrań: {_currentDahuaDevice.Name}, kanał {channel}…";
            ViewModel.Dahua.ArchiveRecords.Clear();
            RefreshDahuaRecordingTimeline();
            _dahuaArchiveAvailableDays.Clear();
            RefreshOpenDahuaArchiveCalendars();

            try
            {
                List<DahuaRecordItem> records = await ViewModel.Dahua.SearchArchiveRecordsAsync(channel, start!.Value, end!.Value);
                RefreshDahuaRecordingTimeline();
                foreach (DahuaRecordItem record in records)
                {
                    for (DateTime day = record.StartTime.Date; day <= record.EndTime.Date; day = day.AddDays(1))
                    {
                        _dahuaArchiveAvailableDays.Add(day);
                        _dahuaRecorderAvailableDays.Add(day);
                    }
                }
                RefreshOpenDahuaArchiveCalendars();
            }
            catch (Exception ex)
            {
                ViewModel.Dahua.DownloadStatusText = "Błąd wyszukiwania archiwum.";
                MessageBox.Show(this, $"Nie udało się wyszukać nagrań: {ex.Message}", "Archiwum Dahua", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private DahuaRecordItem? GetSelectedDahuaRecord() => DahuaArchiveRecordsGrid.SelectedItem as DahuaRecordItem;

        private void ApplyDahuaRecordRange(DahuaRecordItem record)
        {
            DahuaStartDatePicker.SelectedDate = record.StartTime.Date;
            DahuaStartTimeBox.Text = record.StartTime.ToString("HH:mm:ss");
            DahuaEndDatePicker.SelectedDate = record.EndTime.Date;
            DahuaEndTimeBox.Text = record.EndTime.ToString("HH:mm:ss");
            for (int i = 0; i < DahuaArchiveChannelBox.Items.Count; i++)
            {
                if (DahuaArchiveChannelBox.Items[i] is ComboBoxItem item && item.Tag is int ch && ch == record.Channel)
                {
                    DahuaArchiveChannelBox.SelectedIndex = i;
                    break;
                }
            }
        }

        private void DahuaPlaySelectedRecord_Click(object sender, RoutedEventArgs e)
        {
            var record = GetSelectedDahuaRecord();
            if (record == null) { MessageBox.Show(this, "Najpierw zaznacz nagranie na liście.", "Brak zaznaczenia", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            ApplyDahuaRecordRange(record);
            SelectDahuaModernChannel(record.Channel);
            DahuaStartSynchronizedPlayback_Click(sender, e);
        }

        private void DahuaLoadSelectedRecord_Click(object sender, RoutedEventArgs e)
        {
            var record = GetSelectedDahuaRecord();
            if (record == null) { MessageBox.Show(this, "Najpierw zaznacz nagranie na liście.", "Brak zaznaczenia", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            ApplyDahuaRecordRange(record);
            DahuaLoadToPlayer_Click(sender, e);
        }

        private async void DahuaLoadToPlayer_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDahuaDevice == null) return;
            var start = GetDahuaStartDateTime();
            var end = GetDahuaEndDateTime();
            if (!DahuaSessionService.ValidateArchiveRange(start, end, out string error))
            {
                MessageBox.Show(this, error, "Błędny zakres dat", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int channel = GetSelectedDahuaArchiveChannel();
            if (!DahuaSession.IsConnectedTo(_currentDahuaDevice))
            {
                if (!await DahuaConnectAsync() || !DahuaSession.IsConnectedTo(_currentDahuaDevice))
                    return;
            }

            _dahuaArchiveDownloadCts?.Dispose();
            _dahuaArchiveDownloadCts = new CancellationTokenSource();
            ViewModel.Dahua.IsDownloadBusy = true;
            DahuaCancelDownloadButton.IsEnabled = true;
            DahuaDownloadTimelineSelectionButton.IsEnabled = false;

            try
            {
                string ffmpegPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
                var progress = new Progress<string>(status =>
                {
                    ViewModel.Dahua.DownloadStatusText = status;
                    ViewModel.Dahua.PlaybackSyncStatusText = status;
                });

                string? targetFilePath = await DahuaSession.DownloadAndConvertToMp4Async(
                    _currentDahuaDevice, channel, start!.Value, end!.Value, ffmpegPath, progress,
                    _dahuaArchiveDownloadCts?.Token ?? CancellationToken.None);

                if (!string.IsNullOrEmpty(targetFilePath) && File.Exists(targetFilePath))
                {
                    ViewModel.Dahua.DownloadStatusText = "Wczytano do odtwarzacza!";
                    await OpenDahuaStreamAsync(targetFilePath);
                }
                else
                {
                    ViewModel.Dahua.DownloadStatusText = "Nie znaleziono nagrania na rejestratorze w wybranym przedziale czasu.";
                }
            }
            catch (OperationCanceledException)
            {
                ViewModel.Dahua.DownloadStatusText = "Pobieranie anulowano.";
            }
            catch (Exception ex)
            {
                ViewModel.Dahua.DownloadStatusText = $"Błąd pobierania: {ex.Message}";
                MessageBox.Show(this, $"Błąd pobierania nagrania: {ex.Message}", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ViewModel.Dahua.IsDownloadBusy = false;
                DahuaCancelDownloadButton.IsEnabled = false;
                DahuaDownloadTimelineSelectionButton.IsEnabled = true;
                _dahuaArchiveDownloadCts?.Dispose();
                _dahuaArchiveDownloadCts = null;
            }
        }

        private async void DahuaDownloadTimelineSelection_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDahuaDevice == null || _dahuaTimelineSelectionStart == null || _dahuaTimelineSelectionEnd == null)
            {
                MessageBox.Show(this, "Najpierw zaznacz fragment nagrania na osi czasu.", "Brak zaznaczenia",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            DateTime start = _dahuaTimelineSelectionStart.Value <= _dahuaTimelineSelectionEnd.Value
                ? _dahuaTimelineSelectionStart.Value : _dahuaTimelineSelectionEnd.Value;
            DateTime end = _dahuaTimelineSelectionStart.Value <= _dahuaTimelineSelectionEnd.Value
                ? _dahuaTimelineSelectionEnd.Value : _dahuaTimelineSelectionStart.Value;
            int channel = GetSelectedDahuaArchiveChannel();

            if (!DahuaSessionService.ValidateArchiveRange(start, end, out string rangeError))
            {
                MessageBox.Show(this, rangeError, "Nieprawidłowy zakres", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool coveredByRecording = ViewModel.Dahua.ArchiveRecords.Any(record =>
                record.Channel == channel && start >= record.StartTime && end <= record.EndTime);
            if (!coveredByRecording)
            {
                MessageBox.Show(this,
                    "Wybrany przedział nie mieści się w całości w jednym ciągłym nagraniu. Zaznacz fragment bez przerw.",
                    "Nieprawidłowe zaznaczenie", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string safeChannelName = string.Concat(_currentDahuaDevice.GetChannelName(channel)
                .Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)).Replace(' ', '_');
            string fileStem = $"Dahua_{safeChannelName}_CH{channel}_{start:yyyyMMdd_HHmmss}-{end:HHmmss}";
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Zapisz zaznaczony fragment nagrania",
                Filter = "Plik MP4 (*.mp4)|*.mp4|Oryginalne archiwum Dahua (*.dav)|*.dav",
                FilterIndex = 1,
                DefaultExt = ".mp4",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = fileStem
            };
            if (dialog.ShowDialog(this) != true) return;

            bool convertToMp4 = dialog.FilterIndex == 1;
            string targetPath = Path.ChangeExtension(dialog.FileName, convertToMp4 ? ".mp4" : ".dav");

            if (!DahuaSession.IsConnectedTo(_currentDahuaDevice))
            {
                if (!await DahuaConnectAsync() || !DahuaSession.IsConnectedTo(_currentDahuaDevice))
                    return;
            }

            _dahuaArchiveDownloadCts?.Dispose();
            _dahuaArchiveDownloadCts = new CancellationTokenSource();
            CancellationToken token = _dahuaArchiveDownloadCts.Token;
            ViewModel.Dahua.IsDownloadBusy = true;
            DahuaCancelDownloadButton.IsEnabled = true;
            DahuaDownloadTimelineSelectionButton.IsEnabled = false;

            try
            {
                string ffmpegPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
                var progress = new Progress<string>(status =>
                {
                    ViewModel.Dahua.DownloadStatusText = status;
                    ViewModel.Dahua.PlaybackSyncStatusText = status;
                });
                string savedPath = await DahuaSession.DownloadArchiveAsync(
                    _currentDahuaDevice, channel, start, end, targetPath, convertToMp4,
                    ffmpegPath, progress, token);

                ViewModel.Dahua.DownloadStatusText =
                    $"Zapisano {Path.GetFileName(savedPath)} ({(end - start).ToString(@"hh\:mm\:ss")}).";
                ViewModel.Dahua.PlaybackSyncStatusText = ViewModel.Dahua.DownloadStatusText;
                ViewModel.Dahua.StatusText = $"Pobrano archiwum kanału {channel}: {start:HH:mm:ss}–{end:HH:mm:ss}.";
            }
            catch (OperationCanceledException)
            {
                ViewModel.Dahua.DownloadStatusText = "Pobieranie anulowano. Plik tymczasowy został usunięty.";
            }
            catch (Exception ex)
            {
                ViewModel.Dahua.DownloadStatusText = $"Błąd pobierania: {ex.Message}";
                MessageBox.Show(this, $"Nie udało się pobrać zaznaczonego fragmentu: {ex.Message}",
                    "Błąd pobierania", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ViewModel.Dahua.IsDownloadBusy = false;
                DahuaCancelDownloadButton.IsEnabled = false;
                DahuaDownloadTimelineSelectionButton.IsEnabled = true;
                _dahuaArchiveDownloadCts?.Dispose();
                _dahuaArchiveDownloadCts = null;
            }
        }

        private void DahuaCancelDownload_Click(object sender, RoutedEventArgs e)
        {
            _dahuaArchiveDownloadCts?.Cancel();
            ViewModel.Dahua.DownloadStatusText = "Anulowanie pobierania…";
        }

        private void DahuaPlayArchiveInWindow_Click(object sender, RoutedEventArgs e)
        {
            SelectDahuaModernChannel(GetSelectedDahuaArchiveChannel());
            DahuaStartSynchronizedPlayback_Click(sender, e);
        }

        #endregion
    }
}
