using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using SteamStopper.Core;

namespace SteamStopper;

// Download doctor on the Downloads page: measure Steam's servers and the line, say which is the limit.
public partial class MainWindow
{
    private static readonly HttpClient DoctorHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    private async void Doctor_Click(object sender, RoutedEventArgs e)
    {
        DoctorButton.IsEnabled = false;
        DoctorResults.Visibility = Visibility.Collapsed;
        // Steam's own speed first: the line test below takes the bandwidth for a few seconds.
        var steamBps = _lastSnapshot?.SpeedBps ?? 0;
        try
        {
            DoctorVerdict.Text = "Asking Steam which servers it uses for this PC…";
            var servers = await DownloadDoctor.Servers(DoctorHttp, CancellationToken.None);
            DoctorVerdict.Text = $"Measuring {servers.Count} Steam servers…";
            await Task.WhenAll(servers.Select(async s => s.PingMs = await DownloadDoctor.Ping(s.Host, CancellationToken.None)));
            DoctorVerdict.Text = "Measuring your line speed (8 seconds)…";
            var line = await DownloadDoctor.LineSpeed(DoctorHttp, CancellationToken.None);
            var (verdict, fixes) = DownloadDoctor.Diagnose(servers, line, steamBps);
            DoctorVerdict.Text = verdict;
            DoctorVerdict.Foreground = Brush("Text");
            DoctorServers.Children.Clear();
            DoctorServers.Children.Add(Chip($"Your line  {Format.Speed(line)}"));
            foreach (var s in servers.OrderBy(s => s.PingMs ?? double.MaxValue).Take(6))
                DoctorServers.Children.Add(Chip(s.PingMs is { } ms ? $"{s.City}  {ms:0} ms  ·  load {s.Load}%" : $"{s.City}  no answer"));
            DoctorFixes.Text = string.Join(Environment.NewLine, fixes.Select(f => "•  " + f));
            DoctorResults.Visibility = Visibility.Visible;
            await PaintWindowsChecks();
        }
        catch (Exception ex)
        {
            DoctorVerdict.Text = "The check didn't finish: " + ex.Message;
        }
        finally { DoctorButton.IsEnabled = true; }
    }

    /// <summary>Two Windows settings that cap speed without saying so; each gets a Fix button when it's wrong.</summary>
    private async Task PaintWindowsChecks()
    {
        DoctorChecks.Children.Clear();
        var wifi = System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable()
            && _bondRows.Any(l => l.Kind.Contains("Wi", StringComparison.OrdinalIgnoreCase));
        var (tuning, saving) = await Task.Run(() => (NetTuning.AutoTuning(), wifi ? NetTuning.WifiSaving() : null));
        if (tuning is not null)
            DoctorChecks.Children.Add(NetTuning.AutoTuningLimited(tuning)
                ? Problem($"Windows network tuning is {tuning.ToLowerInvariant()}: it caps each connection's speed.", "Fix", NetTuning.FixAutoTuning)
                : Chip("Windows network tuning  ✓"));
        if (saving is { } level)
            DoctorChecks.Children.Add(level > 0
                ? Problem("Wi-Fi power saving is on while plugged in: it can halve Wi-Fi speed.", "Fix", NetTuning.FixWifi)
                : Chip("Wi-Fi power saving off  ✓"));
    }

    private Border Problem(string text, string action, Action fix)
    {
        var button = new Button { Content = action, Style = (Style)FindResource("SmallPrimary"), Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(12, 2, 12, 2) };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            await Task.Run(fix);
            StatusText.Text = "Fixed. It applies to new connections right away.";
            await PaintWindowsChecks();
        };
        var chip = Chip(text);
        chip.Background = Brush("Selected");
        var label = chip.Child;
        chip.Child = null; // detach before it moves into the panel
        DockPanel.SetDock(button, Dock.Right);
        chip.Child = new DockPanel { Children = { button, label } };
        return chip;
    }

    private Border Chip(string text) => new()
    {
        Background = Brush("Raised"),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(10, 5, 10, 5),
        Margin = new Thickness(0, 0, 8, 8),
        Child = new TextBlock { Text = text, FontSize = 12, Foreground = Brush("Text") }
    };

    private void DoctorFlush_Click(object sender, RoutedEventArgs e)
    {
        if (Dialog.Show(this,
                "Clear Steam's download cache?\n\nSteam shows its own confirmation, then restarts. Installed games are not touched, but you may need to log in to Steam again.",
                "Download doctor", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try { Process.Start(new ProcessStartInfo("steam://flushconfig") { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { StatusText.Text = "Couldn't ask Steam: " + ex.Message; }
    }
}
