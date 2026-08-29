using PayBeat.App.Helpers;
using PayBeat.App.Services;
using System.Diagnostics;
using System.Windows.Navigation;

namespace PayBeat.App.Views;

/// <summary>
/// Displays app version plus attribution for the original project.
/// </summary>
public partial class AboutWindow
{
    private readonly ConfigurationStore _store;

    /// <summary>Initializes the about window and populates the version label.</summary>
    public AboutWindow(ConfigurationStore store)
    {
        _store = store;
        InitializeComponent();
        VersionText.Text = $"{LocalizationService.Get("About.Version")} v{AppVersion.Current}";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void GitHubLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DragMove();
    }
}
