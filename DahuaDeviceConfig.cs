using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoPlayer
{
    /// <summary>
    /// Protects Dahua credentials with Windows DPAPI. The CurrentUser scope deliberately
    /// prevents another Windows account (or another computer) from decrypting the values.
    /// </summary>
    internal static class DahuaCredentialProtection
    {
        private const string Prefix = "dpapi:v1:";
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VideoPlayer.Dahua.Credentials.v1");

        public static string Protect(string value)
        {
            byte[] clearBytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            byte[] protectedBytes = ProtectedData.Protect(
                clearBytes, Entropy, DataProtectionScope.CurrentUser);

            try
            {
                return Prefix + Convert.ToBase64String(protectedBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clearBytes);
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }

        public static bool TryUnprotect(string value, out string clearText, out bool isLegacyPlainText)
        {
            clearText = string.Empty;
            isLegacyPlainText = false;

            if (string.IsNullOrEmpty(value) || !value.StartsWith(Prefix, StringComparison.Ordinal))
            {
                // Backward compatibility: older JSON files stored credentials as plain text.
                clearText = value ?? string.Empty;
                isLegacyPlainText = true;
                return true;
            }

            byte[]? cipherBytes = null;
            byte[]? clearBytes = null;
            try
            {
                cipherBytes = Convert.FromBase64String(value.Substring(Prefix.Length));
                clearBytes = ProtectedData.Unprotect(
                    cipherBytes, Entropy, DataProtectionScope.CurrentUser);
                clearText = Encoding.UTF8.GetString(clearBytes);
                return true;
            }
            catch (CryptographicException)
            {
                // Most commonly the file was copied to another PC or Windows account.
                return false;
            }
            catch (FormatException)
            {
                // Treat a damaged protected value as unavailable; never reinterpret it as plain text.
                return false;
            }
            finally
            {
                if (cipherBytes != null) CryptographicOperations.ZeroMemory(cipherBytes);
                if (clearBytes != null) CryptographicOperations.ZeroMemory(clearBytes);
            }
        }

        public static bool IsProtected(string value) =>
            !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);
    }

    public sealed class DahuaRecordItem
    {
        public int Channel { get; set; }
        public string FileName { get; set; } = string.Empty;
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public uint SizeKb { get; set; }
        public int RecordType { get; set; }
        public string DurationText => (EndTime - StartTime).ToString(@"hh\\:mm\\:ss");
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
        private string _username = "admin";
        private string _password = string.Empty;
        private string? _usernameStoredValue;
        private string? _passwordStoredValue;
        private bool _usernameUnavailable;
        private bool _passwordUnavailable;
        private bool _usernameChanged;
        private bool _passwordChanged;
        private bool _usernameNeedsMigration;
        private bool _passwordNeedsMigration;

        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Kamera Dahua IP";
        public string IpAddress { get; set; } = "";
        public ushort Port { get; set; } = 37777;
        public ushort HttpPort { get; set; } = 80;

        // Runtime values used by the SDK. They are never serialized directly.
        [JsonIgnore]
        public string Username
        {
            get => _username;
            set
            {
                _username = value ?? string.Empty;
                _usernameChanged = true;
                _usernameUnavailable = false;
                _usernameNeedsMigration = false;
                _usernameStoredValue = null;
            }
        }

        [JsonIgnore]
        public string Password
        {
            get => _password;
            set
            {
                _password = value ?? string.Empty;
                _passwordChanged = true;
                _passwordUnavailable = false;
                _passwordNeedsMigration = false;
                _passwordStoredValue = null;
            }
        }

        // Keep the established JSON property names for compatibility, but store only DPAPI ciphertext.
        [JsonPropertyName("Username")]
        public string UsernameProtected
        {
            get => GetStoredCredential(_username, _usernameStoredValue, _usernameUnavailable, _usernameChanged);
            set => SetStoredCredential(value, true);
        }

        [JsonPropertyName("Password")]
        public string PasswordProtected
        {
            get => GetStoredCredential(_password, _passwordStoredValue, _passwordUnavailable, _passwordChanged);
            set => SetStoredCredential(value, false);
        }

        [JsonIgnore]
        public bool CredentialsUnavailable => _usernameUnavailable || _passwordUnavailable;

        [JsonIgnore]
        internal bool CredentialsNeedMigration => _usernameNeedsMigration || _passwordNeedsMigration;

        private static string GetStoredCredential(string clearText, string? originalValue, bool unavailable, bool changed)
        {
            if (unavailable && !changed)
                return originalValue ?? string.Empty;

            return DahuaCredentialProtection.Protect(clearText);
        }

        private void SetStoredCredential(string? storedValue, bool isUsername)
        {
            bool ok = DahuaCredentialProtection.TryUnprotect(
                storedValue ?? string.Empty, out string clearText, out bool isLegacyPlainText);

            if (isUsername)
            {
                _usernameStoredValue = storedValue;
                _username = ok ? clearText : string.Empty;
                _usernameUnavailable = !ok;
                _usernameChanged = false;
                _usernameNeedsMigration = ok && isLegacyPlainText;
            }
            else
            {
                _passwordStoredValue = storedValue;
                _password = ok ? clearText : string.Empty;
                _passwordUnavailable = !ok;
                _passwordChanged = false;
                _passwordNeedsMigration = ok && isLegacyPlainText;
            }
        }

        public void SetCredentials(string username, string password)
        {
            Username = username;
            Password = password;
        }

        internal void MarkCredentialsPersisted()
        {
            _usernameNeedsMigration = false;
            _passwordNeedsMigration = false;
            _usernameChanged = false;
            _passwordChanged = false;
            if (!_usernameUnavailable) _usernameStoredValue = null;
            if (!_passwordUnavailable) _passwordStoredValue = null;
        }

        public int Channel { get; set; } = 1;
        public DahuaNetSdk.EM_RealPlayType StreamType { get; set; } = DahuaNetSdk.EM_RealPlayType.Realplay;

        public List<DahuaChannelInfo> Channels { get; set; } = new();
        public List<DahuaPresetItem> Presets { get; set; } = new();

        // Name is a presentation-only label. All network operations must use ConnectionAddress.
        [JsonIgnore]
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
                    SetChannelName(dCh.Number, dCh.Name);
            }
        }

        public void UpdateFromDevicePresets(List<DahuaPresetItem> devicePresets)
        {
            if (devicePresets == null) return;

            Presets = devicePresets
                .Where(p => p.PresetNumber > 0)
                .GroupBy(p => p.PresetNumber)
                .Select(g => new DahuaPresetItem
                {
                    PresetNumber = g.Key,
                    Name = string.IsNullOrWhiteSpace(g.First().Name) ? $"Preset {g.Key}" : g.First().Name.Trim()
                })
                .OrderBy(p => p.PresetNumber)
                .ToList();
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
                match.Name = newName.Trim();
            else
                Channels.Add(new DahuaChannelInfo { Number = channelNumber, Name = newName.Trim() });
        }

        public void EnsureDefaultChannels(int maxCount = 16)
        {
            for (int i = 1; i <= maxCount; i++)
            {
                if (!Channels.Any(c => c.Number == i))
                    Channels.Add(new DahuaChannelInfo { Number = i, Name = $"Kanał {i}" });
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
                // Copy first; the source is removed only after encrypted data has been saved successfully.
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
            bool extractedFromLabel = openParen >= 0 && closeParen > openParen;
            if (extractedFromLabel)
                candidate = candidate.Substring(openParen + 1, closeParen - openParen - 1).Trim();

            if (!extractedFromLabel && !candidate.Contains('.') && !candidate.Contains(':'))
                return false;

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

                            if (string.IsNullOrWhiteSpace(device.IpAddress) &&
                                TryExtractLegacyEndpoint(device.Name, out string legacyHost, out ushort legacyPort))
                            {
                                string legacyLabel = device.Name;
                                device.IpAddress = legacyHost;
                                if (legacyPort > 0 && device.Port == 37777)
                                    device.Port = legacyPort;

                                int labelEnd = legacyLabel.LastIndexOf('(');
                                string readableName = labelEnd > 0 ? legacyLabel.Substring(0, labelEnd).Trim() : string.Empty;
                                device.Name = string.IsNullOrWhiteSpace(readableName)
                                    ? $"Urządzenie {device.IpAddress}"
                                    : readableName;
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

                    bool migratePlainTextCredentials = list.Any(d => d.CredentialsNeedMigration);
                    if (migratePlainTextCredentials)
                        SaveDevices(list); // Rewrites the JSON with DPAPI values and removes plaintext migration backups.
                    else if (!list.Any(d => d.CredentialsUnavailable))
                        TryDeleteFile(LegacyConfigPath); // Remove an obsolete portable copy containing old plaintext secrets.

                    return list;
                }
                catch (Exception ex)
                {
                    throw new InvalidDataException(
                        $"Nie można odczytać konfiguracji urządzeń Dahua: {ConfigPath}. " +
                        "Plik nie został nadpisany. Szczegóły: " + ex.Message, ex);
                }
            }

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
            bool credentialMigration = devices.Any(d => d.CredentialsNeedMigration);
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(devices, options);
            string tempPath = ConfigPath + ".tmp";

            File.WriteAllText(tempPath, json, new UTF8Encoding(false));
            try
            {
                if (File.Exists(ConfigPath))
                {
                    if (credentialMigration)
                    {
                        // Never create a .bak containing the previous plaintext credentials.
                        File.Replace(tempPath, ConfigPath, null, ignoreMetadataErrors: true);
                    }
                    else
                    {
                        string backupPath = ConfigPath + ".bak";
                        File.Replace(tempPath, ConfigPath, backupPath, ignoreMetadataErrors: true);
                    }
                }
                else
                {
                    File.Move(tempPath, ConfigPath);
                }

                foreach (var device in devices)
                    device.MarkCredentialsPersisted();

                if (credentialMigration)
                {
                    TryDeleteFile(ConfigPath + ".bak");
                    TryDeleteFile(LegacyConfigPath);
                }
            }
            finally
            {
                TryDeleteFile(tempPath);
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // Cleanup failure must not corrupt or prevent loading the encrypted configuration.
            }
        }
    }
}
