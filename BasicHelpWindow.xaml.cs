using System.Windows;

namespace VideoPlayer
{
    public partial class BasicHelpWindow : Window
    {
        public BasicHelpWindow()
        {
            InitializeComponent();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
