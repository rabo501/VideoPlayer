using System;
using System.Collections.Generic;
using System.Globalization;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VideoPlayer.ViewModels
{
    internal sealed record DahuaChannelNameChange(int Number, string LocalName, string RemoteName);

    internal sealed record DahuaDeviceStateReadResult(
        int ChannelCount,
        List<DahuaChannelInfo> RemoteChannels,
        int? PresetCount,
        string? PresetError);

    internal sealed record DahuaPresetPositionSaveResult(bool NameSaved, string NameError);

    internal sealed partial class DahuaViewModel : ObservableObject, IDisposable
    {
        public DahuaViewModel(DahuaSessionService? sessionService = null)
        {
            Session = sessionService ?? new DahuaSessionService();
        }

        public DahuaSessionService Session { get; }

        public ObservableCollection<DahuaDeviceItem> Devices { get; } = new();
        public ObservableCollection<DahuaRecordItem> ArchiveRecords { get; } = new();

        [ObservableProperty]
        private string _sdkStatusBadge = "Dahua NetSDK: Oczekuje na uruchomienie";

        [ObservableProperty]
        private string _statusText = "Wybierz urządzenie z listy i połącz.";

        [ObservableProperty]
        private string _playbackSyncStatusText = "Wybierz kanały i zakres czasu.";

        [ObservableProperty]
        private string _playbackPositionText = "00:00:00 / 00:00:00";

        [ObservableProperty]
        private string _calendarAvailabilityStatusText = "Otwórz kalendarz, aby sprawdzić dostępność nagrań.";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsDownloadStatusVisible))]
        private string _downloadStatusText = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanConnect), nameof(CanDisconnect))]
        private bool _isConnected;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanConnect), nameof(CanDisconnect))]
        private bool _isConnecting;

        [ObservableProperty]
        private bool _isSdkInitialized;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsArchiveInputsEnabled), nameof(IsDownloadStatusVisible))]
        private bool _isArchiveSearching;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsArchiveInputsEnabled), nameof(IsDownloadStatusVisible))]
        private bool _isDownloadBusy;

        [ObservableProperty]
        private bool _isStreamOverlayVisible = true;

        [ObservableProperty]
        private DahuaDeviceItem? _selectedDevice;

        [ObservableProperty]
        private int _currentChannel = 1;

        public bool CanConnect => !IsConnected && !IsConnecting;
        public bool CanDisconnect => IsConnected && !IsConnecting;
        public bool IsArchiveInputsEnabled => !IsArchiveSearching && !IsDownloadBusy;
        public bool IsDownloadStatusVisible => !string.IsNullOrWhiteSpace(DownloadStatusText) || IsDownloadBusy || IsArchiveSearching;

        public DahuaSdkInitResult EnsureSdkInitialized()
        {
            DahuaSdkInitResult result = Session.EnsureSdkInitialized();
            IsSdkInitialized = result.Success;
            SdkStatusBadge = result.BadgeText;
            if (!IsConnected || !result.Success)
                StatusText = result.StatusMessage;
            return result;
        }

        public void LoadDevices()
        {
            List<DahuaDeviceItem> loaded = DahuaConfigService.LoadDevices();
            Devices.Clear();
            foreach (DahuaDeviceItem device in loaded)
                Devices.Add(device);

            SelectedDevice = Devices.FirstOrDefault();
        }

        public void SaveDevices()
        {
            DahuaConfigService.SaveDevices(Devices.ToList());
        }

        public static bool TryValidateDeviceConfiguration(
            string? name,
            string? address,
            string? portText,
            string? httpPortText,
            string? username,
            string? password,
            out ushort port,
            out ushort httpPort,
            out string error)
        {
            port = 0;
            httpPort = 0;

            if (string.IsNullOrWhiteSpace(name))
            {
                error = "Podaj nazwę urządzenia.";
                return false;
            }

            if (!DahuaSessionService.IsValidEndpoint(address, portText, out port, out error))
                return false;

            if (!ushort.TryParse(httpPortText?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out httpPort) ||
                httpPort == 0)
            {
                error = "Port HTTP musi mieścić się w zakresie 1–65535.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            {
                error = "Wpisz nazwę użytkownika i hasło. Puste hasło nie zostanie zapisane.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        public void SaveDeviceConfiguration(
            DahuaDeviceItem device,
            string name,
            string address,
            ushort port,
            ushort httpPort,
            string username,
            string password)
        {
            if (!Devices.Contains(device))
                throw new InvalidOperationException("Wybrane urządzenie nie znajduje się na liście konfiguracji.");

            if (Session.IsConnectedTo(device))
                throw new InvalidOperationException("Przed zmianą konfiguracji rozłącz urządzenie.");

            device.Name = name.Trim();
            device.IpAddress = address.Trim();
            device.Port = port;
            device.HttpPort = httpPort;
            device.SetCredentials(username.Trim(), password);
            SaveDevices();

            StatusText = $"Zapisano urządzenie: '{device.Name}' ({device.IpAddress}:{device.Port})";
        }

        public DahuaDeviceItem AddNewDevice()
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
            Devices.Add(device);
            SelectedDevice = device;
            return device;
        }

        public void RemoveDevice(DahuaDeviceItem device)
        {
            if (Session.IsConnectedTo(device))
                Disconnect();

            Devices.Remove(device);
            SaveDevices();
            SelectedDevice = Devices.FirstOrDefault();
            StatusText = $"Usunięto urządzenie '{device.Name}'.";
        }

        public DahuaConnectionResult Connect(DahuaDeviceItem device)
        {
            StatusText = $"Logowanie do {device.ConnectionAddress}:{device.Port}…";
            DahuaConnectionResult result = Session.Connect(device);
            IsConnected = result.Success;
            if (!result.Success && !string.IsNullOrWhiteSpace(result.ErrorMessage))
                StatusText = result.ErrorMessage;
            return result;
        }

        public async Task<DahuaConnectionResult> ConnectAsync(DahuaDeviceItem device, CancellationToken ct = default)
        {
            if (IsConnecting)
                return new DahuaConnectionResult(false, "Trwa poprzednie łączenie z urządzeniem.", 0, null);

            IsConnecting = true;
            StatusText = $"Logowanie do {device.ConnectionAddress}:{device.Port}…";
            try
            {
                DahuaConnectionResult result = await Session.ConnectAsync(device, ct);
                IsConnected = result.Success;
                if (!result.Success && !string.IsNullOrWhiteSpace(result.ErrorMessage))
                    StatusText = result.ErrorMessage;
                return result;
            }
            finally
            {
                IsConnecting = false;
            }
        }

        public void Disconnect()
        {
            Session.Disconnect();
            IsConnected = false;
            StatusText = "Rozłączono ze strumieniem.";
        }

        public bool SendPtz(int channel, DahuaNetSdk.EM_PTZ_COMMAND command, bool stop)
        {
            if (Session.LoginId == IntPtr.Zero)
                return false;

            try
            {
                bool success = Session.SendPtz(channel, command, stop, out uint error);
                if (!success)
                    StatusText = $"Sterowanie PTZ nie powiodło się (kod: 0x{error:X}).";
                return success;
            }
            catch (Exception ex)
            {
                StatusText = $"Błąd sterowania PTZ: {ex.Message}";
                return false;
            }
        }

        public bool SendPtzDiagonal(int channel, string? direction, bool stop)
        {
            DahuaNetSdk.EM_PTZ_COMMAND? command = DahuaSessionService.ParseDiagonalDirection(direction);
            return command.HasValue && SendPtz(channel, command.Value, stop);
        }

        public async Task RunPtzPulseAsync(int channel, DahuaNetSdk.EM_PTZ_COMMAND command, int durationMs)
        {
            if (Session.LoginId == IntPtr.Zero)
                throw new InvalidOperationException("Brak aktywnego połączenia z kamerą Dahua.");

            try
            {
                await Session.RunPtzPulseAsync(channel, command, durationMs);
            }
            catch (Exception ex)
            {
                StatusText = $"Błąd sterowania PTZ: {ex.Message}";
                throw;
            }
        }

        public async Task ApplyAreaZoomAsync(int channel, int viewWidth, int viewHeight, int left, int top, int width, int height)
        {
            if (Session.LoginId == IntPtr.Zero)
                throw new InvalidOperationException("Brak aktywnego połączenia z kamerą Dahua.");

            try
            {
                await Session.ApplyAreaZoomAsync(channel, viewWidth, viewHeight, left, top, width, height);
            }
            catch (Exception ex)
            {
                StatusText = $"Błąd przybliżania obszaru PTZ: {ex.Message}";
                throw;
            }
        }

        public IntPtr StartLiveStream(int channel, IntPtr windowHandle, DahuaNetSdk.EM_RealPlayType streamType) =>
            Session.StartLiveStream(channel, windowHandle, streamType);

        public void StopLiveStream(IntPtr handle) => Session.StopLiveStream(handle);
        public void StopAllLiveStreams() => Session.StopAllLiveStreams();
        public IntPtr StartArchivePlayback(int channel, DateTime start, DateTime end, IntPtr windowHandle) =>
            Session.StartArchivePlayback(channel, start, end, windowHandle);
        public void StopArchivePlayback(IntPtr handle) => Session.StopArchivePlayback(handle);
        public void StopAllArchivePlaybacks() => Session.StopAllArchivePlaybacks();
        public IReadOnlyList<IntPtr> GetArchivePlaybackHandles() => Session.GetArchivePlaybackHandles();
        public int SetArchivePlaybackSpeed(double speed) => Session.SetArchivePlaybackSpeed(speed);
        public bool SetArchivePlaybackPaused(bool pause) => Session.SetArchivePlaybackPaused(pause);
        public int SeekArchivePlayback(int offsetSeconds) => Session.SeekArchivePlayback(offsetSeconds);

        public bool GotoPreset(int channel, int presetNumber)
        {
            if (Session.LoginId == IntPtr.Zero)
            {
                StatusText = "Brak aktywnego połączenia z kamerą Dahua.";
                return false;
            }

            try
            {
                bool success = Session.GotoPreset(channel, presetNumber, out uint error);
                StatusText = success
                    ? $"Kamera PTZ: przechodzenie do presetu {presetNumber}…"
                    : $"Błąd przejścia do presetu {presetNumber}: {DahuaNetSdk.GetErrorDescription(error)}";
                return success;
            }
            catch (Exception ex)
            {
                StatusText = $"Wyjątek PTZ Preset: {ex.Message}";
                return false;
            }
        }

        public static List<DahuaChannelNameChange> GetChannelNameChanges(
            DahuaDeviceItem device,
            IEnumerable<DahuaChannelInfo> remoteChannels)
        {
            ArgumentNullException.ThrowIfNull(device);
            ArgumentNullException.ThrowIfNull(remoteChannels);

            return remoteChannels
                .Where(channel => channel.Number > 0 && !string.IsNullOrWhiteSpace(channel.Name))
                .Select(channel => new DahuaChannelNameChange(
                    channel.Number,
                    device.GetChannelName(channel.Number),
                    channel.Name.Trim()))
                .Where(change => !string.Equals(change.LocalName, change.RemoteName, StringComparison.Ordinal))
                .ToList();
        }

        public async Task<DahuaDeviceStateReadResult> ReadDeviceStateAsync(
            DahuaDeviceItem device,
            int channel,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(device);
            if (!Session.IsConnectedTo(device))
                throw new InvalidOperationException("Brak aktywnego połączenia z wybranym rejestratorem.");

            WriteDiagnostic("Manual device read started.");
            int channelCount = Session.DeviceChannelCount > 0
                ? Session.DeviceChannelCount
                : device.Channels.Count;
            if (channelCount > 0)
                device.EnsureDefaultChannels(channelCount);

            List<DahuaChannelInfo> remoteChannels =
                await Session.QueryChannelNamesAsync(channelCount, ct);

            int? presetCount = null;
            string? presetError = null;
            try
            {
                List<DahuaPresetItem> remotePresets =
                    await DahuaSessionService.QueryPresetsAsync(device, channel, ct);
                device.UpdateFromDevicePresets(remotePresets);
                presetCount = remotePresets.Count;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                presetError = ex.Message;
                WriteDiagnostic($"PTZ preset read failed; local list preserved. {ex}");
            }

            return new DahuaDeviceStateReadResult(channelCount, remoteChannels, presetCount, presetError);
        }

        public int ApplyDeviceChannelNames(DahuaDeviceItem device, IEnumerable<DahuaChannelInfo> remoteChannels)
        {
            EnsureManagedDevice(device);
            List<DahuaChannelNameChange> changes = GetChannelNameChanges(device, remoteChannels);
            foreach (DahuaChannelNameChange change in changes)
                device.SetChannelName(change.Number, change.RemoteName);

            if (changes.Count > 0)
                SaveDevices();

            return changes.Count;
        }

        public void SaveChannelName(DahuaDeviceItem device, int channel, string name)
        {
            EnsureManagedDevice(device);
            if (channel < 1)
                throw new ArgumentOutOfRangeException(nameof(channel));
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Nazwa kanału nie może być pusta.", nameof(name));

            device.SetChannelName(channel, name);
            SaveDevices();
            StatusText = $"Zapisano nową nazwę dla Kanału {channel}: '{name.Trim()}'.";
        }

        public void ApplyDevicePresets(DahuaDeviceItem device, List<DahuaPresetItem> remotePresets)
        {
            EnsureManagedDevice(device);
            device.UpdateFromDevicePresets(remotePresets);
            SaveDevices();
            StatusText = $"Odczytano {remotePresets.Count} presetów dla kanału {CurrentChannel}.";
        }

        public async Task<DahuaPresetPositionSaveResult> SavePresetPositionAsync(
            DahuaDeviceItem device, int channel, int presetNumber, string presetName, CancellationToken ct = default)
        {
            EnsureConnectedDevice(device);
            if (presetNumber < 1 || presetNumber > 255)
                throw new ArgumentOutOfRangeException(nameof(presetNumber));

            await DahuaSessionService.SetPresetPositionAsync(device, channel, presetNumber, ct);

            bool nameSaved = true;
            string nameError = string.Empty;
            try
            {
                await DahuaSessionService.SetPresetNameAsync(device, channel, presetNumber, presetName, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                nameSaved = false;
                nameError = ex.Message;
            }

            string effectiveName = nameSaved ? presetName.Trim() : $"Preset {presetNumber}";
            var existing = device.Presets.FirstOrDefault(preset => preset.PresetNumber == presetNumber);
            if (existing != null)
                existing.Name = effectiveName;
            else
                device.Presets.Add(new DahuaPresetItem { PresetNumber = presetNumber, Name = effectiveName });

            SaveDevices();
            StatusText = nameSaved
                ? $"Zapisano preset {presetNumber}: {effectiveName}."
                : $"Pozycja presetu {presetNumber} została zapisana, ale nie udało się ustawić nazwy.";
            return new DahuaPresetPositionSaveResult(nameSaved, nameError);
        }

        public async Task RenamePresetAsync(
            DahuaDeviceItem device, int channel, int presetNumber, string newName, CancellationToken ct = default)
        {
            EnsureConnectedDevice(device);
            if (string.IsNullOrWhiteSpace(newName))
                throw new ArgumentException("Nazwa presetu nie może być pusta.", nameof(newName));

            await DahuaSessionService.SetPresetNameAsync(device, channel, presetNumber, newName.Trim(), ct);
            var preset = device.Presets.FirstOrDefault(item => item.PresetNumber == presetNumber);
            if (preset != null)
                preset.Name = newName.Trim();

            SaveDevices();
            StatusText = $"Zmieniono nazwę presetu {presetNumber} na '{newName.Trim()}'.";
        }

        public async Task RefreshPresetsFromDeviceAsync(
            DahuaDeviceItem device, int channel, CancellationToken ct = default)
        {
            EnsureConnectedDevice(device);
            List<DahuaPresetItem> remotePresets =
                await DahuaSessionService.QueryPresetsAsync(device, channel, ct);
            device.UpdateFromDevicePresets(remotePresets);
            SaveDevices();
            StatusText = $"Odczytano {remotePresets.Count} presetów dla kanału {channel}.";
        }

        public async Task DeletePresetAsync(
            DahuaDeviceItem device, int channel, int presetNumber, CancellationToken ct = default)
        {
            EnsureConnectedDevice(device);
            await DahuaSessionService.DeletePresetAsync(device, channel, presetNumber, ct);
            device.Presets.RemoveAll(preset => preset.PresetNumber == presetNumber);
            SaveDevices();
            StatusText = $"Usunięto preset {presetNumber} z urządzenia.";
        }

        private void EnsureManagedDevice(DahuaDeviceItem device)
        {
            ArgumentNullException.ThrowIfNull(device);
            if (!Devices.Contains(device))
                throw new InvalidOperationException("Wybrane urządzenie nie znajduje się na liście konfiguracji.");
        }

        private void EnsureConnectedDevice(DahuaDeviceItem device)
        {
            EnsureManagedDevice(device);
            if (!Session.IsConnectedTo(device))
                throw new InvalidOperationException("Brak aktywnego połączenia z wybranym urządzeniem.");
        }

        private static void WriteDiagnostic(string message) => DahuaSessionService.WriteDiagnostic(message);

        public async Task<List<DahuaRecordItem>> SearchArchiveRecordsAsync(int channel, DateTime start, DateTime end, CancellationToken ct = default)
        {
            IsArchiveSearching = true;
            ArchiveRecords.Clear();
            try
            {
                List<DahuaRecordItem> records = await Session.SearchArchiveRecordsAsync(channel, start, end, ct);
                foreach (DahuaRecordItem record in records)
                    ArchiveRecords.Add(record);

                DownloadStatusText = records.Count == 0
                    ? "Rejestrator nie zwrócił nagrań dla wybranego kanału i zakresu."
                    : records.Count >= 200
                        ? "Znaleziono co najmniej 200 nagrań. Zawęź zakres czasu."
                        : $"Znaleziono {records.Count} nagrań.";
                return records;
            }
            finally
            {
                IsArchiveSearching = false;
            }
        }

        public void Dispose()
        {
            Session.Dispose();
        }
    }
}
