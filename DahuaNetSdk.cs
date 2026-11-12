using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Linq;

namespace VideoPlayer
{
    /// <summary>
    /// Wrapper P/Invoke dla biblioteki Dahua NetSDK for Windows (dhnetsdk.dll / dhplay.dll).
    /// Obsługuje logowanie do kamer i rejestratorów Dahua IP, podgląd na żywo (RealPlay),
    /// szybkie przełączanie kanałów, sterowanie PTZ oraz pobieranie/odtwarzanie nagrań wg czasu.
    /// </summary>
    public static class DahuaNetSdk
    {
        private const string NetSdkDll = "dhnetsdk.dll";
        private const string PlaySdkDll = "dhplay.dll";

        static DahuaNetSdk()
        {
            // SDK DLL files are deployed in the application's lib folder.
            // The resolver uses the same location checked by CheckDependencies.
            System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(
                typeof(DahuaNetSdk).Assembly, ResolveDahuaLibrary);
        }

        private static IntPtr ResolveDahuaLibrary(
            string libraryName,
            System.Reflection.Assembly assembly,
            DllImportSearchPath? searchPath)
        {
            string fileName = Path.GetFileName(libraryName);
            if (!string.Equals(fileName, NetSdkDll, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(fileName, PlaySdkDll, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(fileName, "avnetsdk.dll", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(fileName, "dhconfigsdk.dll", StringComparison.OrdinalIgnoreCase))
                return IntPtr.Zero;

            string baseDir = AppContext.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(baseDir, "lib", fileName)
            };

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                    return System.Runtime.InteropServices.NativeLibrary.Load(candidate);
            }
            return IntPtr.Zero;
        }

        #region Enums & Structs

        public enum EM_LOGIN_SPAC_CAP_TYPE
        {
            TCP = 0,
            SSL = 1,
            P2P = 2
        }

        public enum EM_RealPlayType
        {
            Realplay = 0,       // Strumień główny (Main stream)
            Realplay_1 = 1,     // Podstrumień 1 (Sub stream 1)
            Realplay_2 = 2,     // Podstrumień 2 (Sub stream 2)
            Realplay_3 = 3,     // Podstrumień 3 (Sub stream 3)
            Multiplay = 4       // Podgląd wielu kanałów
        }

        public enum EM_PTZ_COMMAND : uint
        {
            UP = 0,
            DOWN = 1,
            LEFT = 2,
            RIGHT = 3,
            ZOOM_IN = 4,
            ZOOM_OUT = 5,
            FOCUS_IN = 6,
            FOCUS_OUT = 7,
            APERTURE_IN = 8,
            APERTURE_OUT = 9,
            POINT_MOVE_CONTROL = 10, // Idź do zapisanego presetu (Goto preset)
            POINT_SET_CONTROL = 11,  // Zapisz pozycję jako preset (Set preset)
            POINT_DEL_CONTROL = 12   // Usuń preset (Delete preset)
        }

        public enum NET_CAPTURE_FORMATS
        {
            BMP = 0,
            JPEG = 1
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NET_TIME
        {
            public uint dwYear;
            public uint dwMonth;
            public uint dwDay;
            public uint dwHour;
            public uint dwMinute;
            public uint dwSecond;

            public static NET_TIME FromDateTime(DateTime dt)
            {
                return new NET_TIME
                {
                    dwYear = (uint)dt.Year,
                    dwMonth = (uint)dt.Month,
                    dwDay = (uint)dt.Day,
                    dwHour = (uint)dt.Hour,
                    dwMinute = (uint)dt.Minute,
                    dwSecond = (uint)dt.Second
                };
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate void fDisConnect(IntPtr lLoginID, string pchDVRIP, int nDVRPort, IntPtr dwUser);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate void fHaveReConnect(IntPtr lLoginID, string pchDVRIP, int nDVRPort, IntPtr dwUser);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate void fTimeDownLoadPosCallBack(
            IntPtr lPlayHandle,
            uint dwTotalSize,
            uint dwDownLoadSize,
            int index,
            IntPtr recordfileinfo,
            IntPtr dwUser);

        // CLIENT_Login (legacy API) expects NET_DEVICEINFO, not the extended structure.
        // This exact native layout avoids overwriting managed memory during login.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct NET_DEVICEINFO
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)]
            public byte[] sSerialNumber;
            public byte byAlarmInPortNum;
            public byte byAlarmOutPortNum;
            public byte byDiskNum;
            public byte byDVRType;
            public byte byChanNum;

            public static NET_DEVICEINFO Create()
            {
                return new NET_DEVICEINFO
                {
                    sSerialNumber = new byte[48]
                };
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
        public struct NET_RECORDFILE_INFO
        {
            public uint ch;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 124)]
            public byte[] filename;
            public uint framenum;
            public uint size;
            public NET_TIME starttime;
            public NET_TIME endtime;
            public uint driveno;
            public uint startcluster;
            public byte nRecordFileType;
            public byte bImportantRecID;
            public byte bHint;
            public byte bRecType;

            public string FileName => filename == null
                ? string.Empty
                : System.Text.Encoding.Default.GetString(filename).TrimEnd('\0');
        }

        #endregion

        #region P/Invoke Native Functions

        [DllImport(NetSdkDll, CallingConvention = CallingConvention.StdCall)]

        public static extern bool CLIENT_Init(fDisConnect cbDisConnect, IntPtr dwUser);

        [DllImport(NetSdkDll, CallingConvention = CallingConvention.StdCall)]
        public static extern void CLIENT_Cleanup();

        [DllImport(NetSdkDll, CallingConvention = CallingConvention.StdCall)]
        public static extern void CLIENT_SetAutoReconnect(fHaveReConnect cbAutoConnect, IntPtr dwUser);

        [DllImport(NetSdkDll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        public static extern IntPtr CLIENT_Login(
            [MarshalAs(UnmanagedType.LPStr)] string pchDVRIP,
            ushort wDVRPort,
            [MarshalAs(UnmanagedType.LPStr)] string pchUserName,
            [MarshalAs(UnmanagedType.LPStr)] string pchPassword,
            ref NET_DEVICEINFO lpDeviceInfo,
            ref int error);

        [DllImport(NetSdkDll, CallingConvention = CallingConvention.StdCall)]

        public static extern bool CLIENT_Logout(IntPtr lLoginID);

        /// <summary>
        /// Odtwarzanie strumienia RealPlay (nowoczesna wersja)
        /// </summary>
        [DllImport(NetSdkDll, CallingConvention = CallingConvention.StdCall)]
        public static extern IntPtr CLIENT_RealPlayEx(
            IntPtr lLoginID,
            int nChannelID,
            IntPtr hWnd,
            EM_RealPlayType rType);

        /// <summary>
        /// Klasyczna funkcja RealPlay
        /// </summary>
        [DllImport(NetSdkDll, CallingConvention = CallingConvention.StdCall)]
        public static extern IntPtr CLIENT_RealPlay(
            IntPtr lLoginID,
            int nChannelID,
            IntPtr hWnd);

        [DllImport(NetSdkDll, CallingConvention = CallingConvention.StdCall)]

        public static extern bool CLIENT_StopRealPlayEx(IntPtr lRealHandle);

        [DllImport(NetSdkDll, CallingConvention = CallingConvention.StdCall)]

        public static extern bool CLIENT_DHPTZControlEx2(
            IntPtr lLoginID,
            int nChannelID,
            uint dwPTZCommand,
            int lParam1,
            int lParam2,
            int lParam3,
            bool bStop,
            IntPtr param4);

        [DllImport(NetSdkDll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]

        public static extern bool CLIENT_CapturePictureEx(
            IntPtr hPlayHandle,
            string pchPicFileName,
            NET_CAPTURE_FORMATS format);

        /// <summary>
        /// Odczytuje nazwy kanałów bezpośrednio z urządzenia (kamery lub NVR).
        /// </summary>
        [DllImport(NetSdkDll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]

        public static extern bool CLIENT_QueryChannelName(
            IntPtr lLoginID,
            byte[] pChannelName,
            int maxlen,
            out int nChannelCount,
            int waittime = 2000);

        /// <summary>
        /// Pobiera konfigurację z urządzenia w formacie JSON lub binarnym (np. Ptz, ChannelTitle).
        /// </summary>
        [DllImport(NetSdkDll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]

        public static extern bool CLIENT_GetNewDevConfig(
            IntPtr lLoginID,
            string szCommand,
            int nChannelID,
            byte[] szOutBuffer,
            int dwOutBufLen,
            out int error,
            int waittime = 2000);

        /// <summary>
        /// Zapytanie o stan urządzenia (np. pozycje PTZ, status dysków).
        /// </summary>
        [DllImport(NetSdkDll, CallingConvention = CallingConvention.StdCall)]

        public static extern bool CLIENT_QueryDevState(
            IntPtr lLoginID,
            int nType,
            IntPtr pBuf,
            int nBufLen,
            out int pRetLen,
            int waittime = 2000);

        // EM_USEDEV_MODE.DH_RECORD_STREAM_TYPE = 6 in the Dahua NetSDK enum.
        // pValue is an int* (0=main+sub stream, 1=main, 2=sub).
        [DllImport(NetSdkDll, CallingConvention = CallingConvention.StdCall)]
        public static extern bool CLIENT_SetDeviceMode(
            IntPtr lLoginID,
            int emType,
            ref int pValue);

        [DllImport(NetSdkDll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        public static extern bool CLIENT_QueryRecordFile(
            IntPtr lLoginID,
            int nChannelId,
            int nRecordFileType,
            ref NET_TIME tmStart,
            ref NET_TIME tmEnd,
            string? pchCardid,
            IntPtr recordInfoBuffer,
            int maxLength,
            out int fileCount,
            int waitTime,
            bool bTime);

        /// <summary>
        /// Pobieranie nagrania z rejestratora wg czasu do pliku lokalnego
        /// </summary>
        [DllImport(NetSdkDll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        public static extern IntPtr CLIENT_DownloadByTimeEx(
            IntPtr lLoginID,
            int nChannelId,
            int nRecordFileType,
            ref NET_TIME tmStart,
            ref NET_TIME tmEnd,
            string sSavedFileName,
            fTimeDownLoadPosCallBack? cbTimeDownLoadPos,
            IntPtr dwUserData,
            IntPtr fDownLoadDataCallBack,
            IntPtr dwDataUser,
            IntPtr reserved);

        [DllImport(NetSdkDll, CallingConvention = CallingConvention.StdCall)]

        public static extern bool CLIENT_StopDownload(IntPtr lFileHandle);

        /// <summary>
        /// Odtwarzanie nagrania z rejestratora wg czasu bezpośrednio na uchwycie okna
        /// </summary>
        [DllImport(NetSdkDll, CallingConvention = CallingConvention.StdCall)]
        public static extern IntPtr CLIENT_PlayBackByTimeEx(
            IntPtr lLoginID,
            int nChannelID,
            ref NET_TIME lpStartTime,
            ref NET_TIME lpStopTime,
            IntPtr hWnd,
            IntPtr cbDownLoadPos,
            IntPtr dwPosUser,
            IntPtr fDownLoadDataCallBack,
            IntPtr dwDataUser);

        [DllImport(NetSdkDll, CallingConvention = CallingConvention.StdCall)]

        public static extern bool CLIENT_StopPlayBack(IntPtr lPlayHandle);

        [DllImport(NetSdkDll, CallingConvention = CallingConvention.StdCall)]
        public static extern uint CLIENT_GetLastError();

        #endregion

        #region Helper Methods for Querying Channels & Presets

        /// <summary>
        /// Dekoduje tekst zwrócony przez urządzenie. Najpierw sprawdza UTF-8,
        /// a dla starszych urządzeń używa Windows-1250 (Europa Środkowa).
        /// </summary>
        public static string DecodeDeviceText(byte[] buffer, int offset, int length)
        {
            if (buffer == null || length <= 0 || offset < 0 || offset + length > buffer.Length)
                return string.Empty;

            byte[] bytes = new byte[length];
            Buffer.BlockCopy(buffer, offset, bytes, 0, length);

            try
            {
                return new System.Text.UTF8Encoding(false, true).GetString(bytes);
            }
            catch (System.Text.DecoderFallbackException)
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                return System.Text.Encoding.GetEncoding(1250).GetString(bytes);
            }
        }

        /// <summary>
        /// Odczytuje listę nazw kanałów bezpośrednio z rejestratora lub kamery Dahua przez NetSDK.
        /// </summary>
        public static List<DahuaChannelInfo> QueryChannelNamesFromDevice(IntPtr lLoginID, int fallbackChannelCount = 16)
        {
            var list = new List<DahuaChannelInfo>();
            if (lLoginID == IntPtr.Zero) return list;

            // CLIENT_QueryChannelName returns a fixed 32-byte slot per channel.
            // Allocate the complete output area (channelCount * 32), not one string.
            // Do not use CLIENT_GetNewDevConfig("ChannelTitle"): this call terminated
            // the process on the user's recorder/SDK combination.
            try
            {
                int requestedCount = Math.Max(1, Math.Min(fallbackChannelCount, 256));
                const int nameSlotSize = 32;
                byte[] nameBuffer = new byte[requestedCount * nameSlotSize];
                int returnedCount = 0;

                bool success = CLIENT_QueryChannelName(
                    lLoginID, nameBuffer, nameBuffer.Length, out returnedCount, 3000);

                if (!success)
                    return list;

                int count = Math.Min(
                    Math.Min(returnedCount, requestedCount),
                    nameBuffer.Length / nameSlotSize);

                for (int i = 0; i < count; i++)
                {
                    int offset = i * nameSlotSize;
                    int length = 0;
                    while (length < nameSlotSize && nameBuffer[offset + length] != 0)
                        length++;

                    if (length == 0)
                        continue;

                    string name = DecodeDeviceText(nameBuffer, offset, length).Trim();

                    if (!string.IsNullOrWhiteSpace(name))
                        list.Add(new DahuaChannelInfo { Number = i + 1, Name = name });
                }
            }
            catch
            {
                // If the SDK rejects this operation, return an empty list.
                // The caller preserves the local operator configuration.
            }

            return list;
        }

        /// <summary>
        /// Odczytuje zapisane pozycje PTZ (presety) bezpośrednio z kamery Dahua.
        /// </summary>
        public static List<DahuaPresetItem> QueryPresetsFromDevice(IntPtr lLoginID, int channelIndex = 0)
        {
            var presets = new List<DahuaPresetItem>();
            if (lLoginID == IntPtr.Zero) return presets;

            try
            {
                // W kamerach Dahua presety pobiera się konfiguracją Ptz
                byte[] outBuf = new byte[32768];
                int err = 0;
                bool ok = CLIENT_GetNewDevConfig(lLoginID, "Ptz", channelIndex, outBuf, outBuf.Length, out err, 2000);
                if (ok)
                {
                    string json = System.Text.Encoding.UTF8.GetString(outBuf).TrimEnd('\0');
                    if (!string.IsNullOrEmpty(json))
                    {
                        // Szukamy wpisów presetów: Index / Name
                        var presetRegex = new System.Text.RegularExpressions.Regex(
                            @"\{[^{}]*""Index""\s*:\s*(\d+)[^{}]*""Name""\s*:\s*""([^""]+)""[^{}]*\}",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                        var matches = presetRegex.Matches(json);
                        foreach (System.Text.RegularExpressions.Match m in matches)
                        {
                            if (int.TryParse(m.Groups[1].Value, out int idx))
                            {
                                string name = m.Groups[2].Value.Trim();
                                if (!presets.Any(p => p.PresetNumber == idx))
                                {
                                    presets.Add(new DahuaPresetItem { PresetNumber = idx, Name = name });
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            return presets;
        }


        /// <summary>
        /// Odczytuje presety przez udokumentowane Dahua HTTP API CGI.
        /// Kanał w tym API jest numerowany od 1.
        /// </summary>
        public static List<DahuaPresetItem> QueryPresetsFromDevice(
            string host, ushort httpPort, string username, string password, int channelNumber)
        {
            var presets = new List<DahuaPresetItem>();
            if (string.IsNullOrWhiteSpace(host) || channelNumber < 1)
                return presets;

            string response = SendDahuaCgiRequest(
                host, httpPort, username, password,
                $"ptz.cgi?action=getPresets&channel={channelNumber}");

            var indexMatches = System.Text.RegularExpressions.Regex.Matches(
                response, @"presets\[(\d+)\]\.Index\s*=\s*(\d+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            foreach (System.Text.RegularExpressions.Match match in indexMatches)
            {
                if (!int.TryParse(match.Groups[1].Value, out int arrayIndex) ||
                    !int.TryParse(match.Groups[2].Value, out int presetNumber))
                    continue;

                string name = $"Preset {presetNumber}";
                var nameMatch = System.Text.RegularExpressions.Regex.Match(
                    response,
                    $@"presets\[{arrayIndex}\]\.Name\s*=\s*([^\r\n]+)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (nameMatch.Success)
                    name = nameMatch.Groups[1].Value.Trim().Trim('"', '\'');

                if (!presets.Any(p => p.PresetNumber == presetNumber))
                    presets.Add(new DahuaPresetItem { PresetNumber = presetNumber, Name = name });
            }

            // Do not silently treat an unknown response format as an empty preset list.
            if (presets.Count == 0 &&
                !response.Contains("presets[", StringComparison.OrdinalIgnoreCase) &&
                !response.Contains("preset", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Urządzenie zwróciło nieznany format listy presetów PTZ. Odpowiedź: " + response);
            }

            return presets.OrderBy(p => p.PresetNumber).ToList();
        }

        /// <summary>
        /// Ustawia nazwę istniejącego presetu na urządzeniu.
        /// </summary>
        public static void SetDevicePresetName(
            string host, ushort httpPort, string username, string password,
            int channelNumber, int presetNumber, string presetName)
        {
            if (presetNumber < 1 || string.IsNullOrWhiteSpace(presetName))
                throw new ArgumentException("Nieprawidłowy numer lub nazwa presetu.");

            string query = "ptz.cgi?action=setPreset" +
                $"&channel={channelNumber}&arg1={presetNumber}&arg2={Uri.EscapeDataString(presetName)}";
            string response = SendDahuaCgiRequest(host, httpPort, username, password, query);
            EnsureCgiSuccess(response, "zmiana nazwy presetu");
        }

        /// <summary>
        /// Zapisuje bieżącą pozycję PTZ jako preset.
        /// </summary>
        public static void SetDevicePresetPosition(
            string host, ushort httpPort, string username, string password,
            int channelNumber, int presetNumber)
        {
            string query = $"ptz.cgi?action=start&channel={channelNumber}&code=SetPreset&arg1=0&arg2={presetNumber}&arg3=0";
            string response = SendDahuaCgiRequest(host, httpPort, username, password, query);
            EnsureCgiSuccess(response, "zapis pozycji PTZ");
        }

        /// <summary>
        /// Usuwa preset z urządzenia.
        /// </summary>
        public static void DeleteDevicePreset(
            string host, ushort httpPort, string username, string password,
            int channelNumber, int presetNumber)
        {
            string query = $"ptz.cgi?action=start&channel={channelNumber}&code=ClearPreset&arg1=0&arg2={presetNumber}&arg3=0";
            string response = SendDahuaCgiRequest(host, httpPort, username, password, query);
            EnsureCgiSuccess(response, "usunięcie presetu");
        }

        private static string SendDahuaCgiRequest(
            string host, ushort httpPort, string username, string password, string query)
        {
            var handler = new System.Net.Http.HttpClientHandler
            {
                Credentials = new System.Net.NetworkCredential(username ?? string.Empty, password ?? string.Empty),
                PreAuthenticate = true,
                UseDefaultCredentials = false
            };
            using var client = new System.Net.Http.HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(8)
            };
            string url = $"http://{host}:{httpPort}/cgi-bin/{query}";
            using var response = client.GetAsync(url).GetAwaiter().GetResult();
            byte[] responseBytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            string body = DecodeDeviceText(responseBytes, 0, responseBytes.Length);
            if (string.IsNullOrWhiteSpace(body) ||
                body.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                body.Trim().Equals("ERROR", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Dahua CGI zwróciło błąd: {body}");
            return body;
        }

        private static void EnsureCgiSuccess(string response, string operation)
        {
            if (!response.Trim().Equals("OK", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Dahua CGI: niepowodzenie operacji ({operation}). Odpowiedź: {response}");
        }
        #endregion

        /// <summary>
        /// Sprawdza dostępność kluczowych bibliotek DLL.
        /// </summary>
        public static List<string> CheckDependencies()
        {
            var missing = new List<string>();
            string baseDir = AppContext.BaseDirectory;

            string[] required = { "dhnetsdk.dll", "dhplay.dll", "avnetsdk.dll" };
            foreach (var dll in required)
            {
                bool inLib = File.Exists(Path.Combine(baseDir, "lib", dll));
                if (!inLib)
                {
                    missing.Add(dll);
                }
            }
            return missing;
        }

        public static bool IsSdkAvailable()
        {
            string baseDir = AppContext.BaseDirectory;
            return File.Exists(Path.Combine(baseDir, "lib", NetSdkDll));
        }

        public static string GetErrorDescription(uint errorCode)
        {
            return errorCode switch
            {
                2147483659 or 0x8000000B => "Błąd 2147483659 (0x8000000B): NET_RENDER_OPEN_ERROR — błąd inicjalizacji renderera/dekodera. Sprawdź obecność dhplay.dll x64 obok dhnetsdk.dll.",
                2147483652 or 0x80000004 => "Błąd 2147483652 (0x80000004): NET_ERROR_INVALID_HANDLE — nieprawidłowy uchwyt połączenia.",
                2147483649 or 0x80000001 => "Błąd 2147483649 (0x80000001): NET_ERROR_ILLEGAL_PARAM — nieprawidłowy parametr żądania.",
                2147483673 or 0x80000019 => "Błąd 2147483673 (0x80000019): NET_ERROR_NO_SUCH_CHANNEL — podany kanał nie istnieje na rejestratorze.",
                2147483672 or 0x80000018 => "Błąd 2147483672 (0x80000018): NET_ERROR_NETWORK_ERROR — błąd sieci lub timeout.",
                2147483679 or 0x8000001F => "Błąd 2147483679 (0x8000001F): Błędna nazwa użytkownika lub hasło.",
                2147483727 or 0x8000004F => "Błąd 2147483727 (0x8000004F): NET_UNSUPPORTED — rejestrator lub użyta wersja NetSDK nie obsługuje tej operacji. Sprawdź tryb strumienia nagrań, typ nagrania i obsługę funkcji przez firmware urządzenia.",
                _ => $"Kod błędu Dahua: {errorCode} (0x{errorCode:X8})"
            };
        }
    }
}
