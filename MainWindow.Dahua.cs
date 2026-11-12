using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace VideoPlayer
{
    public partial class MainWindow
    {
        private DahuaStreamWindow? _dahuaWindow;

        private void DahuaStream_Click(object sender, RoutedEventArgs e)
        {
            if (_dahuaWindow == null || !_dahuaWindow.IsLoaded)
            {
                _dahuaWindow = new DahuaStreamWindow { Owner = this };
                _dahuaWindow.Closed += (_, _) => _dahuaWindow = null;
                _dahuaWindow.Show();
            }
            else
            {
                _dahuaWindow.Activate();
            }
        }

        /// <summary>
        /// Otwiera pobrany strumień / nagranie z rejestratora Dahua w głównym oknie VideoPlayer,
        /// dając użytkownikowi dostęp do osi czasu, klatkowania, OCR tablic, cięcia i raportów.
        /// </summary>
        public async Task OpenDahuaStreamAsync(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                StatusText.Text = "Plik nagrania Dahua nie istnieje.";
                return;
            }

            try
            {
                await OpenMediaAsync(filePath);
                Activate();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Błąd wczytywania strumienia do odtwarzacza: " + ex.Message, "Dahua VideoPlayer");
            }
        }
    }
}
