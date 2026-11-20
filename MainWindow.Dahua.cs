using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
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

namespace VideoPlayer
{
    public partial class MainWindow
    {
        private readonly Forms.PictureBox _dahuaPrimaryPictureBox = new()
        {
            BackColor = System.Drawing.Color.Black,
            Dock = Forms.DockStyle.Fill,
            SizeMode = Forms.PictureBoxSizeMode.Normal
        };

        private List<DahuaDeviceItem> _dahuaDevices = new();
        private DahuaDeviceItem? _currentDahuaDevice;
        private DahuaDeviceItem? _configSelectedDevice;
        private bool _configLoading;
        private string? _connectedDahuaDeviceId;
        private IntPtr _dahuaLoginId = IntPtr.Zero;
        private IntPtr _dahuaRealPlayHandle = IntPtr.Zero;
        private IntPtr _dahuaPlaybackHandle = IntPtr.Zero;
        private bool _dahuaSdkInitialized;
        private int _dahuaCurrentChannel = 1;
        private int _dahuaDeviceChannelCount;
        private HashSet<int>? _dahuaActiveChannelNumbers;
        private readonly List<Button> _dahuaChannelButtons = new();
        private readonly HashSet<DateTime> _dahuaArchiveAvailableDays = new();
        private readonly HashSet<DateTime> _dahuaRecorderAvailableDays = new();
        private readonly HashSet<Calendar> _dahuaHookedArchiveCalendars = new();
        private readonly Dictionary<string, HashSet<DateTime>> _dahuaRecorderAvailabilityCache = new();
        private readonly HashSet<string> _dahuaAvailabilityQueriesInProgress = new();
        private readonly SemaphoreSlim _dahuaArchiveAvailabilitySemaphore = new(1, 1);
        private readonly List<DahuaMultiViewSlot> _dahuaMultiViewSlots = new();
        private readonly List<IntPtr> _dahuaMultiPlaybackHandles = new();
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

        private DahuaNetSdk.fDisConnect? _dahuaDisConnectCallback;
        private DahuaNetSdk.fHaveReConnect? _dahuaHaveReConnectCallback;

        private sealed class DahuaMultiViewSlot
        {
            public int Channel { get; init; }
            public Forms.PictureBox PictureBox { get; init; } = null!;
            public FormsIntegration.WindowsFormsHost Host { get; init; } = null!;
            public Forms.Panel Viewport { get; init; } = null!;
            public double Zoom { get; set; } = 1.0;
            public int PanX { get; set; }
            public int PanY { get; set; }
            public bool IsDragging { get; set; }
            public System.Drawing.Point DragStart { get; set; }
            public System.Drawing.Point PanStart { get; set; }
            public IntPtr StreamHandle { get; set; }
            public IntPtr PlaybackHandle { get; set; }
        }

        private void InitializeDahuaHost()
        {
            _dahuaPrimaryPictureBox.CreateControl();
            DahuaPrimaryVideoHost.Child = _dahuaPrimaryPictureBox;
            InitializeDahuaDatePickers();
            _dahuaPlaybackUiTimer.Tick += DahuaPlaybackUiTimer_Tick;
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
                StatusText.Text = "Plik nagrania Dahua nie istnieje.";
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
            if (!_dahuaSdkInitialized)
            {
                InitializeDahuaSdk();
            }
        }

        private void CleanupDahuaOnClosing()
        {
            _dahuaPlaybackUiTimer.Stop();
            DahuaDisconnect();
            DisposeDahuaMultiViewSlots();
            if (_dahuaSdkInitialized)
            {
                try { DahuaNetSdk.CLIENT_Cleanup(); } catch { }
                _dahuaSdkInitialized = false;
            }
        }

        private static void WriteDahuaSdkDiagnostic(string message)
        {
            try
            {
                string directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VideoPlayer", "logs");
                Directory.CreateDirectory(directory);
                File.AppendAllText(
                    Path.Combine(directory, "dahua-sdk.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
            catch
            {
            }
        }

        private void InitializeDahuaSdk()
        {
            try
            {
                int deviceInfoSize = Marshal.SizeOf<DahuaNetSdk.NET_DEVICEINFO>();
                WriteDahuaSdkDiagnostic($"InitializeSdk started. NET_DEVICEINFO size={deviceInfoSize} bytes; process={Environment.Is64BitProcess switch { true => "x64", false => "x86" }}.");
                if (deviceInfoSize != 53)
                {
                    throw new InvalidOperationException(
                        $"Nieprawidłowy rozmiar NET_DEVICEINFO: {deviceInfoSize} bajtów (oczekiwano 53).");
                }

                var missingDlls = DahuaNetSdk.CheckDependencies();
                if (missingDlls.Count > 0)
                {
                    string missingList = string.Join(", ", missingDlls);
                    DahuaSdkStatusBadge.Text = $"Dahua NetSDK: Brak plików: {missingList}";
                    DahuaSdkStatusBadge.Foreground = Brushes.Orange;
                    DahuaStatusLabel.Text = $"Brak bibliotek DLL: {missingList}. Skopiuj je do folderu programu.";
                    return;
                }

                _dahuaDisConnectCallback = OnDahuaSdkDisconnected;
                _dahuaHaveReConnectCallback = OnDahuaSdkReconnected;

                WriteDahuaSdkDiagnostic("Calling CLIENT_Init.");
                bool initOk = DahuaNetSdk.CLIENT_Init(_dahuaDisConnectCallback, IntPtr.Zero);
                WriteDahuaSdkDiagnostic($"CLIENT_Init returned: {initOk}.");
                if (initOk)
                {
                    _dahuaSdkInitialized = true;
                    DahuaNetSdk.CLIENT_SetAutoReconnect(_dahuaHaveReConnectCallback, IntPtr.Zero);
                    DahuaSdkStatusBadge.Text = "Dahua NetSDK: Zainicjalizowano (x64)";
                    DahuaSdkStatusBadge.Foreground = Brushes.LightGreen;
                    DahuaStatusLabel.Text = "Gotowy do połączenia ze strumieniem IP.";
                }
                else
                {
                    DahuaSdkStatusBadge.Text = "Dahua NetSDK: Błąd CLIENT_Init";
                    DahuaSdkStatusBadge.Foreground = Brushes.Salmon;
                }
            }
            catch (Exception ex)
            {
                DahuaSdkStatusBadge.Text = "Dahua NetSDK: Wyjątek DLL";
                DahuaSdkStatusBadge.Foreground = Brushes.Salmon;
                DahuaStatusLabel.Text = $"Błąd: {ex.Message}";
            }
        }

        private void LoadDahuaDeviceList()
        {
            try
            {
                _dahuaDevices = DahuaConfigService.LoadDevices();
                RefreshDahuaDeviceBindings(selectDevice: _dahuaDevices.FirstOrDefault());
            }
            catch (Exception ex)
            {
                _dahuaDevices = new List<DahuaDeviceItem>();
                DahuaDevicesListBox.ItemsSource = null;
                DahuaConfigDevicesList.ItemsSource = null;
                DahuaStatusLabel.Text = "Nie można wczytać konfiguracji urządzeń. Oryginalny plik nie został nadpisany.";
                MessageBox.Show(this, ex.Message, "Błąd konfiguracji Dahua", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RefreshDahuaDeviceBindings(DahuaDeviceItem? selectDevice)
        {
            _dahuaSyncingDeviceLists = true;
            try
            {
                DahuaDevicesListBox.ItemsSource = null;
                DahuaDevicesListBox.ItemsSource = _dahuaDevices;
                DahuaConfigDevicesList.ItemsSource = null;
                DahuaConfigDevicesList.ItemsSource = _dahuaDevices;

                if (selectDevice != null && _dahuaDevices.Contains(selectDevice))
                {
                    DahuaDevicesListBox.SelectedItem = selectDevice;
                    DahuaConfigDevicesList.SelectedItem = selectDevice;
                }
                else if (_dahuaDevices.Count > 0)
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
            if (_dahuaLoginId != IntPtr.Zero && !string.Equals(_connectedDahuaDeviceId, dev.Id, StringComparison.Ordinal))
                DahuaDisconnect();

            _currentDahuaDevice = dev;
            _dahuaActiveChannelNumbers = null;
            DahuaStopMultiLiveStreams();
            DahuaStopSynchronizedPlayback();
            DisposeDahuaMultiViewSlots();
            BuildDahuaModernChannelChecks();
            _dahuaArchiveAvailableDays.Clear();
            _dahuaRecorderAvailableDays.Clear();
            string selectedAvailabilityPrefix = $"{dev.Id}|{GetSelectedDahuaArchiveChannel()}|";
            foreach (var entry in _dahuaRecorderAvailabilityCache.Where(pair => pair.Key.StartsWith(selectedAvailabilityPrefix, StringComparison.Ordinal)))
                _dahuaRecorderAvailableDays.UnionWith(entry.Value);
            RefreshOpenDahuaArchiveCalendars();
            DahuaArchiveRecordsGrid.ItemsSource = null;
            _currentDahuaDevice.EnsureDefaultChannels(16);
            _currentDahuaDevice.EnsureDefaultPresets();

            _dahuaCurrentChannel = dev.Channel;
            RefreshDahuaChannelLists();
            RefreshDahuaPresetsList();

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

        #region Dahua Device Configuration Sub-tab (replaces DahuaConfigurationWindow)

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
            DahuaConfigPortBox.Text = device.Port.ToString();
            DahuaConfigHttpPortBox.Text = device.HttpPort.ToString();
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
            port = 0;
            httpPort = 0;
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show(this, "Podaj nazwę urządzenia.", "Konfiguracja", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (string.IsNullOrWhiteSpace(address) || Uri.CheckHostName(address.Trim('[', ']')) == UriHostNameType.Unknown)
            {
                MessageBox.Show(this, "Podaj prawidłowy adres IP lub nazwę hosta.", "Konfiguracja", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (!ushort.TryParse(DahuaConfigPortBox.Text.Trim(), out port) || port == 0)
            {
                MessageBox.Show(this, "Port NetSDK musi mieścić się w zakresie 1–65535.", "Konfiguracja", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (!ushort.TryParse(DahuaConfigHttpPortBox.Text.Trim(), out httpPort) || httpPort == 0)
            {
                MessageBox.Show(this, "Port HTTP musi mieścić się w zakresie 1–65535.", "Konfiguracja", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (string.IsNullOrWhiteSpace(DahuaConfigUsernameBox.Text) || string.IsNullOrEmpty(DahuaConfigPasswordBox.Password))
            {
                MessageBox.Show(this, "Wpisz nazwę użytkownika i hasło. Puste hasło nie zostanie zapisane.", "Konfiguracja", MessageBoxButton.OK, MessageBoxImage.Warning);
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

            if (string.Equals(_connectedDahuaDeviceId, _configSelectedDevice.Id, StringComparison.Ordinal))
                DahuaDisconnect();

            _configSelectedDevice.Name = name;
            _configSelectedDevice.IpAddress = address;
            _configSelectedDevice.Port = port;
            _configSelectedDevice.HttpPort = httpPort;
            _configSelectedDevice.SetCredentials(DahuaConfigUsernameBox.Text.Trim(), DahuaConfigPasswordBox.Password);
            DahuaConfigService.SaveDevices(_dahuaDevices);

            DahuaConfigCredentialInfo.Text = "Zapisano. Dane logowania są chronione przez Windows DPAPI.";
            DahuaConfigCredentialInfo.Foreground = Brushes.LightGreen;
            RefreshDahuaDeviceBindings(_configSelectedDevice);
            DahuaStatusLabel.Text = $"Zapisano urządzenie: '{_configSelectedDevice.Name}' ({_configSelectedDevice.IpAddress}:{_configSelectedDevice.Port})";
        }

        private void DahuaConfigAdd_Click(object sender, RoutedEventArgs e)
        {
            var device = new DahuaDeviceItem
            {
                Name = "Nowe urządzenie Dahua",
                IpAddress = string.Empty,
                Port = 37777,
                HttpPort = 80,
                Channel = 1
            };
            device.EnsureDefaultChannels(16);
            _dahuaDevices.Add(device);
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
            if (string.Equals(_connectedDahuaDeviceId, removed.Id, StringComparison.Ordinal))
                DahuaDisconnect();

            _dahuaDevices.Remove(removed);
            DahuaConfigService.SaveDevices(_dahuaDevices);
            RefreshDahuaDeviceBindings(_dahuaDevices.FirstOrDefault());
            DahuaStatusLabel.Text = $"Usunięto urządzenie '{removed.Name}'.";
        }

        #endregion

        #region Channels & Device State Queries

        private void DahuaQueryFromDevice_Click(object sender, RoutedEventArgs e)
        {
            if (_dahuaLoginId == IntPtr.Zero)
            {
                DahuaConnect_Click(sender, e);
                if (_dahuaLoginId == IntPtr.Zero) return;
            }
            QueryDahuaDeviceState(showDialog: true);
        }

        private void QueryDahuaDeviceState(bool showDialog = false)
        {
            if (_currentDahuaDevice == null || _dahuaLoginId == IntPtr.Zero) return;

            try
            {
                WriteDahuaSdkDiagnostic("Manual device read started.");
                int channelCount = _dahuaDeviceChannelCount > 0 ? _dahuaDeviceChannelCount : _currentDahuaDevice.Channels.Count;
                if (channelCount > 0) _currentDahuaDevice.EnsureDefaultChannels(channelCount);

                List<DahuaChannelInfo> remoteNames = DahuaNetSdk.QueryChannelNamesFromDevice(_dahuaLoginId, channelCount);
                int appliedNames = 0;
                if (remoteNames.Count > 0)
                {
                    var changes = remoteNames
                        .Where(remote => !string.IsNullOrWhiteSpace(remote.Name))
                        .Select(remote => new
                        {
                            remote.Number,
                            RemoteName = remote.Name.Trim(),
                            LocalName = _currentDahuaDevice.GetChannelName(remote.Number)
                        })
                        .Where(x => !string.Equals(x.LocalName, x.RemoteName, StringComparison.Ordinal))
                        .ToList();

                    if (changes.Count > 0)
                    {
                        string preview = string.Join(Environment.NewLine,
                            changes.Take(20).Select(x => $"CH {x.Number}: \"{x.LocalName}\"  →  \"{x.RemoteName}\""));
                        if (changes.Count > 20) preview += Environment.NewLine + $"… oraz {changes.Count - 20} kolejnych zmian.";

                        var decision = MessageBox.Show(this,
                            $"Rejestrator zwrócił {remoteNames.Count} nazw kanałów. Wykryto {changes.Count} różnic względem konfiguracji lokalnej.\n\n" +
                            preview + "\n\nCzy zastosować nazwy z rejestratora?\nIdentyfikatory kanałów pozostaną bez zmian.",
                            "Porównanie nazw kanałów Dahua",
                            MessageBoxButton.YesNo, MessageBoxImage.Question);

                        if (decision == MessageBoxResult.Yes)
                        {
                            foreach (var change in changes)
                                _currentDahuaDevice.SetChannelName(change.Number, change.RemoteName);
                            appliedNames = changes.Count;
                        }
                    }
                }

                int presetCount = -1;
                try
                {
                    var remotePresets = DahuaNetSdk.QueryPresetsFromDevice(
                        _currentDahuaDevice.ConnectionAddress, _currentDahuaDevice.HttpPort, _currentDahuaDevice.Username,
                        _currentDahuaDevice.Password, _dahuaCurrentChannel);
                    _currentDahuaDevice.UpdateFromDevicePresets(remotePresets);
                    presetCount = remotePresets.Count;
                }
                catch (Exception presetError)
                {
                    WriteDahuaSdkDiagnostic($"PTZ preset read failed; local list preserved. {presetError}");
                }

                DahuaConfigService.SaveDevices(_dahuaDevices);
                RefreshDahuaChannelLists();
                RefreshDahuaPresetsList();

                string channelMessage = remoteNames.Count > 0
                    ? $"Nazwy kanałów: odczytano {remoteNames.Count}, zastosowano {appliedNames} zmian."
                    : $"Nazwy kanałów: odczyt nieudany; zachowano lokalne etykiety (kanałów: {channelCount}).";
                string presetMessage = presetCount >= 0
                    ? $"Presety PTZ: odczytano {presetCount} pozycji."
                    : "Presety PTZ: odczyt nieudany; zachowano dotychczasową listę.";
                string msg = channelMessage + Environment.NewLine + presetMessage;

                DahuaStatusLabel.Text = msg;
                if (showDialog)
                    MessageBox.Show(this, msg, "Odczyt z urządzenia Dahua", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                WriteDahuaSdkDiagnostic($"Manual device read managed exception: {ex}");
                DahuaStatusLabel.Text = $"Błąd odczytu danych z urządzenia: {ex.Message}";
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
            if (_dahuaLoginId != IntPtr.Zero)
            {
                StartDahuaMultiLiveForSelectedChannels();
            }
            else
            {
                string name = _currentDahuaDevice?.GetChannelName(channelNumber) ?? $"Kanał {channelNumber}";
                DahuaStatusLabel.Text = $"Wybrano: {name} (CH {channelNumber}). Kliknij „Podgląd zaznaczonych” lub „Połącz”.";
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

            _currentDahuaDevice.SetChannelName(_dahuaCurrentChannel, newName);
            DahuaConfigService.SaveDevices(_dahuaDevices);
            RefreshDahuaChannelLists();
            BuildDahuaModernChannelChecks();

            DahuaStatusLabel.Text = $"Zapisano nową nazwę dla Kanału {_dahuaCurrentChannel}: '{newName}'";
            DahuaActiveChannelOverlayText.Text = $"{newName.ToUpper()} (CH {_dahuaCurrentChannel})";
        }

        private void DahuaCustomChannel_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(DahuaCustomChannelBox.Text.Trim(), out int ch) && ch >= 1)
            {
                if (_currentDahuaDevice != null && !_currentDahuaDevice.Channels.Any(c => c.Number == ch))
                {
                    _currentDahuaDevice.SetChannelName(ch, $"Kamera {ch}");
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
                    DahuaPresetNumberBox.Text = preset.PresetNumber.ToString();
                    DahuaPresetNameBox.Text = preset.Name;
                }
            }
        }

        private void GotoDahuaPreset(int presetNumber)
        {
            if (_dahuaLoginId == IntPtr.Zero)
            {
                MessageBox.Show(this, "Najpierw połącz ze strumieniem wideo kamery.", "Brak połączenia", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int channelIndex = Math.Max(0, _dahuaCurrentChannel - 1);
            try
            {
                bool ok = DahuaNetSdk.CLIENT_DHPTZControlEx2(
                    _dahuaLoginId, channelIndex, (uint)DahuaNetSdk.EM_PTZ_COMMAND.POINT_MOVE_CONTROL,
                    0, presetNumber, 0, false, IntPtr.Zero);

                var match = _currentDahuaDevice?.Presets.FirstOrDefault(p => p.PresetNumber == presetNumber);
                string pName = match != null ? match.Name : $"Preset {presetNumber}";
                if (ok)
                {
                    DahuaStatusLabel.Text = $"Kamera PTZ: Przechodzenie do '{pName}' (Preset {presetNumber})…";
                }
                else
                {
                    uint err = DahuaNetSdk.CLIENT_GetLastError();
                    DahuaStatusLabel.Text = $"Błąd przejścia do presetu {presetNumber}: {DahuaNetSdk.GetErrorDescription(err)}";
                }
            }
            catch (Exception ex)
            {
                DahuaStatusLabel.Text = $"Wyjątek PTZ Preset: {ex.Message}";
            }
        }

        private void DahuaGotoPreset_Click(object sender, RoutedEventArgs e)
        {
            if (DahuaPresetsComboBox.SelectedItem is ComboBoxItem item && item.Tag is int pNum)
                GotoDahuaPreset(pNum);
        }

        private void DahuaSaveCurrentPositionAsPreset_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDahuaDevice == null) return;
            if (_dahuaLoginId == IntPtr.Zero || !string.Equals(_connectedDahuaDeviceId, _currentDahuaDevice.Id, StringComparison.Ordinal))
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
                DahuaNetSdk.SetDevicePresetPosition(
                    _currentDahuaDevice.ConnectionAddress, _currentDahuaDevice.HttpPort, _currentDahuaDevice.Username,
                    _currentDahuaDevice.Password, _dahuaCurrentChannel, presetNumber);

                bool nameSaved = true;
                string nameError = string.Empty;
                try
                {
                    DahuaNetSdk.SetDevicePresetName(
                        _currentDahuaDevice.ConnectionAddress, _currentDahuaDevice.HttpPort, _currentDahuaDevice.Username,
                        _currentDahuaDevice.Password, _dahuaCurrentChannel, presetNumber, presetName);
                }
                catch (Exception ex) { nameSaved = false; nameError = ex.Message; }

                string effectiveName = nameSaved ? presetName : $"Preset {presetNumber}";
                var existing = _currentDahuaDevice.Presets.FirstOrDefault(p => p.PresetNumber == presetNumber);
                if (existing != null) existing.Name = effectiveName;
                else _currentDahuaDevice.Presets.Add(new DahuaPresetItem { PresetNumber = presetNumber, Name = effectiveName });

                DahuaConfigService.SaveDevices(_dahuaDevices);
                RefreshDahuaPresetsList();
                DahuaPresetsComboBox.SelectedItem = DahuaPresetsComboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is int n && n == presetNumber);

                if (nameSaved) DahuaStatusLabel.Text = $"Zapisano preset {presetNumber}: {presetName}.";
                else
                {
                    DahuaStatusLabel.Text = $"Pozycja presetu {presetNumber} została zapisana, ale nie udało się ustawić nazwy.";
                    MessageBox.Show(this, $"Pozycja PTZ została zapisana, ale zmiana nazwy nie powiodła się.\n\n{nameError}", "Częściowy zapis presetu", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Nie udało się zapisać pozycji presetu.\n\n{ex.Message}", "Błąd zapisu presetu PTZ", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void DahuaRenameSelectedPreset_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDahuaDevice == null || DahuaPresetsComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not int presetNumber) return;
            string newName = DahuaPresetNameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(newName))
            {
                MessageBox.Show(this, "Wpisz nową nazwę presetu.", "Brak nazwy", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (_dahuaLoginId == IntPtr.Zero || !string.Equals(_connectedDahuaDeviceId, _currentDahuaDevice.Id, StringComparison.Ordinal))
            {
                MessageBox.Show(this, "Najpierw połącz się z urządzeniem.", "Brak połączenia", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                DahuaNetSdk.SetDevicePresetName(
                    _currentDahuaDevice.ConnectionAddress, _currentDahuaDevice.HttpPort, _currentDahuaDevice.Username,
                    _currentDahuaDevice.Password, _dahuaCurrentChannel, presetNumber, newName);

                var preset = _currentDahuaDevice.Presets.FirstOrDefault(p => p.PresetNumber == presetNumber);
                if (preset != null) preset.Name = newName;
                DahuaConfigService.SaveDevices(_dahuaDevices);
                RefreshDahuaPresetsList();
                DahuaStatusLabel.Text = $"Zmieniono nazwę presetu {presetNumber} na '{newName}'.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Błąd zmiany nazwy presetu", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void DahuaRefreshPresetsFromDevice_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDahuaDevice == null) return;
            if (_dahuaLoginId == IntPtr.Zero || !string.Equals(_connectedDahuaDeviceId, _currentDahuaDevice.Id, StringComparison.Ordinal))
            {
                MessageBox.Show(this, "Najpierw połącz się z urządzeniem.", "Brak połączenia", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                var remotePresets = DahuaNetSdk.QueryPresetsFromDevice(
                    _currentDahuaDevice.ConnectionAddress, _currentDahuaDevice.HttpPort, _currentDahuaDevice.Username,
                    _currentDahuaDevice.Password, _dahuaCurrentChannel);
                _currentDahuaDevice.UpdateFromDevicePresets(remotePresets);
                DahuaConfigService.SaveDevices(_dahuaDevices);
                RefreshDahuaPresetsList();
                DahuaStatusLabel.Text = $"Odczytano {remotePresets.Count} presetów dla kanału {_dahuaCurrentChannel}.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Nie udało się odczytać presetów.\n\n{ex.Message}", "Błąd odczytu presetów PTZ", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void DahuaDeletePreset_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDahuaDevice == null || DahuaPresetsComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not int presetNumber) return;
            if (_dahuaLoginId == IntPtr.Zero || !string.Equals(_connectedDahuaDeviceId, _currentDahuaDevice.Id, StringComparison.Ordinal))
            {
                MessageBox.Show(this, "Najpierw połącz się z urządzeniem.", "Brak połączenia", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show(this, $"Czy na pewno usunąć preset {presetNumber} z urządzenia?", "Potwierdź usunięcie presetu",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            try
            {
                DahuaNetSdk.DeleteDevicePreset(
                    _currentDahuaDevice.ConnectionAddress, _currentDahuaDevice.HttpPort, _currentDahuaDevice.Username,
                    _currentDahuaDevice.Password, _dahuaCurrentChannel, presetNumber);
                _currentDahuaDevice.Presets.RemoveAll(p => p.PresetNumber == presetNumber);
                DahuaConfigService.SaveDevices(_dahuaDevices);
                RefreshDahuaPresetsList();
                DahuaStatusLabel.Text = $"Usunięto preset {presetNumber} z urządzenia.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Nie udało się usunąć presetu.\n\n{ex.Message}", "Błąd usuwania presetu PTZ", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SendDahuaPtz(DahuaNetSdk.EM_PTZ_COMMAND command, bool stop)
        {
            if (_dahuaLoginId == IntPtr.Zero) return;
            int channelIndex = Math.Max(0, _dahuaCurrentChannel - 1);
            try
            {
                DahuaNetSdk.CLIENT_DHPTZControlEx2(_dahuaLoginId, channelIndex, (uint)command, 4, 4, 0, stop, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                DahuaStatusLabel.Text = $"Błąd sterowania PTZ: {ex.Message}";
            }
        }

        private void DahuaPtzUp_Down(object sender, MouseButtonEventArgs e) => SendDahuaPtz(DahuaNetSdk.EM_PTZ_COMMAND.UP, false);
        private void DahuaPtzDown_Down(object sender, MouseButtonEventArgs e) => SendDahuaPtz(DahuaNetSdk.EM_PTZ_COMMAND.DOWN, false);
        private void DahuaPtzLeft_Down(object sender, MouseButtonEventArgs e) => SendDahuaPtz(DahuaNetSdk.EM_PTZ_COMMAND.LEFT, false);
        private void DahuaPtzRight_Down(object sender, MouseButtonEventArgs e) => SendDahuaPtz(DahuaNetSdk.EM_PTZ_COMMAND.RIGHT, false);
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

        private void DahuaStreamTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_dahuaLoginId == IntPtr.Zero) return;
            if (_dahuaMultiViewSlots.Any(slot => slot.StreamHandle != IntPtr.Zero))
                StartDahuaMultiLiveForSelectedChannels();
            else if (_dahuaRealPlayHandle != IntPtr.Zero)
                StartDahuaLiveStreamForChannel(_dahuaCurrentChannel);
        }

        private static bool IsValidDahuaEndpoint(string address, string portText, out ushort port, out string error)
        {
            port = 0;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(address))
            {
                error = "Wpisz adres IP lub nazwę hosta urządzenia.";
                return false;
            }
            string host = address.Trim().Trim('[', ']');
            if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
            {
                error = $"Nieprawidłowy adres IP lub nazwa hosta: {address}";
                return false;
            }
            if (!ushort.TryParse(portText, out port) || port == 0)
            {
                error = "Port musi być liczbą z zakresu 1–65535.";
                return false;
            }
            return true;
        }

        private void DahuaConnect_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDahuaDevice == null) return;
            EnsureDahuaSdkInitialized();
            if (!_dahuaSdkInitialized)
            {
                MessageBox.Show(this, "Nie zainicjalizowano Dahua NetSDK. Sprawdź pliki dhnetsdk.dll i dhplay.dll (x64) w katalogu lib.", "Błąd SDK", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (_currentDahuaDevice.CredentialsUnavailable)
            {
                DahuaSubTabs.SelectedIndex = 2;
                MessageBox.Show(this, "Zapisane dane logowania są niedostępne. Wpisz je ponownie w zakładce Urządzenia.", "Dane logowania", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string ip = _currentDahuaDevice.ConnectionAddress;
            if (!IsValidDahuaEndpoint(ip, _currentDahuaDevice.Port.ToString(), out ushort port, out string validationError))
            {
                DahuaStatusLabel.Text = validationError;
                MessageBox.Show(this, validationError, "Nieprawidłowa konfiguracja urządzenia", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DahuaDisconnect();
            string user = _currentDahuaDevice.Username ?? string.Empty;
            string pass = _currentDahuaDevice.Password ?? string.Empty;
            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(pass))
            {
                DahuaSubTabs.SelectedIndex = 2;
                DahuaStatusLabel.Text = "Brak danych logowania. Wpisz użytkownika i hasło w zakładce Urządzenia.";
                MessageBox.Show(this, DahuaStatusLabel.Text, "Brak danych logowania", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DahuaStatusLabel.Text = $"Logowanie do {ip}:{port}…";
            try
            {
                var deviceInfo = DahuaNetSdk.NET_DEVICEINFO.Create();
                int err = 0;
                _dahuaLoginId = DahuaNetSdk.CLIENT_Login(ip, port, user, pass, ref deviceInfo, ref err);
                if (_dahuaLoginId == IntPtr.Zero)
                {
                    uint lastError = DahuaNetSdk.CLIENT_GetLastError();
                    DahuaStatusLabel.Text = $"Błąd logowania do {ip}. {DahuaNetSdk.GetErrorDescription(lastError)}";
                    return;
                }

                _connectedDahuaDeviceId = _currentDahuaDevice.Id;
                _dahuaDeviceChannelCount = Math.Max(0, (int)deviceInfo.byChanNum);
                if (_dahuaDeviceChannelCount > 0) _currentDahuaDevice.EnsureDefaultChannels(_dahuaDeviceChannelCount);

                if (TryQueryActiveDahuaChannelNumbers(out var activeChannels))
                {
                    _dahuaActiveChannelNumbers = activeChannels;
                    if (_dahuaActiveChannelNumbers.Count > 0)
                    {
                        RefreshDahuaChannelLists();
                        BuildDahuaModernChannelChecks();
                    }
                    else DahuaStatusLabel.Text = "Rejestrator nie zgłosił aktywnych kanałów wejściowych.";
                }
                else _dahuaActiveChannelNumbers = null;

                DahuaConnectButton.IsEnabled = false;
                DahuaDisconnectButton.IsEnabled = true;
                DahuaStreamOverlay.Visibility = Visibility.Collapsed;

                if (!_dahuaConnectForCalendarAvailability)
                    StartDahuaMultiLiveForSelectedChannels();
                else
                    DahuaStatusLabel.Text = $"Połączono z {ip}. Sprawdzanie dostępności nagrań w kalendarzu…";
            }
            catch (Exception ex)
            {
                WriteDahuaSdkDiagnostic($"Connect exception: {ex}");
                DahuaStatusLabel.Text = $"Błąd połączenia: {ex.Message}";
            }
        }

        private void StartDahuaLiveStreamForChannel(int channelNumber)
        {
            if (_dahuaLoginId == IntPtr.Zero) return;
            DahuaStopMultiLiveStreams();
            DahuaStopSynchronizedPlayback();
            try
            {
                if (_dahuaRealPlayHandle != IntPtr.Zero)
                {
                    DahuaNetSdk.CLIENT_StopRealPlayEx(_dahuaRealPlayHandle);
                    _dahuaRealPlayHandle = IntPtr.Zero;
                }
                if (_dahuaPlaybackHandle != IntPtr.Zero)
                {
                    DahuaNetSdk.CLIENT_StopPlayBack(_dahuaPlaybackHandle);
                    _dahuaPlaybackHandle = IntPtr.Zero;
                }
                if (!_dahuaPrimaryPictureBox.IsHandleCreated) _dahuaPrimaryPictureBox.CreateControl();

                int channelIndex = Math.Max(0, channelNumber - 1);
                var streamType = DahuaStreamTypeBox.SelectedIndex == 1 ? DahuaNetSdk.EM_RealPlayType.Realplay_1 : DahuaNetSdk.EM_RealPlayType.Realplay;
                _dahuaRealPlayHandle = DahuaNetSdk.CLIENT_RealPlayEx(_dahuaLoginId, channelIndex, _dahuaPrimaryPictureBox.Handle, streamType);
                if (_dahuaRealPlayHandle == IntPtr.Zero)
                    _dahuaRealPlayHandle = DahuaNetSdk.CLIENT_RealPlay(_dahuaLoginId, channelIndex, _dahuaPrimaryPictureBox.Handle);

                string chName = _currentDahuaDevice?.GetChannelName(channelNumber) ?? $"Kanał {channelNumber}";
                if (_dahuaRealPlayHandle == IntPtr.Zero)
                {
                    uint lastError = DahuaNetSdk.CLIENT_GetLastError();
                    DahuaStatusLabel.Text = $"Błąd uruchomienia strumienia na kanale '{chName}'. {DahuaNetSdk.GetErrorDescription(lastError)}";
                }
                else
                {
                    DahuaStatusLabel.Text = $"Na żywo: {chName} (CH {channelNumber})";
                    HighlightActiveDahuaChannelButton(channelNumber);
                }
            }
            catch (Exception ex)
            {
                DahuaStatusLabel.Text = $"Błąd uruchamiania kanału {channelNumber}: {ex.Message}";
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
                    DahuaNetSdk.CLIENT_StopRealPlayEx(_dahuaRealPlayHandle);
                    _dahuaRealPlayHandle = IntPtr.Zero;
                }
                if (_dahuaPlaybackHandle != IntPtr.Zero)
                {
                    DahuaNetSdk.CLIENT_StopPlayBack(_dahuaPlaybackHandle);
                    _dahuaPlaybackHandle = IntPtr.Zero;
                }
                if (_dahuaLoginId != IntPtr.Zero)
                {
                    DahuaNetSdk.CLIENT_Logout(_dahuaLoginId);
                    _dahuaLoginId = IntPtr.Zero;
                }
            }
            catch { }

            _connectedDahuaDeviceId = null;
            if (DahuaConnectButton != null) DahuaConnectButton.IsEnabled = true;
            if (DahuaDisconnectButton != null) DahuaDisconnectButton.IsEnabled = false;
            if (DahuaStreamOverlay != null) DahuaStreamOverlay.Visibility = Visibility.Visible;
            if (DahuaStatusLabel != null) DahuaStatusLabel.Text = "Rozłączono ze strumieniem.";
            _dahuaPrimaryPictureBox.Invalidate();
        }

        private void DahuaDisconnect_Click(object sender, RoutedEventArgs e) => DahuaDisconnect();

        private void OnDahuaSdkDisconnected(IntPtr lLoginID, string pchDVRIP, int nDVRPort, IntPtr dwUser)
        {
            Dispatcher.Invoke(() =>
            {
                DahuaStatusLabel.Text = $"Utracono połączenie z {pchDVRIP}:{nDVRPort}. Trwa automatyczne wznawianie…";
            });
        }

        private void OnDahuaSdkReconnected(IntPtr lLoginID, string pchDVRIP, int nDVRPort, IntPtr dwUser)
        {
            Dispatcher.Invoke(() =>
            {
                DahuaStatusLabel.Text = $"Przywrócono połączenie z {pchDVRIP}:{nDVRPort}.";
                StartDahuaMultiLiveForSelectedChannels();
            });
        }

        #endregion

        #region Multi-View & Synchronized Archive Playback

        private IEnumerable<DahuaChannelInfo> GetVisibleDahuaChannels()
        {
            if (_currentDahuaDevice == null) return Enumerable.Empty<DahuaChannelInfo>();
            if (_dahuaActiveChannelNumbers == null) return _currentDahuaDevice.Channels;
            return _currentDahuaDevice.Channels.Where(channel => _dahuaActiveChannelNumbers.Contains(channel.Number));
        }

        private bool TryQueryActiveDahuaChannelNumbers(out HashSet<int> activeChannels)
        {
            activeChannels = new HashSet<int>();
            if (_currentDahuaDevice == null || _currentDahuaDevice.CredentialsUnavailable) return false;
            try
            {
                string url = $"http://{_currentDahuaDevice.ConnectionAddress}:{_currentDahuaDevice.HttpPort}/cgi-bin/api/LogicDeviceManager/getCameraState";
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Accept = "application/json";
                request.Credentials = new NetworkCredential(_currentDahuaDevice.Username, _currentDahuaDevice.Password);
                request.PreAuthenticate = true;
                request.Timeout = 5000;
                request.ReadWriteTimeout = 5000;

                byte[] body = Encoding.UTF8.GetBytes("{\"uniqueChannels\":[-1]}");
                request.ContentLength = body.Length;
                using (var stream = request.GetRequestStream()) stream.Write(body, 0, body.Length);

                using var response = (HttpWebResponse)request.GetResponse();
                using var responseStream = response.GetResponseStream();
                if (responseStream == null) return false;
                using var document = JsonDocument.Parse(responseStream);
                if (!document.RootElement.TryGetProperty("states", out JsonElement states) || states.ValueKind != JsonValueKind.Array)
                    return false;

                foreach (JsonElement state in states.EnumerateArray())
                {
                    if (!state.TryGetProperty("channel", out JsonElement channelValue) || !channelValue.TryGetInt32(out int zeroBasedChannel))
                        continue;
                    string connectionState = state.TryGetProperty("connectionState", out JsonElement stateValue)
                        ? stateValue.GetString() ?? string.Empty : string.Empty;
                    if (connectionState.Equals("Empty", StringComparison.OrdinalIgnoreCase) ||
                        connectionState.Equals("Disable", StringComparison.OrdinalIgnoreCase) ||
                        connectionState.Equals("UnInited", StringComparison.OrdinalIgnoreCase))
                        continue;
                    activeChannels.Add(zeroBasedChannel + 1);
                }
                return true;
            }
            catch { return false; }
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
                DahuaStatusLabel.Text = $"Wybrany układ obsługuje maksymalnie {capacity} kanał(ów).";
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

        private void BuildDahuaMultiViewSlots(IReadOnlyList<int> channels)
        {
            DahuaStopMultiLiveStreams();
            DahuaStopPlaybackHandlesOnly();
            DisposeDahuaMultiViewSlots();

            int capacity = GetDahuaMultiViewCapacity();
            int columns = capacity == 1 ? 1 : capacity == 4 ? 2 : capacity == 9 ? 3 : 4;
            DahuaMultiVideoGrid.Columns = columns;
            DahuaMultiVideoGrid.Rows = capacity / columns;

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
                picture.CreateControl();
                viewport.MouseWheel += (_, args) => HandleDahuaSlotZoomWheel(slotRef!, args, viewport.PointToClient(Forms.Control.MousePosition));
                picture.MouseWheel += (_, args) => HandleDahuaSlotZoomWheel(slotRef!, args, viewport.PointToClient(Forms.Control.MousePosition));
                viewport.MouseDown += (_, args) => BeginDahuaSlotPan(slotRef!, viewport.PointToClient(Forms.Control.MousePosition), args);
                picture.MouseDown += (_, args) => BeginDahuaSlotPan(slotRef!, viewport.PointToClient(Forms.Control.MousePosition), args);
                viewport.MouseMove += (_, args) => UpdateDahuaSlotPan(slotRef!, viewport.PointToClient(Forms.Control.MousePosition), args);
                picture.MouseMove += (_, args) => UpdateDahuaSlotPan(slotRef!, viewport.PointToClient(Forms.Control.MousePosition), args);
                viewport.MouseUp += (_, args) => EndDahuaSlotPan(slotRef!, args);
                picture.MouseUp += (_, args) => EndDahuaSlotPan(slotRef!, args);

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
            int width = Math.Max(1, (int)Math.Round(viewportWidth * slot.Zoom));
            int height = Math.Max(1, (int)Math.Round(viewportHeight * slot.Zoom));

            slot.PanX = Math.Min(0, Math.Max(viewportWidth - width, slot.PanX));
            slot.PanY = Math.Min(0, Math.Max(viewportHeight - height, slot.PanY));
            if (slot.Zoom <= 1.001) { slot.Zoom = 1.0; slot.PanX = 0; slot.PanY = 0; }

            slot.PictureBox.Size = new System.Drawing.Size(width, height);
            slot.PictureBox.Location = new System.Drawing.Point(slot.PanX, slot.PanY);
        }

        private void HandleDahuaSlotZoomWheel(DahuaMultiViewSlot slot, Forms.MouseEventArgs args, System.Drawing.Point cursor)
        {
            if (args.Delta == 0) return;
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

        private void BeginDahuaSlotPan(DahuaMultiViewSlot slot, System.Drawing.Point cursor, Forms.MouseEventArgs args)
        {
            if (args.Button != Forms.MouseButtons.Left || slot.Zoom <= 1.001) return;
            slot.IsDragging = true;
            slot.DragStart = cursor;
            slot.PanStart = new System.Drawing.Point(slot.PanX, slot.PanY);
            slot.Viewport.Capture = true;
            slot.Viewport.Cursor = Forms.Cursors.SizeAll;
        }

        private void UpdateDahuaSlotPan(DahuaMultiViewSlot slot, System.Drawing.Point cursor, Forms.MouseEventArgs args)
        {
            if (!slot.IsDragging) return;
            slot.PanX = slot.PanStart.X + cursor.X - slot.DragStart.X;
            slot.PanY = slot.PanStart.Y + cursor.Y - slot.DragStart.Y;
            ApplyDahuaSlotViewport(slot);
        }

        private void EndDahuaSlotPan(DahuaMultiViewSlot slot, Forms.MouseEventArgs args)
        {
            if (args.Button != Forms.MouseButtons.Left) return;
            slot.IsDragging = false;
            slot.Viewport.Capture = false;
            slot.Viewport.Cursor = Forms.Cursors.Default;
        }

        private void DahuaStartMultiLive_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDahuaDevice == null) return;
            if (_dahuaLoginId == IntPtr.Zero || !string.Equals(_connectedDahuaDeviceId, _currentDahuaDevice.Id, StringComparison.Ordinal))
            {
                DahuaConnect_Click(sender, e);
                return;
            }
            StartDahuaMultiLiveForSelectedChannels();
        }

        private void StartDahuaMultiLiveForSelectedChannels()
        {
            if (_dahuaLoginId == IntPtr.Zero || _currentDahuaDevice == null) return;
            var channels = GetSelectedDahuaModernChannels();
            if (channels.Count == 0) channels.Add(_dahuaCurrentChannel);
            channels = channels.Take(GetDahuaMultiViewCapacity()).ToList();
            _dahuaCurrentChannel = channels[0];
            HighlightActiveDahuaChannelButton(_dahuaCurrentChannel);

            DahuaStopSynchronizedPlayback();
            if (_dahuaRealPlayHandle != IntPtr.Zero)
            {
                DahuaNetSdk.CLIENT_StopRealPlayEx(_dahuaRealPlayHandle);
                _dahuaRealPlayHandle = IntPtr.Zero;
            }
            BuildDahuaMultiViewSlots(channels);

            var streamType = DahuaStreamTypeBox.SelectedIndex == 1
                ? DahuaNetSdk.EM_RealPlayType.Realplay_1
                : DahuaNetSdk.EM_RealPlayType.Realplay;
            int started = 0;
            foreach (var slot in _dahuaMultiViewSlots)
            {
                slot.StreamHandle = DahuaNetSdk.CLIENT_RealPlayEx(_dahuaLoginId, Math.Max(0, slot.Channel - 1), slot.PictureBox.Handle, streamType);
                if (slot.StreamHandle != IntPtr.Zero) started++;
            }
            DahuaStatusLabel.Text = $"Podgląd wielokanałowy: uruchomiono {started}/{channels.Count} kanałów.";
            DahuaPlaybackSyncStatusText.Text = "Podgląd na żywo";
            DahuaStreamOverlay.Visibility = Visibility.Collapsed;
        }

        private void DahuaStopMultiLiveStreams()
        {
            foreach (var slot in _dahuaMultiViewSlots)
            {
                if (slot.StreamHandle == IntPtr.Zero) continue;
                try { DahuaNetSdk.CLIENT_StopRealPlayEx(slot.StreamHandle); } catch { }
                slot.StreamHandle = IntPtr.Zero;
            }
        }

        private double GetDahuaPlaybackDurationSeconds() => Math.Max(1, (_dahuaPlaybackRangeEnd - _dahuaPlaybackRangeStart).TotalSeconds);

        private void UpdateDahuaPlaybackSliderRange(bool keepPlayheadVisible)
        {
            if (DahuaPlaybackPositionSlider == null || _dahuaMultiPlaybackHandles.Count == 0) return;

            double visibleHours = 24.0 / _dahuaRecordingTimelineZoom;
            double viewStartHour = Math.Clamp(_dahuaRecordingTimelineStartHour, 0, 24.0 - visibleHours);
            double viewEndHour = viewStartHour + visibleHours;
            double playbackStartHour = (_dahuaPlaybackRangeStart - _dahuaPlaybackRangeStart.Date).TotalHours;

            if (keepPlayheadVisible)
            {
                double elapsed = _dahuaPlaybackElapsedBeforePause;
                if (!_dahuaSynchronizedPlaybackPaused)
                    elapsed += (DateTime.Now - _dahuaPlaybackSegmentStartedAt).TotalSeconds * _dahuaArchivePlaybackSpeed;

                double playheadHour = playbackStartHour + elapsed / 3600.0;
                if (playheadHour < viewStartHour || playheadHour > viewEndHour)
                {
                    _dahuaRecordingTimelineStartHour = Math.Clamp(playheadHour - visibleHours * 0.15, 0, 24.0 - visibleHours);
                    viewStartHour = _dahuaRecordingTimelineStartHour;
                    viewEndHour = viewStartHour + visibleHours;
                    RefreshDahuaRecordingTimeline();
                }
            }

            double minOffset = (viewStartHour - playbackStartHour) * 3600.0;
            double maxOffset = (viewEndHour - playbackStartHour) * 3600.0;
            if (maxOffset <= minOffset) maxOffset = minOffset + 1;

            DahuaPlaybackPositionSlider.Minimum = minOffset;
            DahuaPlaybackPositionSlider.Maximum = maxOffset;

            double currentOffset = _dahuaPlaybackElapsedBeforePause;
            if (!_dahuaSynchronizedPlaybackPaused)
                currentOffset += (DateTime.Now - _dahuaPlaybackSegmentStartedAt).TotalSeconds * _dahuaArchivePlaybackSpeed;

            DahuaPlaybackPositionSlider.Value = Math.Clamp(currentOffset, minOffset, maxOffset);
        }

        private void DahuaStartSynchronizedPlayback_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDahuaDevice == null) return;
            DateTime? start = GetDahuaStartDateTime();
            DateTime? end = GetDahuaEndDateTime();
            if (start == null || end == null || end <= start)
            {
                MessageBox.Show(this, "Wybierz poprawny przedział czasu.", "Błędny zakres", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (_dahuaLoginId == IntPtr.Zero || !string.Equals(_connectedDahuaDeviceId, _currentDahuaDevice.Id, StringComparison.Ordinal))
            {
                DahuaConnect_Click(sender, e);
                if (_dahuaLoginId == IntPtr.Zero) return;
            }

            var channels = GetSelectedDahuaModernChannels();
            if (channels.Count == 0) channels.Add(GetSelectedDahuaArchiveChannel());
            channels = channels.Take(GetDahuaMultiViewCapacity()).ToList();
            _dahuaCurrentChannel = channels[0];
            HighlightActiveDahuaChannelButton(_dahuaCurrentChannel);
            DahuaStopMultiLiveStreams();
            if (_dahuaRealPlayHandle != IntPtr.Zero)
            {
                DahuaNetSdk.CLIENT_StopRealPlayEx(_dahuaRealPlayHandle);
                _dahuaRealPlayHandle = IntPtr.Zero;
            }
            DahuaStopSynchronizedPlayback();
            BuildDahuaMultiViewSlots(channels);

            _dahuaPlaybackRangeStart = start.Value;
            _dahuaPlaybackRangeEnd = end.Value;
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
            UpdateDahuaPlaybackSliderRange(keepPlayheadVisible: false);

            try
            {
                ConfigureDahuaRecordModes(_dahuaLoginId);
                int started = 0;
                var failedChannels = new List<int>();
                foreach (var slot in _dahuaMultiViewSlots)
                {
                    var tmStart = DahuaNetSdk.NET_TIME.FromDateTime(start.Value);
                    var tmEnd = DahuaNetSdk.NET_TIME.FromDateTime(end.Value);
                    slot.PlaybackHandle = DahuaNetSdk.CLIENT_PlayBackByTimeEx(
                        _dahuaLoginId, Math.Max(0, slot.Channel - 1), ref tmStart, ref tmEnd,
                        slot.PictureBox.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    if (slot.PlaybackHandle != IntPtr.Zero)
                    {
                        _dahuaMultiPlaybackHandles.Add(slot.PlaybackHandle);
                        started++;
                    }
                    else
                    {
                        failedChannels.Add(slot.Channel);
                    }
                }
                if (started == 0)
                    throw new InvalidOperationException("Rejestrator nie uruchomił odtwarzania na żadnym z wybranych kanałów.");
                _dahuaPlaybackUiTimer.Start();
                DahuaStreamOverlay.Visibility = Visibility.Collapsed;
                string failedSummary = failedChannels.Count == 0
                    ? "Wszystkie wybrane kanały uruchomione."
                    : $"Nie uruchomiono kanałów: {string.Join(", ", failedChannels.Select(channel => $"CH {channel}"))}.";
                DahuaPlaybackSyncStatusText.Text = $"Odtwarzanie archiwum: {started}/{channels.Count} kanałów. {failedSummary}";
                DahuaStatusLabel.Text = failedChannels.Count == 0
                    ? $"Odtwarzanie wielokanałowe uruchomione: {started} kanałów od {start.Value:HH:mm:ss}."
                    : $"Odtwarzanie częściowe: {started}/{channels.Count} kanałów od {start.Value:HH:mm:ss}. Brak: {string.Join(", ", failedChannels.Select(channel => $"CH {channel}"))}.";
            }
            catch (Exception ex)
            {
                DahuaStopSynchronizedPlayback();
                DahuaStatusLabel.Text = $"Błąd zsynchronizowanego odtwarzania: {ex.Message}";
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
            var records = (DahuaArchiveRecordsGrid.ItemsSource as IEnumerable<DahuaRecordItem>)?.ToList() ?? new List<DahuaRecordItem>();
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
            Point point = e.GetPosition(DahuaRecordingTimelineCanvas);
            const double left = 24;
            double usableWidth = Math.Max(1, DahuaRecordingTimelineCanvas.ActualWidth - left - 8);
            double visibleHours = 24.0 / _dahuaRecordingTimelineZoom;
            double viewStartHour = Math.Clamp(_dahuaRecordingTimelineStartHour, 0, 24.0 - visibleHours);
            double pointerRatio = Math.Clamp((point.X - left) / usableWidth, 0, 1);
            double hour = Math.Clamp(viewStartHour + pointerRatio * visibleHours, 0, 24);
            DateTime selectedTime = (DahuaStartDatePicker.SelectedDate ?? DateTime.Today).Date.AddSeconds(hour * 3600);
            DahuaStartTimeBox.Text = selectedTime.ToString("HH:mm:ss");

            if (_dahuaMultiPlaybackHandles.Count > 0 && selectedTime >= _dahuaPlaybackRangeStart && selectedTime <= _dahuaPlaybackRangeEnd)
            {
                int offset = (int)Math.Clamp(Math.Round((selectedTime - _dahuaPlaybackRangeStart).TotalSeconds), 0, GetDahuaPlaybackDurationSeconds());
                SeekDahuaSynchronizedPlayback(offset);
                DahuaPlaybackSyncStatusText.Text = $"Przewinięto do {selectedTime:HH:mm:ss}";
            }
            else
            {
                DahuaPlaybackSyncStatusText.Text = _dahuaMultiPlaybackHandles.Count == 0
                    ? $"Wybrano {selectedTime:HH:mm:ss}. Uruchom odtwarzanie archiwum."
                    : $"Godzina {selectedTime:HH:mm:ss} jest poza zakresem odtwarzania.";
            }
            e.Handled = true;
        }

        private void DahuaArchiveSpeedBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_dahuaUpdatingArchiveSpeed || !IsLoaded || _dahuaMultiPlaybackHandles.Count == 0 ||
                DahuaArchiveSpeedBox.SelectedItem is not ComboBoxItem item ||
                !double.TryParse(item.Tag?.ToString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double requestedSpeed))
                return;

            double elapsed = _dahuaPlaybackElapsedBeforePause;
            if (!_dahuaSynchronizedPlaybackPaused)
                elapsed += (DateTime.Now - _dahuaPlaybackSegmentStartedAt).TotalSeconds * _dahuaArchivePlaybackSpeed;

            int succeeded = 0;
            foreach (IntPtr handle in _dahuaMultiPlaybackHandles)
            {
                bool ok = DahuaNetSdk.CLIENT_NormalPlayBack(handle);
                int steps = requestedSpeed == 1.0 ? 0 : (int)Math.Round(Math.Abs(Math.Log(requestedSpeed, 2)));
                for (int step = 0; ok && step < steps; step++)
                    ok = requestedSpeed > 1.0 ? DahuaNetSdk.CLIENT_FastPlayBack(handle) : DahuaNetSdk.CLIENT_SlowPlayBack(handle);
                if (ok) succeeded++;
            }

            if (succeeded != _dahuaMultiPlaybackHandles.Count)
            {
                foreach (IntPtr handle in _dahuaMultiPlaybackHandles) DahuaNetSdk.CLIENT_NormalPlayBack(handle);
                _dahuaArchivePlaybackSpeed = 1.0;
                _dahuaUpdatingArchiveSpeed = true;
                DahuaArchiveSpeedBox.SelectedIndex = 2;
                _dahuaUpdatingArchiveSpeed = false;
                DahuaPlaybackSyncStatusText.Text = "Rejestrator nie przyjął zmiany tempa. Przywrócono 1×.";
                return;
            }

            _dahuaPlaybackElapsedBeforePause = Math.Clamp(elapsed, 0, DahuaPlaybackPositionSlider.Maximum);
            _dahuaPlaybackSegmentStartedAt = DateTime.Now;
            _dahuaArchivePlaybackSpeed = requestedSpeed;
            DahuaPlaybackSyncStatusText.Text = $"Tempo odtwarzania: {requestedSpeed.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}× ({succeeded} kanałów)";
        }

        private void SeekDahuaSynchronizedPlayback(int offsetSeconds)
        {
            offsetSeconds = (int)Math.Clamp(offsetSeconds, 0, GetDahuaPlaybackDurationSeconds());
            int succeeded = 0;
            foreach (IntPtr handle in _dahuaMultiPlaybackHandles)
                if (DahuaNetSdk.CLIENT_SeekPlayBack(handle, offsetSeconds, 0)) succeeded++;
            if (succeeded != _dahuaMultiPlaybackHandles.Count)
            {
                DahuaPlaybackSyncStatusText.Text = $"Przewijanie nie powiodło się ({succeeded}/{_dahuaMultiPlaybackHandles.Count}).";
                return;
            }
            _dahuaPlaybackElapsedBeforePause = offsetSeconds;
            _dahuaPlaybackSegmentStartedAt = DateTime.Now;
            UpdateDahuaPlaybackSliderRange(keepPlayheadVisible: true);
            DahuaPlaybackPositionSlider.Value = Math.Clamp(offsetSeconds, DahuaPlaybackPositionSlider.Minimum, DahuaPlaybackPositionSlider.Maximum);
            DahuaPlaybackPositionText.Text = $"{_dahuaPlaybackRangeStart.AddSeconds(offsetSeconds):HH:mm:ss} / {_dahuaPlaybackRangeEnd:HH:mm:ss}";
            DahuaPlaybackSyncStatusText.Text = $"Przewinięto kanały: {succeeded}/{_dahuaMultiPlaybackHandles.Count}";
        }

        private void DahuaPlaybackSlider_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_dahuaMultiPlaybackHandles.Count > 0) _dahuaIsSeekingPlayback = true;
        }

        private void DahuaPlaybackSlider_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (_dahuaIsSeekingPlayback && _dahuaMultiPlaybackHandles.Count > 0) CommitDahuaPlaybackSliderSeek();
        }

        private void DahuaPlaybackSlider_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => CommitDahuaPlaybackSliderSeek();

        private void CommitDahuaPlaybackSliderSeek()
        {
            if (!_dahuaIsSeekingPlayback) return;
            _dahuaIsSeekingPlayback = false;
            if (_dahuaMultiPlaybackHandles.Count == 0) return;
            int offsetSeconds = (int)Math.Clamp(Math.Round(DahuaPlaybackPositionSlider.Value), 0, GetDahuaPlaybackDurationSeconds());
            SeekDahuaSynchronizedPlayback(offsetSeconds);
        }

        private void DahuaPlaybackUiTimer_Tick(object? sender, EventArgs e)
        {
            if (_dahuaIsSeekingPlayback || _dahuaSynchronizedPlaybackPaused || _dahuaMultiPlaybackHandles.Count == 0) return;
            double elapsed = _dahuaPlaybackElapsedBeforePause + (DateTime.Now - _dahuaPlaybackSegmentStartedAt).TotalSeconds * _dahuaArchivePlaybackSpeed;
            elapsed = Math.Clamp(elapsed, 0, GetDahuaPlaybackDurationSeconds());
            double playheadHour = (_dahuaPlaybackRangeStart.AddSeconds(elapsed) - _dahuaPlaybackRangeStart.Date).TotalHours;
            double visibleHours = 24.0 / _dahuaRecordingTimelineZoom;
            double viewStart = Math.Clamp(_dahuaRecordingTimelineStartHour, 0, 24.0 - visibleHours);
            if (playheadHour < viewStart || playheadHour > viewStart + visibleHours)
            {
                _dahuaRecordingTimelineStartHour = Math.Clamp(playheadHour - visibleHours * 0.15, 0, 24.0 - visibleHours);
                RefreshDahuaRecordingTimeline();
                UpdateDahuaPlaybackSliderRange(keepPlayheadVisible: false);
            }
            DahuaPlaybackPositionSlider.Value = Math.Clamp(elapsed, DahuaPlaybackPositionSlider.Minimum, DahuaPlaybackPositionSlider.Maximum);
            DahuaPlaybackPositionText.Text = $"{_dahuaPlaybackRangeStart.AddSeconds(elapsed):HH:mm:ss} / {_dahuaPlaybackRangeEnd:HH:mm:ss}";
            if (elapsed >= GetDahuaPlaybackDurationSeconds())
            {
                _dahuaPlaybackUiTimer.Stop();
                DahuaPlaybackSyncStatusText.Text = "Koniec zakresu archiwum";
            }
        }

        private void DahuaToggleSynchronizedPlayback_Click(object sender, RoutedEventArgs e)
        {
            if (_dahuaMultiPlaybackHandles.Count == 0) return;
            bool pause = !_dahuaSynchronizedPlaybackPaused;
            var succeededHandles = new List<IntPtr>();
            foreach (IntPtr handle in _dahuaMultiPlaybackHandles)
                if (DahuaNetSdk.CLIENT_PausePlayBack(handle, pause)) succeededHandles.Add(handle);

            if (succeededHandles.Count != _dahuaMultiPlaybackHandles.Count)
            {
                foreach (IntPtr handle in succeededHandles) DahuaNetSdk.CLIENT_PausePlayBack(handle, !pause);
                DahuaPlaybackSyncStatusText.Text = "Nie udało się zsynchronizować sterowania";
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
            DahuaPlaybackSyncStatusText.Text = pause ? "Wstrzymano wszystkie kanały" : "Wznowiono wszystkie kanały";
        }

        private void DahuaStopPlaybackHandlesOnly()
        {
            foreach (IntPtr handle in _dahuaMultiPlaybackHandles)
            {
                try { DahuaNetSdk.CLIENT_StopPlayBack(handle); } catch { }
            }
            _dahuaMultiPlaybackHandles.Clear();
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
            if (DahuaPlaybackPositionText != null) DahuaPlaybackPositionText.Text = "00:00:00 / 00:00:00";
        }

        private void DahuaStopSynchronizedPlayback_Click(object sender, RoutedEventArgs e)
        {
            DahuaStopSynchronizedPlayback();
            DahuaPlaybackSyncStatusText.Text = "Odtwarzanie zatrzymane";
            DahuaStatusLabel.Text = "Zatrzymano zsynchronizowane odtwarzanie archiwum.";
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

                bool ok = DahuaNetSdk.CLIENT_CapturePictureEx(handle, fullPath, DahuaNetSdk.NET_CAPTURE_FORMATS.JPEG);
                if (ok && File.Exists(fullPath))
                {
                    DahuaStatusLabel.Text = $"Zapisano zrzut: {filename}";
                    AddRecentScreenshot(fullPath);
                    return fullPath;
                }
                DahuaStatusLabel.Text = "Błąd zapisu zrzutu z biblioteki Dahua NetSDK.";
                return null;
            }
            catch (Exception ex)
            {
                DahuaStatusLabel.Text = $"Wyjątek podczas zapisu klatki: {ex.Message}";
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
            _dahuaArchiveAvailableDays.Clear();
            _dahuaRecorderAvailableDays.Clear();
            string cachePrefix = $"{_currentDahuaDevice?.Id}|{GetSelectedDahuaArchiveChannel()}|";
            foreach (var entry in _dahuaRecorderAvailabilityCache.Where(pair => pair.Key.StartsWith(cachePrefix, StringComparison.Ordinal)))
                _dahuaRecorderAvailableDays.UnionWith(entry.Value);
            RefreshOpenDahuaArchiveCalendars();
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
            string cacheKey = $"{_currentDahuaDevice.Id}|{channel}|{monthStart:yyyyMM}";

            if (_dahuaRecorderAvailabilityCache.TryGetValue(cacheKey, out var cachedDays))
            {
                _dahuaRecorderAvailableDays.UnionWith(cachedDays);
                RefreshOpenDahuaArchiveCalendars();
                DahuaCalendarAvailabilityStatusText.Text = $"Dostępność pobrana z pamięci: {cachedDays.Count} dni w {monthStart:MM.yyyy}.";
                return;
            }
            if (!_dahuaAvailabilityQueriesInProgress.Add(cacheKey)) return;

            string deviceId = _currentDahuaDevice.Id;
            DahuaCalendarAvailabilityStatusText.Text = $"Sprawdzanie dostępności nagrań: {monthStart:MM.yyyy}, kanał {channel}…";

            try
            {
                if (_dahuaLoginId == IntPtr.Zero || !string.Equals(_connectedDahuaDeviceId, deviceId, StringComparison.Ordinal))
                {
                    _dahuaConnectForCalendarAvailability = true;
                    try { DahuaConnect_Click(this, new RoutedEventArgs()); }
                    finally { _dahuaConnectForCalendarAvailability = false; }
                    if (_dahuaLoginId == IntPtr.Zero || !string.Equals(_connectedDahuaDeviceId, deviceId, StringComparison.Ordinal))
                        throw new InvalidOperationException("Nie udało się połączyć z wybranym rejestratorem.");
                }

                IntPtr login = _dahuaLoginId;
                HashSet<DateTime> days = await QueryDahuaRecorderAvailabilityForMonthAsync(login, channel, monthStart);
                if (_currentDahuaDevice?.Id != deviceId || GetSelectedDahuaArchiveChannel() != channel) return;

                _dahuaRecorderAvailabilityCache[cacheKey] = days;
                _dahuaRecorderAvailableDays.UnionWith(days);
                RefreshOpenDahuaArchiveCalendars();
                DahuaCalendarAvailabilityStatusText.Text = days.Count == 0
                    ? $"Brak nagrań w {monthStart:MM.yyyy} (kanał {channel})."
                    : $"Nagrania w {days.Count} dniach miesiąca {monthStart:MM.yyyy} (kanał {channel}).";
            }
            catch (Exception ex)
            {
                if (_currentDahuaDevice?.Id == deviceId && GetSelectedDahuaArchiveChannel() == channel)
                    DahuaCalendarAvailabilityStatusText.Text = $"Nie udało się pobrać dostępności: {ex.Message}";
            }
            finally { _dahuaAvailabilityQueriesInProgress.Remove(cacheKey); }
        }

        private async Task<HashSet<DateTime>> QueryDahuaRecorderAvailabilityForMonthAsync(IntPtr login, int channel, DateTime monthStart)
        {
            await _dahuaArchiveAvailabilitySemaphore.WaitAsync();
            try
            {
                return await Task.Run(() =>
                {
                    var availableDays = new HashSet<DateTime>();
                    ConfigureDahuaRecordModes(login);
                    DateTime monthEnd = monthStart.AddMonths(1);
                    DateTime lastDay = monthEnd < DateTime.Today.AddDays(1) ? monthEnd : DateTime.Today.AddDays(1);
                    int itemSize = Marshal.SizeOf<DahuaNetSdk.NET_RECORDFILE_INFO>();
                    IntPtr buffer = Marshal.AllocHGlobal(itemSize);
                    try
                    {
                        for (DateTime day = monthStart; day < lastDay; day = day.AddDays(1))
                        {
                            for (int i = 0; i < itemSize; i++) Marshal.WriteByte(buffer, i, 0);
                            var from = DahuaNetSdk.NET_TIME.FromDateTime(day);
                            var to = DahuaNetSdk.NET_TIME.FromDateTime(day.AddDays(1));
                            bool ok = DahuaNetSdk.CLIENT_QueryRecordFile(login, Math.Max(0, channel - 1), 0, ref from, ref to, null, buffer, itemSize, out int count, 8000, false);
                            if (!ok)
                            {
                                uint error = DahuaNetSdk.CLIENT_GetLastError();
                                throw new InvalidOperationException($"Zapytanie o {day:dd.MM.yyyy} nie powiodło się: {DahuaNetSdk.GetErrorDescription(error)}");
                            }
                            if (count > 0) availableDays.Add(day.Date);
                        }
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                    return availableDays;
                });
            }
            finally { _dahuaArchiveAvailabilitySemaphore.Release(); }
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

        private static void ConfigureDahuaRecordModes(IntPtr login)
        {
            if (login == IntPtr.Zero) throw new InvalidOperationException("Brak aktywnego uchwytu logowania Dahua.");
            int streamType = 0;
            DahuaNetSdk.CLIENT_SetDeviceMode(login, 6, ref streamType);
            int recordType = 0;
            DahuaNetSdk.CLIENT_SetDeviceMode(login, 8, ref recordType);
        }

        private int GetSelectedDahuaArchiveChannel() =>
            DahuaArchiveChannelBox.SelectedItem is ComboBoxItem item && item.Tag is int ch ? ch : _dahuaCurrentChannel;

        private async void DahuaSearchArchive_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDahuaDevice == null) return;
            var start = GetDahuaStartDateTime();
            var end = GetDahuaEndDateTime();
            if (start == null || end == null || end <= start)
            {
                MessageBox.Show(this, "Wybierz poprawny przedział czasu.", "Błędny zakres dat", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (_dahuaLoginId == IntPtr.Zero || !string.Equals(_connectedDahuaDeviceId, _currentDahuaDevice.Id, StringComparison.Ordinal))
            {
                DahuaConnect_Click(sender, e);
                if (_dahuaLoginId == IntPtr.Zero) return;
            }

            int channel = GetSelectedDahuaArchiveChannel();
            int channelIndex = Math.Max(0, channel - 1);
            IntPtr login = _dahuaLoginId;
            var tmStart = DahuaNetSdk.NET_TIME.FromDateTime(start.Value);
            var tmEnd = DahuaNetSdk.NET_TIME.FromDateTime(end.Value);

            DahuaDownloadStatusText.Visibility = Visibility.Visible;
            DahuaDownloadStatusText.Text = $"Wyszukiwanie nagrań: {_currentDahuaDevice.Name}, kanał {channel}…";
            DahuaArchiveRecordsGrid.ItemsSource = null;
            RefreshDahuaRecordingTimeline();
            _dahuaArchiveAvailableDays.Clear();
            RefreshOpenDahuaArchiveCalendars();
            DahuaDevicesListBox.IsEnabled = false;
            DahuaArchiveChannelBox.IsEnabled = false;
            DahuaStartDatePicker.IsEnabled = false;
            DahuaEndDatePicker.IsEnabled = false;
            DahuaStartTimeBox.IsEnabled = false;
            DahuaEndTimeBox.IsEnabled = false;
            DahuaSearchArchiveButton.IsEnabled = false;

            try
            {
                List<DahuaRecordItem> records = await Task.Run(() =>
                {
                    const int maxRecords = 200;
                    int itemSize = Marshal.SizeOf<DahuaNetSdk.NET_RECORDFILE_INFO>();
                    int bufferSize = checked(itemSize * maxRecords);
                    IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
                    try
                    {
                        for (int i = 0; i < bufferSize; i++) Marshal.WriteByte(buffer, i, 0);
                        ConfigureDahuaRecordModes(login);
                        bool ok = DahuaNetSdk.CLIENT_QueryRecordFile(login, channelIndex, 0, ref tmStart, ref tmEnd, null, buffer, bufferSize, out int count, 8000, false);
                        if (!ok) throw new InvalidOperationException(DahuaNetSdk.GetErrorDescription(DahuaNetSdk.CLIENT_GetLastError()));

                        var result = new List<DahuaRecordItem>();
                        int safeCount = Math.Clamp(count, 0, maxRecords);
                        for (int i = 0; i < safeCount; i++)
                        {
                            var native = Marshal.PtrToStructure<DahuaNetSdk.NET_RECORDFILE_INFO>(IntPtr.Add(buffer, i * itemSize));
                            DateTime recordStart = new((int)native.starttime.dwYear, (int)native.starttime.dwMonth, (int)native.starttime.dwDay, (int)native.starttime.dwHour, (int)native.starttime.dwMinute, (int)native.starttime.dwSecond);
                            DateTime recordEnd = new((int)native.endtime.dwYear, (int)native.endtime.dwMonth, (int)native.endtime.dwDay, (int)native.endtime.dwHour, (int)native.endtime.dwMinute, (int)native.endtime.dwSecond);
                            if (recordEnd <= recordStart) continue;
                            result.Add(new DahuaRecordItem
                            {
                                Channel = channel, FileName = native.FileName, StartTime = recordStart,
                                EndTime = recordEnd, SizeKb = native.size, RecordType = native.nRecordFileType
                            });
                        }
                        return result.OrderBy(x => x.StartTime).ToList();
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                });

                DahuaArchiveRecordsGrid.ItemsSource = records;
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

                DahuaDownloadStatusText.Text = records.Count == 0
                    ? "Rejestrator nie zwrócił nagrań dla wybranego kanału i zakresu."
                    : records.Count >= 200
                        ? "Znaleziono co najmniej 200 nagrań. Zawęź zakres czasu."
                        : $"Znaleziono {records.Count} nagrań.";
            }
            catch (Exception ex)
            {
                DahuaDownloadStatusText.Text = "Błąd wyszukiwania archiwum.";
                MessageBox.Show(this, $"Nie udało się wyszukać nagrań: {ex.Message}", "Archiwum Dahua", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                DahuaDevicesListBox.IsEnabled = true;
                DahuaArchiveChannelBox.IsEnabled = true;
                DahuaStartDatePicker.IsEnabled = true;
                DahuaEndDatePicker.IsEnabled = true;
                DahuaStartTimeBox.IsEnabled = true;
                DahuaEndTimeBox.IsEnabled = true;
                DahuaSearchArchiveButton.IsEnabled = true;
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

        private async Task<string> RunDahuaFfmpegConversionAsync(string ffmpegPath, string inputPath, string outputPath, bool transcodeVideo)
        {
            var psi = new ProcessStartInfo { FileName = ffmpegPath, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(inputPath);
            psi.ArgumentList.Add("-map");
            psi.ArgumentList.Add("0:v:0");
            psi.ArgumentList.Add("-map");
            psi.ArgumentList.Add("0:a?");
            if (transcodeVideo)
            {
                psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("libx264");
                psi.ArgumentList.Add("-preset"); psi.ArgumentList.Add("veryfast");
                psi.ArgumentList.Add("-crf"); psi.ArgumentList.Add("20");
            }
            else { psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("copy"); }
            psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("aac");
            psi.ArgumentList.Add("-b:a"); psi.ArgumentList.Add("128k");
            psi.ArgumentList.Add("-movflags"); psi.ArgumentList.Add("+faststart");
            psi.ArgumentList.Add(outputPath);

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Nie można uruchomić FFmpeg.");
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            string error = await errorTask;
            if (process.ExitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                throw new InvalidOperationException(error);
            return error;
        }

        private async Task ConvertDahuaRecordingToMp4Async(string inputPath, string outputPath)
        {
            string ffmpegPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
            if (!File.Exists(ffmpegPath))
                throw new FileNotFoundException("Nie znaleziono ffmpeg.exe.", ffmpegPath);
            try { await RunDahuaFfmpegConversionAsync(ffmpegPath, inputPath, outputPath, transcodeVideo: false); }
            catch (Exception remuxError)
            {
                if (File.Exists(outputPath)) File.Delete(outputPath);
                try { await RunDahuaFfmpegConversionAsync(ffmpegPath, inputPath, outputPath, transcodeVideo: true); }
                catch (Exception transcodeError)
                {
                    throw new InvalidOperationException("Konwersja DAV do MP4 nie powiodła się. Remux: " + remuxError.Message + " | H.264: " + transcodeError.Message, transcodeError);
                }
            }
        }

        private async void DahuaLoadToPlayer_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDahuaDevice == null) return;
            var start = GetDahuaStartDateTime();
            var end = GetDahuaEndDateTime();
            if (start == null || end == null || end <= start)
            {
                MessageBox.Show(this, "Wybierz poprawny przedział czasu.", "Błędny zakres dat", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int channel = GetSelectedDahuaArchiveChannel();
            string chName = _currentDahuaDevice.GetChannelName(channel);
            if (_dahuaLoginId == IntPtr.Zero || !string.Equals(_connectedDahuaDeviceId, _currentDahuaDevice.Id, StringComparison.Ordinal))
            {
                DahuaConnect_Click(sender, e);
                if (_dahuaLoginId == IntPtr.Zero) return;
            }

            string outputDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoPlayer", "nagrania_dahua");
            Directory.CreateDirectory(outputDir);
            string safeChannelName = string.Concat(chName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)).Replace(' ', '_');
            string fileStem = $"Dahua_{safeChannelName}_CH{channel}_{start.Value:yyyyMMdd_HHmmss}-{end.Value:HHmmss}";
            string davFilePath = Path.Combine(outputDir, fileStem + ".dav");
            string targetFilePath = Path.Combine(outputDir, fileStem + ".mp4");
            if (File.Exists(davFilePath)) File.Delete(davFilePath);
            if (File.Exists(targetFilePath)) File.Delete(targetFilePath);

            DahuaDownloadProgressBar.Visibility = Visibility.Visible;
            DahuaDownloadProgressBar.IsIndeterminate = true;
            DahuaDownloadStatusText.Visibility = Visibility.Visible;
            DahuaDownloadStatusText.Text = $"Pobieranie nagrania: {chName} (CH {channel})…";
            DahuaLoadToPlayerButton.IsEnabled = false;
            DahuaDevicesListBox.IsEnabled = false;
            DahuaArchiveChannelBox.IsEnabled = false;
            DahuaSearchArchiveButton.IsEnabled = false;

            try
            {
                int channelIndex = Math.Max(0, channel - 1);
                var tmStart = DahuaNetSdk.NET_TIME.FromDateTime(start.Value);
                var tmEnd = DahuaNetSdk.NET_TIME.FromDateTime(end.Value);
                IntPtr activeLoginId = _dahuaLoginId;
                bool downloadFinished = false;

                await Task.Run(async () =>
                {
                    ConfigureDahuaRecordModes(activeLoginId);
                    var completed = new ManualResetEventSlim(false);
                    DahuaNetSdk.fTimeDownLoadPosCallBack progressCallback = (_, totalSize, downloadedSize, _, _, _) =>
                    {
                        if (downloadedSize == uint.MaxValue || (totalSize > 0 && downloadedSize >= totalSize))
                            completed.Set();
                    };

                    IntPtr hDown = DahuaNetSdk.CLIENT_DownloadByTimeEx(
                        activeLoginId, channelIndex, 0, ref tmStart, ref tmEnd, davFilePath,
                        progressCallback, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    if (hDown == IntPtr.Zero)
                        throw new InvalidOperationException($"CLIENT_DownloadByTimeEx: {DahuaNetSdk.GetErrorDescription(DahuaNetSdk.CLIENT_GetLastError())}");

                    try
                    {
                        bool signaled = await Task.Run(() => completed.Wait(TimeSpan.FromMinutes(30)));
                        if (signaled && File.Exists(davFilePath) && new FileInfo(davFilePath).Length > 0)
                            downloadFinished = true;
                    }
                    finally
                    {
                        DahuaNetSdk.CLIENT_StopDownload(hDown);
                        GC.KeepAlive(progressCallback);
                    }
                });

                if (!downloadFinished || !File.Exists(davFilePath) || new FileInfo(davFilePath).Length == 0)
                    throw new InvalidOperationException("NetSDK nie zakończył pobierania pliku archiwum.");

                DahuaDownloadStatusText.Text = "Pobrano plik DAV. Konwersja do MP4…";
                await ConvertDahuaRecordingToMp4Async(davFilePath, targetFilePath);

                if (File.Exists(targetFilePath) && new FileInfo(targetFilePath).Length > 0)
                {
                    DahuaDownloadStatusText.Text = "Wczytano do odtwarzacza!";
                    await OpenDahuaStreamAsync(targetFilePath);
                }
                else
                {
                    DahuaDownloadStatusText.Text = "Nie znaleziono nagrania na rejestratorze w wybranym przedziale czasu.";
                }
            }
            catch (Exception ex)
            {
                DahuaDownloadStatusText.Text = $"Błąd pobierania: {ex.Message}";
                MessageBox.Show(this, $"Błąd pobierania nagrania: {ex.Message}", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                DahuaDownloadProgressBar.Visibility = Visibility.Collapsed;
                DahuaLoadToPlayerButton.IsEnabled = true;
                DahuaDevicesListBox.IsEnabled = true;
                DahuaArchiveChannelBox.IsEnabled = true;
                DahuaSearchArchiveButton.IsEnabled = true;
            }
        }

        private void DahuaPlayArchiveInWindow_Click(object sender, RoutedEventArgs e)
        {
            SelectDahuaModernChannel(GetSelectedDahuaArchiveChannel());
            DahuaStartSynchronizedPlayback_Click(sender, e);
        }

        #endregion
    }
}
