using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace VideoPlayer
{
    public partial class DahuaStreamWindow : Window
    {
        private readonly Forms.PictureBox _primaryPictureBox = new()
        {
            BackColor = System.Drawing.Color.Black,
            Dock = Forms.DockStyle.Fill,
            SizeMode = Forms.PictureBoxSizeMode.Normal
        };

        private List<DahuaDeviceItem> _devices = new();
        private DahuaDeviceItem? _currentDevice;
        private IntPtr _loginId = IntPtr.Zero;
        private IntPtr _realPlayHandle = IntPtr.Zero;
        private IntPtr _playbackHandle = IntPtr.Zero;
        private bool _sdkInitialized = false;
        private int _currentChannel = 1;
        private readonly List<Button> _channelButtons = new();

        // Callback references to prevent GC collection
        private DahuaNetSdk.fDisConnect? _disConnectCallback;
        private DahuaNetSdk.fHaveReConnect? _haveReConnectCallback;

        public DahuaStreamWindow()
        {
            InitializeComponent();
            _primaryPictureBox.CreateControl();
            PrimaryVideoHost.Child = _primaryPictureBox;

            InitializeDatePickers();
        }

        private void InitializeDatePickers()
        {
            DateTime now = DateTime.Now;
            StartDatePicker.SelectedDate = now;
            EndDatePicker.SelectedDate = now;
            StartTimeBox.Text = now.AddHours(-1).ToString("HH:mm:ss");
            EndTimeBox.Text = now.ToString("HH:mm:ss");
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            InitializeSdk();
            LoadDeviceList();
        }

        private void InitializeSdk()
        {
            try
            {
                var missingDlls = DahuaNetSdk.CheckDependencies();
                if (missingDlls.Count > 0)
                {
                    string missingList = string.Join(", ", missingDlls);
                    SdkStatusBadge.Text = $"Dahua NetSDK: Brak plików: {missingList}";
                    SdkStatusBadge.Foreground = Brushes.Orange;
                    StatusLabel.Text = $"Brak bibliotek DLL: {missingList}. Skopiuj je do folderu programu.";
                    return;
                }

                _disConnectCallback = OnSdkDisconnected;
                _haveReConnectCallback = OnSdkReconnected;

                bool initOk = DahuaNetSdk.CLIENT_Init(_disConnectCallback, IntPtr.Zero);
                if (initOk)
                {
                    _sdkInitialized = true;
                    DahuaNetSdk.CLIENT_SetAutoReconnect(_haveReConnectCallback, IntPtr.Zero);
                    SdkStatusBadge.Text = "Dahua NetSDK: Zainicjalizowano (x64)";
                    SdkStatusBadge.Foreground = Brushes.LightGreen;
                    StatusLabel.Text = "Gotowy do połączenia ze strumieniem IP.";
                }
                else
                {
                    SdkStatusBadge.Text = "Dahua NetSDK: Błąd CLIENT_Init";
                    SdkStatusBadge.Foreground = Brushes.Salmon;
                }
            }
            catch (Exception ex)
            {
                SdkStatusBadge.Text = "Dahua NetSDK: Wyjątek DLL";
                SdkStatusBadge.Foreground = Brushes.Salmon;
                StatusLabel.Text = $"Błąd: {ex.Message}";
            }
        }

        private void LoadDeviceList()
        {
            _devices = DahuaConfigService.LoadDevices();
            DevicesListBox.ItemsSource = null;
            DevicesListBox.ItemsSource = _devices;
            if (_devices.Count > 0)
            {
                DevicesListBox.SelectedIndex = 0;
            }
        }

        private void DevicesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DevicesListBox.SelectedItem is DahuaDeviceItem dev)
            {
                _currentDevice = dev;
                _currentDevice.EnsureDefaultChannels(16);
                _currentDevice.EnsureDefaultPresets();

                DeviceCustomNameBox.Text = dev.Name;
                DeviceIpBox.Text = dev.IpAddress;
                DevicePortBox.Text = dev.Port.ToString();
                DeviceUserBox.Text = dev.Username;
                DevicePassBox.Password = dev.Password;

                _currentChannel = dev.Channel;
                RefreshChannelLists();
                RefreshPresetsList();

                ChannelNameEditBox.Text = dev.GetChannelName(_currentChannel);
                StreamTypeBox.SelectedIndex = dev.StreamType == DahuaNetSdk.EM_RealPlayType.Realplay ? 0 : 1;
                HighlightActiveChannelButton(_currentChannel);
            }
        }

        #region Device Management for Operator

        private void SaveDevice_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDevice == null) return;

            string name = DeviceCustomNameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                name = $"Kamera {DeviceIpBox.Text.Trim()}";
            }

            _currentDevice.Name = name;
            _currentDevice.IpAddress = DeviceIpBox.Text.Trim();
            if (ushort.TryParse(DevicePortBox.Text.Trim(), out ushort port))
            {
                _currentDevice.Port = port;
            }
            _currentDevice.Username = DeviceUserBox.Text.Trim();
            _currentDevice.Password = DevicePassBox.Password;

            DahuaConfigService.SaveDevices(_devices);
            DevicesListBox.Items.Refresh();
            StatusLabel.Text = $"Zapisano urządzenie dla operatora: '{_currentDevice.Name}' ({_currentDevice.IpAddress}:{_currentDevice.Port})";
        }

        private void AddDevice_Click(object sender, RoutedEventArgs e)
        {
            string name = DeviceCustomNameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                name = $"Kamera {DeviceIpBox.Text.Trim()}";
            }

            var newDev = new DahuaDeviceItem
            {
                Name = name,
                IpAddress = string.IsNullOrWhiteSpace(DeviceIpBox.Text) ? "192.168.1.108" : DeviceIpBox.Text.Trim(),
                Port = ushort.TryParse(DevicePortBox.Text.Trim(), out ushort p) ? p : (ushort)37777,
                Username = string.IsNullOrWhiteSpace(DeviceUserBox.Text) ? "admin" : DeviceUserBox.Text.Trim(),
                Password = DevicePassBox.Password,
                Channel = 1,
                StreamType = DahuaNetSdk.EM_RealPlayType.Realplay
            };
            newDev.EnsureDefaultChannels(16);
            newDev.EnsureDefaultPresets();

            _devices.Add(newDev);
            DahuaConfigService.SaveDevices(_devices);

            DevicesListBox.ItemsSource = null;
            DevicesListBox.ItemsSource = _devices;
            DevicesListBox.SelectedItem = newDev;

            StatusLabel.Text = $"Dodano nowe urządzenie: '{newDev.Name}' na listę operatora.";
        }

        private void DeleteDevice_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDevice == null) return;
            if (_devices.Count <= 1)
            {
                MessageBox.Show("Nie można usunąć ostatniego urządzenia z listy.", "Informacja", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var toRemove = _currentDevice;
            int idx = _devices.IndexOf(toRemove);
            _devices.Remove(toRemove);
            DahuaConfigService.SaveDevices(_devices);

            DevicesListBox.ItemsSource = null;
            DevicesListBox.ItemsSource = _devices;
            DevicesListBox.SelectedIndex = Math.Max(0, idx - 1);

            StatusLabel.Text = $"Usunięto urządzenie '{toRemove.Name}' z listy.";
        }

        private void QueryFromDevice_Click(object sender, RoutedEventArgs e)
        {
            if (_loginId == IntPtr.Zero)
            {
                Connect_Click(sender, e);
                if (_loginId == IntPtr.Zero) return;
            }
            QueryDeviceState(showDialog: true);
        }

        private void QueryDeviceState(bool showDialog = false)
        {
            if (_currentDevice == null || _loginId == IntPtr.Zero) return;

            try
            {
                // 1. Odczyt nazw kanałów z kamery lub rejestratora
                var readChannels = DahuaNetSdk.QueryChannelNamesFromDevice(_loginId);
                int chCount = readChannels.Count;
                if (chCount > 0)
                {
                    _currentDevice.UpdateFromDeviceChannels(readChannels);
                }

                // 2. Odczyt presetów PTZ z kamery
                int channelIndex = Math.Max(0, _currentChannel - 1);
                var readPresets = DahuaNetSdk.QueryPresetsFromDevice(_loginId, channelIndex);
                int pCount = readPresets.Count;
                if (pCount > 0)
                {
                    _currentDevice.UpdateFromDevicePresets(readPresets);
                }

                DahuaConfigService.SaveDevices(_devices);
                RefreshChannelLists();
                RefreshPresetsList();

                string msg = $"Odczytano z urządzenia: {chCount} kanałów oraz {pCount} presetów PTZ.";
                StatusLabel.Text = msg;

                if (showDialog)
                {
                    MessageBox.Show(
                        $"{msg}\n\nNazwy kanałów i presety zostały zaktualizowane w interfejsie i trwale zapisane dla operatora.",
                        "Odczyt z urządzenia Dahua",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                StatusLabel.Text = $"Błąd odczytu danych z urządzenia: {ex.Message}";
            }
        }

        #endregion

        #region Channel Management by Name

        private void RefreshChannelLists()
        {
            if (_currentDevice == null) return;

            // 1. Rozwijana lista kanałów w zakładce Live
            ChannelComboBox.SelectionChanged -= ChannelComboBox_SelectionChanged;
            ChannelComboBox.Items.Clear();

            // 2. Rozwijana lista kanałów w zakładce Archiwum
            ArchiveChannelBox.Items.Clear();

            foreach (var chInfo in _currentDevice.Channels.OrderBy(c => c.Number))
            {
                string text = chInfo.DisplayName;
                var itemLive = new ComboBoxItem { Content = text, Tag = chInfo.Number };
                var itemArch = new ComboBoxItem { Content = text, Tag = chInfo.Number };

                ChannelComboBox.Items.Add(itemLive);
                ArchiveChannelBox.Items.Add(itemArch);
            }

            // Zaznaczenie aktywnego kanału
            for (int i = 0; i < ChannelComboBox.Items.Count; i++)
            {
                if ((ChannelComboBox.Items[i] as ComboBoxItem)?.Tag is int num && num == _currentChannel)
                {
                    ChannelComboBox.SelectedIndex = i;
                    ArchiveChannelBox.SelectedIndex = i;
                    break;
                }
            }
            ChannelComboBox.SelectionChanged += ChannelComboBox_SelectionChanged;

            // 3. Kafelki szybkiego wyboru kanałów z nazwami pod obrazem wideo
            BuildChannelButtons();
        }

        private void BuildChannelButtons()
        {
            if (_currentDevice == null) return;

            ChannelButtonsPanel.Children.Clear();
            _channelButtons.Clear();

            foreach (var chInfo in _currentDevice.Channels.OrderBy(c => c.Number).Take(16))
            {
                int ch = chInfo.Number;
                string displayName = string.IsNullOrWhiteSpace(chInfo.Name) ? $"CH {ch}" : $"{chInfo.Name}\n[CH {ch}]";

                var btn = new Button
                {
                    Content = displayName,
                    Tag = ch,
                    Style = (Style)FindResource("ChannelButton"),
                    ToolTip = $"Kanał {ch}: {chInfo.Name}"
                };
                btn.Click += (_, _) => SwitchChannel(ch);
                _channelButtons.Add(btn);
                ChannelButtonsPanel.Children.Add(btn);
            }
            HighlightActiveChannelButton(_currentChannel);
        }

        private void HighlightActiveChannelButton(int channelNumber)
        {
            string channelName = _currentDevice?.GetChannelName(channelNumber) ?? $"Kanał {channelNumber}";

            foreach (var btn in _channelButtons)
            {
                if (btn.Tag is int ch && ch == channelNumber)
                {
                    btn.Background = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A)); // Zielony
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
            ActiveChannelOverlayText.Text = $"{channelName.ToUpper()} (CH {channelNumber})";
        }

        public void SwitchChannel(int channelNumber)
        {
            _currentChannel = channelNumber;
            HighlightActiveChannelButton(channelNumber);

            if (_currentDevice != null)
            {
                ChannelNameEditBox.Text = _currentDevice.GetChannelName(channelNumber);
            }

            // Synchronizacja ComboBox
            for (int i = 0; i < ChannelComboBox.Items.Count; i++)
            {
                if ((ChannelComboBox.Items[i] as ComboBoxItem)?.Tag is int num && num == channelNumber)
                {
                    ChannelComboBox.SelectedIndex = i;
                    ArchiveChannelBox.SelectedIndex = i;
                    break;
                }
            }

            if (_loginId != IntPtr.Zero)
            {
                StartLiveStreamForChannel(_currentChannel);
            }
            else
            {
                string name = _currentDevice?.GetChannelName(channelNumber) ?? $"Kanał {channelNumber}";
                StatusLabel.Text = $"Wybrano: {name} (CH {channelNumber}). Kliknij 'Połącz ze strumieniem'.";
            }
        }

        private void ChannelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ChannelComboBox.SelectedItem is ComboBoxItem item && item.Tag is int ch && ch != _currentChannel)
            {
                SwitchChannel(ch);
            }
        }

        private void SaveChannelName_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDevice == null) return;
            string newName = ChannelNameEditBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(newName)) return;

            _currentDevice.SetChannelName(_currentChannel, newName);
            DahuaConfigService.SaveDevices(_devices);
            RefreshChannelLists();

            StatusLabel.Text = $"Zapisano nową nazwę dla Kanału {_currentChannel}: '{newName}'";
            ActiveChannelOverlayText.Text = $"{newName.ToUpper()} (CH {_currentChannel})";
        }

        private void CustomChannel_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(CustomChannelBox.Text.Trim(), out int ch) && ch >= 1)
            {
                if (_currentDevice != null && !_currentDevice.Channels.Any(c => c.Number == ch))
                {
                    _currentDevice.SetChannelName(ch, $"Kamera {ch}");
                    RefreshChannelLists();
                }
                SwitchChannel(ch);
            }
        }

        #endregion

        #region PTZ Presets Management (Wczytywanie i obsługa zapisanych pozycji)

        private void RefreshPresetsList()
        {
            PresetsComboBox.Items.Clear();
            QuickPresetsPanel.Children.Clear();

            if (_currentDevice == null || _currentDevice.Presets == null) return;

            foreach (var p in _currentDevice.Presets.OrderBy(x => x.PresetNumber))
            {
                var item = new ComboBoxItem
                {
                    Content = $"[P{p.PresetNumber}] {p.Name}",
                    Tag = p.PresetNumber
                };
                PresetsComboBox.Items.Add(item);

                // Szybkie kafelki presetów
                int pNum = p.PresetNumber;
                var quickBtn = new Button
                {
                    Content = $"P{pNum}: {p.Name}",
                    Tag = pNum,
                    Style = (Style)FindResource("PresetTileButton"),
                    ToolTip = $"Obróć do: {p.Name} (Preset {pNum})"
                };
                quickBtn.Click += (_, _) => GotoPreset(pNum);
                QuickPresetsPanel.Children.Add(quickBtn);
            }

            if (PresetsComboBox.Items.Count > 0)
            {
                PresetsComboBox.SelectedIndex = 0;
            }
        }

        private void GotoPreset(int presetNumber)
        {
            if (_loginId == IntPtr.Zero)
            {
                MessageBox.Show("Najpierw połącz ze strumieniem wideo kamery.", "Brak połączenia", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int channelIndex = Math.Max(0, _currentChannel - 1);
            try
            {
                bool ok = DahuaNetSdk.CLIENT_DHPTZControlEx2(
                    _loginId,
                    channelIndex,
                    (uint)DahuaNetSdk.EM_PTZ_COMMAND.POINT_MOVE_CONTROL, // 10
                    0,
                    presetNumber,
                    0,
                    false,
                    IntPtr.Zero);

                var match = _currentDevice?.Presets.FirstOrDefault(p => p.PresetNumber == presetNumber);
                string pName = match != null ? match.Name : $"Preset {presetNumber}";

                if (ok)
                {
                    StatusLabel.Text = $"Kamera PTZ: Przechodzenie do pozycji '{pName}' (Preset {presetNumber})…";
                }
                else
                {
                    uint err = DahuaNetSdk.CLIENT_GetLastError();
                    StatusLabel.Text = $"Błąd przejścia do presetu {presetNumber}: {DahuaNetSdk.GetErrorDescription(err)}";
                }
            }
            catch (Exception ex)
            {
                StatusLabel.Text = $"Wyjątek PTZ Preset: {ex.Message}";
            }
        }

        private void GotoPreset_Click(object sender, RoutedEventArgs e)
        {
            if (PresetsComboBox.SelectedItem is ComboBoxItem item && item.Tag is int pNum)
            {
                GotoPreset(pNum);
            }
        }

        private void SaveCurrentPositionAsPreset_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDevice == null) return;
            if (!int.TryParse(PresetNumberBox.Text.Trim(), out int pNum) || pNum < 1 || pNum > 255)
            {
                MessageBox.Show("Podaj prawidłowy numer presetu (1-255).", "Nieprawidłowy numer", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string pName = PresetNameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(pName)) pName = $"Preset {pNum}";

            int channelIndex = Math.Max(0, _currentChannel - 1);

            // Zapisanie presetu w kamerze przez NetSDK
            if (_loginId != IntPtr.Zero)
            {
                try
                {
                    DahuaNetSdk.CLIENT_DHPTZControlEx2(
                        _loginId,
                        channelIndex,
                        (uint)DahuaNetSdk.EM_PTZ_COMMAND.POINT_SET_CONTROL, // 11
                        0,
                        pNum,
                        0,
                        false,
                        IntPtr.Zero);
                }
                catch { }
            }

            // Zapisanie w konfiguracji lokalnej
            var existing = _currentDevice.Presets.FirstOrDefault(p => p.PresetNumber == pNum);
            if (existing != null)
            {
                existing.Name = pName;
            }
            else
            {
                _currentDevice.Presets.Add(new DahuaPresetItem { PresetNumber = pNum, Name = pName });
            }

            DahuaConfigService.SaveDevices(_devices);
            RefreshPresetsList();
            StatusLabel.Text = $"Zapisano bieżącą pozycję jako Preset {pNum} ('{pName}').";
        }

        private void DeletePreset_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDevice == null) return;
            if (PresetsComboBox.SelectedItem is ComboBoxItem item && item.Tag is int pNum)
            {
                int channelIndex = Math.Max(0, _currentChannel - 1);
                if (_loginId != IntPtr.Zero)
                {
                    try
                    {
                        DahuaNetSdk.CLIENT_DHPTZControlEx2(
                            _loginId,
                            channelIndex,
                            (uint)DahuaNetSdk.EM_PTZ_COMMAND.POINT_DEL_CONTROL, // 12
                            0,
                            pNum,
                            0,
                            false,
                            IntPtr.Zero);
                    }
                    catch { }
                }

                _currentDevice.Presets.RemoveAll(p => p.PresetNumber == pNum);
                DahuaConfigService.SaveDevices(_devices);
                RefreshPresetsList();
                StatusLabel.Text = $"Usunięto Preset {pNum}.";
            }
        }

        #endregion

        #region Live Stream & Connection

        private void StreamTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loginId != IntPtr.Zero && _realPlayHandle != IntPtr.Zero)
            {
                StartLiveStreamForChannel(_currentChannel);
            }
        }

        private void Connect_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDevice == null) return;

            if (!_sdkInitialized)
            {
                InitializeSdk();
                if (!_sdkInitialized)
                {
                    MessageBox.Show("Nie zainicjalizowano Dahua NetSDK. Sprawdź czy pliki dhnetsdk.dll i dhplay.dll (x64) znajdują się w folderze aplikacji.", "Błąd SDK", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            Disconnect();

            string ip = _currentDevice.IpAddress;
            ushort port = _currentDevice.Port;
            string user = _currentDevice.Username;
            string pass = _currentDevice.Password;

            StatusLabel.Text = $"Logowanie do {ip}:{port}…";

            try
            {
                var deviceInfo = new DahuaNetSdk.NET_DEVICEINFO();
                int err = 0;
                _loginId = DahuaNetSdk.CLIENT_Login(ip, port, user, pass, out deviceInfo, out err);

                if (_loginId == IntPtr.Zero)
                {
                    uint lastError = DahuaNetSdk.CLIENT_GetLastError();
                    StatusLabel.Text = $"Błąd logowania do urządzenia {ip}. {DahuaNetSdk.GetErrorDescription(lastError)}";
                    return;
                }

                ConnectButton.IsEnabled = false;
                DisconnectButton.IsEnabled = true;
                StreamOverlay.Visibility = Visibility.Collapsed;

                // Automatyczny odczyt nazw kanałów i presetów z urządzenia przy połączeniu
                QueryDeviceState(showDialog: false);

                StartLiveStreamForChannel(_currentChannel);
            }
            catch (Exception ex)
            {
                StatusLabel.Text = $"Błąd połączenia: {ex.Message}";
            }
        }

        private void StartLiveStreamForChannel(int channelNumber)
        {
            if (_loginId == IntPtr.Zero) return;

            try
            {
                if (_realPlayHandle != IntPtr.Zero)
                {
                    DahuaNetSdk.CLIENT_StopRealPlayEx(_realPlayHandle);
                    _realPlayHandle = IntPtr.Zero;
                }
                if (_playbackHandle != IntPtr.Zero)
                {
                    DahuaNetSdk.CLIENT_StopPlayBack(_playbackHandle);
                    _playbackHandle = IntPtr.Zero;
                }

                if (!_primaryPictureBox.IsHandleCreated)
                {
                    _primaryPictureBox.CreateControl();
                }

                int channelIndex = Math.Max(0, channelNumber - 1);
                var streamType = StreamTypeBox.SelectedIndex == 1 ? DahuaNetSdk.EM_RealPlayType.Realplay_1 : DahuaNetSdk.EM_RealPlayType.Realplay;

                _realPlayHandle = DahuaNetSdk.CLIENT_RealPlayEx(_loginId, channelIndex, _primaryPictureBox.Handle, streamType);
                if (_realPlayHandle == IntPtr.Zero)
                {
                    _realPlayHandle = DahuaNetSdk.CLIENT_RealPlay(_loginId, channelIndex, _primaryPictureBox.Handle);
                }

                string chName = _currentDevice?.GetChannelName(channelNumber) ?? $"Kanał {channelNumber}";

                if (_realPlayHandle == IntPtr.Zero)
                {
                    uint lastError = DahuaNetSdk.CLIENT_GetLastError();
                    StatusLabel.Text = $"Błąd uruchomienia strumienia na kanale '{chName}'. {DahuaNetSdk.GetErrorDescription(lastError)}";
                }
                else
                {
                    StatusLabel.Text = $"Odtwarzanie na żywo: {chName} (CH {channelNumber}) | {(streamType == DahuaNetSdk.EM_RealPlayType.Realplay ? "Strumień główny HD" : "Podstrumień")}";
                    HighlightActiveChannelButton(channelNumber);
                }
            }
            catch (Exception ex)
            {
                StatusLabel.Text = $"Błąd uruchamiania kanału {channelNumber}: {ex.Message}";
            }
        }

        private void Disconnect()
        {
            try
            {
                if (_realPlayHandle != IntPtr.Zero)
                {
                    DahuaNetSdk.CLIENT_StopRealPlayEx(_realPlayHandle);
                    _realPlayHandle = IntPtr.Zero;
                }

                if (_playbackHandle != IntPtr.Zero)
                {
                    DahuaNetSdk.CLIENT_StopPlayBack(_playbackHandle);
                    _playbackHandle = IntPtr.Zero;
                }

                if (_loginId != IntPtr.Zero)
                {
                    DahuaNetSdk.CLIENT_Logout(_loginId);
                    _loginId = IntPtr.Zero;
                }
            }
            catch { }

            ConnectButton.IsEnabled = true;
            DisconnectButton.IsEnabled = false;
            StatusLabel.Text = "Rozłączono ze strumieniem.";
            _primaryPictureBox.Invalidate();
        }

        private void Disconnect_Click(object sender, RoutedEventArgs e)
        {
            Disconnect();
        }

        private void OnSdkDisconnected(IntPtr lLoginID, string pchDVRIP, int nDVRPort, IntPtr dwUser)
        {
            Dispatcher.Invoke(() =>
            {
                StatusLabel.Text = $"Utracono połączenie z {pchDVRIP}:{nDVRPort}. Trwa automatyczne wznawianie…";
            });
        }

        private void OnSdkReconnected(IntPtr lLoginID, string pchDVRIP, int nDVRPort, IntPtr dwUser)
        {
            Dispatcher.Invoke(() =>
            {
                StatusLabel.Text = $"Przywrócono połączenie z {pchDVRIP}:{nDVRPort}.";
                StartLiveStreamForChannel(_currentChannel);
            });
        }

        #endregion

        #region Manual PTZ Controls

        private void SendPtz(DahuaNetSdk.EM_PTZ_COMMAND command, bool stop)
        {
            if (_loginId == IntPtr.Zero) return;
            int channelIndex = Math.Max(0, _currentChannel - 1);

            try
            {
                DahuaNetSdk.CLIENT_DHPTZControlEx2(
                    _loginId,
                    channelIndex,
                    (uint)command,
                    4,
                    4,
                    0,
                    stop,
                    IntPtr.Zero);
            }
            catch (Exception ex)
            {
                StatusLabel.Text = $"Błąd sterowania PTZ: {ex.Message}";
            }
        }

        private void PtzUp_Down(object sender, MouseButtonEventArgs e) => SendPtz(DahuaNetSdk.EM_PTZ_COMMAND.UP, false);
        private void PtzDown_Down(object sender, MouseButtonEventArgs e) => SendPtz(DahuaNetSdk.EM_PTZ_COMMAND.DOWN, false);
        private void PtzLeft_Down(object sender, MouseButtonEventArgs e) => SendPtz(DahuaNetSdk.EM_PTZ_COMMAND.LEFT, false);
        private void PtzRight_Down(object sender, MouseButtonEventArgs e) => SendPtz(DahuaNetSdk.EM_PTZ_COMMAND.RIGHT, false);
        private void PtzZoomIn_Down(object sender, MouseButtonEventArgs e) => SendPtz(DahuaNetSdk.EM_PTZ_COMMAND.ZOOM_IN, false);
        private void PtzZoomOut_Down(object sender, MouseButtonEventArgs e) => SendPtz(DahuaNetSdk.EM_PTZ_COMMAND.ZOOM_OUT, false);
        private void PtzFocusIn_Down(object sender, MouseButtonEventArgs e) => SendPtz(DahuaNetSdk.EM_PTZ_COMMAND.FOCUS_IN, false);
        private void PtzFocusOut_Down(object sender, MouseButtonEventArgs e) => SendPtz(DahuaNetSdk.EM_PTZ_COMMAND.FOCUS_OUT, false);

        // Stop must use the same command as the movement that was started.
        private void Ptz_Stop(object sender, MouseButtonEventArgs e)
        {
            if (sender is Button button &&
                Enum.TryParse(button.Tag?.ToString(), true, out DahuaNetSdk.EM_PTZ_COMMAND command))
            {
                SendPtz(command, true);
            }
        }

        #endregion

        #region Snapshot & OCR Integration

        private string? CaptureFrameToFile()
        {
            if (_realPlayHandle == IntPtr.Zero && _playbackHandle == IntPtr.Zero)
            {
                MessageBox.Show("Najpierw uruchom strumień wideo.", "Brak strumienia", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }

            IntPtr handle = _realPlayHandle != IntPtr.Zero ? _realPlayHandle : _playbackHandle;

            try
            {
                string folder = Path.Combine(AppContext.BaseDirectory, "zrzuty");
                Directory.CreateDirectory(folder);
                string filename = $"Dahua_CH{_currentChannel}_{DateTime.Now:yyyyMMdd_HHmmssfff}.jpg";
                string fullPath = Path.Combine(folder, filename);

                bool ok = DahuaNetSdk.CLIENT_CapturePictureEx(handle, fullPath, DahuaNetSdk.NET_CAPTURE_FORMATS.JPEG);
                if (ok && File.Exists(fullPath))
                {
                    StatusLabel.Text = $"Zapisano zrzut: {filename}";
                    return fullPath;
                }
                else
                {
                    StatusLabel.Text = "Błąd zapisu zrzutu z biblioteki Dahua NetSDK.";
                    return null;
                }
            }
            catch (Exception ex)
            {
                StatusLabel.Text = $"Wyjątek podczas zapisu klatki: {ex.Message}";
                return null;
            }
        }

        private void Snapshot_Click(object sender, RoutedEventArgs e)
        {
            CaptureFrameToFile();
        }

        private void OcrFromLive_Click(object sender, RoutedEventArgs e)
        {
            string? framePath = CaptureFrameToFile();
            if (string.IsNullOrEmpty(framePath)) return;

            try
            {
                var frame = new BitmapImage();
                frame.BeginInit();
                frame.CacheOption = BitmapCacheOption.OnLoad;
                frame.UriSource = new Uri(framePath);
                frame.EndInit();
                frame.Freeze();

                var ocrDialog = new PlateOcrWindow(frame, 0) { Owner = this };
                if (ocrDialog.ShowDialog() == true)
                {
                    MessageBox.Show(
                        $"Rozpoznano numer tablicy: {ocrDialog.PlateNumber}\nMarka: {ocrDialog.VehicleMake}",
                        "Rozpoznanie tablicy ze strumienia Dahua",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Błąd otwarcia modułu OCR: {ex.Message}", "Błąd OCR", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Archive / Playback by Channel & Date to Main VideoPlayer

        private DateTime? GetStartDateTime()
        {
            if (StartDatePicker.SelectedDate == null) return null;
            if (!TimeSpan.TryParse(StartTimeBox.Text.Trim(), out var time) ||
                time < TimeSpan.Zero || time >= TimeSpan.FromDays(1))
                return null;
            return StartDatePicker.SelectedDate.Value.Date + time;
        }

        private DateTime? GetEndDateTime()
        {
            if (EndDatePicker.SelectedDate == null) return null;
            if (!TimeSpan.TryParse(EndTimeBox.Text.Trim(), out var time) ||
                time < TimeSpan.Zero || time >= TimeSpan.FromDays(1))
                return null;
            return EndDatePicker.SelectedDate.Value.Date + time;
        }

        private int GetSelectedArchiveChannel()
        {
            if (ArchiveChannelBox.SelectedItem is ComboBoxItem item && item.Tag is int ch) return ch;
            return _currentChannel;
        }

        private async void LoadToPlayer_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDevice == null) return;

            var start = GetStartDateTime();
            var end = GetEndDateTime();

            if (start == null || end == null || end <= start)
            {
                MessageBox.Show("Wybierz poprawny przedział czasu (początek musi być wcześniejszy niż koniec).", "Błędny zakres dat", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int channel = GetSelectedArchiveChannel();
            string chName = _currentDevice.GetChannelName(channel);
            string ip = _currentDevice.IpAddress;
            ushort port = _currentDevice.Port;
            string user = _currentDevice.Username;
            string pass = _currentDevice.Password;

            if (_loginId == IntPtr.Zero)
            {
                // Reuse the standard connection path so CLIENT_Init and callbacks
                // are always configured before any login attempt.
                Connect_Click(sender, e);
                if (_loginId == IntPtr.Zero)
                {
                    MessageBox.Show("Nie udało się połączyć z rejestratorem. Sprawdź stan SDK, adres, port i dane logowania.", "Błąd logowania", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            string outputDir = Path.Combine(AppContext.BaseDirectory, "nagrania_dahua");
            Directory.CreateDirectory(outputDir);
            string filename = $"Dahua_{chName.Replace(' ', '_')}_CH{channel}_{start.Value:yyyyMMdd_HHmmss}-{end.Value:HHmmss}.mp4";
            string targetFilePath = Path.Combine(outputDir, filename);

            DownloadProgressBar.Visibility = Visibility.Visible;
            DownloadProgressBar.IsIndeterminate = true;
            DownloadStatusText.Visibility = Visibility.Visible;
            DownloadStatusText.Text = $"Pobieranie nagrania: {chName} (CH {channel}) za okres {start.Value:g} — {end.Value:g}…";
            LoadToPlayerButton.IsEnabled = false;

            try
            {
                int channelIndex = Math.Max(0, channel - 1);
                var tmStart = DahuaNetSdk.NET_TIME.FromDateTime(start.Value);
                var tmEnd = DahuaNetSdk.NET_TIME.FromDateTime(end.Value);

                bool downloadFinished = false;

                await Task.Run(async () =>
                {
                    // Native download is asynchronous. Do not stop it after a fixed
                    // 15-second delay: larger archive segments need substantially longer.
                    using var completed = new System.Threading.ManualResetEventSlim(false);
                    DahuaNetSdk.fTimeDownLoadPosCallBack progressCallback =
                        (playHandle, totalSize, downloadedSize, index, recordInfo, userData) =>
                        {
                            if (totalSize > 0 && downloadedSize >= totalSize)
                                completed.Set();
                        };

                    IntPtr hDown = DahuaNetSdk.CLIENT_DownloadByTimeEx(
                        _loginId, channelIndex, 0, ref tmStart, ref tmEnd, targetFilePath,
                        progressCallback, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

                    if (hDown != IntPtr.Zero)
                    {
                        try
                        {
                            // Timeout prevents a hung SDK call from blocking the UI forever.
                            bool signaled = await Task.Run(() => completed.Wait(TimeSpan.FromMinutes(30)));
                            if (signaled && File.Exists(targetFilePath) &&
                                new FileInfo(targetFilePath).Length > 0)
                                downloadFinished = true;
                        }
                        finally
                        {
                            DahuaNetSdk.CLIENT_StopDownload(hDown);
                            GC.KeepAlive(progressCallback);
                        }
                    }

                    // 2. Fallback na wbudowany FFmpeg / RTSP playback
                    if (!downloadFinished)
                    {
                        string ffmpegPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
                        if (File.Exists(ffmpegPath))
                        {
                            string rtspUrl = $"rtsp://{user}:{pass}@{ip}:554/cam/playback?channel={channel}&starttime={start.Value:yyyy_MM_dd_HH_mm_ss}&endtime={end.Value:yyyy_MM_dd_HH_mm_ss}";
                            var psi = new ProcessStartInfo(ffmpegPath)
                            {
                                UseShellExecute = false,
                                CreateNoWindow = true
                            };
                            psi.ArgumentList.Add("-y");
                            psi.ArgumentList.Add("-rtsp_transport");
                            psi.ArgumentList.Add("tcp");
                            psi.ArgumentList.Add("-t");
                            int durationSec = Math.Max(1, (int)Math.Ceiling((end.Value - start.Value).TotalSeconds));
                            psi.ArgumentList.Add(durationSec.ToString(System.Globalization.CultureInfo.InvariantCulture));
                            psi.ArgumentList.Add("-i");
                            psi.ArgumentList.Add(rtspUrl);
                            psi.ArgumentList.Add("-c");
                            psi.ArgumentList.Add("copy");
                            psi.ArgumentList.Add(targetFilePath);

                            using var proc = Process.Start(psi);
                            if (proc != null)
                            {
                                await proc.WaitForExitAsync();
                                if (File.Exists(targetFilePath) && new FileInfo(targetFilePath).Length > 0)
                                {
                                    downloadFinished = true;
                                }
                            }
                        }
                    }
                });

                if (File.Exists(targetFilePath) && new FileInfo(targetFilePath).Length > 0)
                {
                    DownloadStatusText.Text = "Pobrano! Ładowanie do Głównego Odtwarzacza VideoPlayer…";

                    if (Owner is MainWindow main)
                    {
                        await main.OpenDahuaStreamAsync(targetFilePath);
                        MessageBox.Show(
                            $"Nagranie z kamery '{chName}' ({start.Value:HH:mm:ss} — {end.Value:HH:mm:ss}) zostało załadowane do Głównego Odtwarzacza!\n\n" +
                            "Dostępne funkcje:\n" +
                            "• Pełna oś czasu i przewijanie suwakiem\n" +
                            "• Klatkowanie strzałkami w przód i w tył\n" +
                            "• Rozpoznawanie tablic (OCR Tesseract)\n" +
                            "• Zaznaczanie fragmentów (I / O) i eksport MP4\n" +
                            "• Raporty PDF i CSV ze zdarzeń",
                            "Wczytano strumień Dahua do odtwarzacza",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }
                }
                else
                {
                    DownloadStatusText.Text = "Nie znaleziono nagrania na rejestratorze w wybranym przedziale czasu.";
                    MessageBox.Show("Rejestrator nie zwrócił nagrania dla wybranego kanału i przedziału dat.", "Brak nagrania", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                DownloadStatusText.Text = $"Błąd pobierania: {ex.Message}";
                MessageBox.Show($"Błąd pobierania nagrania: {ex.Message}", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                DownloadProgressBar.Visibility = Visibility.Collapsed;
                LoadToPlayerButton.IsEnabled = true;
            }
        }

        private void PlayArchiveInWindow_Click(object sender, RoutedEventArgs e)
        {
            var start = GetStartDateTime();
            var end = GetEndDateTime();
            if (start == null || end == null || end <= start)
            {
                MessageBox.Show("Wybierz poprawny przedział czasu.", "Błędny zakres dat", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int channel = GetSelectedArchiveChannel();
            int channelIndex = Math.Max(0, channel - 1);

            if (_loginId == IntPtr.Zero)
            {
                Connect_Click(sender, e);
                if (_loginId == IntPtr.Zero) return;
            }

            try
            {
                if (_realPlayHandle != IntPtr.Zero)
                {
                    DahuaNetSdk.CLIENT_StopRealPlayEx(_realPlayHandle);
                    _realPlayHandle = IntPtr.Zero;
                }
                if (_playbackHandle != IntPtr.Zero)
                {
                    DahuaNetSdk.CLIENT_StopPlayBack(_playbackHandle);
                    _playbackHandle = IntPtr.Zero;
                }

                var tmStart = DahuaNetSdk.NET_TIME.FromDateTime(start.Value);
                var tmEnd = DahuaNetSdk.NET_TIME.FromDateTime(end.Value);

                _playbackHandle = DahuaNetSdk.CLIENT_PlayBackByTimeEx(_loginId, channelIndex, ref tmStart, ref tmEnd, _primaryPictureBox.Handle, IntPtr.Zero, IntPtr.Zero);
                string chName = _currentDevice?.GetChannelName(channel) ?? $"Kanał {channel}";

                if (_playbackHandle == IntPtr.Zero)
                {
                    uint err = DahuaNetSdk.CLIENT_GetLastError();
                    StatusLabel.Text = $"Błąd odtwarzania archiwalnego: {DahuaNetSdk.GetErrorDescription(err)}";
                }
                else
                {
                    StatusLabel.Text = $"Odtwarzanie archiwalne: {chName} ({start.Value:g} — {end.Value:g})";
                }
            }
            catch (Exception ex)
            {
                StatusLabel.Text = $"Wyjątek odtwarzania archiwalnego: {ex.Message}";
            }
        }

        #endregion

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            Disconnect();
            if (_sdkInitialized)
            {
                try { DahuaNetSdk.CLIENT_Cleanup(); } catch { }
                _sdkInitialized = false;
            }
        }
    }
}
