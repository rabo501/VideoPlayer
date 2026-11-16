using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Net;
using System.Text;
using System.Text.Json;
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
        private string? _connectedDeviceId;
        private IntPtr _loginId = IntPtr.Zero;
        private IntPtr _realPlayHandle = IntPtr.Zero;
        private IntPtr _playbackHandle = IntPtr.Zero;
        private bool _sdkInitialized = false;
        private int _currentChannel = 1;
        // Channel count reported by NET_DEVICEINFO during the successful login.
        // Retain it so the manual device-read action does not need another risky
        // vendor configuration call.
        private int _deviceChannelCount = 0;
        // null means the recorder does not expose channel-state API; an empty set means no active channels.
        private HashSet<int>? _activeChannelNumbers;
        private readonly List<Button> _channelButtons = new();
        // Dates returned by the last detailed archive search (bright green).
        private readonly HashSet<DateTime> _archiveAvailableDays = new();
        // Dates verified against the recorder for the selected device/channel (dark green).
        private readonly HashSet<DateTime> _recorderAvailableDays = new();
        private readonly HashSet<Calendar> _hookedArchiveCalendars = new();
        private readonly Dictionary<string, HashSet<DateTime>> _recorderAvailabilityCache = new();
        private readonly HashSet<string> _availabilityQueriesInProgress = new();
        private readonly System.Threading.SemaphoreSlim _archiveAvailabilitySemaphore = new(1, 1);
        private readonly List<MultiViewSlot> _multiViewSlots = new();
        private readonly List<IntPtr> _multiPlaybackHandles = new();
        private readonly DispatcherTimer _playbackUiTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private DateTime _playbackRangeStart;
        private DateTime _playbackRangeEnd;
        private DateTime _playbackSegmentStartedAt;
        private double _playbackElapsedBeforePause;
        private bool _synchronizedPlaybackPaused;
        private bool _isSeekingPlayback;
        private bool _updatingChannelChecks;
        private bool _connectForCalendarAvailability;
        private double _archivePlaybackSpeed = 1.0;
        private bool _updatingArchiveSpeed;
        private double _recordingTimelineZoom = 1.0;
        private double _recordingTimelineStartHour = 0.0;
        private double GetPlaybackDurationSeconds() =>
            Math.Max(1, (_playbackRangeEnd - _playbackRangeStart).TotalSeconds);

        // Slider values remain offsets from playback start, but its visible
        // range follows the currently zoomed portion of the 24-hour timeline.
        private void UpdatePlaybackSliderRange(bool keepPlayheadVisible)
        {
            if (PlaybackPositionSlider == null || _multiPlaybackHandles.Count == 0) return;

            double visibleHours = 24.0 / _recordingTimelineZoom;
            double viewStartHour = Math.Clamp(_recordingTimelineStartHour, 0, 24.0 - visibleHours);
            double viewEndHour = viewStartHour + visibleHours;
            double playbackStartHour = (_playbackRangeStart - _playbackRangeStart.Date).TotalHours;

            if (keepPlayheadVisible)
            {
                double elapsed = _playbackElapsedBeforePause;
                if (!_synchronizedPlaybackPaused)
                    elapsed += (DateTime.Now - _playbackSegmentStartedAt).TotalSeconds * _archivePlaybackSpeed;

                double playheadHour = playbackStartHour + elapsed / 3600.0;
                if (playheadHour < viewStartHour || playheadHour > viewEndHour)
                {
                    _recordingTimelineStartHour = Math.Clamp(
                        playheadHour - visibleHours * 0.15, 0, 24.0 - visibleHours);
                    viewStartHour = _recordingTimelineStartHour;
                    viewEndHour = viewStartHour + visibleHours;
                    RefreshRecordingTimeline();
                }
            }

            // Important: do NOT clamp this range to the recording duration.
            // The slider must represent the complete visible timeline window.
            // Example: a 1-hour recording in a 2-hour zoomed window occupies
            // exactly half of the slider track, matching the green timeline block.
            double minOffset = (viewStartHour - playbackStartHour) * 3600.0;
            double maxOffset = (viewEndHour - playbackStartHour) * 3600.0;
            if (maxOffset <= minOffset) maxOffset = minOffset + 1;

            PlaybackPositionSlider.Minimum = minOffset;
            PlaybackPositionSlider.Maximum = maxOffset;

            double currentOffset = _playbackElapsedBeforePause;
            if (!_synchronizedPlaybackPaused)
                currentOffset += (DateTime.Now - _playbackSegmentStartedAt).TotalSeconds * _archivePlaybackSpeed;

            // The displayed thumb position is allowed to sit at the edge of the
            // visible window, while actual SDK seeking is clamped to the recording.
            PlaybackPositionSlider.Value = Math.Clamp(currentOffset, minOffset, maxOffset);
        }

        private sealed class MultiViewSlot
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

        // Callback references to prevent GC collection
        private DahuaNetSdk.fDisConnect? _disConnectCallback;
        private DahuaNetSdk.fHaveReConnect? _haveReConnectCallback;

        public DahuaStreamWindow()
        {
            InitializeComponent();
            _primaryPictureBox.CreateControl();
            PrimaryVideoHost.Child = _primaryPictureBox;

            InitializeDatePickers();
            _playbackUiTimer.Tick += PlaybackUiTimer_Tick;
        }

        private void InitializeDatePickers()
        {
            DateTime now = DateTime.Now;
            StartDatePicker.SelectedDate = now;
            EndDatePicker.SelectedDate = now;
            StartTimeBox.Text = now.AddHours(-1).ToString("HH:mm:ss");
            EndTimeBox.Text = now.ToString("HH:mm:ss");
        }

        private void ArchiveChannelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Detailed-search markers are channel-specific; recorder availability is cached separately.
            _archiveAvailableDays.Clear();
            _recorderAvailableDays.Clear();
            string cachePrefix = $"{_currentDevice?.Id}|{GetSelectedArchiveChannel()}|";
            foreach (var entry in _recorderAvailabilityCache.Where(pair => pair.Key.StartsWith(cachePrefix, StringComparison.Ordinal)))
                _recorderAvailableDays.UnionWith(entry.Value);
            RefreshOpenArchiveCalendars();
            foreach (DatePicker picker in new[] { StartDatePicker, EndDatePicker })
                LoadRecorderAvailabilityForVisibleMonth(picker);
        }

        private void ArchiveDatePicker_CalendarOpened(object sender, RoutedEventArgs e)
        {
            if (sender is not DatePicker picker) return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                picker.ApplyTemplate();
                if (picker.Template.FindName("PART_Popup", picker) is not Popup popup ||
                    popup.Child is not DependencyObject popupRoot)
                    return;

                Calendar? calendar = popupRoot as Calendar ?? FindVisualChild<Calendar>(popupRoot);
                if (calendar == null) return;

                if (_hookedArchiveCalendars.Add(calendar))
                {
                    calendar.DisplayDateChanged += (_, _) =>
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            RefreshCalendarButtons(calendar);
                            _ = LoadRecorderAvailabilityForMonthAsync(calendar.DisplayDate);
                        }));
                    };
                }

                RefreshCalendarButtons(calendar);
                _ = LoadRecorderAvailabilityForMonthAsync(calendar.DisplayDate);
            }));
        }

        private void RefreshOpenArchiveCalendars()
        {
            RefreshDatePickerCalendar(StartDatePicker);
            RefreshDatePickerCalendar(EndDatePicker);
        }

        private void LoadRecorderAvailabilityForVisibleMonth(DatePicker picker)
        {
            if (picker.Template.FindName("PART_Popup", picker) is Popup popup &&
                popup.IsOpen && popup.Child is DependencyObject popupRoot)
            {
                Calendar? calendar = popupRoot as Calendar ?? FindVisualChild<Calendar>(popupRoot);
                if (calendar != null)
                    _ = LoadRecorderAvailabilityForMonthAsync(calendar.DisplayDate);
            }
        }

        private async Task LoadRecorderAvailabilityForMonthAsync(DateTime displayedDate)
        {
            if (_currentDevice == null || ArchiveChannelBox.SelectedItem == null)
                return;

            int channel = GetSelectedArchiveChannel();
            DateTime monthStart = new(displayedDate.Year, displayedDate.Month, 1);
            string cacheKey = $"{_currentDevice.Id}|{channel}|{monthStart:yyyyMM}";

            if (_recorderAvailabilityCache.TryGetValue(cacheKey, out var cachedDays))
            {
                _recorderAvailableDays.UnionWith(cachedDays);
                RefreshOpenArchiveCalendars();
                CalendarAvailabilityStatusText.Text =
                    $"Dostępność pobrana z pamięci: {cachedDays.Count} dni z nagraniami w {monthStart:MM.yyyy}.";
                return;
            }

            if (!_availabilityQueriesInProgress.Add(cacheKey))
                return;

            string deviceId = _currentDevice.Id;
            CalendarAvailabilityStatusText.Text =
                $"Sprawdzanie dostępności nagrań na rejestratorze: {monthStart:MM.yyyy}, kanał {channel}…";

            try
            {
                if (_loginId == IntPtr.Zero || !string.Equals(_connectedDeviceId, deviceId, StringComparison.Ordinal))
                {
                    _connectForCalendarAvailability = true;
                    try
                    {
                        Connect_Click(this, new RoutedEventArgs());
                    }
                    finally
                    {
                        _connectForCalendarAvailability = false;
                    }
                    if (_loginId == IntPtr.Zero || !string.Equals(_connectedDeviceId, deviceId, StringComparison.Ordinal))
                        throw new InvalidOperationException("Nie udało się połączyć z wybranym rejestratorem.");
                }

                IntPtr login = _loginId;
                HashSet<DateTime> days = await QueryRecorderAvailabilityForMonthAsync(login, channel, monthStart);

                if (_currentDevice?.Id != deviceId || GetSelectedArchiveChannel() != channel)
                    return;

                _recorderAvailabilityCache[cacheKey] = days;
                _recorderAvailableDays.UnionWith(days);
                RefreshOpenArchiveCalendars();
                CalendarAvailabilityStatusText.Text = days.Count == 0
                    ? $"Rejestrator nie zgłasza nagrań w {monthStart:MM.yyyy} dla kanału {channel}."
                    : $"Rejestrator zgłasza nagrania w {days.Count} dniach miesiąca {monthStart:MM.yyyy} (kanał {channel}).";
            }
            catch (Exception ex)
            {
                if (_currentDevice?.Id == deviceId && GetSelectedArchiveChannel() == channel)
                    CalendarAvailabilityStatusText.Text = $"Nie udało się pobrać dostępności: {ex.Message}";
                WriteSdkDiagnostic($"Calendar availability query failed. Device={deviceId}; channel={channel}; month={monthStart:yyyy-MM}; {ex}");
            }
            finally
            {
                _availabilityQueriesInProgress.Remove(cacheKey);
            }
        }

        private async Task<HashSet<DateTime>> QueryRecorderAvailabilityForMonthAsync(
            IntPtr login, int channel, DateTime monthStart)
        {
            await _archiveAvailabilitySemaphore.WaitAsync();
            try
            {
                return await Task.Run(() =>
                {
                    var availableDays = new HashSet<DateTime>();
                    ConfigureDahuaRecordModes(login);

                    DateTime monthEnd = monthStart.AddMonths(1);
                    DateTime lastDay = monthEnd < DateTime.Today.AddDays(1)
                        ? monthEnd
                        : DateTime.Today.AddDays(1);

                    const int maxRecordsPerDay = 1;
                    int itemSize = Marshal.SizeOf<DahuaNetSdk.NET_RECORDFILE_INFO>();
                    int bufferSize = checked(itemSize * maxRecordsPerDay);
                    IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
                    try
                    {
                        for (DateTime day = monthStart; day < lastDay; day = day.AddDays(1))
                        {
                            for (int i = 0; i < bufferSize; i++)
                                Marshal.WriteByte(buffer, i, 0);

                            var from = DahuaNetSdk.NET_TIME.FromDateTime(day);
                            var to = DahuaNetSdk.NET_TIME.FromDateTime(day.AddDays(1));
                            int count;

                            bool ok = DahuaNetSdk.CLIENT_QueryRecordFile(
                                login, Math.Max(0, channel - 1), 0,
                                ref from, ref to, null,
                                buffer, bufferSize, out count, 8000, false);

                            if (!ok)
                            {
                                uint error = DahuaNetSdk.CLIENT_GetLastError();
                                throw new InvalidOperationException(
                                    $"Zapytanie o {day:dd.MM.yyyy} nie powiodło się: {DahuaNetSdk.GetErrorDescription(error)}");
                            }

                            if (count > 0)
                                availableDays.Add(day.Date);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }

                    return availableDays;
                });
            }
            finally
            {
                _archiveAvailabilitySemaphore.Release();
            }
        }

        private void RefreshDatePickerCalendar(DatePicker picker)
        {
            if (picker.Template.FindName("PART_Popup", picker) is Popup popup &&
                popup.IsOpen && popup.Child is DependencyObject popupRoot)
            {
                Calendar? calendar = popupRoot as Calendar ?? FindVisualChild<Calendar>(popupRoot);
                if (calendar != null) RefreshCalendarButtons(calendar);
            }
        }

        private void RefreshCalendarButtons(Calendar calendar)
        {
            foreach (CalendarDayButton dayButton in FindVisualChildren<CalendarDayButton>(calendar))
            {
                dayButton.ClearValue(Control.BackgroundProperty);
                dayButton.ClearValue(Control.ForegroundProperty);
                dayButton.ClearValue(Control.BorderBrushProperty);
                dayButton.ClearValue(Control.BorderThicknessProperty);
                dayButton.ClearValue(Control.FontWeightProperty);
                dayButton.ClearValue(ToolTipService.ToolTipProperty);

                if (dayButton.DataContext is DateTime date)
                {
                    bool foundInLastSearch = _archiveAvailableDays.Contains(date.Date);
                    bool availableOnRecorder = _recorderAvailableDays.Contains(date.Date);
                    if (foundInLastSearch || availableOnRecorder)
                    {
                        dayButton.ToolTip = foundInLastSearch
                            ? $"Znaleziono nagrania w ostatnim wyszukiwaniu: {date:dd.MM.yyyy}"
                            : $"Rejestrator zgłasza nagrania: {date:dd.MM.yyyy}";

                        // Keep the native blue selection treatment distinct from availability.
                        if (calendar.SelectedDate?.Date != date.Date)
                        {
                            dayButton.Background = foundInLastSearch
                                ? new SolidColorBrush(Color.FromRgb(22, 163, 74))
                                : new SolidColorBrush(Color.FromRgb(20, 83, 45));
                            dayButton.Foreground = Brushes.White;
                            dayButton.BorderBrush = foundInLastSearch
                                ? new SolidColorBrush(Color.FromRgb(134, 239, 172))
                                : new SolidColorBrush(Color.FromRgb(74, 222, 128));
                            dayButton.BorderThickness = new Thickness(1);
                            dayButton.FontWeight = FontWeights.Bold;
                        }
                    }
                }
            }
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match) return match;
                T? nested = FindVisualChild<T>(child);
                if (nested != null) return nested;
            }
            return null;
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match) yield return match;
                foreach (T nested in FindVisualChildren<T>(child)) yield return nested;
            }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            InitializeSdk();
            LoadDeviceList();
        }

        private static void WriteSdkDiagnostic(string message)
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
                // Diagnostics must never prevent the application from running.
            }
        }

        private void InitializeSdk()
        {
            try
            {
                int deviceInfoSize = Marshal.SizeOf<DahuaNetSdk.NET_DEVICEINFO>();
                WriteSdkDiagnostic($"InitializeSdk started. NET_DEVICEINFO size={deviceInfoSize} bytes; process={Environment.Is64BitProcess switch { true => "x64", false => "x86" }}.");
                if (deviceInfoSize != 53)
                {
                    throw new InvalidOperationException(
                        $"Nieprawidłowy rozmiar NET_DEVICEINFO: {deviceInfoSize} bajtów (oczekiwano 53).");
                }

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

                WriteSdkDiagnostic("Calling CLIENT_Init.");
                bool initOk = DahuaNetSdk.CLIENT_Init(_disConnectCallback, IntPtr.Zero);
                WriteSdkDiagnostic($"CLIENT_Init returned: {initOk}.");
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
            try
            {
                _devices = DahuaConfigService.LoadDevices();
                DevicesListBox.ItemsSource = null;
                DevicesListBox.ItemsSource = _devices;
                ModernDevicesListBox.ItemsSource = null;
                ModernDevicesListBox.ItemsSource = _devices;
                if (_devices.Count > 0) DevicesListBox.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                _devices = new List<DahuaDeviceItem>();
                DevicesListBox.ItemsSource = null;
                StatusLabel.Text = "Nie można wczytać konfiguracji urządzeń. Oryginalny plik nie został nadpisany.";
                MessageBox.Show(ex.Message, "Błąd konfiguracji Dahua", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void DevicesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DevicesListBox.SelectedItem is DahuaDeviceItem dev)
            {
                if (_loginId != IntPtr.Zero && !string.Equals(_connectedDeviceId, dev.Id, StringComparison.Ordinal))
                    Disconnect();

                _currentDevice = dev;
                _activeChannelNumbers = null;
                ModernDevicesListBox.SelectedItem = dev;
                StopMultiLiveStreams();
                StopSynchronizedPlayback();
                DisposeMultiViewSlots();
                BuildModernChannelChecks();
                _archiveAvailableDays.Clear();
                _recorderAvailableDays.Clear();
                string selectedAvailabilityPrefix = $"{dev.Id}|{GetSelectedArchiveChannel()}|";
                foreach (var entry in _recorderAvailabilityCache.Where(pair => pair.Key.StartsWith(selectedAvailabilityPrefix, StringComparison.Ordinal)))
                    _recorderAvailableDays.UnionWith(entry.Value);
                RefreshOpenArchiveCalendars();
                ArchiveRecordsGrid.ItemsSource = null;
                _currentDevice.EnsureDefaultChannels(16);
                _currentDevice.EnsureDefaultPresets();

                DeviceCustomNameBox.Text = dev.Name;
                DeviceIpBox.Text = dev.IpAddress;
                DevicePortBox.Text = dev.Port.ToString();
                DeviceHttpPortBox.Text = dev.HttpPort.ToString();
                if (dev.CredentialsUnavailable)
                {
                    // DPAPI CurrentUser data cannot be decrypted under another Windows account.
                    // Do not silently replace unavailable secrets with empty values.
                    DeviceUserBox.Text = string.Empty;
                    DevicePassBox.Password = string.Empty;
                    CredentialsWarningText.Text = "Nie można odszyfrować zapisanych danych logowania (inne konto Windows lub komputer). Wpisz ponownie nazwę użytkownika i hasło, a następnie zapisz urządzenie.";
                    CredentialsWarningText.Visibility = Visibility.Visible;
                }
                else
                {
                    DeviceUserBox.Text = dev.Username;
                    DevicePassBox.Password = dev.Password;
                    CredentialsWarningText.Text = "Dane logowania są chronione przez Windows DPAPI.";
                    CredentialsWarningText.Visibility = Visibility.Visible;
                }

                _currentChannel = dev.Channel;
                RefreshChannelLists();
                RefreshPresetsList();

                ChannelNameEditBox.Text = dev.GetChannelName(_currentChannel);
                StreamTypeBox.SelectedIndex = dev.StreamType == DahuaNetSdk.EM_RealPlayType.Realplay ? 0 : 1;
                HighlightActiveChannelButton(_currentChannel);
            }
        }

        private void ModernDevicesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ModernDevicesListBox.SelectedItem is DahuaDeviceItem device &&
                !ReferenceEquals(DevicesListBox.SelectedItem, device))
                DevicesListBox.SelectedItem = device;
        }

        private void OpenConfiguration_Click(object sender, RoutedEventArgs e)
        {
            var window = new DahuaConfigurationWindow(_devices) { Owner = this };
            window.ShowDialog();
            _devices = DahuaConfigService.LoadDevices();
            DevicesListBox.ItemsSource = null;
            DevicesListBox.ItemsSource = _devices;
            ModernDevicesListBox.ItemsSource = null;
            ModernDevicesListBox.ItemsSource = _devices;
            if (_devices.Count > 0)
                DevicesListBox.SelectedIndex = 0;
            StatusLabel.Text = "Zamknięto konfigurację urządzeń.";
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

            string address = DeviceIpBox.Text.Trim();
            if (!IsValidEndpoint(address, DevicePortBox.Text, out ushort port, out string validationError))
            {
                MessageBox.Show(validationError, "Nieprawidłowy adres urządzenia", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!ushort.TryParse(DeviceHttpPortBox.Text, out ushort httpPort) || httpPort == 0)
            {
                MessageBox.Show("Port HTTP musi być liczbą z zakresu 1–65535.", "Nieprawidłowy port HTTP",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.Equals(_connectedDeviceId, _currentDevice.Id, StringComparison.Ordinal))
                Disconnect();

            _currentDevice.Name = name;
            _currentDevice.IpAddress = address;
            _currentDevice.Port = port;
            _currentDevice.HttpPort = httpPort;
            if (_currentDevice.CredentialsUnavailable &&
                (string.IsNullOrWhiteSpace(DeviceUserBox.Text) || string.IsNullOrEmpty(DevicePassBox.Password)))
            {
                MessageBox.Show(
                    "Nie można odszyfrować poprzednich danych logowania. Wpisz ponownie nazwę użytkownika i hasło przed zapisaniem urządzenia.",
                    "Wymagane ponowne podanie danych", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _currentDevice.SetCredentials(DeviceUserBox.Text.Trim(), DevicePassBox.Password);
            DahuaConfigService.SaveDevices(_devices);
            CredentialsWarningText.Text = "Dane logowania są chronione przez Windows DPAPI.";
            CredentialsWarningText.Visibility = Visibility.Visible;
            DevicesListBox.Items.Refresh();
            StatusLabel.Text = $"Zapisano urządzenie dla operatora: '{_currentDevice.Name}' ({_currentDevice.IpAddress}:{_currentDevice.Port})";
        }

        private void AddDevice_Click(object sender, RoutedEventArgs e)
        {
            string address = DeviceIpBox.Text.Trim();
            if (!IsValidEndpoint(address, DevicePortBox.Text, out ushort port, out string validationError))
            {
                MessageBox.Show(validationError, "Nieprawidłowy adres urządzenia", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!ushort.TryParse(DeviceHttpPortBox.Text, out ushort httpPort) || httpPort == 0)
            {
                MessageBox.Show("Port HTTP musi być liczbą z zakresu 1–65535.", "Nieprawidłowy port HTTP",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            string name = DeviceCustomNameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name)) name = $"Urządzenie {address}";

            var newDev = new DahuaDeviceItem
            {
                Name = name,
                IpAddress = address,
                Port = port,
                HttpPort = httpPort,
                Channel = 1,
                StreamType = DahuaNetSdk.EM_RealPlayType.Realplay
            };
            newDev.SetCredentials(
                string.IsNullOrWhiteSpace(DeviceUserBox.Text) ? "admin" : DeviceUserBox.Text.Trim(),
                DevicePassBox.Password);
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
                WriteSdkDiagnostic("Manual device read started.");
                // Channel count comes from NET_DEVICEINFO returned by CLIENT_Login.
                int channelCount = _deviceChannelCount;
                if (channelCount <= 0)
                    channelCount = _currentDevice.Channels.Count;

                WriteSdkDiagnostic($"Using channel count captured at login: {channelCount}.");
                if (channelCount > 0)
                    _currentDevice.EnsureDefaultChannels(channelCount);

                // Query channel titles with CLIENT_QueryChannelName using a bounded
                // 32-byte slot per channel. Never call the known-crashing
                // CLIENT_GetNewDevConfig("ChannelTitle") path here.
                WriteSdkDiagnostic($"Calling CLIENT_QueryChannelName for up to {channelCount} channels.");
                List<DahuaChannelInfo> remoteNames =
                    DahuaNetSdk.QueryChannelNamesFromDevice(_loginId, channelCount);
                WriteSdkDiagnostic($"CLIENT_QueryChannelName returned {remoteNames.Count} non-empty channel names.");

                int appliedNames = 0;
                if (remoteNames.Count > 0)
                {
                    var changes = remoteNames
                        .Where(remote => !string.IsNullOrWhiteSpace(remote.Name))
                        .Select(remote => new
                        {
                            remote.Number,
                            RemoteName = remote.Name.Trim(),
                            LocalName = _currentDevice.GetChannelName(remote.Number)
                        })
                        .Where(x => !string.Equals(x.LocalName, x.RemoteName, StringComparison.Ordinal))
                        .ToList();

                    if (changes.Count > 0)
                    {
                        string preview = string.Join(Environment.NewLine,
                            changes.Take(20).Select(x =>
                                $"CH {x.Number}: \"{x.LocalName}\"  →  \"{x.RemoteName}\""));
                        if (changes.Count > 20)
                            preview += Environment.NewLine + $"… oraz {changes.Count - 20} kolejnych zmian.";

                        var decision = MessageBox.Show(
                            $"Rejestrator zwrócił {remoteNames.Count} nazw kanałów. " +
                            $"Wykryto {changes.Count} różnic względem konfiguracji lokalnej.\n\n" +
                            preview + "\n\nCzy zastosować nazwy z rejestratora?\n" +
                            "Identyfikatory kanałów pozostaną bez zmian.",
                            "Porównanie nazw kanałów Dahua",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Question);

                        if (decision == MessageBoxResult.Yes)
                        {
                            foreach (var change in changes)
                                _currentDevice.SetChannelName(change.Number, change.RemoteName);
                            appliedNames = changes.Count;
                        }
                    }
                }

                int presetCount = -1;
                try
                {
                    WriteSdkDiagnostic($"Reading PTZ presets over Dahua CGI. Channel={_currentChannel}; HTTP port={_currentDevice.HttpPort}.");
                    var remotePresets = DahuaNetSdk.QueryPresetsFromDevice(
                        _currentDevice.ConnectionAddress, _currentDevice.HttpPort, _currentDevice.Username,
                        _currentDevice.Password, _currentChannel);
                    _currentDevice.UpdateFromDevicePresets(remotePresets);
                    presetCount = remotePresets.Count;
                    WriteSdkDiagnostic($"PTZ preset read returned {presetCount} entries.");
                }
                catch (Exception presetError)
                {
                    // A failed PTZ query must not undo channel-name synchronization.
                    WriteSdkDiagnostic($"PTZ preset read failed; local list preserved. {presetError}");
                }

                // Keep local names if the native query fails/returns no names or
                // the operator declines the proposed changes. Presets are only
                // replaced after a successful CGI response.
                DahuaConfigService.SaveDevices(_devices);
                WriteSdkDiagnostic($"Device configuration saved; applied channel-name changes={appliedNames}.");
                RefreshChannelLists();
                WriteSdkDiagnostic("Live/archive channel lists refreshed.");
                RefreshPresetsList();
                WriteSdkDiagnostic($"Preset list refreshed. Device preset count={presetCount}.");

                string channelMessage = remoteNames.Count > 0
                    ? $"Nazwy kanałów: odczytano {remoteNames.Count}, zastosowano {appliedNames} zmian."
                    : $"Nazwy kanałów: odczyt nieudany; zachowano lokalne etykiety (kanałów: {channelCount}).";
                string presetMessage = presetCount >= 0
                    ? $"Presety PTZ: odczytano {presetCount} pozycji."
                    : "Presety PTZ: odczyt nieudany; zachowano dotychczasową listę.";
                string msg = channelMessage + Environment.NewLine + presetMessage;

                StatusLabel.Text = msg;
                WriteSdkDiagnostic("Manual device read completed.");

                if (showDialog)
                {
                    MessageBox.Show(
                        msg + "\n\nPresety PTZ nie były odczytywane ani zmieniane.",
                        "Odczyt z urządzenia Dahua",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                WriteSdkDiagnostic($"Manual device read managed exception: {ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}");
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

            var visibleChannels = GetVisibleChannels().OrderBy(c => c.Number).ToList();
            if (visibleChannels.Count > 0 && !visibleChannels.Any(c => c.Number == _currentChannel))
                _currentChannel = visibleChannels[0].Number;

            foreach (var chInfo in visibleChannels)
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

            foreach (var chInfo in GetVisibleChannels().OrderBy(c => c.Number).Take(16))
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

            SelectModernChannel(channelNumber);
            if (_loginId != IntPtr.Zero)
            {
                StartMultiLiveForSelectedChannels();
            }
            else
            {
                string name = _currentDevice?.GetChannelName(channelNumber) ?? $"Kanał {channelNumber}";
                StatusLabel.Text = $"Wybrano: {name} (CH {channelNumber}). Kliknij „Podgląd zaznaczonych”.";
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
            BuildModernChannelChecks();

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

        private void PresetsComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PresetsComboBox.SelectedItem is ComboBoxItem item && item.Tag is int presetNumber)
            {
                var preset = _currentDevice?.Presets.FirstOrDefault(p => p.PresetNumber == presetNumber);
                if (preset != null)
                {
                    PresetNumberBox.Text = preset.PresetNumber.ToString();
                    PresetNameBox.Text = preset.Name;
                }
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
            if (_loginId == IntPtr.Zero ||
                !string.Equals(_connectedDeviceId, _currentDevice.Id, StringComparison.Ordinal))
            {
                MessageBox.Show("Najpierw połącz się z rejestratorem/kamerą.", "Brak połączenia",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!int.TryParse(PresetNumberBox.Text.Trim(), out int presetNumber) ||
                presetNumber < 1 || presetNumber > 255)
            {
                MessageBox.Show("Podaj prawidłowy numer presetu (1-255).", "Nieprawidłowy numer",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string presetName = PresetNameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(presetName))
                presetName = $"Preset {presetNumber}";

            try
            {
                // First store the current PTZ position. Only after that try to set its label.
                DahuaNetSdk.SetDevicePresetPosition(
                    _currentDevice.ConnectionAddress, _currentDevice.HttpPort, _currentDevice.Username,
                    _currentDevice.Password, _currentChannel, presetNumber);

                bool nameSaved = true;
                string nameError = string.Empty;
                try
                {
                    DahuaNetSdk.SetDevicePresetName(
                        _currentDevice.ConnectionAddress, _currentDevice.HttpPort, _currentDevice.Username,
                        _currentDevice.Password, _currentChannel, presetNumber, presetName);
                }
                catch (Exception ex)
                {
                    nameSaved = false;
                    nameError = ex.Message;
                }

                string effectiveName = nameSaved ? presetName : $"Preset {presetNumber}";
                var existing = _currentDevice.Presets.FirstOrDefault(p => p.PresetNumber == presetNumber);
                if (existing != null)
                    existing.Name = effectiveName;
                else
                    _currentDevice.Presets.Add(new DahuaPresetItem { PresetNumber = presetNumber, Name = effectiveName });

                DahuaConfigService.SaveDevices(_devices);
                RefreshPresetsList();
                PresetsComboBox.SelectedItem = PresetsComboBox.Items
                    .OfType<ComboBoxItem>()
                    .FirstOrDefault(item => item.Tag is int n && n == presetNumber);

                if (nameSaved)
                {
                    StatusLabel.Text = $"Zapisano preset {presetNumber}: {presetName}.";
                }
                else
                {
                    StatusLabel.Text = $"Pozycja presetu {presetNumber} została zapisana, ale nie udało się ustawić nazwy.";
                    MessageBox.Show(
                        $"Pozycja PTZ została zapisana w urządzeniu, ale zmiana nazwy nie powiodła się. " +
                        $"Lista lokalna używa nazwy „{effectiveName}”.\\n\\n{nameError}",
                        "Częściowy zapis presetu", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                WriteSdkDiagnostic($"Saving PTZ preset {presetNumber} failed: {ex}");
                MessageBox.Show(
                    $"Nie udało się zapisać pozycji presetu na urządzeniu. Lokalna lista nie została zmieniona.\\n\\n{ex.Message}",
                    "Błąd zapisu presetu PTZ", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RenameSelectedPreset_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDevice == null ||
                PresetsComboBox.SelectedItem is not ComboBoxItem item ||
                item.Tag is not int presetNumber)
                return;

            string newName = PresetNameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(newName))
            {
                MessageBox.Show("Wpisz nową nazwę presetu.", "Brak nazwy",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_loginId == IntPtr.Zero ||
                !string.Equals(_connectedDeviceId, _currentDevice.Id, StringComparison.Ordinal))
            {
                MessageBox.Show("Najpierw połącz się z urządzeniem.", "Brak połączenia",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                DahuaNetSdk.SetDevicePresetName(
                    _currentDevice.ConnectionAddress, _currentDevice.HttpPort, _currentDevice.Username,
                    _currentDevice.Password, _currentChannel, presetNumber, newName);

                var preset = _currentDevice.Presets.FirstOrDefault(p => p.PresetNumber == presetNumber);
                if (preset != null) preset.Name = newName;
                DahuaConfigService.SaveDevices(_devices);
                RefreshPresetsList();
                StatusLabel.Text = $"Zmieniono nazwę presetu {presetNumber} na '{newName}'.";
            }
            catch (Exception ex)
            {
                WriteSdkDiagnostic($"Renaming PTZ preset {presetNumber} failed: {ex}");
                MessageBox.Show(ex.Message, "Błąd zmiany nazwy presetu",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RefreshPresetsFromDevice_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDevice == null) return;
            if (_loginId == IntPtr.Zero ||
                !string.Equals(_connectedDeviceId, _currentDevice.Id, StringComparison.Ordinal))
            {
                MessageBox.Show("Najpierw połącz się z urządzeniem.", "Brak połączenia",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                var remotePresets = DahuaNetSdk.QueryPresetsFromDevice(
                    _currentDevice.ConnectionAddress, _currentDevice.HttpPort, _currentDevice.Username,
                    _currentDevice.Password, _currentChannel);

                _currentDevice.UpdateFromDevicePresets(remotePresets);
                DahuaConfigService.SaveDevices(_devices);
                RefreshPresetsList();
                StatusLabel.Text = $"Odczytano {remotePresets.Count} presetów dla kanału {_currentChannel}.";
                MessageBox.Show(
                    $"Odczytano {remotePresets.Count} presetów PTZ z urządzenia dla kanału {_currentChannel}.",
                    "Presety PTZ", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                WriteSdkDiagnostic($"Reading PTZ presets failed: {ex}");
                MessageBox.Show(
                    $"Nie udało się odczytać presetów. Lokalna lista pozostała bez zmian.\\n\\n{ex.Message}",
                    "Błąd odczytu presetów PTZ", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void DeletePreset_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDevice == null ||
                PresetsComboBox.SelectedItem is not ComboBoxItem item ||
                item.Tag is not int presetNumber)
                return;

            if (_loginId == IntPtr.Zero ||
                !string.Equals(_connectedDeviceId, _currentDevice.Id, StringComparison.Ordinal))
            {
                MessageBox.Show("Najpierw połącz się z urządzeniem.", "Brak połączenia",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show(
                $"Czy na pewno usunąć preset {presetNumber} z urządzenia?",
                "Potwierdź usunięcie presetu", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                DahuaNetSdk.DeleteDevicePreset(
                    _currentDevice.ConnectionAddress, _currentDevice.HttpPort, _currentDevice.Username,
                    _currentDevice.Password, _currentChannel, presetNumber);

                _currentDevice.Presets.RemoveAll(p => p.PresetNumber == presetNumber);
                DahuaConfigService.SaveDevices(_devices);
                RefreshPresetsList();
                StatusLabel.Text = $"Usunięto preset {presetNumber} z urządzenia.";
            }
            catch (Exception ex)
            {
                WriteSdkDiagnostic($"Deleting PTZ preset {presetNumber} failed: {ex}");
                MessageBox.Show(
                    $"Nie udało się usunąć presetu z urządzenia. Lokalna lista nie została zmieniona.\\n\\n{ex.Message}",
                    "Błąd usuwania presetu PTZ", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


        #endregion

        #region Live Stream & Connection

        private void StreamTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loginId == IntPtr.Zero) return;
            if (_multiViewSlots.Any(slot => slot.StreamHandle != IntPtr.Zero))
                StartMultiLiveForSelectedChannels();
            else if (_realPlayHandle != IntPtr.Zero)
                StartLiveStreamForChannel(_currentChannel);
        }

        private static bool IsValidEndpoint(string address, string portText, out ushort port, out string error)
        {
            port = 0;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(address))
            {
                error = "Wpisz adres IP lub nazwę hosta urządzenia. Pole nazwy operatora jest niezależne.";
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

            if (_currentDevice.CredentialsUnavailable)
            {
                MessageBox.Show(
                    "Zapisane dane logowania są zaszyfrowane dla innego konta Windows lub komputera. Wpisz ponownie nazwę użytkownika i hasło w sekcji urządzenia i zapisz konfigurację.",
                    "Dane logowania niedostępne", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string ip = _currentDevice.ConnectionAddress;
            if (string.IsNullOrWhiteSpace(ip))
            {
                StatusLabel.Text = "Brak adresu IP/hosta urządzenia. Nazwa operatora nie jest adresem połączenia.";
                MessageBox.Show(StatusLabel.Text, "Nieprawidłowa konfiguracja urządzenia", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!IsValidEndpoint(ip, _currentDevice.Port.ToString(), out ushort validatedPort, out string validationError))
            {
                StatusLabel.Text = validationError;
                MessageBox.Show(validationError, "Nieprawidłowa konfiguracja urządzenia", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Disconnect();
            ushort port = validatedPort;
            string user = _currentDevice.Username ?? string.Empty;
            string pass = _currentDevice.Password ?? string.Empty;

            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(pass))
            {
                StatusLabel.Text = "Brak danych logowania. Otwórz konfigurację urządzenia i wpisz użytkownika oraz hasło.";
                MessageBox.Show(StatusLabel.Text, "Brak danych logowania", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            StatusLabel.Text = $"Logowanie do {ip}:{port}…";
            WriteSdkDiagnostic($"Connect requested. Host={ip}; port={port}; username length={user?.Length ?? 0}.");

            try
            {
                // NET_DEVICEINFO is a 53-byte native structure with a fixed 48-byte serial buffer.
                // Initialize it explicitly and pass it by reference to CLIENT_Login.
                var deviceInfo = DahuaNetSdk.NET_DEVICEINFO.Create();
                int err = 0;
                WriteSdkDiagnostic("Calling CLIENT_Login.");
                _loginId = DahuaNetSdk.CLIENT_Login(ip, port, user, pass, ref deviceInfo, ref err);
                WriteSdkDiagnostic($"CLIENT_Login returned. LoginId=0x{_loginId.ToInt64():X}; error={err}; channel count={deviceInfo.byChanNum}.");

                if (_loginId == IntPtr.Zero)
                {
                    uint lastError = DahuaNetSdk.CLIENT_GetLastError();
                    StatusLabel.Text = $"Błąd logowania do urządzenia {ip}. {DahuaNetSdk.GetErrorDescription(lastError)}";
                    return;
                }

                _connectedDeviceId = _currentDevice.Id;
                _deviceChannelCount = Math.Max(0, (int)deviceInfo.byChanNum);
                WriteSdkDiagnostic($"Device channel count cached from CLIENT_Login: {_deviceChannelCount}.");
                if (_deviceChannelCount > 0)
                    _currentDevice.EnsureDefaultChannels(_deviceChannelCount);
                if (TryQueryActiveChannelNumbers(out var activeChannels))
                {
                    _activeChannelNumbers = activeChannels;
                    if (_activeChannelNumbers.Count > 0)
                    {
                        RefreshChannelLists();
                        BuildModernChannelChecks();
                    }
                    else
                    {
                        StatusLabel.Text = "Rejestrator nie zgłosił aktywnych kanałów wejściowych.";
                    }
                }
                else
                {
                    _activeChannelNumbers = null;
                }
                ConnectButton.IsEnabled = false;
                DisconnectButton.IsEnabled = true;
                StreamOverlay.Visibility = Visibility.Collapsed;

                // Start the live stream first. Channel-name and PTZ-preset queries are
                // optional and are deliberately left to the explicit "read from device"
                // action so a metadata call cannot take down an otherwise valid login.
                if (!_connectForCalendarAvailability)
                {
                    WriteSdkDiagnostic("Login succeeded; starting multi-channel live view.");
                    StartMultiLiveForSelectedChannels();
                    WriteSdkDiagnostic("Multi-channel live view start routine returned.");
                }
                else
                {
                    StatusLabel.Text = $"Połączono z {ip}. Sprawdzanie dostępności nagrań w kalendarzu…";
                    WriteSdkDiagnostic("Login succeeded for calendar availability query; live view start intentionally skipped.");
                }
            }
            catch (Exception ex)
            {
                WriteSdkDiagnostic($"Connect exception: {ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}");
                StatusLabel.Text = $"Błąd połączenia: {ex.Message}";
            }
        }

        private void StartLiveStreamForChannel(int channelNumber)
        {
            if (_loginId == IntPtr.Zero) return;

            StopMultiLiveStreams();
            StopSynchronizedPlayback();
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

                WriteSdkDiagnostic($"Calling CLIENT_RealPlayEx. Channel={channelIndex}; window=0x{_primaryPictureBox.Handle.ToInt64():X}; stream={streamType}.");
                _realPlayHandle = DahuaNetSdk.CLIENT_RealPlayEx(_loginId, channelIndex, _primaryPictureBox.Handle, streamType);
                WriteSdkDiagnostic($"CLIENT_RealPlayEx returned: 0x{_realPlayHandle.ToInt64():X}.");
                if (_realPlayHandle == IntPtr.Zero)
                {
                    WriteSdkDiagnostic("Calling legacy CLIENT_RealPlay fallback.");
                    _realPlayHandle = DahuaNetSdk.CLIENT_RealPlay(_loginId, channelIndex, _primaryPictureBox.Handle);
                    WriteSdkDiagnostic($"CLIENT_RealPlay returned: 0x{_realPlayHandle.ToInt64():X}.");
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
            StopMultiLiveStreams();
            StopSynchronizedPlayback();
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
                    _connectedDeviceId = null;
                }
            }
            catch { }

            _connectedDeviceId = null;
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
                StartMultiLiveForSelectedChannels();
            });
        }

        #endregion

        #region SmartPSS-style Multi-view and Synchronized Archive Playback

        private IEnumerable<DahuaChannelInfo> GetVisibleChannels()
        {
            if (_currentDevice == null) return Enumerable.Empty<DahuaChannelInfo>();
            if (_activeChannelNumbers == null) return _currentDevice.Channels;
            return _currentDevice.Channels.Where(channel => _activeChannelNumbers.Contains(channel.Number));
        }

        // Dahua HTTP API 3.26, section 4.6.29: getCameraState.
        // Channel state values Empty/Disable/UnInited are not active inputs.
        private bool TryQueryActiveChannelNumbers(out HashSet<int> activeChannels)
        {
            activeChannels = new HashSet<int>();
            if (_currentDevice == null || _currentDevice.CredentialsUnavailable)
                return false;

            try
            {
                string url = $"http://{_currentDevice.ConnectionAddress}:{_currentDevice.HttpPort}/cgi-bin/api/LogicDeviceManager/getCameraState";
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Accept = "application/json";
                request.Credentials = new NetworkCredential(_currentDevice.Username, _currentDevice.Password);
                request.PreAuthenticate = true;
                request.Timeout = 5000;
                request.ReadWriteTimeout = 5000;

                byte[] body = Encoding.UTF8.GetBytes("{\"uniqueChannels\":[-1]}");
                request.ContentLength = body.Length;
                using (var stream = request.GetRequestStream())
                    stream.Write(body, 0, body.Length);

                using var response = (HttpWebResponse)request.GetResponse();
                using var responseStream = response.GetResponseStream();
                if (responseStream == null) return false;
                using var document = JsonDocument.Parse(responseStream);
                if (!document.RootElement.TryGetProperty("states", out JsonElement states) ||
                    states.ValueKind != JsonValueKind.Array)
                    return false;

                foreach (JsonElement state in states.EnumerateArray())
                {
                    if (!state.TryGetProperty("channel", out JsonElement channelValue) ||
                        !channelValue.TryGetInt32(out int zeroBasedChannel))
                        continue;

                    string connectionState = state.TryGetProperty("connectionState", out JsonElement stateValue)
                        ? stateValue.GetString() ?? string.Empty : string.Empty;

                    if (connectionState.Equals("Empty", StringComparison.OrdinalIgnoreCase) ||
                        connectionState.Equals("Disable", StringComparison.OrdinalIgnoreCase) ||
                        connectionState.Equals("UnInited", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Dahua HTTP API reports channel indexes from zero; the application uses 1-based channel numbers.
                    activeChannels.Add(zeroBasedChannel + 1);
                }

                WriteSdkDiagnostic($"Channel-state API returned {activeChannels.Count} configured/active channel(s).");
                return true;
            }
            catch (Exception ex)
            {
                WriteSdkDiagnostic("Channel-state API unavailable; keeping the SDK-reported channel list. " + ex.Message);
                return false;
            }
        }

        private void BuildModernChannelChecks()
        {
            ModernChannelChecksPanel.Children.Clear();
            if (_currentDevice == null) return;

            foreach (var channel in GetVisibleChannels().OrderBy(ch => ch.Number))
            {
                var check = new CheckBox
                {
                    Content = $"{channel.Name}  (CH {channel.Number})",
                    Tag = channel.Number,
                    IsChecked = channel.Number == GetVisibleChannels().OrderBy(ch => ch.Number).Select(ch => ch.Number).FirstOrDefault(),
                    Foreground = Brushes.White,
                    FontSize = 11,
                    Margin = new Thickness(3, 5, 2, 5),
                    ToolTip = channel.Name
                };
                check.Checked += ModernChannelCheck_Changed;
                check.Unchecked += ModernChannelCheck_Changed;
                ModernChannelChecksPanel.Children.Add(check);
            }
            int capacity = GetMultiViewCapacity();
            int selectedCount = GetModernSelectedChannels().Count;
            foreach (CheckBox check in ModernChannelChecksPanel.Children.OfType<CheckBox>())
                check.IsEnabled = check.IsChecked == true || selectedCount < capacity;
        }

        private void SelectModernChannel(int channelNumber)
        {
            foreach (CheckBox check in ModernChannelChecksPanel.Children.OfType<CheckBox>())
                check.IsChecked = check.Tag is int value && value == channelNumber;
        }

        private List<int> GetModernSelectedChannels()
        {
            return ModernChannelChecksPanel.Children.OfType<CheckBox>()
                .Where(ch => ch.IsChecked == true && ch.Tag is int)
                .Select(ch => (int)ch.Tag).ToList();
        }

        private int GetMultiViewCapacity() => ModernGridModeBox.SelectedIndex switch
        {
            0 => 1, 2 => 9, 3 => 16, _ => 4
        };

        private void ModernGridModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || ModernChannelChecksPanel == null) return;
            int capacity = GetMultiViewCapacity();
            _updatingChannelChecks = true;
            var selected = ModernChannelChecksPanel.Children.OfType<CheckBox>()
                .Where(check => check.IsChecked == true).ToList();
            foreach (CheckBox check in selected.Skip(capacity))
                check.IsChecked = false;
            _updatingChannelChecks = false;

            int selectedCount = GetModernSelectedChannels().Count;
            foreach (CheckBox check in ModernChannelChecksPanel.Children.OfType<CheckBox>())
                check.IsEnabled = check.IsChecked == true || selectedCount < capacity;
        }

        private void ModernChannelCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_updatingChannelChecks) return;
            int capacity = GetMultiViewCapacity();
            var selected = ModernChannelChecksPanel.Children.OfType<CheckBox>()
                .Where(ch => ch.IsChecked == true).ToList();
            if (selected.Count > capacity && sender is CheckBox changed)
            {
                changed.IsChecked = false;
                StatusLabel.Text = $"Wybrany układ obsługuje maksymalnie {capacity} kanał(ów).";
                selected = ModernChannelChecksPanel.Children.OfType<CheckBox>()
                    .Where(ch => ch.IsChecked == true).ToList();
            }
            foreach (CheckBox check in ModernChannelChecksPanel.Children.OfType<CheckBox>())
                check.IsEnabled = check.IsChecked == true || selected.Count < capacity;
        }

        private void DisposeMultiViewSlots()
        {
            foreach (var oldSlot in _multiViewSlots)
            {
                try { oldSlot.Host.Child = null; } catch { }
                try { oldSlot.PictureBox.Dispose(); } catch { }
                try { oldSlot.Host.Dispose(); } catch { }
            }
            _multiViewSlots.Clear();
            MultiVideoGrid.Children.Clear();
        }

        private void BuildMultiViewSlots(IReadOnlyList<int> channels)
        {
            StopMultiLiveStreams();
            StopPlaybackHandlesOnly();
            DisposeMultiViewSlots();

            int count = Math.Max(1, channels.Count);
            int capacity = GetMultiViewCapacity();
            int columns = capacity == 1 ? 1 : capacity == 4 ? 2 : capacity == 9 ? 3 : 4;
            MultiVideoGrid.Columns = columns;
            MultiVideoGrid.Rows = capacity / columns;

            foreach (int channel in channels.Take(capacity))
            {
                MultiViewSlot? slotRef = null;
                var viewport = new Forms.Panel
                {
                    BackColor = System.Drawing.Color.Black,
                    Dock = Forms.DockStyle.Fill,
                    TabStop = true
                };
                var picture = new Forms.PictureBox
                {
                    BackColor = System.Drawing.Color.Black,
                    SizeMode = Forms.PictureBoxSizeMode.Normal,
                    Location = new System.Drawing.Point(0, 0),
                    Size = new System.Drawing.Size(640, 360)
                };
                viewport.Controls.Add(picture);
                picture.CreateControl();
                viewport.MouseWheel += (_, args) => HandleSlotZoomWheel(slotRef!, args, viewport.PointToClient(Forms.Control.MousePosition));
                picture.MouseWheel += (_, args) => HandleSlotZoomWheel(slotRef!, args, viewport.PointToClient(Forms.Control.MousePosition));
                viewport.MouseDown += (_, args) => BeginSlotPan(slotRef!, viewport.PointToClient(Forms.Control.MousePosition), args);
                picture.MouseDown += (_, args) => BeginSlotPan(slotRef!, viewport.PointToClient(Forms.Control.MousePosition), args);
                viewport.MouseMove += (_, args) => UpdateSlotPan(slotRef!, viewport.PointToClient(Forms.Control.MousePosition), args);
                picture.MouseMove += (_, args) => UpdateSlotPan(slotRef!, viewport.PointToClient(Forms.Control.MousePosition), args);
                viewport.MouseUp += (_, args) => EndSlotPan(slotRef!, args);
                picture.MouseUp += (_, args) => EndSlotPan(slotRef!, args);
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
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Child = new TextBlock
                    {
                        Text = $"{_currentDevice?.GetChannelName(channel) ?? $"Kanał {channel}"} | CH {channel}",
                        Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 11
                    }
                };
                channelLabel.MouseLeftButtonDown += (_, args) =>
                {
                    _currentChannel = channel;
                    HighlightActiveChannelButton(channel);
                    ChannelNameEditBox.Text = _currentDevice?.GetChannelName(channel) ?? $"Kanał {channel}";
                    args.Handled = true;
                };
                tile.Children.Add(channelLabel);
                MultiVideoGrid.Children.Add(tile);
                slotRef = new MultiViewSlot { Channel = channel, PictureBox = picture, Host = host, Viewport = viewport };
                _multiViewSlots.Add(slotRef);
                // Attach Resize only after slotRef is initialized: WinForms can raise
                // Resize while the WindowsFormsHost is being created/attached.
                viewport.Resize += (_, _) =>
                {
                    if (slotRef != null)
                        ApplySlotViewport(slotRef);
                };
                ApplySlotViewport(slotRef);
            }
        }

        private void ApplySlotViewport(MultiViewSlot? slot)
        {
            if (slot == null || slot.Viewport == null || slot.PictureBox == null ||
                slot.Viewport.IsDisposed || slot.PictureBox.IsDisposed) return;
            int viewportWidth = Math.Max(1, slot.Viewport.ClientSize.Width);
            int viewportHeight = Math.Max(1, slot.Viewport.ClientSize.Height);
            int width = Math.Max(1, (int)Math.Round(viewportWidth * slot.Zoom));
            int height = Math.Max(1, (int)Math.Round(viewportHeight * slot.Zoom));

            slot.PanX = Math.Min(0, Math.Max(viewportWidth - width, slot.PanX));
            slot.PanY = Math.Min(0, Math.Max(viewportHeight - height, slot.PanY));
            if (slot.Zoom <= 1.001)
            {
                slot.Zoom = 1.0;
                slot.PanX = 0;
                slot.PanY = 0;
            }

            slot.PictureBox.Size = new System.Drawing.Size(width, height);
            slot.PictureBox.Location = new System.Drawing.Point(slot.PanX, slot.PanY);
        }

        private void HandleSlotZoomWheel(MultiViewSlot slot, Forms.MouseEventArgs args, System.Drawing.Point cursor)
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
            ApplySlotViewport(slot);
            slot.Viewport.Focus();
        }

        private void BeginSlotPan(MultiViewSlot slot, System.Drawing.Point cursor, Forms.MouseEventArgs args)
        {
            if (args.Button != Forms.MouseButtons.Left || slot.Zoom <= 1.001) return;
            slot.IsDragging = true;
            slot.DragStart = cursor;
            slot.PanStart = new System.Drawing.Point(slot.PanX, slot.PanY);
            slot.Viewport.Capture = true;
            slot.Viewport.Cursor = Forms.Cursors.SizeAll;
        }

        private void UpdateSlotPan(MultiViewSlot slot, System.Drawing.Point cursor, Forms.MouseEventArgs args)
        {
            if (!slot.IsDragging) return;
            slot.PanX = slot.PanStart.X + cursor.X - slot.DragStart.X;
            slot.PanY = slot.PanStart.Y + cursor.Y - slot.DragStart.Y;
            ApplySlotViewport(slot);
        }

        private void EndSlotPan(MultiViewSlot slot, Forms.MouseEventArgs args)
        {
            if (args.Button != Forms.MouseButtons.Left) return;
            slot.IsDragging = false;
            slot.Viewport.Capture = false;
            slot.Viewport.Cursor = Forms.Cursors.Default;
        }

        private void StartMultiLive_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDevice == null) return;
            if (_loginId == IntPtr.Zero || !string.Equals(_connectedDeviceId, _currentDevice.Id, StringComparison.Ordinal))
            {
                Connect_Click(sender, e);
                return;
            }
            StartMultiLiveForSelectedChannels();
        }

        private void StartMultiLiveForSelectedChannels()
        {
            if (_loginId == IntPtr.Zero || _currentDevice == null) return;
            var channels = GetModernSelectedChannels();
            if (channels.Count == 0) channels.Add(_currentChannel);
            channels = channels.Take(GetMultiViewCapacity()).ToList();
            _currentChannel = channels[0];
            HighlightActiveChannelButton(_currentChannel);

            StopSynchronizedPlayback();
            if (_realPlayHandle != IntPtr.Zero)
            {
                DahuaNetSdk.CLIENT_StopRealPlayEx(_realPlayHandle);
                _realPlayHandle = IntPtr.Zero;
            }
            BuildMultiViewSlots(channels);

            var streamType = StreamTypeBox.SelectedIndex == 1
                ? DahuaNetSdk.EM_RealPlayType.Realplay_1 : DahuaNetSdk.EM_RealPlayType.Realplay;
            int started = 0;
            foreach (var slot in _multiViewSlots)
            {
                slot.StreamHandle = DahuaNetSdk.CLIENT_RealPlayEx(
                    _loginId, Math.Max(0, slot.Channel - 1), slot.PictureBox.Handle, streamType);
                if (slot.StreamHandle != IntPtr.Zero) started++;
                else WriteSdkDiagnostic($"Multi-view live failed. Channel={slot.Channel}; error=0x{DahuaNetSdk.CLIENT_GetLastError():X8}.");
            }
            StatusLabel.Text = $"Podgląd wielokanałowy: uruchomiono {started}/{channels.Count} kanałów.";
            PlaybackSyncStatusText.Text = "Podgląd na żywo";
        }

        private void StopMultiLiveStreams()
        {
            foreach (var slot in _multiViewSlots)
            {
                if (slot.StreamHandle == IntPtr.Zero) continue;
                try { DahuaNetSdk.CLIENT_StopRealPlayEx(slot.StreamHandle); } catch { }
                slot.StreamHandle = IntPtr.Zero;
            }
        }

        private void StartSynchronizedPlayback_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDevice == null) return;
            DateTime? start = GetStartDateTime();
            DateTime? end = GetEndDateTime();
            if (start == null || end == null || end <= start)
            {
                MessageBox.Show("Wybierz poprawny przedział czasu.", "Błędny zakres", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (_loginId == IntPtr.Zero || !string.Equals(_connectedDeviceId, _currentDevice.Id, StringComparison.Ordinal))
            {
                Connect_Click(sender, e);
                if (_loginId == IntPtr.Zero) return;
            }

            var channels = GetModernSelectedChannels();
            if (channels.Count == 0) channels.Add(GetSelectedArchiveChannel());
            channels = channels.Take(GetMultiViewCapacity()).ToList();
            _currentChannel = channels[0];
            HighlightActiveChannelButton(_currentChannel);
            StopMultiLiveStreams();
            if (_realPlayHandle != IntPtr.Zero)
            {
                DahuaNetSdk.CLIENT_StopRealPlayEx(_realPlayHandle);
                _realPlayHandle = IntPtr.Zero;
            }
            StopSynchronizedPlayback();
            BuildMultiViewSlots(channels);

            _playbackRangeStart = start.Value;
            _playbackRangeEnd = end.Value;
            _playbackElapsedBeforePause = 0;
            _playbackSegmentStartedAt = DateTime.Now;
            _synchronizedPlaybackPaused = false;
            _archivePlaybackSpeed = 1.0;
            _updatingArchiveSpeed = true;
            ArchiveSpeedBox.SelectedIndex = 2;
            _updatingArchiveSpeed = false;
            PlaybackPositionSlider.Minimum = 0;
            PlaybackPositionSlider.Maximum = Math.Max(1, (end.Value - start.Value).TotalSeconds);
            PlaybackPositionSlider.Value = 0;
            PlaybackPositionSlider.IsEnabled = true;
            UpdatePlaybackSliderRange(keepPlayheadVisible: false);

            try
            {
                ConfigureDahuaRecordModes(_loginId);
                int started = 0;
                foreach (var slot in _multiViewSlots)
                {
                    var tmStart = DahuaNetSdk.NET_TIME.FromDateTime(start.Value);
                    var tmEnd = DahuaNetSdk.NET_TIME.FromDateTime(end.Value);
                    slot.PlaybackHandle = DahuaNetSdk.CLIENT_PlayBackByTimeEx(
                        _loginId, Math.Max(0, slot.Channel - 1), ref tmStart, ref tmEnd,
                        slot.PictureBox.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    if (slot.PlaybackHandle != IntPtr.Zero)
                    {
                        _multiPlaybackHandles.Add(slot.PlaybackHandle);
                        started++;
                    }
                    else WriteSdkDiagnostic($"Synchronized playback failed. Channel={slot.Channel}; error=0x{DahuaNetSdk.CLIENT_GetLastError():X8}.");
                }
                if (started == 0)
                    throw new InvalidOperationException("Rejestrator nie uruchomił odtwarzania na żadnym z wybranych kanałów.");
                _playbackUiTimer.Start();
                PlaybackSyncStatusText.Text = $"Synchronizacja czasu: {started}/{channels.Count} kanałów";
                StatusLabel.Text = $"Odtwarzanie zsynchronizowane: {started} kanałów od {start.Value:HH:mm:ss}.";
            }
            catch (Exception ex)
            {
                StopSynchronizedPlayback();
                StatusLabel.Text = $"Błąd zsynchronizowanego odtwarzania: {ex.Message}";
                MessageBox.Show(ex.Message, "Archiwum Dahua", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RecordingTimelineCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            RefreshRecordingTimeline();
        }

        private void RefreshRecordingTimeline()
        {
            if (RecordingTimelineCanvas == null) return;
            RecordingTimelineCanvas.Children.Clear();
            double width = RecordingTimelineCanvas.ActualWidth;
            if (width <= 0) return;

            DateTime selectedDay = (StartDatePicker.SelectedDate ?? DateTime.Today).Date;
            var records = (ArchiveRecordsGrid.ItemsSource as IEnumerable<DahuaRecordItem>)?.ToList()
                          ?? new List<DahuaRecordItem>();
            const double left = 24;
            const double right = 8;
            double usableWidth = Math.Max(1, width - left - right);
            double visibleHours = 24.0 / _recordingTimelineZoom;
            double viewStartHour = Math.Clamp(_recordingTimelineStartHour, 0, 24.0 - visibleHours);
            double viewEndHour = viewStartHour + visibleHours;
            double xForHour(double hour) => left + usableWidth * (hour - viewStartHour) / visibleHours;

            // Tick/label interval follows zoom: at close zoom show minutes,
            // at wider views gradually switch to 15/30/60/120-minute marks.
            int tickMinutes = visibleHours <= 0.5 ? 1
                : visibleHours <= 1.0 ? 2
                : visibleHours <= 2.5 ? 5
                : visibleHours <= 4.0 ? 10
                : visibleHours <= 8.0 ? 15
                : visibleHours <= 12.0 ? 30
                : 120;

            double viewStartMinutes = viewStartHour * 60.0;
            double viewEndMinutes = viewEndHour * 60.0;
            int firstTick = (int)(Math.Ceiling(viewStartMinutes / tickMinutes) * tickMinutes);
            for (int minuteOfDay = firstTick; minuteOfDay <= Math.Min(1440, viewEndMinutes); minuteOfDay += tickMinutes)
            {
                double hour = minuteOfDay / 60.0;
                double xPos = xForHour(hour);
                bool majorTick = minuteOfDay % 60 == 0;
                var line = new System.Windows.Shapes.Line
                {
                    X1 = xPos,
                    X2 = xPos,
                    Y1 = majorTick ? 0 : 13,
                    Y2 = 22,
                    Stroke = new SolidColorBrush(majorTick
                        ? Color.FromRgb(100, 116, 139)
                        : Color.FromRgb(55, 65, 81)),
                    StrokeThickness = majorTick ? 1.0 : 0.7,
                    IsHitTestVisible = false
                };
                RecordingTimelineCanvas.Children.Add(line);

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
                RecordingTimelineCanvas.Children.Add(label);
            }

            foreach (DahuaRecordItem record in records)
            {
                DateTime segmentStart = record.StartTime < selectedDay ? selectedDay : record.StartTime;
                DateTime dayEnd = selectedDay.AddDays(1);
                DateTime segmentEnd = record.EndTime > dayEnd ? dayEnd : record.EndTime;
                if (segmentEnd <= segmentStart || record.StartTime.Date > selectedDay || record.EndTime.Date < selectedDay)
                    continue;

                double recordStartHour = (segmentStart - selectedDay).TotalHours;
                double recordEndHour = (segmentEnd - selectedDay).TotalHours;
                if (recordEndHour < viewStartHour || recordStartHour > viewEndHour) continue;
                double x1 = xForHour(Math.Max(recordStartHour, viewStartHour));
                double x2 = xForHour(Math.Min(recordEndHour, viewEndHour));
                var block = new System.Windows.Shapes.Rectangle
                {
                    Width = Math.Max(2, x2 - x1),
                    Height = 10,
                    RadiusX = 2,
                    RadiusY = 2,
                    Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94)),
                    Stroke = new SolidColorBrush(Color.FromRgb(74, 222, 128)),
                    StrokeThickness = 0.5,
                    ToolTip = $"{record.StartTime:HH:mm:ss} – {record.EndTime:HH:mm:ss}",
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(block, x1);
                Canvas.SetTop(block, 25);
                RecordingTimelineCanvas.Children.Add(block);
            }

            if (records.Count == 0)
            {
                var hint = new TextBlock
                {
                    Text = $"Widok {viewStartHour:00}:00–{viewEndHour:00}:00 | Kółko myszy: przybliż/oddal | Prawy przycisk: reset",
                    Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                    FontSize = 9,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(hint, left);
                Canvas.SetTop(hint, 27);
                RecordingTimelineCanvas.Children.Add(hint);
            }
        }

        private void RecordingTimelineCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (RecordingTimelineCanvas.ActualWidth <= 0) return;

            const double left = 24;
            double usableWidth = Math.Max(1, RecordingTimelineCanvas.ActualWidth - left - 8);
            Point pointer = e.GetPosition(RecordingTimelineCanvas);
            double pointerRatio = Math.Clamp((pointer.X - left) / usableWidth, 0, 1);
            double oldVisibleHours = 24.0 / _recordingTimelineZoom;
            double oldStart = Math.Clamp(_recordingTimelineStartHour, 0, 24.0 - oldVisibleHours);
            double anchorHour = oldStart + pointerRatio * oldVisibleHours;

            double factor = e.Delta > 0 ? 1.25 : 1.0 / 1.25;
            _recordingTimelineZoom = Math.Clamp(_recordingTimelineZoom * factor, 1.0, 12.0);
            double newVisibleHours = 24.0 / _recordingTimelineZoom;
            _recordingTimelineStartHour = Math.Clamp(anchorHour - pointerRatio * newVisibleHours,
                0, 24.0 - newVisibleHours);

            RefreshRecordingTimeline();
            UpdatePlaybackSliderRange(keepPlayheadVisible: false);
            e.Handled = true;
        }

        private void RecordingTimelineCanvas_ResetZoom(object sender, MouseButtonEventArgs e)
        {
            _recordingTimelineZoom = 1.0;
            _recordingTimelineStartHour = 0.0;
            RefreshRecordingTimeline();
            UpdatePlaybackSliderRange(keepPlayheadVisible: false);
            e.Handled = true;
        }

        private void RecordingTimelineCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_currentDevice == null || RecordingTimelineCanvas.ActualWidth <= 0) return;
            Point point = e.GetPosition(RecordingTimelineCanvas);
            const double left = 24;
            double usableWidth = Math.Max(1, RecordingTimelineCanvas.ActualWidth - left - 8);
            double visibleHours = 24.0 / _recordingTimelineZoom;
            double viewStartHour = Math.Clamp(_recordingTimelineStartHour, 0, 24.0 - visibleHours);
            double pointerRatio = Math.Clamp((point.X - left) / usableWidth, 0, 1);
            double hour = Math.Clamp(viewStartHour + pointerRatio * visibleHours, 0, 24);
            DateTime selectedTime = (StartDatePicker.SelectedDate ?? DateTime.Today).Date.AddSeconds(hour * 3600);
            StartTimeBox.Text = selectedTime.ToString("HH:mm:ss");

            if (_multiPlaybackHandles.Count > 0 &&
                selectedTime >= _playbackRangeStart && selectedTime <= _playbackRangeEnd)
            {
                int offset = (int)Math.Round((selectedTime - _playbackRangeStart).TotalSeconds);
                offset = (int)Math.Clamp(offset, 0, GetPlaybackDurationSeconds());
                SeekSynchronizedPlayback(offset);
                PlaybackSyncStatusText.Text = $"Przewinięto do {selectedTime:HH:mm:ss}";
            }
            else
            {
                PlaybackSyncStatusText.Text = _multiPlaybackHandles.Count == 0
                    ? $"Wybrano {selectedTime:HH:mm:ss}. Najpierw uruchom archiwum dla wybranego zakresu."
                    : $"Godzina {selectedTime:HH:mm:ss} jest poza aktualnym zakresem odtwarzania.";
            }
            e.Handled = true;
        }

        private void ArchiveSpeedBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingArchiveSpeed || !IsLoaded || _multiPlaybackHandles.Count == 0 ||
                ArchiveSpeedBox.SelectedItem is not ComboBoxItem item ||
                !double.TryParse(item.Tag?.ToString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double requestedSpeed))
                return;

            double elapsed = _playbackElapsedBeforePause;
            if (!_synchronizedPlaybackPaused)
                elapsed += (DateTime.Now - _playbackSegmentStartedAt).TotalSeconds * _archivePlaybackSpeed;

            int succeeded = 0;
            foreach (IntPtr handle in _multiPlaybackHandles)
            {
                bool ok = DahuaNetSdk.CLIENT_NormalPlayBack(handle);
                int steps = requestedSpeed == 1.0 ? 0 : (int)Math.Round(Math.Abs(Math.Log(requestedSpeed, 2)));
                for (int step = 0; ok && step < steps; step++)
                    ok = requestedSpeed > 1.0
                        ? DahuaNetSdk.CLIENT_FastPlayBack(handle)
                        : DahuaNetSdk.CLIENT_SlowPlayBack(handle);
                if (ok) succeeded++;
            }

            if (succeeded != _multiPlaybackHandles.Count)
            {
                foreach (IntPtr handle in _multiPlaybackHandles)
                    DahuaNetSdk.CLIENT_NormalPlayBack(handle);
                _archivePlaybackSpeed = 1.0;
                _updatingArchiveSpeed = true;
                ArchiveSpeedBox.SelectedIndex = 2;
                _updatingArchiveSpeed = false;
                PlaybackSyncStatusText.Text = "Rejestrator nie przyjął zmiany tempa na wszystkich kanałach. Przywrócono 1×.";
                return;
            }

            _playbackElapsedBeforePause = Math.Clamp(elapsed, 0, PlaybackPositionSlider.Maximum);
            _playbackSegmentStartedAt = DateTime.Now;
            _archivePlaybackSpeed = requestedSpeed;
            PlaybackSyncStatusText.Text = $"Tempo odtwarzania: {requestedSpeed.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}× ({succeeded} kanałów)";
        }

        private void SeekSynchronizedPlayback(int offsetSeconds)
        {
            offsetSeconds = (int)Math.Clamp(offsetSeconds, 0, GetPlaybackDurationSeconds());
            int succeeded = 0;
            foreach (IntPtr handle in _multiPlaybackHandles)
                if (DahuaNetSdk.CLIENT_SeekPlayBack(handle, offsetSeconds, 0)) succeeded++;
            if (succeeded != _multiPlaybackHandles.Count)
            {
                PlaybackSyncStatusText.Text = $"Przewijanie nie powiodło się na wszystkich kanałach ({succeeded}/{_multiPlaybackHandles.Count}).";
                return;
            }
            _playbackElapsedBeforePause = offsetSeconds;
            _playbackSegmentStartedAt = DateTime.Now;
            UpdatePlaybackSliderRange(keepPlayheadVisible: true);
            PlaybackPositionSlider.Value = Math.Clamp(offsetSeconds, PlaybackPositionSlider.Minimum, PlaybackPositionSlider.Maximum);
            PlaybackPositionText.Text = $"{_playbackRangeStart.AddSeconds(offsetSeconds):HH:mm:ss} / {_playbackRangeEnd:HH:mm:ss}";
            PlaybackSyncStatusText.Text = $"Przewinięto kanały: {succeeded}/{_multiPlaybackHandles.Count}";
        }

        private void PlaybackSlider_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_multiPlaybackHandles.Count == 0) return;
            _isSeekingPlayback = true;
        }

        private void PlaybackSlider_LostMouseCapture(object sender, MouseEventArgs e)
        {
            // Some WPF themes transfer mouse capture from Slider to its Thumb.
            // Commit the final value if the normal mouse-up route was bypassed.
            if (_isSeekingPlayback && _multiPlaybackHandles.Count > 0)
                CommitPlaybackSliderSeek();
        }

        private void CommitPlaybackSliderSeek()
        {
            if (!_isSeekingPlayback) return;
            _isSeekingPlayback = false;
            if (_multiPlaybackHandles.Count == 0) return;

            int offsetSeconds = (int)Math.Round(PlaybackPositionSlider.Value);
            offsetSeconds = (int)Math.Clamp(offsetSeconds, 0, GetPlaybackDurationSeconds());
            SeekSynchronizedPlayback(offsetSeconds);
        }

        private void PlaybackUiTimer_Tick(object? sender, EventArgs e)
        {
            if (_isSeekingPlayback || _synchronizedPlaybackPaused || _multiPlaybackHandles.Count == 0) return;
            double elapsed = _playbackElapsedBeforePause + (DateTime.Now - _playbackSegmentStartedAt).TotalSeconds * _archivePlaybackSpeed;
            elapsed = Math.Clamp(elapsed, 0, GetPlaybackDurationSeconds());
            double playheadHour = (_playbackRangeStart.AddSeconds(elapsed) - _playbackRangeStart.Date).TotalHours;
            double visibleHours = 24.0 / _recordingTimelineZoom;
            double viewStart = Math.Clamp(_recordingTimelineStartHour, 0, 24.0 - visibleHours);
            if (playheadHour < viewStart || playheadHour > viewStart + visibleHours)
            {
                _recordingTimelineStartHour = Math.Clamp(playheadHour - visibleHours * 0.15, 0, 24.0 - visibleHours);
                RefreshRecordingTimeline();
                UpdatePlaybackSliderRange(keepPlayheadVisible: false);
            }
            PlaybackPositionSlider.Value = Math.Clamp(elapsed, PlaybackPositionSlider.Minimum, PlaybackPositionSlider.Maximum);
            TimeSpan position = TimeSpan.FromSeconds(elapsed);
            PlaybackPositionText.Text = $"{_playbackRangeStart.Add(position):HH:mm:ss} / {_playbackRangeEnd:HH:mm:ss}";
            if (elapsed >= GetPlaybackDurationSeconds())
            {
                _playbackUiTimer.Stop();
                PlaybackSyncStatusText.Text = "Koniec zakresu archiwum";
            }
        }

        private void ToggleSynchronizedPlayback_Click(object sender, RoutedEventArgs e)
        {
            if (_multiPlaybackHandles.Count == 0) return;
            bool pause = !_synchronizedPlaybackPaused;
            var succeededHandles = new List<IntPtr>();
            foreach (IntPtr handle in _multiPlaybackHandles)
                if (DahuaNetSdk.CLIENT_PausePlayBack(handle, pause))
                    succeededHandles.Add(handle);

            if (succeededHandles.Count != _multiPlaybackHandles.Count)
            {
                // Roll back successful handles so the group does not knowingly drift
                // into a mixed paused/playing state.
                foreach (IntPtr handle in succeededHandles)
                    DahuaNetSdk.CLIENT_PausePlayBack(handle, !pause);
                StatusLabel.Text = $"Nie wszystkie kanały przyjęły polecenie pauzy/wznowienia ({succeededHandles.Count}/{_multiPlaybackHandles.Count}).";
                PlaybackSyncStatusText.Text = "Nie udało się zsynchronizować sterowania";
                return;
            }
            if (pause)
            {
                _playbackElapsedBeforePause += (DateTime.Now - _playbackSegmentStartedAt).TotalSeconds * _archivePlaybackSpeed;
                _synchronizedPlaybackPaused = true;
                _playbackUiTimer.Stop();
            }
            else
            {
                _playbackSegmentStartedAt = DateTime.Now;
                _synchronizedPlaybackPaused = false;
                _playbackUiTimer.Start();
            }
            PlaybackSyncStatusText.Text = pause ? "Wstrzymano wszystkie kanały" : "Wznowiono wszystkie kanały";
        }

        private void PlaybackSlider_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            CommitPlaybackSliderSeek();
        }

        private void StopPlaybackHandlesOnly()
        {
            foreach (IntPtr handle in _multiPlaybackHandles)
            {
                try { DahuaNetSdk.CLIENT_StopPlayBack(handle); } catch { }
            }
            _multiPlaybackHandles.Clear();
            foreach (var slot in _multiViewSlots) slot.PlaybackHandle = IntPtr.Zero;
        }

        private void StopSynchronizedPlayback()
        {
            _playbackUiTimer.Stop();
            StopPlaybackHandlesOnly();
            _synchronizedPlaybackPaused = false;
            _playbackElapsedBeforePause = 0;
            _archivePlaybackSpeed = 1.0;
            if (ArchiveSpeedBox != null)
            {
                _updatingArchiveSpeed = true;
                ArchiveSpeedBox.SelectedIndex = 2;
                _updatingArchiveSpeed = false;
            }
            if (PlaybackPositionSlider != null)
            {
                PlaybackPositionSlider.IsEnabled = false;
                PlaybackPositionSlider.Value = 0;
            }
            if (PlaybackPositionText != null) PlaybackPositionText.Text = "00:00:00 / 00:00:00";
        }

        private void StopSynchronizedPlayback_Click(object sender, RoutedEventArgs e)
        {
            StopSynchronizedPlayback();
            PlaybackSyncStatusText.Text = "Odtwarzanie zatrzymane";
            StatusLabel.Text = "Zatrzymano zsynchronizowane odtwarzanie archiwum.";
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
            MultiViewSlot? activeSlot = _multiViewSlots.FirstOrDefault(slot =>
                slot.Channel == _currentChannel &&
                (slot.StreamHandle != IntPtr.Zero || slot.PlaybackHandle != IntPtr.Zero));
            activeSlot ??= _multiViewSlots.FirstOrDefault(slot =>
                slot.StreamHandle != IntPtr.Zero || slot.PlaybackHandle != IntPtr.Zero);

            if (activeSlot == null && _realPlayHandle == IntPtr.Zero && _playbackHandle == IntPtr.Zero)
            {
                MessageBox.Show("Najpierw uruchom strumień wideo.", "Brak strumienia", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }

            IntPtr handle = activeSlot != null
                ? (activeSlot.StreamHandle != IntPtr.Zero ? activeSlot.StreamHandle : activeSlot.PlaybackHandle)
                : (_realPlayHandle != IntPtr.Zero ? _realPlayHandle : _playbackHandle);
            int captureChannel = activeSlot?.Channel ?? _currentChannel;

            try
            {
                string folder = Path.Combine(AppContext.BaseDirectory, "zrzuty");
                Directory.CreateDirectory(folder);
                string filename = $"Dahua_CH{captureChannel}_{DateTime.Now:yyyyMMdd_HHmmssfff}.jpg";
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

        private static void ConfigureDahuaRecordModes(IntPtr login)
        {
            if (login == IntPtr.Zero)
                throw new InvalidOperationException("Brak aktywnego uchwytu logowania Dahua.");

            // EM_USEDEV_MODE values from the SDK header are sequential:
            // DH_RECORD_STREAM_TYPE = 6, DH_RECORD_TYPE = 8.
            // 0 means main+sub stream and NET_RECORD_TYPE_ALL respectively.
            int streamType = 0;
            bool streamOk = DahuaNetSdk.CLIENT_SetDeviceMode(login, 6, ref streamType);
            uint streamError = streamOk ? 0u : DahuaNetSdk.CLIENT_GetLastError();
            WriteSdkDiagnostic($"SetDeviceMode(DH_RECORD_STREAM_TYPE=6, value=0): ok={streamOk}; error=0x{streamError:X8}.");

            int recordType = 0;
            bool typeOk = DahuaNetSdk.CLIENT_SetDeviceMode(login, 8, ref recordType);
            uint typeError = typeOk ? 0u : DahuaNetSdk.CLIENT_GetLastError();
            WriteSdkDiagnostic($"SetDeviceMode(DH_RECORD_TYPE=8, value=0/ALL): ok={typeOk}; error=0x{typeError:X8}.");

            // Older firmware may reject one of these optional modes. Keep the
            // failure in the diagnostic log and allow the actual archive API call
            // to return its own, more specific error code.
        }

        private int GetSelectedArchiveChannel()
        {
            if (ArchiveChannelBox.SelectedItem is ComboBoxItem item && item.Tag is int ch) return ch;
            return _currentChannel;
        }

        private async void SearchArchive_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDevice == null) return;

            var start = GetStartDateTime();
            var end = GetEndDateTime();
            if (start == null || end == null || end <= start)
            {
                MessageBox.Show("Wybierz poprawny przedział czasu.", "Błędny zakres dat", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_loginId == IntPtr.Zero || !string.Equals(_connectedDeviceId, _currentDevice.Id, StringComparison.Ordinal))
            {
                Connect_Click(sender, e);
                if (_loginId == IntPtr.Zero) return;
            }

            int channel = GetSelectedArchiveChannel();
            int channelIndex = Math.Max(0, channel - 1);
            IntPtr login = _loginId;
            var tmStart = DahuaNetSdk.NET_TIME.FromDateTime(start.Value);
            var tmEnd = DahuaNetSdk.NET_TIME.FromDateTime(end.Value);

            DownloadStatusText.Visibility = Visibility.Visible;
            DownloadStatusText.Text = $"Wyszukiwanie nagrań: {_currentDevice.Name}, kanał {channel}…";
            ArchiveRecordsGrid.ItemsSource = null;
            RefreshRecordingTimeline();
            _archiveAvailableDays.Clear();
            RefreshOpenArchiveCalendars();
            DevicesListBox.IsEnabled = false;
            ArchiveChannelBox.IsEnabled = false;
            StartDatePicker.IsEnabled = false;
            EndDatePicker.IsEnabled = false;
            StartTimeBox.IsEnabled = false;
            EndTimeBox.IsEnabled = false;
            SearchArchiveButton.IsEnabled = false;

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
                        int count;
                        ConfigureDahuaRecordModes(login);

                        WriteSdkDiagnostic($"CLIENT_QueryRecordFile: channel={channelIndex}; recordType=0 (ALL); timeRange={start.Value:O}..{end.Value:O}; bTime=true; recordInfoSize={itemSize}; bufferSize={bufferSize}.");
                        bool ok = DahuaNetSdk.CLIENT_QueryRecordFile(
                            login, channelIndex, 0, ref tmStart, ref tmEnd, null,
                            buffer, bufferSize, out count, 8000, false);
                        WriteSdkDiagnostic($"CLIENT_QueryRecordFile returned ok={ok}; count={count}; lastError=0x{(ok ? 0u : DahuaNetSdk.CLIENT_GetLastError()):X8}.");

                        if (!ok)
                        {
                            uint error = DahuaNetSdk.CLIENT_GetLastError();
                            throw new InvalidOperationException(DahuaNetSdk.GetErrorDescription(error));
                        }

                        var result = new List<DahuaRecordItem>();
                        int safeCount = Math.Clamp(count, 0, maxRecords);
                        for (int i = 0; i < safeCount; i++)
                        {
                            var native = Marshal.PtrToStructure<DahuaNetSdk.NET_RECORDFILE_INFO>(
                                IntPtr.Add(buffer, i * itemSize));
                            DateTime recordStart = new DateTime(
                                (int)native.starttime.dwYear, (int)native.starttime.dwMonth,
                                (int)native.starttime.dwDay, (int)native.starttime.dwHour,
                                (int)native.starttime.dwMinute, (int)native.starttime.dwSecond);
                            DateTime recordEnd = new DateTime(
                                (int)native.endtime.dwYear, (int)native.endtime.dwMonth,
                                (int)native.endtime.dwDay, (int)native.endtime.dwHour,
                                (int)native.endtime.dwMinute, (int)native.endtime.dwSecond);

                            if (recordEnd <= recordStart) continue;
                            result.Add(new DahuaRecordItem
                            {
                                Channel = channel,
                                FileName = native.FileName,
                                StartTime = recordStart,
                                EndTime = recordEnd,
                                SizeKb = native.size,
                                RecordType = native.nRecordFileType
                            });
                        }
                        return result.OrderBy(x => x.StartTime).ToList();
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                });

                ArchiveRecordsGrid.ItemsSource = records;
                RefreshRecordingTimeline();
                foreach (DahuaRecordItem record in records)
                {
                    // A detailed search is stronger confirmation than the month availability probe.
                    for (DateTime day = record.StartTime.Date; day <= record.EndTime.Date; day = day.AddDays(1))
                    {
                        _archiveAvailableDays.Add(day);
                        _recorderAvailableDays.Add(day);
                    }
                }
                RefreshOpenArchiveCalendars();

                DownloadStatusText.Text = records.Count == 0
                    ? "Rejestrator nie zwrócił nagrań dla wybranego kanału i zakresu."
                    : records.Count >= 200
                        ? "Znaleziono co najmniej 200 nagrań (limit jednego zapytania). Zawęź zakres czasu, aby wyświetlić kolejne."
                        : $"Znaleziono {records.Count} nagrań. Zaznacz pozycję, aby ją odtworzyć lub wczytać do MPV.";
            }
            catch (Exception ex)
            {
                DownloadStatusText.Text = "Błąd wyszukiwania archiwum.";
                MessageBox.Show($"Nie udało się wyszukać nagrań: {ex.Message}", "Archiwum Dahua", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                DevicesListBox.IsEnabled = true;
                ArchiveChannelBox.IsEnabled = true;
                StartDatePicker.IsEnabled = true;
                EndDatePicker.IsEnabled = true;
                StartTimeBox.IsEnabled = true;
                EndTimeBox.IsEnabled = true;
                SearchArchiveButton.IsEnabled = true;
            }
        }

        private DahuaRecordItem? GetSelectedRecord() => ArchiveRecordsGrid.SelectedItem as DahuaRecordItem;

        private void ApplyRecordRange(DahuaRecordItem record)
        {
            StartDatePicker.SelectedDate = record.StartTime.Date;
            StartTimeBox.Text = record.StartTime.ToString("HH:mm:ss");
            EndDatePicker.SelectedDate = record.EndTime.Date;
            EndTimeBox.Text = record.EndTime.ToString("HH:mm:ss");
            for (int i = 0; i < ArchiveChannelBox.Items.Count; i++)
            {
                if (ArchiveChannelBox.Items[i] is ComboBoxItem item &&
                    item.Tag is int channel && channel == record.Channel)
                {
                    ArchiveChannelBox.SelectedIndex = i;
                    break;
                }
            }
        }

        private void PlaySelectedRecord_Click(object sender, RoutedEventArgs e)
        {
            var record = GetSelectedRecord();
            if (record == null)
            {
                MessageBox.Show("Najpierw zaznacz nagranie na liście.", "Brak zaznaczenia", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            ApplyRecordRange(record);
            SelectModernChannel(record.Channel);
            StartSynchronizedPlayback_Click(sender, e);
        }

        private void LoadSelectedRecord_Click(object sender, RoutedEventArgs e)
        {
            var record = GetSelectedRecord();
            if (record == null)
            {
                MessageBox.Show("Najpierw zaznacz nagranie na liście.", "Brak zaznaczenia", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            ApplyRecordRange(record);
            LoadToPlayer_Click(sender, e);
        }

        private async Task<string> RunFfmpegConversionAsync(string ffmpegPath, string inputPath, string outputPath, bool transcodeVideo)
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
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
                psi.ArgumentList.Add("-c:v");
                psi.ArgumentList.Add("libx264");
                psi.ArgumentList.Add("-preset");
                psi.ArgumentList.Add("veryfast");
                psi.ArgumentList.Add("-crf");
                psi.ArgumentList.Add("20");
            }
            else
            {
                psi.ArgumentList.Add("-c:v");
                psi.ArgumentList.Add("copy");
            }
            psi.ArgumentList.Add("-c:a");
            psi.ArgumentList.Add("aac");
            psi.ArgumentList.Add("-b:a");
            psi.ArgumentList.Add("128k");
            psi.ArgumentList.Add("-movflags");
            psi.ArgumentList.Add("+faststart");
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
                throw new FileNotFoundException("Nie znaleziono ffmpeg.exe. Wymagany jest FFmpeg do konwersji pliku DAV do MP4.", ffmpegPath);

            try
            {
                // Prefer fast remuxing: keep the original video bitstream.
                await RunFfmpegConversionAsync(ffmpegPath, inputPath, outputPath, transcodeVideo: false);
            }
            catch (Exception remuxError)
            {
                // Some DAV recordings contain video streams that cannot be muxed
                // into MP4 as-is. Retry by encoding video to broadly supported H.264.
                if (File.Exists(outputPath)) File.Delete(outputPath);
                try
                {
                    await RunFfmpegConversionAsync(ffmpegPath, inputPath, outputPath, transcodeVideo: true);
                }
                catch (Exception transcodeError)
                {
                    throw new InvalidOperationException(
                        "Konwersja DAV do MP4 nie powiodła się. Remux: " + remuxError.Message +
                        " | Kodowanie H.264: " + transcodeError.Message, transcodeError);
                }
            }
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
            if (_loginId == IntPtr.Zero || !string.Equals(_connectedDeviceId, _currentDevice.Id, StringComparison.Ordinal))
            {
                // Reconnect to the selected endpoint, not to a previously selected device.
                Connect_Click(sender, e);
                if (_loginId == IntPtr.Zero)
                {
                    MessageBox.Show("Nie udało się połączyć z rejestratorem. Sprawdź stan SDK, adres, port i dane logowania.", "Błąd logowania", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            string outputDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VideoPlayer", "nagrania_dahua");
            Directory.CreateDirectory(outputDir);
            string safeChannelName = string.Concat(chName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)).Replace(' ', '_');
            string fileStem = $"Dahua_{safeChannelName}_CH{channel}_{start.Value:yyyyMMdd_HHmmss}-{end.Value:HHmmss}";
            string davFilePath = Path.Combine(outputDir, fileStem + ".dav");
            string targetFilePath = Path.Combine(outputDir, fileStem + ".mp4");
            if (File.Exists(davFilePath)) File.Delete(davFilePath);
            if (File.Exists(targetFilePath)) File.Delete(targetFilePath);

            DownloadProgressBar.Visibility = Visibility.Visible;
            DownloadProgressBar.IsIndeterminate = true;
            DownloadStatusText.Visibility = Visibility.Visible;
            DownloadStatusText.Text = $"Pobieranie nagrania: {chName} (CH {channel}) za okres {start.Value:g} — {end.Value:g}…";
            LoadToPlayerButton.IsEnabled = false;
            DevicesListBox.IsEnabled = false;
            ArchiveChannelBox.IsEnabled = false;
            SearchArchiveButton.IsEnabled = false;

            try
            {
                int channelIndex = Math.Max(0, channel - 1);
                var tmStart = DahuaNetSdk.NET_TIME.FromDateTime(start.Value);
                var tmEnd = DahuaNetSdk.NET_TIME.FromDateTime(end.Value);
                // CLIENT_DownloadByTimeEx expects the SDK query record type,
                // not NET_RECORDFILE_INFO.nRecordFileType from the returned file row.
                // Use NET_RECORD_TYPE_ALL (0) for a time-range download.
                int recordType = 0;
                IntPtr activeLoginId = _loginId;

                bool downloadFinished = false;

                await Task.Run(async () =>
                {
                    ConfigureDahuaRecordModes(activeLoginId);

                    // Native download is asynchronous. Do not stop it after a fixed
                    // 15-second delay: larger archive segments need substantially longer.
                    // Keep the wait handle alive until the native download has stopped;
                    // an unmanaged callback may arrive just after the completion signal.
                    var completed = new System.Threading.ManualResetEventSlim(false);
                    DahuaNetSdk.fTimeDownLoadPosCallBack progressCallback =
                        (playHandle, totalSize, downloadedSize, index, recordInfo, userData) =>
                        {
                            // Dahua NetSDK reports -1 in dwDownLoadSize when the transfer ends.
                            if (downloadedSize == uint.MaxValue ||
                                (totalSize > 0 && downloadedSize >= totalSize))
                                completed.Set();
                        };

                    WriteSdkDiagnostic($"Calling CLIENT_DownloadByTimeEx: channel={channelIndex}; recordType={recordType}; start={start.Value:O}; end={end.Value:O}; file={davFilePath}.");
                    IntPtr hDown = DahuaNetSdk.CLIENT_DownloadByTimeEx(
                        activeLoginId, channelIndex, recordType, ref tmStart, ref tmEnd, davFilePath,
                        progressCallback, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    WriteSdkDiagnostic($"CLIENT_DownloadByTimeEx returned handle=0x{hDown.ToInt64():X}.");

                    if (hDown == IntPtr.Zero)
                    {
                        uint downloadError = DahuaNetSdk.CLIENT_GetLastError();
                        throw new InvalidOperationException(
                            $"CLIENT_DownloadByTimeEx: {DahuaNetSdk.GetErrorDescription(downloadError)}");
                    }

                    if (hDown != IntPtr.Zero)
                    {
                        try
                        {
                            // Timeout prevents a hung SDK call from blocking the UI forever.
                            bool signaled = await Task.Run(() => completed.Wait(TimeSpan.FromMinutes(30)));
                            if (signaled && File.Exists(davFilePath) &&
                                new FileInfo(davFilePath).Length > 0)
                                downloadFinished = true;
                        }
                        finally
                        {
                            DahuaNetSdk.CLIENT_StopDownload(hDown);
                            GC.KeepAlive(progressCallback);
                        }
                    }

                });

                if (!downloadFinished || !File.Exists(davFilePath) || new FileInfo(davFilePath).Length == 0)
                    throw new InvalidOperationException("NetSDK nie zakończył pobierania pliku archiwum. Sprawdź dostępność nagrania i log SDK.");

                DownloadStatusText.Text = "Pobrano plik DAV. Konwersja do MP4…";
                await ConvertDahuaRecordingToMp4Async(davFilePath, targetFilePath);

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
                DevicesListBox.IsEnabled = true;
                ArchiveChannelBox.IsEnabled = true;
                SearchArchiveButton.IsEnabled = true;
            }
        }

        private void PlayArchiveInWindow_Click(object sender, RoutedEventArgs e)
        {
            SelectModernChannel(GetSelectedArchiveChannel());
            StartSynchronizedPlayback_Click(sender, e);
        }

        #endregion

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _playbackUiTimer.Stop();
            Disconnect();
            if (_sdkInitialized)
            {
                try { DahuaNetSdk.CLIENT_Cleanup(); } catch { }
                _sdkInitialized = false;
            }
        }
    }
}
