using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SteamStopper.Core;

namespace SteamStopper;

// Update banner: checks GitHub at launch and every few hours, asks before installing.
public partial class MainWindow
{
    private UpdateInfo? _update;
    private CancellationTokenSource? _updateRun;
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };

    private void StartUpdateChecks()
    {
        Updater.CleanTemp();
        _updateTimer.Tick += (_, _) => _ = CheckForUpdates(manual: false);
        _updateTimer.Tick += (_, _) => _ = CheckEmulatorUpdates(force: true);
        _updateTimer.Start();
        // Give the window a moment; the check is not urgent.
        _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => Dispatcher.Invoke(() => CheckForUpdates(manual: false)));
    }

    private async Task CheckForUpdates(bool manual)
    {
        if (_updateRun is not null) return;
        if (manual) StatusText.Text = "Checking for updates…";
        try
        {
            var info = await Updater.FindAsync(CancellationToken.None);
            if (info is null)
            {
                if (manual) StatusText.Text = $"Steam Stopper {Updater.Version} is up to date.";
                return;
            }
            if (!manual && info.Version.ToString() == _settings.SkippedVersion) return;
            _update = info;
            ShowUpdateBanner();
            if (manual) StatusText.Text = $"Steam Stopper {info.Version} is available.";
        }
        catch (Exception ex)
        {
            if (manual) StatusText.Text = "Could not check for updates: " + ex.Message;
        }
    }

    private void ShowUpdateBanner(string? detail = null, bool downloading = false)
    {
        if (_update is not { } info) return;
        UpdateBanner.Visibility = Visibility.Visible;
        UpdateTitle.Text = downloading ? $"Updating to {info.Version}" : $"Steam Stopper {info.Version} is available";
        // First line of the release notes, without markdown markers.
        var note = info.Notes.Split('\n').Select(l => l.Trim().TrimStart('#', '-', '*', ' ').Replace("**", "")).FirstOrDefault(l => l.Length > 0 && l != "New");
        UpdateDetail.Text = detail ?? $"You have {Updater.Version}.{(note is null ? "" : "  " + note)}";
        UpdateNowButton.Content = downloading ? "Cancel" : "Update now";
        UpdateLaterButton.Visibility = UpdateSkipButton.Visibility = downloading ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (_updateRun is { } running)
        {
            running.Cancel();
            return;
        }
        if (_update is not { } info) return;
        _updateRun = new CancellationTokenSource();
        FetchJob? job = null;
        var ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        ticker.Tick += (_, _) =>
        {
            if (job is not { Running: true }) return;
            job.Sample(1);
            ShowUpdateBanner($"Downloading  {job.ProgressText}  ·  {Format.Speed(job.Bps)}", downloading: true);
        };
        ShowUpdateBanner("Downloading…", downloading: true);
        ticker.Start();
        try
        {
            var files = await Updater.PrepareAsync(info, FetchEngine, j => job = j, _updateRun.Token);
            ShowUpdateBanner("Restarting into the new version…", downloading: true);
            // Unfinished Fetch downloads keep their progress and resume after the restart.
            Updater.Launch(files);
            Quit();
        }
        catch (OperationCanceledException)
        {
            ShowUpdateBanner();
            StatusText.Text = "Update cancelled.";
        }
        catch (Exception ex)
        {
            ShowUpdateBanner();
            StatusText.Text = "Update failed: " + ex.Message;
        }
        finally
        {
            ticker.Stop();
            _updateRun?.Dispose();
            _updateRun = null;
        }
    }

    private void UpdateLater_Click(object sender, RoutedEventArgs e) => UpdateBanner.Visibility = Visibility.Collapsed;

    private void UpdateSkip_Click(object sender, RoutedEventArgs e)
    {
        if (_update is null) return;
        _settings.SkippedVersion = _update.Version.ToString();
        _settings.Save();
        UpdateBanner.Visibility = Visibility.Collapsed;
        StatusText.Text = $"Skipping {_update.Version}. Click the version number to check again.";
    }

    private void UpdateNotes_Click(object sender, RoutedEventArgs e)
    {
        if (_update?.PageUrl is { Length: > 0 } url)
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
    }

    private void VersionText_Click(object sender, MouseButtonEventArgs e) => _ = CheckForUpdates(manual: true);
}
