using System.Windows;

namespace VideoPlayer
{
    public partial class ReportDetailsWindow : Window
    {
        internal ReportMetadata Metadata { get; private set; } = new ReportMetadata();

        internal ReportDetailsWindow()
        {
            InitializeComponent();
        }

        private void Continue_Click(object sender, RoutedEventArgs e)
        {
            Metadata = new ReportMetadata
            {
                CaseNumber = CaseNumberBox.Text.Trim(),
                CaseTitle = CaseTitleBox.Text.Trim(),
                Location = LocationBox.Text.Trim(),
                Notes = NotesBox.Text.Trim()
            };
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}