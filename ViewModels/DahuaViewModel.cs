using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VideoPlayer.ViewModels
{
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
