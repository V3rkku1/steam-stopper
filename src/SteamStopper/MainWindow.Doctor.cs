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
        }
        catch (Exception ex)
        {
            DoctorVerdict.Text = "The check didn't finish: " + ex.Message;
        }
        finally { DoctorButton.IsEnabled = true; }
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
