using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VideoPlayer
{
    internal sealed record DahuaSdkInitResult(bool Success, string BadgeText, string StatusMessage, bool IsError);

    internal sealed record DahuaConnectionResult(
        bool Success,
        string? ErrorMessage,
        int ChannelCount,
        HashSet<int>? ActiveChannels);

    internal sealed record DahuaPtzPulsePlan(
        DahuaNetSdk.EM_PTZ_COMMAND? PanCommand,
        int PanDurationMs,
        DahuaNetSdk.EM_PTZ_COMMAND? TiltCommand,
        int TiltDurationMs,
        int ZoomDurationMs);

    /// <summary>
    /// Serwis obsługujący połączenia, PTZ, podgląd wielokanałowy, kwerendy kalendarza,
    /// wyszukiwanie w archiwum, odtwarzanie zsynchronizowane oraz konwersję DAV → MP4 dla urządzeń Dahua.
    /// Nie zależy od klas UI WPF (Dispatcher, Controls, MessageBox), dzięki czemu może być testowany niezależnie.
    /// </summary>
    internal sealed class DahuaSessionService : IDisposable
    {
        private readonly Dictionary<string, HashSet<DateTime>> _recorderAvailabilityCache = new();
        private readonly HashSet<string> _availabilityQueriesInProgress = new();
        private readonly SemaphoreSlim _archiveAvailabilitySemaphore = new(1, 1);

        private DahuaNetSdk.fDisConnect? _disConnectCallback;
        private DahuaNetSdk.fHaveReConnect? _haveReConnectCallback;
        private bool _disposed;
        private readonly HashSet<IntPtr> _liveStreamHandles = new();
        private readonly HashSet<IntPtr> _archivePlaybackHandles = new();

        public bool IsSdkInitialized { get; private set; }
        public IntPtr LoginId { get; private set; } = IntPtr.Zero;
        public string? ConnectedDeviceId { get; private set; }
        public int DeviceChannelCount { get; private set; }
        public HashSet<int>? ActiveChannelNumbers { get; private set; }

        public event Action<string, int>? SdkDisconnected;
        public event Action<string, int>? SdkReconnected;

        #region Pure Validation & Calculation Helpers (Unit-Testable without Hardware or WPF)

        public static bool IsValidEndpoint(string? address, string? portText, out ushort port, out string error)
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
            if (!ushort.TryParse(portText?.Trim(), out port) || port == 0)
            {
                error = "Port musi być liczbą z zakresu 1–65535.";
                return false;
            }
            return true;
        }

        public static bool ValidateArchiveRange(DateTime? start, DateTime? end, out string error)
        {
            if (start == null || end == null || end <= start)
            {
                error = "Wybierz poprawny przedział czasu.";
                return false;
            }
            error = string.Empty;
            return true;
        }

        public static (int Columns, int Rows) GetGridDimensions(int capacity)
        {
            int normalized = capacity switch
            {
                <= 1 => 1,
                <= 4 => 4,
                <= 9 => 9,
                _ => 16
            };
            int columns = normalized == 1 ? 1 : normalized == 4 ? 2 : normalized == 9 ? 3 : 4;
            return (columns, normalized / columns);
        }

        public static DahuaPtzPulsePlan ComputeAreaZoomPulses(
            int viewWidth, int viewHeight, int areaLeft, int areaTop, int areaWidth, int areaHeight)
        {
            int safeW = Math.Max(1, viewWidth);
            int safeH = Math.Max(1, viewHeight);
            double centerX = (areaLeft + areaWidth / 2.0) / safeW;
            double centerY = (areaTop + areaHeight / 2.0) / safeH;

            double horizontalOffset = (centerX - 0.5) * 2.0;
            double verticalOffset = (centerY - 0.5) * 2.0;

            DahuaNetSdk.EM_PTZ_COMMAND? panCmd = null;
            int panMs = 0;
            if (Math.Abs(horizontalOffset) > 0.07)
            {
                panCmd = horizontalOffset > 0 ? DahuaNetSdk.EM_PTZ_COMMAND.RIGHT : DahuaNetSdk.EM_PTZ_COMMAND.LEFT;
                panMs = (int)Math.Clamp(Math.Abs(horizontalOffset) * 650, 90, 650);
            }

            DahuaNetSdk.EM_PTZ_COMMAND? tiltCmd = null;
            int tiltMs = 0;
            if (Math.Abs(verticalOffset) > 0.07)
            {
                tiltCmd = verticalOffset > 0 ? DahuaNetSdk.EM_PTZ_COMMAND.DOWN : DahuaNetSdk.EM_PTZ_COMMAND.UP;
                tiltMs = (int)Math.Clamp(Math.Abs(verticalOffset) * 550, 90, 550);
            }

            double relativeWidth = Math.Max(1, areaWidth) / (double)safeW;
            double relativeHeight = Math.Max(1, areaHeight) / (double)safeH;
            double targetFactor = Math.Clamp(1.0 / Math.Max(0.05, Math.Min(relativeWidth, relativeHeight)), 1.0, 12.0);
            int zoomMs = targetFactor > 1.08
                ? (int)Math.Clamp(Math.Log(targetFactor) * 950, 150, 4000)
                : 0;

            return new DahuaPtzPulsePlan(panCmd, panMs, tiltCmd, tiltMs, zoomMs);
        }

        public static int ComputeArchiveSeekOffsetSeconds(DateTime selectedTime, DateTime rangeStart, DateTime rangeEnd)
        {
            double duration = Math.Max(1, (rangeEnd - rangeStart).TotalSeconds);
            return (int)Math.Clamp(Math.Round((selectedTime - rangeStart).TotalSeconds), 0, duration);
        }

        public static DahuaNetSdk.EM_PTZ_COMMAND? ParseDiagonalDirection(string? direction) =>
            direction?.ToUpperInvariant() switch
            {
                "UP_LEFT" => DahuaNetSdk.EM_PTZ_COMMAND.LEFTTOP,
                "UP_RIGHT" => DahuaNetSdk.EM_PTZ_COMMAND.RIGHTTOP,
                "DOWN_LEFT" => DahuaNetSdk.EM_PTZ_COMMAND.LEFTDOWN,
                "DOWN_RIGHT" => DahuaNetSdk.EM_PTZ_COMMAND.RIGHTDOWN,
                _ => null
            };

        #endregion

        public static void WriteDiagnostic(string message)
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

        public DahuaSdkInitResult EnsureSdkInitialized()
        {
            if (IsSdkInitialized)
                return new DahuaSdkInitResult(true, "Dahua NetSDK: Zainicjalizowano (x64)", "Gotowy do połączenia ze strumieniem IP.", false);

            try
            {
                int deviceInfoSize = Marshal.SizeOf<DahuaNetSdk.NET_DEVICEINFO>();
                WriteDiagnostic($"InitializeSdk started. NET_DEVICEINFO size={deviceInfoSize} bytes; process={(Environment.Is64BitProcess ? "x64" : "x86")}.");
                if (deviceInfoSize != 53)
                {
                    throw new InvalidOperationException(
                        $"Nieprawidłowy rozmiar NET_DEVICEINFO: {deviceInfoSize} bajtów (oczekiwano 53).");
                }

                List<string> missingDlls = DahuaNetSdk.CheckDependencies();
                if (missingDlls.Count > 0)
                {
                    string missingList = string.Join(", ", missingDlls);
                    return new DahuaSdkInitResult(
                        false,
                        $"Dahua NetSDK: Brak plików: {missingList}",
                        $"Brak bibliotek DLL: {missingList}. Skopiuj je do folderu programu.",
                        true);
                }

                _disConnectCallback = (_, pchDVRIP, nDVRPort, _) => SdkDisconnected?.Invoke(pchDVRIP, nDVRPort);
                _haveReConnectCallback = (_, pchDVRIP, nDVRPort, _) => SdkReconnected?.Invoke(pchDVRIP, nDVRPort);

                WriteDiagnostic("Calling CLIENT_Init.");
                bool initOk = DahuaNetSdk.CLIENT_Init(_disConnectCallback, IntPtr.Zero);
                WriteDiagnostic($"CLIENT_Init returned: {initOk}.");

                if (initOk)
                {
                    IsSdkInitialized = true;
                    DahuaNetSdk.CLIENT_SetAutoReconnect(_haveReConnectCallback, IntPtr.Zero);
                    return new DahuaSdkInitResult(true, "Dahua NetSDK: Zainicjalizowano (x64)", "Gotowy do połączenia ze strumieniem IP.", false);
                }

                return new DahuaSdkInitResult(false, "Dahua NetSDK: Błąd CLIENT_Init", "Błąd inicjalizacji biblioteki CLIENT_Init.", true);
            }
            catch (Exception ex)
            {
                return new DahuaSdkInitResult(false, "Dahua NetSDK: Wyjątek DLL", $"Błąd: {ex.Message}", true);
            }
        }

        public DahuaConnectionResult Connect(DahuaDeviceItem device)
        {
            ArgumentNullException.ThrowIfNull(device);
            var init = EnsureSdkInitialized();
            if (!init.Success)
                return new DahuaConnectionResult(false, init.StatusMessage, 0, null);

            Disconnect();

            string ip = device.ConnectionAddress;
            string user = device.Username ?? string.Empty;
            string pass = device.Password ?? string.Empty;

            var deviceInfo = DahuaNetSdk.NET_DEVICEINFO.Create();
            int err = 0;
            IntPtr loginId = DahuaNetSdk.CLIENT_Login(ip, device.Port, user, pass, ref deviceInfo, ref err);
            if (loginId == IntPtr.Zero)
            {
                uint lastError = DahuaNetSdk.CLIENT_GetLastError();
                return new DahuaConnectionResult(false, $"Błąd logowania do {ip}. {DahuaNetSdk.GetErrorDescription(lastError)}", 0, null);
            }

            LoginId = loginId;
            ConnectedDeviceId = device.Id;
            DeviceChannelCount = Math.Max(0, (int)deviceInfo.byChanNum);
            if (DeviceChannelCount > 0)
                device.EnsureDefaultChannels(DeviceChannelCount);

            ActiveChannelNumbers = TryQueryActiveChannelNumbers(device, out var active) ? active : null;
            return new DahuaConnectionResult(true, null, DeviceChannelCount, ActiveChannelNumbers);
        }

        public async Task<DahuaConnectionResult> ConnectAsync(DahuaDeviceItem device, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(device);
            DahuaSdkInitResult init = EnsureSdkInitialized();
            if (!init.Success)
                return new DahuaConnectionResult(false, init.StatusMessage, 0, null);

            Disconnect();

            string ip = device.ConnectionAddress;
            string user = device.Username ?? string.Empty;
            string pass = device.Password ?? string.Empty;
            ushort port = device.Port;

            var loginOutcome = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var deviceInfo = DahuaNetSdk.NET_DEVICEINFO.Create();
                int err = 0;
                IntPtr id = DahuaNetSdk.CLIENT_Login(ip, port, user, pass, ref deviceInfo, ref err);
                uint lastErr = id == IntPtr.Zero ? DahuaNetSdk.CLIENT_GetLastError() : 0;
                return (LoginId: id, ChannelCount: Math.Max(0, (int)deviceInfo.byChanNum), LastError: lastErr);
            }, ct).ConfigureAwait(false);

            if (loginOutcome.LoginId == IntPtr.Zero)
            {
                return new DahuaConnectionResult(
                    false,
                    $"Błąd logowania do {ip}. {DahuaNetSdk.GetErrorDescription(loginOutcome.LastError)}",
                    0,
                    null);
            }

            if (ct.IsCancellationRequested)
            {
                try { DahuaNetSdk.CLIENT_Logout(loginOutcome.LoginId); } catch { }
                ct.ThrowIfCancellationRequested();
            }

            LoginId = loginOutcome.LoginId;
            ConnectedDeviceId = device.Id;
            DeviceChannelCount = loginOutcome.ChannelCount;
            if (DeviceChannelCount > 0)
                device.EnsureDefaultChannels(DeviceChannelCount);

            ActiveChannelNumbers = await TryQueryActiveChannelNumbersAsync(device, ct).ConfigureAwait(false);
            return new DahuaConnectionResult(true, null, DeviceChannelCount, ActiveChannelNumbers);
        }

        public void Disconnect()
        {
            StopAllLiveStreams();
            StopAllArchivePlaybacks();
            if (LoginId != IntPtr.Zero)
            {
                try { DahuaNetSdk.CLIENT_Logout(LoginId); } catch { }
                LoginId = IntPtr.Zero;
            }
            ConnectedDeviceId = null;
            ActiveChannelNumbers = null;
            DeviceChannelCount = 0;
        }

        public bool IsConnectedTo(DahuaDeviceItem? device) =>
            device != null &&
            LoginId != IntPtr.Zero &&
            string.Equals(ConnectedDeviceId, device.Id, StringComparison.Ordinal);

        public static bool TryQueryActiveChannelNumbers(DahuaDeviceItem device, out HashSet<int> activeChannels)
        {
            HashSet<int>? result = TryQueryActiveChannelNumbersAsync(device, CancellationToken.None).GetAwaiter().GetResult();
            activeChannels = result ?? new HashSet<int>();
            return result != null;
        }

        public static async Task<HashSet<int>?> TryQueryActiveChannelNumbersAsync(DahuaDeviceItem device, CancellationToken ct = default)
        {
            if (device.CredentialsUnavailable || string.IsNullOrWhiteSpace(device.ConnectionAddress))
                return null;

            try
            {
                using var handler = new HttpClientHandler
                {
                    Credentials = new NetworkCredential(device.Username, device.Password),
                    PreAuthenticate = true,
                    UseDefaultCredentials = false
                };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
                string url = $"http://{device.ConnectionAddress}:{device.HttpPort}/cgi-bin/api/LogicDeviceManager/getCameraState";
                using var content = new StringContent("{\"uniqueChannels\":[-1]}", Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(url, content, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                using var responseStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: ct).ConfigureAwait(false);
                if (!document.RootElement.TryGetProperty("states", out JsonElement states) || states.ValueKind != JsonValueKind.Array)
                    return null;

                var activeChannels = new HashSet<int>();
                foreach (JsonElement state in states.EnumerateArray())
                {
                    if (!state.TryGetProperty("channel", out JsonElement channelValue) || !channelValue.TryGetInt32(out int zeroBasedChannel))
                        continue;
                    string connectionState = state.TryGetProperty("connectionState", out JsonElement stateValue)
                        ? stateValue.GetString() ?? string.Empty
                        : string.Empty;
                    if (connectionState.Equals("Empty", StringComparison.OrdinalIgnoreCase) ||
                        connectionState.Equals("Disable", StringComparison.OrdinalIgnoreCase) ||
                        connectionState.Equals("UnInited", StringComparison.OrdinalIgnoreCase))
                        continue;
                    activeChannels.Add(zeroBasedChannel + 1);
                }

                return activeChannels;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        public List<DahuaChannelInfo> QueryChannelNames(int fallbackChannelCount)
        {
            if (LoginId == IntPtr.Zero) return new List<DahuaChannelInfo>();
            return DahuaNetSdk.QueryChannelNamesFromDevice(LoginId, fallbackChannelCount);
        }

        public Task<List<DahuaChannelInfo>> QueryChannelNamesAsync(int fallbackChannelCount, CancellationToken ct = default)
        {
            IntPtr login = LoginId;
            if (login == IntPtr.Zero) return Task.FromResult(new List<DahuaChannelInfo>());
            return Task.Run(() => DahuaNetSdk.QueryChannelNamesFromDevice(login, fallbackChannelCount), ct);
        }

        public static List<DahuaPresetItem> QueryPresets(DahuaDeviceItem device, int channelNumber) =>
            DahuaNetSdk.QueryPresetsFromDevice(
                device.ConnectionAddress, device.HttpPort, device.Username, device.Password, channelNumber);

        public static Task<List<DahuaPresetItem>> QueryPresetsAsync(DahuaDeviceItem device, int channelNumber, CancellationToken ct = default) =>
            Task.Run(() => QueryPresets(device, channelNumber), ct);

        public static void SetPresetPosition(DahuaDeviceItem device, int channelNumber, int presetNumber) =>
            DahuaNetSdk.SetDevicePresetPosition(
                device.ConnectionAddress, device.HttpPort, device.Username, device.Password, channelNumber, presetNumber);

        public static Task SetPresetPositionAsync(DahuaDeviceItem device, int channelNumber, int presetNumber, CancellationToken ct = default) =>
            Task.Run(() => SetPresetPosition(device, channelNumber, presetNumber), ct);

        public static void SetPresetName(DahuaDeviceItem device, int channelNumber, int presetNumber, string presetName) =>
            DahuaNetSdk.SetDevicePresetName(
                device.ConnectionAddress, device.HttpPort, device.Username, device.Password, channelNumber, presetNumber, presetName);

        public static Task SetPresetNameAsync(DahuaDeviceItem device, int channelNumber, int presetNumber, string presetName, CancellationToken ct = default) =>
            Task.Run(() => SetPresetName(device, channelNumber, presetNumber, presetName), ct);

        public static void DeletePreset(DahuaDeviceItem device, int channelNumber, int presetNumber) =>
            DahuaNetSdk.DeleteDevicePreset(
                device.ConnectionAddress, device.HttpPort, device.Username, device.Password, channelNumber, presetNumber);

        public static Task DeletePresetAsync(DahuaDeviceItem device, int channelNumber, int presetNumber, CancellationToken ct = default) =>
            Task.Run(() => DeletePreset(device, channelNumber, presetNumber), ct);

        public bool SendPtz(int channelNumber, DahuaNetSdk.EM_PTZ_COMMAND command, bool stop, out uint lastError)
        {
            lastError = 0;
            if (LoginId == IntPtr.Zero) return false;
            int channelIndex = Math.Max(0, channelNumber - 1);
            bool ok = DahuaNetSdk.CLIENT_DHPTZControlEx2(LoginId, channelIndex, (uint)command, 4, 4, 0, stop, IntPtr.Zero);
            if (!ok) lastError = DahuaNetSdk.CLIENT_GetLastError();
            return ok;
        }

        public bool GotoPreset(int channelNumber, int presetNumber, out uint lastError)
        {
            lastError = 0;
            if (LoginId == IntPtr.Zero) return false;
            int channelIndex = Math.Max(0, channelNumber - 1);
            bool ok = DahuaNetSdk.CLIENT_DHPTZControlEx2(
                LoginId, channelIndex, (uint)DahuaNetSdk.EM_PTZ_COMMAND.POINT_MOVE_CONTROL, 0, presetNumber, 0, false, IntPtr.Zero);
            if (!ok) lastError = DahuaNetSdk.CLIENT_GetLastError();
            return ok;
        }

        public async Task RunPtzPulseAsync(int channelNumber, DahuaNetSdk.EM_PTZ_COMMAND command, int durationMs)
        {
            if (LoginId == IntPtr.Zero) return;
            int channelIndex = Math.Max(0, channelNumber - 1);
            bool started = DahuaNetSdk.CLIENT_DHPTZControlEx2(
                LoginId, channelIndex, (uint)command, 4, 4, 0, false, IntPtr.Zero);
            if (!started)
            {
                uint error = DahuaNetSdk.CLIENT_GetLastError();
                throw new InvalidOperationException(DahuaNetSdk.GetErrorDescription(error));
            }

            try
            {
                await Task.Delay(durationMs).ConfigureAwait(false);
            }
            finally
            {
                DahuaNetSdk.CLIENT_DHPTZControlEx2(
                    LoginId, channelIndex, (uint)command, 4, 4, 0, true, IntPtr.Zero);
            }
        }

        public async Task ApplyAreaZoomAsync(
            int channelNumber, int viewWidth, int viewHeight, int areaLeft, int areaTop, int areaWidth, int areaHeight)
        {
            var plan = ComputeAreaZoomPulses(viewWidth, viewHeight, areaLeft, areaTop, areaWidth, areaHeight);
            if (plan.PanCommand is DahuaNetSdk.EM_PTZ_COMMAND panCmd && plan.PanDurationMs > 0)
                await RunPtzPulseAsync(channelNumber, panCmd, plan.PanDurationMs).ConfigureAwait(false);
            if (plan.TiltCommand is DahuaNetSdk.EM_PTZ_COMMAND tiltCmd && plan.TiltDurationMs > 0)
                await RunPtzPulseAsync(channelNumber, tiltCmd, plan.TiltDurationMs).ConfigureAwait(false);
            if (plan.ZoomDurationMs > 0)
                await RunPtzPulseAsync(channelNumber, DahuaNetSdk.EM_PTZ_COMMAND.ZOOM_IN, plan.ZoomDurationMs).ConfigureAwait(false);
        }

        public IntPtr StartRealPlay(int channelNumber, IntPtr windowHandle, DahuaNetSdk.EM_RealPlayType streamType)
        {
            if (LoginId == IntPtr.Zero || windowHandle == IntPtr.Zero) return IntPtr.Zero;
            int channelIndex = Math.Max(0, channelNumber - 1);
            IntPtr handle = DahuaNetSdk.CLIENT_RealPlayEx(LoginId, channelIndex, windowHandle, streamType);
            if (handle == IntPtr.Zero)
                handle = DahuaNetSdk.CLIENT_RealPlay(LoginId, channelIndex, windowHandle);
            return handle;
        }

        public IntPtr StartLiveStream(int channelNumber, IntPtr windowHandle, DahuaNetSdk.EM_RealPlayType streamType)
        {
            IntPtr handle = StartRealPlay(channelNumber, windowHandle, streamType);
            if (handle != IntPtr.Zero) _liveStreamHandles.Add(handle);
            return handle;
        }

        public void StopLiveStream(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;
            _liveStreamHandles.Remove(handle);
            StopRealPlay(handle);
        }

        public void StopAllLiveStreams()
        {
            foreach (IntPtr handle in _liveStreamHandles.ToArray()) StopRealPlay(handle);
            _liveStreamHandles.Clear();
        }

        public static void StopRealPlay(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;
            try { DahuaNetSdk.CLIENT_StopRealPlayEx(handle); } catch { }
        }

        public IntPtr StartPlayBackByTime(int channelNumber, DateTime start, DateTime end, IntPtr windowHandle)
        {
            if (LoginId == IntPtr.Zero || windowHandle == IntPtr.Zero) return IntPtr.Zero;
            var tmStart = DahuaNetSdk.NET_TIME.FromDateTime(start);
            var tmEnd = DahuaNetSdk.NET_TIME.FromDateTime(end);
            return DahuaNetSdk.CLIENT_PlayBackByTimeEx(
                LoginId, Math.Max(0, channelNumber - 1), ref tmStart, ref tmEnd,
                windowHandle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }

        public IntPtr StartArchivePlayback(int channelNumber, DateTime start, DateTime end, IntPtr windowHandle)
        {
            IntPtr handle = StartPlayBackByTime(channelNumber, start, end, windowHandle);
            if (handle != IntPtr.Zero) _archivePlaybackHandles.Add(handle);
            return handle;
        }

        public void StopArchivePlayback(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;
            _archivePlaybackHandles.Remove(handle);
            StopPlayBack(handle);
        }

        public void StopAllArchivePlaybacks()
        {
            foreach (IntPtr handle in _archivePlaybackHandles.ToArray()) StopPlayBack(handle);
            _archivePlaybackHandles.Clear();
        }

        public IReadOnlyList<IntPtr> GetArchivePlaybackHandles() => _archivePlaybackHandles.ToArray();

        public int SetArchivePlaybackSpeed(double requestedSpeed)
        {
            if (requestedSpeed <= 0 || double.IsNaN(requestedSpeed) || double.IsInfinity(requestedSpeed))
                throw new ArgumentOutOfRangeException(nameof(requestedSpeed));
            IntPtr[] handles = _archivePlaybackHandles.ToArray();
            int succeeded = 0;
            foreach (IntPtr handle in handles)
            {
                bool ok = DahuaNetSdk.CLIENT_NormalPlayBack(handle);
                int steps = requestedSpeed == 1.0 ? 0 : (int)Math.Round(Math.Abs(Math.Log(requestedSpeed, 2)));
                for (int step = 0; ok && step < steps; step++)
                    ok = requestedSpeed > 1.0 ? DahuaNetSdk.CLIENT_FastPlayBack(handle) : DahuaNetSdk.CLIENT_SlowPlayBack(handle);
                if (ok) succeeded++;
            }
            if (succeeded != handles.Length)
                foreach (IntPtr handle in handles) DahuaNetSdk.CLIENT_NormalPlayBack(handle);
            return succeeded;
        }

        public bool SetArchivePlaybackPaused(bool pause)
        {
            IntPtr[] handles = _archivePlaybackHandles.ToArray();
            var succeeded = new List<IntPtr>();
            foreach (IntPtr handle in handles)
                if (DahuaNetSdk.CLIENT_PausePlayBack(handle, pause)) succeeded.Add(handle);
            if (succeeded.Count == handles.Length) return true;
            foreach (IntPtr handle in succeeded) DahuaNetSdk.CLIENT_PausePlayBack(handle, !pause);
            return false;
        }

        public int SeekArchivePlayback(int offsetSeconds)
        {
            int succeeded = 0;
            foreach (IntPtr handle in _archivePlaybackHandles.ToArray())
                if (DahuaNetSdk.CLIENT_SeekPlayBack(handle, offsetSeconds, 0)) succeeded++;
            return succeeded;
        }

        public static void StopPlayBack(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;
            try { DahuaNetSdk.CLIENT_StopPlayBack(handle); } catch { }
        }

        public static void ConfigureRecordModes(IntPtr login)
        {
            if (login == IntPtr.Zero) throw new InvalidOperationException("Brak aktywnego uchwytu logowania Dahua.");
            int streamType = 0;
            DahuaNetSdk.CLIENT_SetDeviceMode(login, 6, ref streamType);
            int recordType = 0;
            DahuaNetSdk.CLIENT_SetDeviceMode(login, 8, ref recordType);
        }

        public void ConfigureCurrentRecordModes() => ConfigureRecordModes(LoginId);

        public static bool CapturePicture(IntPtr playHandle, string destinationPath) =>
            playHandle != IntPtr.Zero &&
            DahuaNetSdk.CLIENT_CapturePictureEx(playHandle, destinationPath, DahuaNetSdk.NET_CAPTURE_FORMATS.JPEG) &&
            File.Exists(destinationPath);

        public IEnumerable<DateTime> GetCachedAvailabilityForChannel(string deviceId, int channel)
        {
            string prefix = $"{deviceId}|{channel}|";
            foreach (var entry in _recorderAvailabilityCache.Where(p => p.Key.StartsWith(prefix, StringComparison.Ordinal)))
            {
                foreach (DateTime day in entry.Value)
                    yield return day;
            }
        }

        public bool TryGetCachedMonthAvailability(string deviceId, int channel, DateTime monthStart, out HashSet<DateTime>? days) =>
            _recorderAvailabilityCache.TryGetValue($"{deviceId}|{channel}|{monthStart:yyyyMM}", out days);

        public bool TryBeginMonthAvailabilityQuery(string deviceId, int channel, DateTime monthStart) =>
            _availabilityQueriesInProgress.Add($"{deviceId}|{channel}|{monthStart:yyyyMM}");

        public void EndMonthAvailabilityQuery(string deviceId, int channel, DateTime monthStart) =>
            _availabilityQueriesInProgress.Remove($"{deviceId}|{channel}|{monthStart:yyyyMM}");

        public async Task<HashSet<DateTime>> QueryMonthAvailabilityAsync(string deviceId, int channel, DateTime monthStart, CancellationToken ct = default)
        {
            IntPtr login = LoginId;
            if (login == IntPtr.Zero)
                throw new InvalidOperationException("Brak aktywnego połączenia z rejestratorem.");

            await _archiveAvailabilitySemaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                HashSet<DateTime> days = await Task.Run(() =>
                {
                    var availableDays = new HashSet<DateTime>();
                    ConfigureRecordModes(login);
                    DateTime monthEnd = monthStart.AddMonths(1);
                    DateTime lastDay = monthEnd < DateTime.Today.AddDays(1) ? monthEnd : DateTime.Today.AddDays(1);
                    int itemSize = Marshal.SizeOf<DahuaNetSdk.NET_RECORDFILE_INFO>();
                    IntPtr buffer = Marshal.AllocHGlobal(itemSize);
                    try
                    {
                        for (DateTime day = monthStart; day < lastDay; day = day.AddDays(1))
                        {
                            ct.ThrowIfCancellationRequested();
                            for (int i = 0; i < itemSize; i++) Marshal.WriteByte(buffer, i, 0);
                            var from = DahuaNetSdk.NET_TIME.FromDateTime(day);
                            var to = DahuaNetSdk.NET_TIME.FromDateTime(day.AddDays(1));
                            bool ok = DahuaNetSdk.CLIENT_QueryRecordFile(
                                login, Math.Max(0, channel - 1), 0, ref from, ref to, null, buffer, itemSize, out int count, 8000, false);
                            if (!ok)
                            {
                                uint error = DahuaNetSdk.CLIENT_GetLastError();
                                throw new InvalidOperationException($"Zapytanie o {day:dd.MM.yyyy} nie powiodło się: {DahuaNetSdk.GetErrorDescription(error)}");
                            }
                            if (count > 0) availableDays.Add(day.Date);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                    return availableDays;
                }, ct).ConfigureAwait(false);

                _recorderAvailabilityCache[$"{deviceId}|{channel}|{monthStart:yyyyMM}"] = days;
                return days;
            }
            finally
            {
                _archiveAvailabilitySemaphore.Release();
            }
        }

        public async Task<List<DahuaRecordItem>> SearchArchiveRecordsAsync(int channel, DateTime start, DateTime end, CancellationToken ct = default)
        {
            IntPtr login = LoginId;
            if (login == IntPtr.Zero)
                throw new InvalidOperationException("Brak aktywnego połączenia z rejestratorem.");

            int channelIndex = Math.Max(0, channel - 1);
            var tmStart = DahuaNetSdk.NET_TIME.FromDateTime(start);
            var tmEnd = DahuaNetSdk.NET_TIME.FromDateTime(end);

            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                const int maxRecords = 200;
                int itemSize = Marshal.SizeOf<DahuaNetSdk.NET_RECORDFILE_INFO>();
                int bufferSize = checked(itemSize * maxRecords);
                IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
                try
                {
                    for (int i = 0; i < bufferSize; i++) Marshal.WriteByte(buffer, i, 0);
                    ConfigureRecordModes(login);
                    bool ok = DahuaNetSdk.CLIENT_QueryRecordFile(
                        login, channelIndex, 0, ref tmStart, ref tmEnd, null, buffer, bufferSize, out int count, 8000, false);
                    if (!ok)
                        throw new InvalidOperationException(DahuaNetSdk.GetErrorDescription(DahuaNetSdk.CLIENT_GetLastError()));

                    var result = new List<DahuaRecordItem>();
                    int safeCount = Math.Clamp(count, 0, maxRecords);
                    for (int i = 0; i < safeCount; i++)
                    {
                        var native = Marshal.PtrToStructure<DahuaNetSdk.NET_RECORDFILE_INFO>(IntPtr.Add(buffer, i * itemSize));
                        DateTime recordStart = new(
                            (int)native.starttime.dwYear, (int)native.starttime.dwMonth, (int)native.starttime.dwDay,
                            (int)native.starttime.dwHour, (int)native.starttime.dwMinute, (int)native.starttime.dwSecond);
                        DateTime recordEnd = new(
                            (int)native.endtime.dwYear, (int)native.endtime.dwMonth, (int)native.endtime.dwDay,
                            (int)native.endtime.dwHour, (int)native.endtime.dwMinute, (int)native.endtime.dwSecond);
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
            }, ct).ConfigureAwait(false);
        }

        public async Task<string> DownloadArchiveAsync(
            DahuaDeviceItem device,
            int channel,
            DateTime start,
            DateTime end,
            string outputFilePath,
            bool convertToMp4,
            string ffmpegPath,
            IProgress<string>? statusProgress,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(device);
            if (string.IsNullOrWhiteSpace(outputFilePath))
                throw new ArgumentException("Wskaż plik docelowy.", nameof(outputFilePath));

            IntPtr activeLoginId = LoginId;
            if (activeLoginId == IntPtr.Zero)
                throw new InvalidOperationException("Brak aktywnego połączenia z rejestratorem.");
            if (convertToMp4 && !File.Exists(ffmpegPath))
                throw new FileNotFoundException("Nie znaleziono ffmpeg.exe.", ffmpegPath);
            if (!ValidateArchiveRange(start, end, out string rangeError))
                throw new ArgumentException(rangeError, nameof(end));

            string finalPath = Path.GetFullPath(outputFilePath);
            string outputDirectory = Path.GetDirectoryName(finalPath)
                ?? throw new InvalidOperationException("Nie można ustalić katalogu docelowego.");
            Directory.CreateDirectory(outputDirectory);

            // NetSDK zapisuje archiwum w natywnym kontenerze DAV. Dla MP4 plik DAV
            // jest wyłącznie plikiem roboczym i zawsze usuwamy go po konwersji.
            string davFilePath = convertToMp4
                ? Path.Combine(Path.GetTempPath(), $"VideoPlayer_Dahua_{Guid.NewGuid():N}.dav")
                : finalPath;

            if (File.Exists(finalPath)) File.Delete(finalPath);
            if (convertToMp4 && File.Exists(davFilePath)) File.Delete(davFilePath);

            string channelName = device.GetChannelName(channel);
            statusProgress?.Report($"Przygotowanie pobierania: {channelName} (CH {channel})…");

            int channelIndex = Math.Max(0, channel - 1);
            var tmStart = DahuaNetSdk.NET_TIME.FromDateTime(start);
            var tmEnd = DahuaNetSdk.NET_TIME.FromDateTime(end);
            bool downloadFinished = false;

            try
            {
                await Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    ConfigureRecordModes(activeLoginId);
                    using var completed = new ManualResetEventSlim(false);
                    DahuaNetSdk.fTimeDownLoadPosCallBack progressCallback = (_, totalSize, downloadedSize, _, _, _) =>
                    {
                        if (downloadedSize == uint.MaxValue)
                        {
                            completed.Set();
                            statusProgress?.Report("Pobieranie z rejestratora zakończone.");
                            return;
                        }

                        if (totalSize > 0)
                        {
                            double percent = Math.Clamp(downloadedSize * 100.0 / totalSize, 0, 100);
                            statusProgress?.Report($"Pobieranie z rejestratora: {percent:0}%");
                            if (downloadedSize >= totalSize)
                                completed.Set();
                        }
                        else
                        {
                            statusProgress?.Report("Pobieranie nagrania z rejestratora…");
                        }
                    };

                    IntPtr downloadHandle = DahuaNetSdk.CLIENT_DownloadByTimeEx(
                        activeLoginId, channelIndex, 0, ref tmStart, ref tmEnd, davFilePath,
                        progressCallback, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    if (downloadHandle == IntPtr.Zero)
                        throw new InvalidOperationException($"CLIENT_DownloadByTimeEx: {DahuaNetSdk.GetErrorDescription(DahuaNetSdk.CLIENT_GetLastError())}");

                    try
                    {
                        bool signaled = completed.Wait(TimeSpan.FromMinutes(30), ct);
                        if (!signaled)
                            throw new TimeoutException("Przekroczono limit czasu pobierania nagrania z rejestratora.");
                        if (File.Exists(davFilePath) && new FileInfo(davFilePath).Length > 0)
                            downloadFinished = true;
                    }
                    finally
                    {
                        DahuaNetSdk.CLIENT_StopDownload(downloadHandle);
                        GC.KeepAlive(progressCallback);
                    }
                }, ct).ConfigureAwait(false);

                ct.ThrowIfCancellationRequested();
                if (!downloadFinished || !File.Exists(davFilePath) || new FileInfo(davFilePath).Length == 0)
                    throw new InvalidOperationException("NetSDK nie zakończył pobierania pliku archiwum.");

                if (convertToMp4)
                {
                    statusProgress?.Report("Konwersja nagrania do MP4…");
                    await ConvertRecordingToMp4Async(ffmpegPath, davFilePath, finalPath, ct).ConfigureAwait(false);
                }

                if (!File.Exists(finalPath) || new FileInfo(finalPath).Length == 0)
                    throw new InvalidOperationException("Pobrany plik jest pusty lub nie został utworzony.");

                statusProgress?.Report($"Zapisano nagranie: {finalPath}");
                return finalPath;
            }
            catch
            {
                try { if (File.Exists(finalPath)) File.Delete(finalPath); } catch { }
                throw;
            }
            finally
            {
                if (convertToMp4)
                {
                    try { if (File.Exists(davFilePath)) File.Delete(davFilePath); } catch { }
                }
            }
        }

        public async Task<string?> DownloadAndConvertToMp4Async(
            DahuaDeviceItem device,
            int channel,
            DateTime start,
            DateTime end,
            string ffmpegPath,
            IProgress<string>? statusProgress,
            CancellationToken ct = default)
        {
            string outputDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VideoPlayer", "nagrania_dahua");
            string safeChannelName = string.Concat(device.GetChannelName(channel)
                .Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)).Replace(' ', '_');
            string fileStem = $"Dahua_{safeChannelName}_CH{channel}_{start:yyyyMMdd_HHmmss}-{end:HHmmss}";
            string targetFilePath = Path.Combine(outputDir, fileStem + ".mp4");

            return await DownloadArchiveAsync(
                device, channel, start, end, targetFilePath,
                convertToMp4: true, ffmpegPath: ffmpegPath, statusProgress: statusProgress, ct: ct).ConfigureAwait(false);
        }

        private static async Task ConvertRecordingToMp4Async(string ffmpegPath, string inputPath, string outputPath, CancellationToken ct)
        {
            try
            {
                await RunFfmpegConversionAsync(ffmpegPath, inputPath, outputPath, transcodeVideo: false, ct).ConfigureAwait(false);
            }
            catch (Exception remuxError) when (remuxError is not OperationCanceledException)
            {
                if (File.Exists(outputPath)) File.Delete(outputPath);
                try
                {
                    await RunFfmpegConversionAsync(ffmpegPath, inputPath, outputPath, transcodeVideo: true, ct).ConfigureAwait(false);
                }
                catch (Exception transcodeError)
                {
                    throw new InvalidOperationException(
                        "Konwersja DAV do MP4 nie powiodła się. Remux: " + remuxError.Message + " | H.264: " + transcodeError.Message,
                        transcodeError);
                }
            }
        }

        private static async Task RunFfmpegConversionAsync(string ffmpegPath, string inputPath, string outputPath, bool transcodeVideo, CancellationToken ct)
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
            else
            {
                psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("copy");
            }
            psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("aac");
            psi.ArgumentList.Add("-b:a"); psi.ArgumentList.Add("128k");
            psi.ArgumentList.Add("-movflags"); psi.ArgumentList.Add("+faststart");
            psi.ArgumentList.Add(outputPath);

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Nie można uruchomić FFmpeg.");
            using var reg = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            string error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                throw new InvalidOperationException(error);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
            if (IsSdkInitialized)
            {
                try { DahuaNetSdk.CLIENT_Cleanup(); } catch { }
                IsSdkInitialized = false;
            }
            _archiveAvailabilitySemaphore.Dispose();
        }
    }
}
