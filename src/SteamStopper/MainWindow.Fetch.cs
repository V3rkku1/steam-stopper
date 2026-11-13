using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;
using SteamStopper.Core;

namespace SteamStopper;

// Fetch page: segmented downloads spread over the links ticked on the Bond page.
public partial class MainWindow
{
    private static readonly int[] FetchConnChoices = [1, 2, 4, 8, 16, 32];
    private static readonly int[] FetchAtOnceChoices = [1, 2, 3, 4, 6];
    private static readonly Dictionary<int, string> FetchLimits = new()
    {
        [0] = "No limit", [512] = "500 KB/s", [1024] = "1 MB/s", [2048] = "2 MB/s", [5120] = "5 MB/s",
        [10240] = "10 MB/s", [25600] = "25 MB/s", [51200] = "50 MB/s"
    };
    private static readonly Dictionary<string, string> FetchAfterActions = new()
    {
        ["shutdown"] = "Shut down PC", ["sleep"] = "Sleep", ["hibernate"] = "Hibernate", ["restart"] = "Restart PC", ["notify"] = "Just tell me"
    };
    // Downloads the "when all finish" action waits for: the ones running or queued since it was switched on.
    private readonly HashSet<FetchJob> _fetchAfterJobs = [];
    private int _fetchAfterRun;
    private readonly ObservableCollection<FetchJob> _fetchJobs = [];
    private readonly Dictionary<FetchJob, CancellationTokenSource> _fetchRuns = [];
    private readonly HashSet<FetchJob> _fetchRemoved = [];
    private Fetcher? _fetcher;
    private int _fetchTicks;

    // Reads _bondRows from worker threads; the UI only ever swaps the whole list.
    private Fetcher FetchEngine => _fetcher ??= new Fetcher(() => _settings.FetchBond ? _bondRows : []);

    private void InitFetch()
    {
        // "Auto" (stored as 0) adds connections while they make the download faster.
        FetchConnBox.Items.Add("Auto");
        foreach (var n in FetchConnChoices) FetchConnBox.Items.Add(n);
        foreach (var n in FetchAtOnceChoices) FetchAtOnceBox.Items.Add(n);
        FetchConnBox.SelectedItem = FetchConnChoices.Contains(_settings.FetchConnections) ? _settings.FetchConnections : "Auto";
        FetchAtOnceBox.SelectedItem = FetchAtOnceChoices.Contains(_settings.FetchAtOnce) ? _settings.FetchAtOnce : 2;
        FetchAtOnceBox.SelectionChanged += (_, _) => { SaveFetchSettings(); PumpFetch(); };
        FetchConnBox.SelectionChanged += (_, _) => SaveFetchSettings();
        FetchGrid.ItemsSource = _fetchJobs;
        FetchGrid.PreviewMouseRightButtonDown += FetchGrid_RightDown;
        // Plain labels: the app's ComboBox template shows the item itself, not a DisplayMemberPath.
        FetchLimitBox.ItemsSource = FetchLimits.Values.ToList();
        FetchLimitBox.SelectedItem = FetchLimits.GetValueOrDefault(_settings.FetchLimitKbps, "No limit");
        FetchEngine.LimitBps = FetchLimits.ContainsKey(_settings.FetchLimitKbps) ? _settings.FetchLimitKbps * 1024L : 0;
        FetchLimitBox.SelectionChanged += (_, _) =>
        {
            _settings.FetchLimitKbps = FetchLimits.FirstOrDefault(p => p.Value == FetchLimitBox.SelectedItem as string).Key;
            FetchEngine.LimitBps = _settings.FetchLimitKbps * 1024L;
            _settings.Save();
        };
        FetchAfterActionBox.ItemsSource = FetchAfterActions.Values.ToList();
        FetchAfterActionBox.SelectedItem = FetchAfterActions.GetValueOrDefault(_settings.FetchAfterAction, "Shut down PC");
        FetchAfterActionBox.SelectionChanged += (_, _) =>
        {
            _settings.FetchAfterAction = FetchAfterKey();
            _settings.Save();
            PaintFetchAfter();
        };
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
        _settings.FetchConnections = FetchConnBox.SelectedItem is int conns ? conns : 0;
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
        if (!_bonder.Running)
        {
            foreach (var link in _bondRows) link.Sample(_pollTimer.Interval.TotalSeconds);
            BondLink.Rebalance(_bondRows);
        }
        if (++_fetchTicks % 5 == 0) foreach (var job in running) job.Save();
        var total = running.Sum(j => j.Bps);
        FetchHeadline.Text = running.Count == 0 ? "Idle" : Format.Speed(total);
        FetchHeadline.Foreground = Brush(running.Count == 0 ? "Muted" : "Green");
        if (running.Count > 0)
        {
            PaintFetch();
            // Whole-queue time left: everything not done yet, at today's combined speed.
            var left = _fetchJobs.Where(j => j.State != FetchState.Done && j.Total > 0 && (j.Running || j.State == FetchState.Queued))
                .Sum(j => j.Total - j.Received);
            if (total > 1024 && left > 0) FetchHint.Text += $"  {Format.Bytes(left)} left, about {Format.Duration(left / total)}.";
            if (_settings.FetchLimitKbps > 0) FetchHint.Text += $"  Limited to {FetchLimits.GetValueOrDefault(_settings.FetchLimitKbps)}.";
        }
        // Progress on the taskbar button, like a browser download.
        TaskbarItemInfo ??= new TaskbarItemInfo();
        var sized = running.Where(j => j.Total > 0).ToList();
        TaskbarItemInfo.ProgressState = running.Count == 0 ? TaskbarItemProgressState.None
            : sized.Count == 0 ? TaskbarItemProgressState.Indeterminate : TaskbarItemProgressState.Normal;
        if (sized.Count > 0) TaskbarItemInfo.ProgressValue = sized.Sum(j => j.Received) / (double)sized.Sum(j => j.Total);
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
        AddFetchLinks(urls);
        FetchUrlBox.Clear();
    }

    private void AddFetchLinks(List<string> urls)
    {
        if (_bondRows.Count == 0) RefreshBondUi();
        var folder = FetchFolder();
        foreach (var url in urls) _fetchJobs.Add(new FetchJob { Url = url, Folder = folder });
        FetchBusy.Text = urls.Count == 1 ? "Added 1 download." : $"Added {urls.Count} downloads.";
        PumpFetch();
    }

    // Links dragged from a browser's address bar or a page arrive as text.
    private void Fetch_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.UnicodeText) is string text && ParseLinks(text) is { Count: > 0 } urls)
            AddFetchLinks(urls);
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
        CheckFetchAfter();
    }

    private async Task RunFetch(FetchJob job)
    {
        var cancel = new CancellationTokenSource();
        _fetchRuns[job] = cancel;
        try { await FetchEngine.Run(job, FetchConnBox.SelectedItem is int conns ? conns : 0, cancel.Token); }
        finally
        {
            _fetchRuns.Remove(job);
            cancel.Dispose();
        }
        if (_fetchRemoved.Remove(job)) job.DeleteLeftovers();
        else if (job.State == FetchState.Done) { _fetchRetries.Remove(job); StatusText.Text = $"Downloaded {job.FileName}."; }
        else if (job.State == FetchState.Failed) { StatusText.Text = $"{job.Name}: {job.Error}"; RetryFetch(job); }
        PumpFetch();
    }

    // Breaks that Resume fixes (dropped connections, server hiccups) are retried by themselves.
    private readonly Dictionary<FetchJob, (int Tries, long Received, int Id)> _fetchRetries = [];
    private int _fetchRetryId;
    private const int FetchRetries = 10;

    private async void RetryFetch(FetchJob job)
    {
        var (tries, seen, _) = _fetchRetries.GetValueOrDefault(job);
        if (job.Received > seen) tries = 0; // it got further this time, so it isn't stuck
        if (tries >= FetchRetries)
        {
            _fetchRetries.Remove(job);
            StatusText.Text = $"{job.Name}: gave up after {FetchRetries} tries. {job.Error} Resume tries again.";
            return;
        }
        var id = ++_fetchRetryId;
        _fetchRetries[job] = (tries + 1, job.Received, id);
        var wait = Math.Min(60, 5 << tries); // 5, 10, 20, 40, then every 60 s
        var note = $"trying again in {wait} s ({tries + 1} of {FetchRetries})";
        StatusText.Text = $"{job.Name}: {job.Error} Trying again in {wait} s.";
        job.SetState(FetchState.Failed, $"{note} · {job.Error}"); // the note first, so a narrow row still shows it
        await Task.Delay(TimeSpan.FromSeconds(wait));
        // Paused, resumed, removed or failed again meanwhile: that newer state wins.
        if (job.State != FetchState.Failed || !_fetchJobs.Contains(job) || _fetchRetries.GetValueOrDefault(job).Id != id) return;
        job.SetState(FetchState.Queued);
        PumpFetch();
    }

    private void FetchAfter_Click(object sender, RoutedEventArgs e)
    {
        _fetchAfterJobs.Clear();
        _fetchAfterRun++;
        FetchAfterCountdown.Visibility = Visibility.Collapsed;
        CheckFetchAfter();
    }

    private void FetchAfterCancel_Click(object sender, RoutedEventArgs e)
    {
        FetchAfterBox.IsChecked = false;
        FetchAfter_Click(sender, e);
        StatusText.Text = "Cancelled. Nothing will happen when the downloads finish.";
    }

    private string FetchAfterKey() =>
        FetchAfterActions.FirstOrDefault(p => p.Value == FetchAfterActionBox.SelectedItem as string).Key ?? "shutdown";

    private void PaintFetchAfter()
    {
        var action = FetchAfterActions[FetchAfterKey()];
        var waiting = _fetchAfterJobs.Count(j => j.State is not (FetchState.Done or FetchState.Failed));
        FetchAfterHint.Text = FetchAfterBox.IsChecked != true ? "Off. Turns itself off again after it runs."
            : _fetchAfterJobs.Count == 0 ? $"{action} once a download has run and all are done."
            : waiting == 0 ? "All downloads finished."
            : $"{action} after {waiting} more download{(waiting == 1 ? "" : "s")}. Paused ones are waited for.";
    }

    /// <summary>Start the countdown once every download the action is waiting for has finished or failed.</summary>
    private void CheckFetchAfter()
    {
        if (FetchAfterBox.IsChecked != true)
        {
            PaintFetchAfter();
            return;
        }
        foreach (var job in _fetchJobs.Where(j => j.Running || j.State == FetchState.Queued)) _fetchAfterJobs.Add(job);
        _fetchAfterJobs.RemoveWhere(j => !_fetchJobs.Contains(j));
        PaintFetchAfter();
        if (FetchAfterCountdown.Visibility == Visibility.Visible || _fetchAfterJobs.Count == 0
            || _fetchAfterJobs.Any(j => j.State is not (FetchState.Done or FetchState.Failed))
            || !_fetchAfterJobs.Any(j => j.State == FetchState.Done))
            return;
        _ = FetchAfterCountdownAsync(++_fetchAfterRun);
    }

    private async Task FetchAfterCountdownAsync(int run)
    {
        var key = FetchAfterKey();
        var failed = _fetchAfterJobs.Count(j => j.State == FetchState.Failed);
        var note = failed > 0 ? $" ({failed} failed)" : "";
        if (key != "notify")
        {
            FetchAfterCountdown.Visibility = Visibility.Visible;
            Activate();
            for (var left = 60; left > 0; left--)
            {
                if (run != _fetchAfterRun) return;
                FetchAfterCountdownText.Text = $"Downloads finished{note}. {FetchAfterActions[key]} in {left} s.";
                await Task.Delay(1000);
            }
            if (run != _fetchAfterRun) return;
        }
        FetchAfterBox.IsChecked = false;
        FetchAfterCountdown.Visibility = Visibility.Collapsed;
        _fetchAfterJobs.Clear();
        PaintFetchAfter();
        var root = _steamRoot;
        var message = await Task.Run(() => Power.Run(key, root));
        StatusText.Text = message + note;
        if (key == "notify") Dialog.Show(this, "All downloads finished" + note + ".", "Steam Multitool");
    }

    private void FetchGrid_RightDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Right-clicking a row that isn't selected acts on that row, like Explorer.
        for (var d = e.OriginalSource as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is not DataGridRow row) continue;
            if (!row.IsSelected)
            {
                FetchGrid.SelectedItems.Clear();
                row.IsSelected = true;
            }
            return;
        }
    }

    private void FetchMenu_Opened(object sender, RoutedEventArgs e)
    {
        var picked = SelectedFetchJobs();
        // Found by header: names inside a ContextMenu aren't wired to fields.
        foreach (var item in ((ContextMenu)sender).Items.OfType<MenuItem>())
            item.IsEnabled = item.Header switch
            {
                "Open" => picked.Any(j => j.State == FetchState.Done && File.Exists(j.FinalPath)),
                "Resume" => picked.Count == 0 ? _fetchJobs.Any(j => j.CanResume) : picked.Any(j => j.CanResume),
                "Pause" => picked.Any(j => j.Running || j.State == FetchState.Queued),
                "Continue with a new link" => picked is [{ CanResume: true }],
                "Remove" or "Show in folder" => true,
                _ => picked.Count > 0
            };
    }

    private void FetchMenuOpen_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFetchJobs().FirstOrDefault(j => j.State == FetchState.Done && File.Exists(j.FinalPath)) is { } job)
            Process.Start(new ProcessStartInfo(job.FinalPath) { UseShellExecute = true })?.Dispose();
    }

    /// <summary>Swap an expired or broken link for a fresh one to the same file, keeping what's downloaded.</summary>
    private async void FetchRelink_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFetchJobs() is not [{ CanResume: true } job]) return;
        var typed = ParseLinks(FetchUrlBox.Text);
        var url = typed.Count == 1 ? typed[0] : "";
        if (url.Length == 0)
            try { url = Clipboard.ContainsText() ? ParseLinks(Clipboard.GetText()).FirstOrDefault() ?? "" : ""; }
            catch { } // another app holding the clipboard
        if (url.Length == 0)
        {
            FetchBusy.Text = "Copy the new link (or paste it in the Links box) first, then choose Continue with a new link.";
            return;
        }
        FetchBusy.Text = "Checking the new link…";
        var error = await FetchEngine.Relink(job, url, CancellationToken.None);
        if (error is not null)
        {
            FetchBusy.Text = error;
            return;
        }
        if (typed.Count == 1) FetchUrlBox.Clear();
        FetchBusy.Text = $"{job.Name} continues from the new link.";
        job.SetState(FetchState.Queued);
        PumpFetch();
    }

    private void FetchCopyLink_Click(object sender, RoutedEventArgs e)
    {
        var links = SelectedFetchJobs().Select(j => j.Url).ToList();
        if (links.Count == 0) return;
        try { Clipboard.SetText(string.Join(Environment.NewLine, links)); }
        catch { return; } // another app holding the clipboard
        StatusText.Text = links.Count == 1 ? "Link copied." : $"{links.Count} links copied.";
    }

    /// <summary>Move the selected files to the top of the queue; PumpFetch starts queued files in list order.</summary>
    private void FetchTop_Click(object sender, RoutedEventArgs e)
    {
        var picked = SelectedFetchJobs().OrderBy(_fetchJobs.IndexOf).ToList();
        for (var i = 0; i < picked.Count; i++)
        {
            _fetchJobs.Move(_fetchJobs.IndexOf(picked[i]), i);
            if (picked[i].CanResume) picked[i].SetState(FetchState.Queued);
        }
        PumpFetch();
    }

    private List<FetchJob> SelectedFetchJobs() => FetchGrid.SelectedItems.OfType<FetchJob>().ToList();

    private void FetchPause_Click(object sender, RoutedEventArgs e)
    {
        foreach (var job in SelectedFetchJobs())
        {
            if (_fetchRuns.TryGetValue(job, out var run)) run.Cancel();
            // A failed one waiting to try again stops waiting too.
            else if (job.State is FetchState.Queued or FetchState.Failed) job.SetState(FetchState.Paused);
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
        if (unfinished > 0 && Dialog.Show(
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
