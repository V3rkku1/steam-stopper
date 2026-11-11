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
    private DateTime? _updateChecked;
    private string _updateResult = "Not checked yet";

    private void StartUpdateChecks()
    {
        Updater.CleanTemp();
        _updateTimer.Tick += (_, _) => _ = CheckForUpdates(manual: false);
        _updateTimer.Tick += (_, _) => _ = CheckEmulatorUpdates(force: true);
        _updateTimer.Start();
        // Keeps "checked 5 min ago" current.
        _pollTimer.Tick += (_, _) => PaintUpdateState();
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
            _updateChecked = DateTime.Now;
            _updateResult = info is null ? $"Up to date ({Updater.Version})" : $"{info.Version} available";
            if (info is null)
            {
                if (manual) StatusText.Text = $"Steam Multitool {Updater.Version} is up to date.";
                return;
            }
            if (!manual && info.Version.ToString() == _settings.SkippedVersion) return;
            _update = info;
            ShowUpdateBanner();
            if (manual) StatusText.Text = $"Steam Multitool {info.Version} is available.";
        }
        catch (Exception ex)
        {
            _updateChecked = DateTime.Now;
            _updateResult = "Couldn't check";
            if (manual) StatusText.Text = "Could not check for updates: " + ex.Message;
        }
        finally { PaintUpdateState(); }
    }

    private void PaintUpdateState()
    {
        var ago = _updateChecked is not { } at ? "" : (DateTime.Now - at).TotalMinutes switch
        {
            < 1 => "  ·  just now",
            < 60 and var m => $"  ·  {(int)m} min ago",
            var m => $"  ·  {(int)(m / 60)} h ago"
        };
        var text = _updateResult + ago;
        if (UpdateState.Text != text) UpdateState.Text = text;
    }

    /// <summary>The visible "Check for updates": the app, then every installed emulator, ignoring skip and the hourly limit.</summary>
    private async Task CheckEverything()
    {
        UpdateCheckButton.IsEnabled = false;
        UpdateCheckButton.Content = "Checking…";
        try
        {
            await CheckForUpdates(manual: true);
            var appStatus = StatusText.Text;
            await CheckEmulatorUpdates(force: true);
            // The emulator check only speaks when it found or installed something; otherwise keep the app result.
            if (StatusText.Text == appStatus && _emuFailed.Count == 0 && AppHub.Emulators.Any(em => em.Source is not null && EmuStore.Find(em.Name) is not null))
                StatusText.Text = appStatus + " Your emulators are up to date too.";
        }
        finally
        {
            UpdateCheckButton.IsEnabled = true;
            UpdateCheckButton.Content = "Check for updates";
        }
    }

    private void UpdateCheck_Click(object sender, RoutedEventArgs e) => _ = CheckEverything();

    private void ShowUpdateBanner(string? detail = null, bool downloading = false)
    {
        if (_update is not { } info) return;
        UpdateBanner.Visibility = Visibility.Visible;
        UpdateTitle.Text = downloading ? $"Updating to {info.Version}" : $"Steam Multitool {info.Version} is available";
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
        if (_update is not { } info) return;
        // Release notes in plain text: markdown markers stripped, empty "Downloads" boilerplate left out.
        var notes = string.Join("\n", info.Notes.Split('\n')
                .TakeWhile(l => !l.TrimStart().StartsWith("## Downloads"))
                .Select(l => l.TrimEnd().Replace("**", "").Replace("`", ""))
                .Select(l => l.StartsWith("## ") ? "\n" + l[3..].ToUpperInvariant() : l.StartsWith("- ") ? "•  " + l[2..] : l))
            .Trim();
        var open = MessageBox.Show(
            $"{notes}\n\nOpen the release page?",
            $"What's new in Steam Multitool {info.Version}", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (open == MessageBoxResult.Yes && info.PageUrl.Length > 0)
            Process.Start(new ProcessStartInfo(info.PageUrl) { UseShellExecute = true })?.Dispose();
    }

    private void VersionText_Click(object sender, MouseButtonEventArgs e) => _ = CheckEverything();
}
