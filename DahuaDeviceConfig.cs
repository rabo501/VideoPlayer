using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace VideoPlayer
{
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
        public string IpAddress { get; set; } = "192.168.1.108";
        public ushort Port { get; set; } = 37777;
        public string Username { get; set; } = "admin";
        public string Password { get; set; } = "";
        public int Channel { get; set; } = 1;
        public DahuaNetSdk.EM_RealPlayType StreamType { get; set; } = DahuaNetSdk.EM_RealPlayType.Realplay;

        public List<DahuaChannelInfo> Channels { get; set; } = new();
        public List<DahuaPresetItem> Presets { get; set; } = new();

        public string DisplayLabel => $"{Name} ({IpAddress}:{Port})";
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
            string[] defaultNames = Array.Empty<string>();

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
        private static readonly string ConfigPath = Path.Combine(AppContext.BaseDirectory, "dahua_devices.json");

        public static List<DahuaDeviceItem> LoadDevices()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    var list = JsonSerializer.Deserialize<List<DahuaDeviceItem>>(json);
                    if (list != null && list.Count > 0)
                    {
                        foreach (var d in list)
                        {
                            d.EnsureDefaultChannels(16);
                            d.EnsureDefaultPresets();
                        }
                        return list;
                    }
                }
            }
            catch { }

            // Domyślne urządzenia demonstracyjne
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
            nvr.EnsureDefaultPresets();

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
            cam.EnsureDefaultPresets();

            var listDefault = new List<DahuaDeviceItem> { nvr, cam };
            SaveDevices(listDefault);
            return listDefault;
        }

        public static void SaveDevices(List<DahuaDeviceItem> devices)
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(devices, options);
                File.WriteAllText(ConfigPath, json);
            }
            catch { }
        }
    }
}
