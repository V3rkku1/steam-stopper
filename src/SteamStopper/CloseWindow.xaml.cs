using System.Windows;

namespace SteamStopper;

/// <summary>Asked when the window's X is pressed: hide to the tray or quit for real.</summary>
public partial class CloseWindow : Window
{
    public CloseWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
    }

    /// <summary>"tray" or "quit"; only meaningful when the dialog returns true.</summary>
    public string Choice { get; private set; } = "tray";
    public bool Remember => RememberBox.IsChecked == true;

    private void Tray_Click(object sender, RoutedEventArgs e) => Finish("tray");
    private void Quit_Click(object sender, RoutedEventArgs e) => Finish("quit");
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Finish(string choice)
    {
        Choice = choice;
        DialogResult = true;
    }
}
