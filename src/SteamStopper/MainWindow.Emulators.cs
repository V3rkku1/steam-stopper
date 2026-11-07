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
    private void AppCardRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AppOffer app }) return;
        if (EmuStore.Find(app.Name) is not null) RemovePortable(app);
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

    private async Task InstallEmulator(AppOffer app)
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
