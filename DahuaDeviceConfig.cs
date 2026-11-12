using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace VideoPlayer
{
    public sealed class DahuaRecordItem
    {
        public int Channel { get; set; }
        public string FileName { get; set; } = string.Empty;
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public uint SizeKb { get; set; }
        public int RecordType { get; set; }
        public string DurationText => (EndTime - StartTime).ToString(@"hh\:mm\:ss");
        public string SizeText => SizeKb >= 1024
            ? $"{SizeKb / 1024.0:N1} MB"
            : $"{SizeKb:N0} KB";
        public override string ToString() => $"{StartTime:yyyy-MM-dd HH:mm:ss} — {EndTime:HH:mm:ss}";
    }

    public class DahuaPresetItem
    {
        public int PresetNumber { get; set; } = 1;
        public string Name { get; set; } = "Preset 1";

        public override string ToString() => $"[P{PresetNumber}] {Name}";
    }

    public class DahuaChannelInfo
    {
        public int Number { get; set; } = 1;
        public string Name { get; set; } = "";

        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"Kanał {Number} (CH {Number})" : $"{Name} (CH {Number})";
        public override string ToString() => DisplayName;
    }

    public class DahuaDeviceItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Kamera Dahua IP";
        public string IpAddress { get; set; } = "";
        public ushort Port { get; set; } = 37777;
        public string Username { get; set; } = "admin";
        public string Password { get; set; } = "";
        public int Channel { get; set; } = 1;
        public DahuaNetSdk.EM_RealPlayType StreamType { get; set; } = DahuaNetSdk.EM_RealPlayType.Realplay;

        public List<DahuaChannelInfo> Channels { get; set; } = new();
        public List<DahuaPresetItem> Presets { get; set; } = new();

        // Name is a presentation-only label. All network operations must use ConnectionAddress.
        [System.Text.Json.Serialization.JsonIgnore]
        public string ConnectionAddress => (IpAddress ?? string.Empty).Trim();

        public string DisplayLabel => string.IsNullOrWhiteSpace(IpAddress)
            ? "Brak adresu połączenia"
            : $"{IpAddress}:{Port}";
        public override string ToString() => DisplayLabel;

        public void UpdateFromDeviceChannels(List<DahuaChannelInfo> deviceChannels)
        {
            if (deviceChannels == null || deviceChannels.Count == 0) return;
            foreach (var dCh in deviceChannels)
            {
                if (!string.IsNullOrWhiteSpace(dCh.Name))
                {
                    SetChannelName(dCh.Number, dCh.Name);
                }
            }
        }

        public void UpdateFromDevicePresets(List<DahuaPresetItem> devicePresets)
        {
            if (devicePresets == null || devicePresets.Count == 0) return;
            foreach (var p in devicePresets)
            {
                var existing = Presets.FirstOrDefault(x => x.PresetNumber == p.PresetNumber);
                if (existing != null)
                {
                    existing.Name = p.Name;
                }
                else
                {
                    Presets.Add(new DahuaPresetItem { PresetNumber = p.PresetNumber, Name = p.Name });
                }
            }
            Presets = Presets.OrderBy(x => x.PresetNumber).ToList();
        }

        public string GetChannelName(int channelNumber)
        {
            var match = Channels.FirstOrDefault(c => c.Number == channelNumber);
            return match != null && !string.IsNullOrWhiteSpace(match.Name) ? match.Name : $"Kanał {channelNumber}";
        }

        public void SetChannelName(int channelNumber, string newName)
        {
            var match = Channels.FirstOrDefault(c => c.Number == channelNumber);
            if (match != null)
            {
                match.Name = newName.Trim();
            }
            else
            {
                Channels.Add(new DahuaChannelInfo { Number = channelNumber, Name = newName.Trim() });
            }
        }

        public void EnsureDefaultChannels(int maxCount = 16)
        {
            for (int i = 1; i <= maxCount; i++)
            {
                if (!Channels.Any(c => c.Number == i))
                {
                    string name = $"Kanał {i}";
                    Channels.Add(new DahuaChannelInfo { Number = i, Name = name });
                }
            }
        }

        public void EnsureDefaultPresets()
        {
            // Do not invent PTZ presets: the local list must contain only presets
            // read from the device or explicitly created by the operator.
        }
    }

    public static class DahuaConfigService
    {
        private static readonly string ConfigDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoPlayer");
        private static readonly string ConfigPath = Path.Combine(ConfigDirectory, "dahua_devices.json");
        private static readonly string LegacyConfigPath = Path.Combine(AppContext.BaseDirectory, "dahua_devices.json");

        private static void MigrateLegacyConfigIfNeeded()
        {
            Directory.CreateDirectory(ConfigDirectory);
            if (!File.Exists(ConfigPath) && File.Exists(LegacyConfigPath))
            {
                // Copy, never move: the original portable configuration remains as backup.
                File.Copy(LegacyConfigPath, ConfigPath, overwrite: false);
            }
        }

        private static bool TryExtractLegacyEndpoint(string value, out string host, out ushort port)
        {
            host = string.Empty;
            port = 0;
            if (string.IsNullOrWhiteSpace(value)) return false;

            string candidate = value.Trim();
            int openParen = candidate.LastIndexOf('(');
            int closeParen = candidate.LastIndexOf(')');
            if (openParen >= 0 && closeParen > openParen)
                candidate = candidate.Substring(openParen + 1, closeParen - openParen - 1).Trim();

            if (Uri.TryCreate("http://" + candidate, UriKind.Absolute, out Uri? parsed) &&
                !string.IsNullOrWhiteSpace(parsed.Host) &&
                Uri.CheckHostName(parsed.Host) != UriHostNameType.Unknown)
            {
                host = parsed.Host;
                if (!parsed.IsDefaultPort && parsed.Port > 0 && parsed.Port <= ushort.MaxValue)
                    port = (ushort)parsed.Port;
                return true;
            }

            string plainHost = candidate.Trim('[', ']');
            if (Uri.CheckHostName(plainHost) != UriHostNameType.Unknown)
            {
                host = plainHost;
                return true;
            }
            return false;
        }

        public static List<DahuaDeviceItem> LoadDevices()
        {
            MigrateLegacyConfigIfNeeded();
            if (File.Exists(ConfigPath))
            {
                try
                {
                    string json = File.ReadAllText(ConfigPath);
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var list = JsonSerializer.Deserialize<List<DahuaDeviceItem>>(json, options) ?? new List<DahuaDeviceItem>();

                    // Compatibility with older files where the IP address was stored
                    // in Name (the UI used to display the address as the device label),
                    // or where the endpoint property had a different name.
                    using var document = JsonDocument.Parse(json);
                    if (document.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        int index = 0;
                        foreach (var element in document.RootElement.EnumerateArray())
                        {
                            if (index >= list.Count) break;
                            var device = list[index++];
                            if (element.ValueKind != JsonValueKind.Object) continue;

                            string? ReadString(params string[] names)
                            {
                                foreach (var property in element.EnumerateObject())
                                {
                                    if (names.Any(n => string.Equals(n, property.Name, StringComparison.OrdinalIgnoreCase)) &&
                                        property.Value.ValueKind == JsonValueKind.String)
                                        return property.Value.GetString();
                                }
                                return null;
                            }

                            if (string.IsNullOrWhiteSpace(device.IpAddress))
                                device.IpAddress = ReadString("IpAddress", "IPAddress", "IP", "Ip", "Address", "Host", "HostAddress", "ServerIp", "DeviceIP", "DeviceAddress") ?? string.Empty;

                            if (string.IsNullOrWhiteSpace(device.Name))
                                device.Name = ReadString("Name", "DisplayName", "Title") ?? string.Empty;

                            // Legacy schema: older versions stored the endpoint in Name
                            // or in a combined display label such as "NVR (192.168.1.20:37777)".
                            if (string.IsNullOrWhiteSpace(device.IpAddress) &&
                                TryExtractLegacyEndpoint(device.Name, out string legacyHost, out ushort legacyPort))
                            {
                                device.IpAddress = legacyHost;
                                if (legacyPort > 0 && device.Port == 37777)
                                    device.Port = legacyPort;
                                device.Name = $"Urządzenie {device.IpAddress}";
                            }

                            if (string.IsNullOrWhiteSpace(device.Name))
                                device.Name = string.IsNullOrWhiteSpace(device.IpAddress)
                                    ? "Urządzenie Dahua"
                                    : $"Urządzenie {device.IpAddress}";

                            device.Channels ??= new List<DahuaChannelInfo>();
                            device.Presets ??= new List<DahuaPresetItem>();
                            device.EnsureDefaultChannels(16);
                            device.EnsureDefaultPresets();
                        }
                    }

                    return list;
                }
                catch (Exception ex)
                {
                    // Do not silently replace a damaged/old configuration with demo devices.
                    // Keep the source file for recovery and let the operator see the problem.
                    throw new InvalidDataException(
                        $"Nie można odczytać konfiguracji urządzeń Dahua: {ConfigPath}. " +
                        "Plik nie został nadpisany. Szczegóły: " + ex.Message, ex);
                }
            }

            // First launch only: create demo entries when no configuration exists.
            var nvr = new DahuaDeviceItem
            {
                Name = "Rejestrator NVR Dahua (8 kanałów)",
                IpAddress = "192.168.1.200",
                Port = 37777,
                Username = "admin",
                Channel = 1,
                StreamType = DahuaNetSdk.EM_RealPlayType.Realplay
            };
            nvr.EnsureDefaultChannels(16);

            var cam = new DahuaDeviceItem
            {
                Name = "Kamera Obrotowa PTZ - Brama",
                IpAddress = "192.168.1.108",
                Port = 37777,
                Username = "admin",
                Channel = 1,
                StreamType = DahuaNetSdk.EM_RealPlayType.Realplay
            };
            cam.EnsureDefaultChannels(4);

            var defaults = new List<DahuaDeviceItem> { nvr, cam };
            SaveDevices(defaults);
            return defaults;
        }

        public static void SaveDevices(List<DahuaDeviceItem> devices)
        {
            Directory.CreateDirectory(ConfigDirectory);
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(devices, options);
            string tempPath = ConfigPath + ".tmp";
            File.WriteAllText(tempPath, json);
            if (File.Exists(ConfigPath))
            {
                string backupPath = ConfigPath + ".bak";
                File.Replace(tempPath, ConfigPath, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, ConfigPath);
            }
        }
    }
}
