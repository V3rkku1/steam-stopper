using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SteamStopper.Core;

namespace SteamStopper;

// Emulators tab: portable installs through the Fetch downloader, Play and Remove.
public partial class MainWindow
{
    private readonly Dictionary<string, EmuRelease> _emuLatest = [];
    private DateTime _emuChecked = DateTime.MinValue;
    private bool _emuChecking;

    private void AppCardRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AppOffer app }) return;
        if (app.UpdateTo is not null)
        {
            if (EmulatorRunning(app.Name)) StatusText.Text = $"Close {app.Name} first, then update it.";
            else _ = InstallEmulator(app);
        }
        else if (EmuStore.Find(app.Name) is not null) RemovePortable(app);
        else _ = UninstallApp(app);
    }

    private void LaunchEmulator(AppOffer app, string exe)
    {
        try
        {
            // Steam Stopper runs as admin; going through explorer starts the emulator as the normal user,
            // so its saves, drag and drop and controller prompts behave as if opened from the Start menu.
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{exe}\"") { UseShellExecute = false })?.Dispose();
            StatusText.Text = $"Started {app.Name}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not start {app.Name}: {ex.Message}";
        }
    }

    /// <param name="background">Auto-update: on a failed lookup, stay quiet instead of falling back to winget or the website.</param>
    private async Task InstallEmulator(AppOffer app, bool background = false)
    {
        if (_toolsWorking || app.Source is not { } source) return;
        _toolsWorking = true;
        FetchJob? job = null;
        string? archive = null;
        var ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        ticker.Tick += (_, _) =>
        {
            if (job is not { Running: true }) return;
            job.Sample(1);
            AppBusy.Text = $"Downloading {app.Name}  {job.ProgressText}  ·  {Format.Speed(job.Bps)}";
        };
        try
        {
            AppBusy.Text = $"Looking for the latest {app.Name}…";
            EmuRelease? release = null;
            string? lookupError = null;
            try { release = await EmuStore.FindLatest(source, CancellationToken.None); }
            catch (System.Net.Http.HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests)
            {
                lookupError = "the release server refused the lookup, usually its hourly limit; try again later";
            }
            catch (Exception ex) { lookupError = ex.Message.TrimEnd('.'); }
            if (release is null && background)
            {
                AppBusy.Text = "";
                return;
            }
            if (release is null)
            {
                // No portable build found (API down, rate limit, renamed files): fall back to winget or the website.
                var fallback = app.Id.Length > 0 || app.Url.Length > 0 ? await Task.Run(() => AppHub.Install(app)) : "";
                StatusText.Text = $"No portable {app.Name} build found{(lookupError is null ? "" : $" ({lookupError})")}. {fallback}".Trim();
                AppBusy.Text = "";
                return;
            }

            job = new FetchJob { Url = release.Url, Folder = Path.Combine(EmuStore.Root, "_downloads") };
            ticker.Start();
            StatusText.Text = $"Downloading {app.Name} {release.Version}…";
            await FetchEngine.Run(job, _settings.FetchConnections, CancellationToken.None);
            if (job.State != FetchState.Done)
                throw new IOException(job.Error.Length > 0 ? job.Error : "The download stopped.");

            // Extract picks the unpacker by extension, so keep the release's own file name.
            archive = job.FinalPath;
            if (!archive.EndsWith(Path.GetExtension(release.File), StringComparison.OrdinalIgnoreCase))
            {
                var renamed = Path.Combine(job.Folder, release.File);
                File.Move(archive, renamed, true);
                archive = renamed;
            }
            AppBusy.Text = $"Unpacking {app.Name}…";
            var folder = EmuStore.FolderFor(app.Name);
            await Task.Run(() => EmuStore.Extract(archive, folder));
            try { File.Delete(archive); } catch { }
            var exe = EmuStore.FindExe(folder, source.Exe)
                ?? throw new IOException("It unpacked, but no program was found inside.");
            EmuStore.Record(app.Name, new EmuStore.Entry(exe, release.Version));
            StatusText.Text = $"{app.Name} {release.Version} is ready. Press Play.";
            AppBusy.Text = "";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"{app.Name}: {ex.Message}";
            AppBusy.Text = "";
            // A half download can't be resumed from here, so don't leave it in _downloads.
            job?.DeleteLeftovers();
            // Or a finished archive that failed to unpack.
            if (archive is not null) try { File.Delete(archive); } catch { }
        }
        finally
        {
            ticker.Stop();
            _toolsWorking = false;
            RefreshApps();
        }
    }

    /// <summary>
    /// Look for newer builds of the portable emulators (at most hourly unless forced), install them in the
    /// background when auto-update is on and the emulator is closed, and flag the rest with an Update button.
    /// </summary>
    private async Task CheckEmulatorUpdates(bool force = false)
    {
        if (_emuChecking || (!force && DateTime.UtcNow - _emuChecked < TimeSpan.FromHours(1))) return;
        _emuChecking = true;
        try
        {
            var installed = AppHub.Emulators.Where(e => e.Source is not null && EmuStore.Find(e.Name) is not null).ToList();
            foreach (var emu in installed)
            {
                // One failed lookup (rate limit, server down) shouldn't stop the others.
                try
                {
                    if (await EmuStore.FindLatest(emu.Source!, CancellationToken.None) is { } latest) _emuLatest[emu.Name] = latest;
                }
                catch { }
            }
            _emuChecked = DateTime.UtcNow;
            if (_settings.AutoUpdateEmulators)
            {
                foreach (var emu in installed.Where(e => UpdateFor(e.Name) is not null && !EmulatorRunning(e.Name)))
                {
                    var from = EmuStore.Find(emu.Name)?.Version;
                    await InstallEmulator(emu, background: true);
                    if (UpdateFor(emu.Name) is null) StatusText.Text = $"Updated {emu.Name} from {from} to {EmuStore.Find(emu.Name)?.Version}.";
                }
            }
            var waiting = installed.Where(e => UpdateFor(e.Name) is not null).Select(e => e.Name).ToList();
            if (waiting.Count > 0)
                StatusText.Text = $"New build ready for {string.Join(", ", waiting)}. Press Update on the Emulators tab"
                    + (_settings.AutoUpdateEmulators ? ", or it updates by itself once closed." : ".");
            if (_emuMode) RefreshApps();
        }
        finally { _emuChecking = false; }
    }

    private EmuRelease? UpdateFor(string name) =>
        EmuStore.Find(name) is { } have && _emuLatest.TryGetValue(name, out var latest) && latest.Version != have.Version ? latest : null;

    private void MarkEmulatorUpdates(List<AppOffer> rows)
    {
        foreach (var row in rows)
        {
            if (UpdateFor(row.Name) is not { } latest) continue;
            row.UpdateTo = latest.Version;
            // The card has no room for both versions; "Update ready" plus the status bar message say enough.
            row.State = "Update ready";
        }
    }

    // Updating unpacks over the emulator's files, which fails while it is running.
    private static bool EmulatorRunning(string name)
    {
        var dir = EmuStore.FolderFor(name);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.MainModule?.FileName?.StartsWith(dir, StringComparison.OrdinalIgnoreCase) == true) return true;
                }
                catch { } // System and other users' processes can't be read.
            }
        }
        return false;
    }

    private void EmuAuto_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoUpdateEmulators = EmuAutoBox.IsChecked == true;
        _settings.Save();
        if (_settings.AutoUpdateEmulators) _ = CheckEmulatorUpdates(force: true);
    }

    private void RemovePortable(AppOffer app)
    {
        if (_toolsWorking) return;
        var folder = EmuStore.FolderFor(app.Name);
        if (MessageBox.Show(
                $"Remove {app.Name}?\n\nThis deletes {folder}, including anything you put in it (keys, firmware, portable saves). Saves the emulator keeps in AppData stay.",
                "Remove emulator", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            EmuStore.Remove(app.Name);
            StatusText.Text = $"Removed {app.Name}.";
        }
        catch (Exception ex)
        {
            // Usually the emulator is still running and holds its files open.
            StatusText.Text = $"Could not remove {app.Name}: {ex.Message} Close it and try again.";
        }
        RefreshApps();
    }
}
