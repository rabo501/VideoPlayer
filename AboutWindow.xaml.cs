using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Navigation;
namespace VideoPlayer
{
 public partial class AboutWindow : Window
 {
  public AboutWindow()
  {
   InitializeComponent();
   Assembly assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
   Version? version = assembly.GetName().Version;
   string displayVersion = version == null ? "Nieznana" : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
   AssemblyInformationalVersionAttribute? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
   ProductVersionText.Text = !string.IsNullOrWhiteSpace(informational?.InformationalVersion) ? informational!.InformationalVersion : displayVersion;
   VersionText.Text = "Wersja " + displayVersion;
   ArchitectureText.Text = RuntimeInformation.ProcessArchitecture.ToString();
   CopyrightYearText.Text = DateTime.Now.Year.ToString();
  }
  private void Close_Click(object sender, RoutedEventArgs e) => Close();
  private void ProjectLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
  {
   try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
   catch (Exception ex) { MessageBox.Show(this, "Nie można otworzyć strony projektu.\n" + ex.Message, "VideoPlayer", MessageBoxButton.OK, MessageBoxImage.Information); }
   e.Handled = true;
  }
 }
}