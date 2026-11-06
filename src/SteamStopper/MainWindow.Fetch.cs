using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using SteamStopper.Core;

namespace SteamStopper;

// Fetch page: segmented downloads spread over the links ticked on the Bond page.
public partial class MainWindow
{
    private static readonly int[] FetchConnChoices = [1, 2, 4, 8, 16, 32];
    private static readonly int[] FetchAtOnceChoices = [1, 2, 3, 4, 6];
    private readonly ObservableCollection<FetchJob> _fetchJobs = [];
    private readonly Dictionary<FetchJob, CancellationTokenSource> _fetchRuns = [];
    private readonly HashSet<FetchJob> _fetchRemoved = [];
    private Fetcher? _fetcher;
    private int _fetchTicks;

    // Reads _bondRows from worker threads; the UI only ever swaps the whole list.
    private Fetcher FetchEngine => _fetcher ??= new Fetcher(() => _settings.FetchBond ? _bondRows : []);

    private void InitFetch()
    {
        foreach (var n in FetchConnChoices) FetchConnBox.Items.Add(n);
        foreach (var n in FetchAtOnceChoices) FetchAtOnceBox.Items.Add(n);
        FetchConnBox.SelectedItem = FetchConnChoices.Contains(_settings.FetchConnections) ? _settings.FetchConnections : 8;
        FetchAtOnceBox.SelectedItem = FetchAtOnceChoices.Contains(_settings.FetchAtOnce) ? _settings.FetchAtOnce : 2;
        FetchAtOnceBox.SelectionChanged += (_, _) => { SaveFetchSettings(); PumpFetch(); };
        FetchConnBox.SelectionChanged += (_, _) => SaveFetchSettings();
        FetchGrid.ItemsSource = _fetchJobs;
        FetchBondBox.IsChecked = _settings.FetchBond;
        _fetchJobs.CollectionChanged += (_, _) => PaintFetch();

        foreach (var entry in _settings.FetchJobs)
        {
            var job = entry.State.Length > 0 ? FetchJob.Load(entry.State) : null;
            if (job is null && entry.State.Length == 0 && entry.Url.Length > 0)
            {
                job = new FetchJob { Url = entry.Url, Folder = entry.Folder };
                job.SetState(FetchState.Paused);
            }
            if (job is not null) _fetchJobs.Add(job);
        }
        PaintFetch();
    }

    private string FetchFolder() =>
        _settings.FetchFolder.Length > 0 && Directory.Exists(_settings.FetchFolder) ? _settings.FetchFolder : Organizer.DownloadsFolder();

    private void SaveFetchSettings()
    {
        if (FetchConnBox.SelectedItem is int conns) _settings.FetchConnections = conns;
        if (FetchAtOnceBox.SelectedItem is int atOnce) _settings.FetchAtOnce = atOnce;
        _settings.FetchJobs = _fetchJobs
            .Where(j => j.State != FetchState.Done && !_fetchRemoved.Contains(j))
            .Select(j => j.Probed ? new FetchEntry(j.StatePath, "", "") : new FetchEntry("", j.Url, j.Folder))
            .ToList();
    }

    private void RefreshFetchUi()
    {
        // From a fresh start the Bond page may never have been opened, so the link list is still empty.
        if (_bondRows.Count == 0) RefreshBondUi();
        FetchLinks.ItemsSource = _bondRows;
        FetchPath.Text = FetchFolder();
        FetchPath.ToolTip = FetchPath.Text;
        if (FetchUrlBox.Text.Length == 0)
        {
            try
            {
                var clip = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "";
                if (ParseLinks(clip).Count > 0 && !clip.Contains('\n')) FetchUrlBox.Text = clip;
            }
            catch { }
        }
        PaintFetch();
    }

    private static List<string> ParseLinks(string text) => text
        .Split(['\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
        .Where(t => Uri.TryCreate(t, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
        .Distinct()
        .ToList();

    private void PaintFetch()
    {
        var bond = _settings.FetchBond;
        var picked = _bondRows.Count(l => l.Selected);
        FetchBondHint.Text = !bond ? "Off. Files use Windows' normal connection."
            : _bondRows.Count == 0 ? "No internet connection found. Press Rescan."
            : _bondRows.Count == 1 ? "Only one connection found. Plug in your phone with USB tethering or join a hotspot, then press Rescan."
            : picked == 0 ? "Tick at least one connection below."
            : picked == 1 ? "Only one ticked. Tick another to combine them."
            : $"Each file downloads over {picked} connections at once.";
        FetchLinks.IsEnabled = bond;
        FetchLinks.Opacity = bond ? 1 : 0.45;
        FetchHint.Text = bond && picked > 1 ? $"Combining {picked} connections." : "Using one connection.";
        FetchEmpty.Visibility = _fetchJobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var running = _fetchJobs.Count(j => j.Running);
        var queued = _fetchJobs.Count(j => j.State == FetchState.Queued);
        var done = _fetchJobs.Count(j => j.State == FetchState.Done);
        var stopped = _fetchJobs.Count - running - queued - done;
        var parts = new[] { (running, "downloading"), (queued, "queued"), (stopped, "stopped"), (done, "done") }
            .Where(p => p.Item1 > 0).Select(p => $"{p.Item1} {p.Item2}");
        FetchSummary.Text = _fetchJobs.Count == 0 ? "Nothing yet." : string.Join(" · ", parts);
    }

    private void PaintFetchSpeed()
    {
        var running = _fetchJobs.Where(j => j.Running).ToList();
        foreach (var job in running) job.Sample(_pollTimer.Interval.TotalSeconds);
        // The bonder samples link speeds itself while it runs; sampling twice would halve them.
        if (!_bonder.Running) foreach (var link in _bondRows) link.Sample(_pollTimer.Interval.TotalSeconds);
        if (++_fetchTicks % 5 == 0) foreach (var job in running) job.Save();
        var total = running.Sum(j => j.Bps);
        FetchHeadline.Text = running.Count == 0 ? "Idle" : Format.Speed(total);
        FetchHeadline.Foreground = Brush(running.Count == 0 ? "Muted" : "Green");
        if (running.Count > 0) PaintFetch();
    }

    private void FetchBond_Click(object sender, RoutedEventArgs e)
    {
        _settings.FetchBond = FetchBondBox.IsChecked == true;
        _settings.Save();
        PaintFetch();
    }

    private void FetchLink_Click(object sender, RoutedEventArgs e)
    {
        SaveBondLinks();
        _settings.Save();
        PaintFetch();
    }

    private void FetchRescan_Click(object sender, RoutedEventArgs e)
    {
        if (_bonder.Running)
        {
            FetchBusy.Text = "Stop bonding on the Bond page before rescanning connections.";
            return;
        }
        RefreshBondUi();
        RefreshFetchUi();
    }

    private void FetchAdd_Click(object sender, RoutedEventArgs e)
    {
        var urls = ParseLinks(FetchUrlBox.Text);
        if (urls.Count == 0)
        {
            FetchBusy.Text = "Paste an http:// or https:// link.";
            return;
        }
        if (_bondRows.Count == 0) RefreshBondUi();
        var folder = FetchFolder();
        foreach (var url in urls) _fetchJobs.Add(new FetchJob { Url = url, Folder = folder });
        FetchUrlBox.Clear();
        FetchBusy.Text = urls.Count == 1 ? "Added 1 download." : $"Added {urls.Count} downloads.";
        PumpFetch();
    }

    private void PumpFetch()
    {
        var limit = FetchAtOnceBox.SelectedItem as int? ?? _settings.FetchAtOnce;
        foreach (var job in _fetchJobs.Where(j => j.State == FetchState.Queued).ToList())
        {
            if (_fetchRuns.Count >= limit) break;
            _ = RunFetch(job);
        }
        SaveFetchSettings();
        _settings.Save();
        PaintFetch();
    }

    private async Task RunFetch(FetchJob job)
    {
        var cancel = new CancellationTokenSource();
        _fetchRuns[job] = cancel;
        try { await FetchEngine.Run(job, FetchConnBox.SelectedItem as int? ?? _settings.FetchConnections, cancel.Token); }
        finally
        {
            _fetchRuns.Remove(job);
            cancel.Dispose();
        }
        if (_fetchRemoved.Remove(job)) job.DeleteLeftovers();
        else if (job.State == FetchState.Done) StatusText.Text = $"Downloaded {job.FileName}.";
        else if (job.State == FetchState.Failed) StatusText.Text = $"{job.Name}: {job.Error}";
        PumpFetch();
    }

    private List<FetchJob> SelectedFetchJobs() => FetchGrid.SelectedItems.OfType<FetchJob>().ToList();

    private void FetchPause_Click(object sender, RoutedEventArgs e)
    {
        foreach (var job in SelectedFetchJobs())
        {
            if (_fetchRuns.TryGetValue(job, out var run)) run.Cancel();
            else if (job.State == FetchState.Queued) job.SetState(FetchState.Paused);
        }
        PumpFetch();
    }

    private void FetchResume_Click(object sender, RoutedEventArgs e)
    {
        var picked = SelectedFetchJobs();
        // Nothing selected: resume everything that is waiting.
        foreach (var job in picked.Count > 0 ? picked : _fetchJobs.ToList())
            if (job.CanResume) job.SetState(FetchState.Queued);
        PumpFetch();
    }

    private void FetchRemove_Click(object sender, RoutedEventArgs e)
    {
        var picked = SelectedFetchJobs();
        if (picked.Count == 0)
        {
            // Nothing selected: just tidy up the finished ones.
            picked = _fetchJobs.Where(j => j.State == FetchState.Done).ToList();
            if (picked.Count == 0) return;
        }
        var unfinished = picked.Count(j => j.State != FetchState.Done);
        if (unfinished > 0 && MessageBox.Show(
                $"Remove {picked.Count} download(s)?\n\n{unfinished} unfinished file(s) will be deleted. Finished files stay on disk.",
                "Remove downloads", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;
        foreach (var job in picked)
        {
            _fetchJobs.Remove(job);
            if (_fetchRuns.TryGetValue(job, out var run))
            {
                // RunFetch deletes the leftovers once the workers have let go of the file.
                _fetchRemoved.Add(job);
                run.Cancel();
            }
            else job.DeleteLeftovers();
        }
        PumpFetch();
    }

    private void FetchOpen_Click(object sender, RoutedEventArgs e)
    {
        var done = SelectedFetchJobs().FirstOrDefault(j => j.State == FetchState.Done && File.Exists(j.FinalPath));
        if (done is not null)
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{done.FinalPath}\"") { UseShellExecute = true });
        else
            SteamClient.OpenFolder(SelectedFetchJobs().FirstOrDefault()?.Folder ?? FetchFolder());
    }

    private void FetchGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (FetchGrid.SelectedItem is not FetchJob job) return;
        if (job.State == FetchState.Done && File.Exists(job.FinalPath))
            Process.Start(new ProcessStartInfo(job.FinalPath) { UseShellExecute = true });
        else if (job.CanResume)
        {
            job.SetState(FetchState.Queued);
            PumpFetch();
        }
    }

    private void FetchFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = FetchFolder(), Title = "Choose where downloads are saved" };
        if (dialog.ShowDialog(this) != true) return;
        // Picking the Downloads folder again goes back to following it automatically.
        _settings.FetchFolder = string.Equals(dialog.FolderName, Organizer.DownloadsFolder(), StringComparison.OrdinalIgnoreCase)
            ? "" : dialog.FolderName;
        _settings.Save();
        FetchPath.Text = FetchFolder();
        FetchPath.ToolTip = FetchPath.Text;
    }

    /// <summary>On exit: stop the workers and keep each file's progress so it resumes next time.</summary>
    private void StopFetchJobs()
    {
        foreach (var run in _fetchRuns.Values) run.Cancel();
        foreach (var job in _fetchJobs.Where(j => j.Running)) job.Save();
        SaveFetchSettings();
    }
}
