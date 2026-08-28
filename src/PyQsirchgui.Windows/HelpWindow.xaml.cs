using System.Diagnostics;
using System.Windows;
using PyQsirchgui.Windows.Services;

namespace PyQsirchgui.Windows;

public partial class HelpWindow : Window
{
    public HelpWindow(string theme)
    {
        InitializeComponent();
        ThemePalette.Apply(Resources, string.IsNullOrWhiteSpace(theme) ? "system" : theme);
    }

    private void DonateClicked(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("https://www.paypal.com/paypalme/rjc862003") { UseShellExecute = true });
    }

    private void GitHubClicked(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("https://github.com/senposage/qsirch-gui/tree/main") { UseShellExecute = true });
    }
}
