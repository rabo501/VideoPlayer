using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace VideoPlayer
{
    public partial class DahuaConfigurationWindow : Window
    {
        private readonly List<DahuaDeviceItem> _devices;
        private DahuaDeviceItem? _selectedDevice;
        private bool _loading;

        public DahuaConfigurationWindow(List<DahuaDeviceItem> devices)
        {
            InitializeComponent();
            _devices = devices ?? new List<DahuaDeviceItem>();
            DevicesList.ItemsSource = _devices;
            if (_devices.Count > 0)
                DevicesList.SelectedIndex = 0;
            else
                ClearEditor();
        }

        private void DevicesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            if (DevicesList.SelectedItem is DahuaDeviceItem device)
                LoadEditor(device);
        }

        private void LoadEditor(DahuaDeviceItem device)
        {
            _loading = true;
            _selectedDevice = device;
            NameBox.Text = device.Name;
            AddressBox.Text = device.IpAddress;
            PortBox.Text = device.Port.ToString();
            HttpPortBox.Text = device.HttpPort.ToString();
            UsernameBox.Text = device.CredentialsUnavailable ? string.Empty : device.Username;
            PasswordBox.Password = device.CredentialsUnavailable ? string.Empty : device.Password;
            CredentialInfo.Text = device.CredentialsUnavailable
                ? "Nie można odszyfrować danych logowania dla tego konta Windows. Wpisz ponownie użytkownika i hasło."
                : "Dane logowania są chronione przez Windows DPAPI (CurrentUser).";
            CredentialInfo.Foreground = device.CredentialsUnavailable
                ? System.Windows.Media.Brushes.Orange
                : System.Windows.Media.Brushes.LightGreen;
            _loading = false;
        }

        private void ClearEditor()
        {
            _loading = true;
            _selectedDevice = null;
            NameBox.Text = string.Empty;
            AddressBox.Text = string.Empty;
            PortBox.Text = "37777";
            HttpPortBox.Text = "80";
            UsernameBox.Text = "admin";
            PasswordBox.Password = string.Empty;
            CredentialInfo.Text = "Nowe urządzenie. Podaj dane logowania i zapisz konfigurację.";
            CredentialInfo.Foreground = System.Windows.Media.Brushes.LightSkyBlue;
            _loading = false;
        }

        private bool TryReadEditor(out string name, out string address, out ushort port, out ushort httpPort)
        {
            name = NameBox.Text.Trim();
            address = AddressBox.Text.Trim();
            // Assign every out parameter before any validation can return false.
            port = 0;
            httpPort = 0;
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show("Podaj nazwę urządzenia.", "Konfiguracja", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (string.IsNullOrWhiteSpace(address) ||
                Uri.CheckHostName(address.Trim('[', ']')) == UriHostNameType.Unknown)
            {
                MessageBox.Show("Podaj prawidłowy adres IP lub nazwę hosta.", "Konfiguracja", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (!ushort.TryParse(PortBox.Text.Trim(), out port) || port == 0)
            {
                MessageBox.Show("Port NetSDK musi mieścić się w zakresie 1–65535.", "Konfiguracja", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (!ushort.TryParse(HttpPortBox.Text.Trim(), out httpPort) || httpPort == 0)
            {
                MessageBox.Show("Port HTTP musi mieścić się w zakresie 1–65535.", "Konfiguracja", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (string.IsNullOrWhiteSpace(UsernameBox.Text) || string.IsNullOrEmpty(PasswordBox.Password))
            {
                MessageBox.Show("Wpisz nazwę użytkownika i hasło. Puste hasło nie zostanie zapisane.", "Konfiguracja", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            return true;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (!TryReadEditor(out string name, out string address, out ushort port, out ushort httpPort))
                return;

            if (_selectedDevice == null)
            {
                MessageBox.Show("Najpierw wybierz urządzenie lub kliknij Dodaj urządzenie.", "Konfiguracja", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _selectedDevice.Name = name;
            _selectedDevice.IpAddress = address;
            _selectedDevice.Port = port;
            _selectedDevice.HttpPort = httpPort;
            _selectedDevice.SetCredentials(UsernameBox.Text.Trim(), PasswordBox.Password);
            DahuaConfigService.SaveDevices(_devices);
            DevicesList.Items.Refresh();
            CredentialInfo.Text = "Zapisano. Dane logowania są chronione przez Windows DPAPI.";
            CredentialInfo.Foreground = System.Windows.Media.Brushes.LightGreen;
        }

        private void Add_Click(object sender, RoutedEventArgs e)
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
            _devices.Add(device);
            DevicesList.Items.Refresh();
            DevicesList.SelectedItem = device;
            LoadEditor(device);
            NameBox.Text = "Nowe urządzenie Dahua";
            AddressBox.Focus();
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedDevice == null) return;
            if (MessageBox.Show($"Usunąć urządzenie „{_selectedDevice.Name}”?", "Potwierdzenie",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            _devices.Remove(_selectedDevice);
            DahuaConfigService.SaveDevices(_devices);
            DevicesList.Items.Refresh();
            if (_devices.Count > 0)
                DevicesList.SelectedIndex = 0;
            else
                ClearEditor();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }
    }
}