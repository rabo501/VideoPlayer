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
            string pchDVRIP,
            ushort wDVRPort,
            string pchUserName,
            string pchPassword,
            out NET_DEVICEINFO lpDeviceInfo,
            out int error);

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
        /// Odczytuje listę nazw kanałów bezpośrednio z rejestratora lub kamery Dahua przez NetSDK.
        /// </summary>
        public static List<DahuaChannelInfo> QueryChannelNamesFromDevice(IntPtr lLoginID, int fallbackChannelCount = 16)
        {
            var list = new List<DahuaChannelInfo>();
            if (lLoginID == IntPtr.Zero) return list;

            try
            {
                // 1. CLIENT_QueryChannelName: bufor dla maks. 64 kanałów po 32 bajty
                const int maxChannels = 64;
                const int nameLen = 32;
                byte[] buffer = new byte[maxChannels * nameLen];
                int channelCount = 0;

                bool success = CLIENT_QueryChannelName(lLoginID, buffer, buffer.Length, out channelCount, 2500);
                if (success && channelCount > 0)
                {
                    for (int i = 0; i < channelCount; i++)
                    {
                        int offset = i * nameLen;
                        int len = 0;
                        while (len < nameLen && buffer[offset + len] != 0) len++;

                        string rawName = System.Text.Encoding.Default.GetString(buffer, offset, len).Trim();
                        string name = string.IsNullOrWhiteSpace(rawName) ? $"Kamera {i + 1}" : rawName;
                        list.Add(new DahuaChannelInfo { Number = i + 1, Name = name });
                    }
                    if (list.Count > 0) return list;
                }
            }
            catch { }

            try
            {
                // 2. CLIENT_GetNewDevConfig("ChannelTitle")
                byte[] cfgBuf = new byte[16384];
                int err = 0;
                if (CLIENT_GetNewDevConfig(lLoginID, "ChannelTitle", -1, cfgBuf, cfgBuf.Length, out err, 2000))
                {
                    string json = System.Text.Encoding.UTF8.GetString(cfgBuf).TrimEnd('\0');
                    if (!string.IsNullOrEmpty(json) && json.Contains("Name"))
                    {
                        // Proste wyciąganie nazw kanałów z JSON
                        var matches = System.Text.RegularExpressions.Regex.Matches(json, @"""Name""\s*:\s*""([^""]+)""");
                        if (matches.Count > 0)
                        {
                            for (int i = 0; i < matches.Count; i++)
                            {
                                list.Add(new DahuaChannelInfo { Number = i + 1, Name = matches[i].Groups[1].Value.Trim() });
                            }
                            return list;
                        }
                    }
                }
            }
            catch { }

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
                bool inRoot = File.Exists(Path.Combine(baseDir, dll));
                bool inX64 = File.Exists(Path.Combine(baseDir, "x64", dll));
                bool inDahua = File.Exists(Path.Combine(baseDir, "dahua", dll));
                if (!inRoot && !inX64 && !inDahua)
                {
                    missing.Add(dll);
                }
            }
            return missing;
        }

        public static bool IsSdkAvailable()
        {
            string baseDir = AppContext.BaseDirectory;
            return File.Exists(Path.Combine(baseDir, NetSdkDll)) ||
                   File.Exists(Path.Combine(baseDir, "x64", NetSdkDll)) ||
                   File.Exists(Path.Combine(baseDir, "dahua", NetSdkDll));
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
                _ => $"Kod błędu Dahua: {errorCode} (0x{errorCode:X8})"
            };
        }
    }
}
