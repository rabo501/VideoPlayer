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
        private bool _isArchiveSearching;

        [ObservableProperty]
        private bool _isDownloadBusy;

        [ObservableProperty]
        private DahuaDeviceItem? _selectedDevice;

        [ObservableProperty]
        private int _currentChannel = 1;

        public bool CanConnect => !IsConnected && !IsConnecting;
        public bool CanDisconnect => IsConnected && !IsConnecting;

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
