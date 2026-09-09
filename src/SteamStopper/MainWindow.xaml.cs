using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SteamStopper.Core;

namespace SteamStopper;

public partial class MainWindow : Window
{
    private static readonly Dictionary<string, int> SecondsMap = new()
    {
        ["Immediately"] = 0,
        ["15 seconds"] = 15,
        ["30 seconds"] = 30,
        ["45 seconds"] = 45,
        ["60 seconds"] = 60,
        ["2 minutes"] = 120,
        ["5 minutes"] = 300
    };

    private static readonly (string Name, string[] Parts)[] CacheTargets =
    [
        ("Downloading", ["steamapps", "downloading"]),
        ("Temp", ["steamapps", "temp"]),
        ("HTML cache", ["config", "htmlcache"]),
        ("Logs", ["logs"]),
        ("Dumps", ["dumps"])
    ];

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly string? _steamRoot = SteamClient.FindRoot();
    private readonly DownloadWatcher? _watcher;
    private readonly SpeedWatch _speedWatch = new();
    private readonly TrafficSampler _traffic = new();
    private readonly NicMonitor _nic = new();
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, UIElement> _pages;
    private List<GameEntry> _games = [];
    private List<DnsProvider> _dnsRows = [];
    private List<CleanerTarget> _cleanRows = [];
    private List<AppOffer> _appRows = [];
    private string _appCategory = "All";
    private bool _dnsWorking;
    private bool _toolsWorking;
    private DownloadSnapshot? _lastSnapshot;
    private bool _armed;
    private int? _countdown;
    private bool _suppress;
    private string _finishReason = "Downloads finished";

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        StateChanged += (_, _) => UpdateCaptionButtons();
        _pages = new Dictionary<string, UIElement>
        {
            ["control"] = PageControl,
            ["downloads"] = PageDownloads,
            ["library"] = PageLibrary,
            ["dns"] = PageDns,
            ["clean"] = PageClean,
            ["apps"] = PageApps
        };

        if (_steamRoot is not null)
            _watcher = new DownloadWatcher(_steamRoot);

        PagePath.Text = _steamRoot ?? "Steam install not found";
        PageTitle.Text = "Steam Stopper  " + Updater.Version;
        Loaded += (_, _) => _ = CheckForAppUpdates();
        FillComboBoxes();
        FillAppCategories();
        LoadUiFromSettings();
        RefreshAdmin();
        RefreshFirewallAsync();
        RefreshProcesses();
        RescanLibrary();

        _pollTimer.Tick += (_, _) => PollWatch();
        _pollTimer.Start();
        Closed += (_, _) => { PersistSettings(); _pollTimer.Stop(); };
    }

    private async Task CheckForAppUpdates()
    {
        try
        {
            var (message, script) = await Updater.CheckAsync(text => Dispatcher.Invoke(() => StatusText.Text = text));
            StatusText.Text = message;
            if (string.IsNullOrWhiteSpace(script)) return;
            Process.Start(new ProcessStartInfo(script) { UseShellExecute = true });
            Close();
        }
        catch { }
    }

    private void FillComboBoxes()
    {
        foreach (var (_, label) in Power.Actions) ActionBox.Items.Add(label);
        foreach (var (_, label, _) in Traffic.Apps) WatchBox.Items.Add(label);
        foreach (var (label, _) in Traffic.Floors) FloorBox.Items.Add(label);
        foreach (var label in new[] { "Immediately", "15 seconds", "30 seconds", "60 seconds", "2 minutes", "5 minutes" })
            CountdownBox.Items.Add(label);
        foreach (var label in new[] { "15 seconds", "30 seconds", "45 seconds", "60 seconds", "2 minutes" })
            GraceBox.Items.Add(label);
    }

    private void LoadUiFromSettings()
    {
        _suppress = true;
        ActionBox.SelectedItem = Power.Actions.FirstOrDefault(a => a.Key == _settings.AutoAction).Label
            ?? Power.Actions.Last().Label;
        WatchBox.SelectedItem = Traffic.Apps.FirstOrDefault(a => a.Key == _settings.WatchTarget).Label
            ?? Traffic.Apps[0].Label;
        FloorBox.SelectedItem = Traffic.Floors.FirstOrDefault(f => (int)(f.Bps / 1024) == _settings.SpeedFloorKbps).Label
            ?? "200 KB/s";
        CountdownBox.SelectedItem = SecondsMap.FirstOrDefault(p => p.Value == _settings.CountdownSeconds).Key ?? "60 seconds";
        GraceBox.SelectedItem = SecondsMap.FirstOrDefault(p => p.Value == _settings.GraceSeconds).Key ?? "45 seconds";
        QueuedBox.IsChecked = _settings.IncludeQueued;
        SeenBox.IsChecked = _settings.RequireActivityFirst;
        GameExeBox.IsChecked = _settings.BlockGameExes;
        KillAfterBox.IsChecked = _settings.KillAfterBlock;
        RestartAfterBox.IsChecked = _settings.RestartAfterUnblock;
        ApplyWatcherSettings();
        _suppress = false;
        ActionBox.SelectionChanged += WatchSettings_Changed;
        WatchBox.SelectionChanged += WatchSettings_Changed;
        FloorBox.SelectionChanged += WatchSettings_Changed;
        CountdownBox.SelectionChanged += WatchSettings_Changed;
        GraceBox.SelectionChanged += WatchSettings_Changed;
        PaintWatchAction();
    }

    private void TitleDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2)
        {
            Max_Click(sender, e);
            return;
        }
        try { DragMove(); } catch { /* ignore */ }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Max_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void UpdateCaptionButtons()
    {
        MaxIcon.Visibility = WindowState == WindowState.Maximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreIcon.Visibility = WindowState == WindowState.Maximized ? Visibility.Visible : Visibility.Collapsed;
        MaxButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
        BorderThickness = WindowState == WindowState.Maximized ? new Thickness(8) : new Thickness(1);
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string key }) return;
        foreach (var (name, page) in _pages)
            page.Visibility = name == key ? Visibility.Visible : Visibility.Collapsed;
        if (key == "control") RefreshProcesses();
        if (key == "dns") RefreshDnsUi();
        if (key == "clean") _ = ScanCleanerAsync();
        if (key == "apps") RefreshApps();
        if (key == "downloads")
        {
            try
            {
                PaintWatchAction();
                if (_lastSnapshot is not null) PaintDownloads(_lastSnapshot, _speedWatch.LastBps);
                else PaintSpeed(_speedWatch.LastBps);
            }
            catch (Exception ex)
            {
                StatusText.Text = "Downloads view: " + ex.Message;
            }
        }
    }

    private void OpenDownloads_Click(object sender, RoutedEventArgs e)
    {
        NavDownloads.IsChecked = true;
        Nav_Click(NavDownloads, e);
    }

    private void BlockSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        var allow = BlockSwitch.IsChecked == true;
        _suppress = true;
        BlockSwitch.IsChecked = !allow;
        _suppress = false;
        if (allow) Unblock_Click(sender, e);
        else Block_Click(sender, e);
    }

    private void PollWatch()
    {
        try
        {
            DownloadSnapshot? snapshot = null;
            if (_watcher is not null)
            {
                try { snapshot = _watcher.Poll(); }
                catch { }
            }

            var bps = SampleWatchSpeed(snapshot);
            var speedDone = _speedWatch.IsFinished(bps);
            var queueDone = WatchTargetKey() == "steam" && snapshot is { Finished: true };
            if (snapshot is not null)
                ApplySnapshot(snapshot, bps);
            else
                ApplySpeedOnly(bps);

            if (_armed && _countdown is null && (speedDone || queueDone))
            {
                var floor = FloorBox.SelectedItem?.ToString() ?? "200 KB/s";
                _finishReason = speedDone
                    ? $"{WatchLabel()} stayed under {floor}"
                    : "Steam download queue emptied";
                BeginCountdown();
            }
        }
        catch { }
    }

    private double SampleWatchSpeed(DownloadSnapshot? snapshot)
    {
        var nic = _nic.ReceiveBps();
        var key = WatchTargetKey();
        if (key == "pc")
            return nic;

        var names = Traffic.Apps.FirstOrDefault(a => a.Key == key).Processes;
        var io = names is { Length: > 0 } ? _traffic.Bps("io-" + key, Traffic.ProcessIoBytes(names)) : 0;
        var acf = key == "steam" ? snapshot?.SpeedBps ?? 0 : 0;
        return Max(nic, io, acf);
    }

    private static double Max(params double[] values)
    {
        double m = 0;
        foreach (var v in values)
            if (v > m) m = v;
        return m;
    }

    private void ApplySpeedOnly(double bps)
    {
        TileDownloads.Text = bps >= _speedWatch.ThresholdBps ? Format.Speed(bps) : "idle";
        DownloadChip.Text = $"watch: {Format.Speed(bps)}";
        DownloadChip.Foreground = Brush(bps >= _speedWatch.ThresholdBps ? "Accent" : "Muted");
        UpdateAutoSummary(null, bps);
        if (PageDownloads.Visibility == Visibility.Visible)
            PaintSpeed(bps);
    }

    private void ApplySnapshot(DownloadSnapshot snapshot, double watchBps)
    {
        _lastSnapshot = snapshot;
        var active = snapshot.ActiveCount;
        TileDownloads.Text = active > 0 ? active.ToString() : snapshot.Jobs.Count > 0 ? "queued" : Format.Speed(watchBps);
        DownloadChip.Text = $"watch: {Format.Speed(watchBps)}";
        DownloadChip.Foreground = Brush(watchBps >= _speedWatch.ThresholdBps ? "Accent" : "Muted");
        UpdateAutoSummary(snapshot, watchBps);
        if (PageDownloads.Visibility == Visibility.Visible)
            PaintDownloads(snapshot, watchBps);
    }

    private void PaintDownloads(DownloadSnapshot snapshot, double watchBps)
    {
        var active = snapshot.ActiveCount;
        var watching = WatchLabel();
        if (WatchTargetKey() != "steam")
        {
            DlHeadline.Text = $"Watching {watching}";
            DlState.Text = watchBps >= _speedWatch.ThresholdBps ? "downloading" : "under limit";
            DlState.Foreground = Brush(watchBps >= _speedWatch.ThresholdBps ? "Accent" : "Amber");
        }
        else if (snapshot.Jobs.Count == 0)
        {
            DlHeadline.Text = "No Steam downloads pending";
            DlState.Text = watchBps >= _speedWatch.ThresholdBps ? "traffic" : "idle";
            DlState.Foreground = Brush("Muted");
        }
        else if (active > 0)
        {
            DlHeadline.Text = $"Downloading {active} item(s)";
            DlState.Text = "active";
            DlState.Foreground = Brush("Accent");
        }
        else
        {
            DlHeadline.Text = $"{snapshot.Jobs.Count} item(s) waiting";
            DlState.Text = "queued";
            DlState.Foreground = Brush("Amber");
        }
        DlBar.Value = snapshot.Percent;
        TileSpeed.Text = Format.Speed(watchBps);
        TileRemaining.Text = snapshot.Remaining > 0 ? Format.Bytes(snapshot.Remaining) : "0 B";
        TileEta.Text = Format.Duration(snapshot.EtaSeconds);
        TileJobs.Text = snapshot.Jobs.Count.ToString();
        JobsGrid.ItemsSource = snapshot.Jobs;
        JobsEmpty.Text = "Nothing in the Steam download queue.";
        JobsEmpty.Visibility = snapshot.Jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PaintWatchAction();
    }

    private void PaintSpeed(double bps)
    {
        var watching = WatchLabel();
        DlHeadline.Text = $"Watching {watching}";
        DlState.Text = bps >= _speedWatch.ThresholdBps ? "downloading" : "under limit";
        DlState.Foreground = Brush(bps >= _speedWatch.ThresholdBps ? "Accent" : "Amber");
        TileSpeed.Text = Format.Speed(bps);
        TileRemaining.Text = "--";
        TileEta.Text = "--";
        TileJobs.Text = "--";
        JobsEmpty.Text = $"No Steam queue. Measuring {watching} traffic.";
        JobsEmpty.Visibility = Visibility.Visible;
        PaintWatchAction();
    }

    private void UpdateAutoSummary(DownloadSnapshot? snapshot, double watchBps)
    {
        var action = ActionBox.SelectedItem?.ToString() ?? "Shut down PC";
        var floor = FloorBox.SelectedItem?.ToString() ?? "200 KB/s";
        if (!_armed)
        {
            DashAuto.Text = $"Not armed. After {WatchLabel()} stays under {floor}: {action}.";
            DashAuto.Foreground = Brush("Muted");
            ArmHint.Text = "Not watching";
            ArmHint.Foreground = Brush("Muted");
            return;
        }
        if (_countdown is not null)
        {
            DashAuto.Text = $"{action} in {_countdown}s.";
            DashAuto.Foreground = Brush("Amber");
            ArmHint.Text = $"Firing in {_countdown}s";
            ArmHint.Foreground = Brush("Amber");
            return;
        }
        if (watchBps >= _speedWatch.ThresholdBps)
        {
            DashAuto.Text = $"Armed. {WatchLabel()} at {Format.Speed(watchBps)}. Will {action.ToLowerInvariant()} after it stays under {floor}.";
            DashAuto.Foreground = Brush("Green");
            ArmHint.Text = "Download in progress";
            ArmHint.Foreground = Brush("Green");
            return;
        }
        if (_speedWatch.RequireBurst && !_speedWatch.SeenFast)
        {
            DashAuto.Text = $"Armed. Waiting for {WatchLabel()} to go above {floor} first.";
            DashAuto.Foreground = Brush("Muted");
            ArmHint.Text = "Waiting for a fast download";
            ArmHint.Foreground = Brush("Muted");
            return;
        }
        var remaining = Math.Max(0, (int)(_speedWatch.HoldSeconds - _speedWatch.BelowSeconds));
        DashAuto.Text = $"{WatchLabel()} is under {floor}. Confirming {remaining}s, then {action.ToLowerInvariant()}.";
        DashAuto.Foreground = Brush("Amber");
        ArmHint.Text = $"Under {floor} · {remaining}s";
        ArmHint.Foreground = Brush("Amber");
        _ = snapshot;
    }

    private void PaintWatchAction()
    {
        var action = ActionBox.SelectedItem?.ToString() ?? "Shut down PC";
        var wait = CountdownBox.SelectedItem?.ToString() ?? "60 seconds";
        var hold = GraceBox.SelectedItem?.ToString() ?? "45 seconds";
        var floor = FloorBox.SelectedItem?.ToString() ?? "200 KB/s";
        ActionPreview.Text = wait == "Immediately"
            ? $"{WatchLabel()} goes above {floor}, then stays under it for {hold}, then {action.ToLowerInvariant()}."
            : $"{WatchLabel()} goes above {floor}, stays under it for {hold}, warn {wait}, then {action.ToLowerInvariant()}.";
    }

    private async void Block_Click(object sender, RoutedEventArgs e)
    {
        if (!NeedAdmin() || _steamRoot is null) return;
        var includeGames = GameExeBox.IsChecked == true;
        var kill = KillAfterBox.IsChecked == true;
        StatusText.Text = "Adding firewall rules…";
        var count = await Task.Run(() =>
        {
            var n = Firewall.Block(_steamRoot, includeGames);
            if (kill) SteamClient.KillSteam();
            return n;
        });
        StatusText.Text = $"Blocked {count} Steam binary path(s).";
        RefreshFirewallAsync();
        RefreshProcesses();
    }

    private async void Unblock_Click(object sender, RoutedEventArgs e)
    {
        if (!NeedAdmin()) return;
        var restart = RestartAfterBox.IsChecked == true && _steamRoot is not null;
        var root = _steamRoot;
        StatusText.Text = restart ? "Restoring internet and restarting Steam…" : "Removing firewall rules…";
        var removed = await Task.Run(() =>
        {
            var n = Firewall.Unblock();
            if (restart && root is not null)
                SteamClient.Restart(root);
            return n;
        });
        StatusText.Text = restart
            ? $"Removed {removed} rule(s). Steam is restarting."
            : $"Removed {removed} firewall rule(s).";
        RefreshFirewallAsync();
        RefreshProcesses();
        if (restart)
            _ = RefreshProcessesLater();
    }

    private async Task RefreshProcessesLater()
    {
        await Task.Delay(2500);
        RefreshProcesses();
    }

    private void Kill_Click(object sender, RoutedEventArgs e)
    {
        var n = SteamClient.KillSteam();
        StatusText.Text = $"Kill signals sent for {n} process(es).";
        RefreshProcesses();
    }

    private void Launch_Click(object sender, RoutedEventArgs e) => Launch(false);
    private void LaunchOffline_Click(object sender, RoutedEventArgs e) => Launch(true);

    private void Launch(bool offline)
    {
        if (_steamRoot is null) { MessageBox.Show("Could not find a Steam install."); return; }
        SteamClient.Launch(_steamRoot, offline);
        StatusText.Text = offline ? "Launching Steam in offline mode." : "Launching Steam.";
        Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(2000);
            RefreshProcesses();
        });
    }

    private void OfflineOn_Click(object sender, RoutedEventArgs e) => SetOffline(true);
    private void OfflineOff_Click(object sender, RoutedEventArgs e) => SetOffline(false);

    private void SetOffline(bool enabled)
    {
        if (_steamRoot is null) return;
        StatusText.Text = SteamClient.SetOfflinePreference(_steamRoot, enabled)
            ? "Updated WantsOfflineMode. Restart Steam to apply."
            : "Could not update loginusers.vdf.";
    }

    private void Arm_Click(object sender, RoutedEventArgs e)
    {
        if (_armed)
        {
            Disarm("Watcher disarmed.");
            return;
        }
        PersistSettings();
        ApplyWatcherSettings();
        _watcher?.Reset();
        _speedWatch.Reset();
        _armed = true;
        ArmButton.Content = "Disarm";
        ArmButton.Style = (Style)FindResource("WideDanger");
        ArmHint.Text = "Watching speed";
        ArmHint.Foreground = Brush("Green");
        AutoChip.Text = "auto: " + ActionKey();
        AutoChip.Foreground = Brush("Accent");
        StatusText.Text = $"Armed. Watching {WatchLabel()} until speed stays under {FloorBox.SelectedItem}.";
    }

    private void Disarm(string status)
    {
        _armed = false;
        _countdown = null;
        CountdownCard.Visibility = Visibility.Collapsed;
        ArmButton.Content = "Arm";
        ArmButton.Style = (Style)FindResource("WideSafe");
        ArmHint.Text = "Not watching";
        ArmHint.Foreground = Brush("Muted");
        AutoChip.Text = "auto: off";
        AutoChip.Foreground = Brush("Muted");
        StatusText.Text = status;
    }

    private void BeginCountdown()
    {
        _countdown = SelectedSeconds(CountdownBox, 60);
        CountdownCard.Visibility = Visibility.Visible;
        Activate();
        Topmost = true;
        Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(1200);
            Topmost = false;
        });
        TickCountdown();
    }

    private async void TickCountdown()
    {
        while (_countdown is int left)
        {
            if (left <= 0)
            {
                var key = ActionKey();
                CountdownLabel.Text = "Running: " + ActionBox.SelectedItem;
                Disarm("Auto action firing…");
                AutoChip.Text = "auto: firing";
                AutoChip.Foreground = Brush("Red");
                var root = _steamRoot;
                var message = await Task.Run(() => Power.Run(key, root));
                StatusText.Text = message;
                RefreshFirewallAsync();
                if (key is "notify" or "exit_steam" or "block_internet")
                    MessageBox.Show(message, "Steam Stopper");
                return;
            }
            CountdownLabel.Text = $"{_finishReason}  ·  {ActionBox.SelectedItem} in {left}s";
            _countdown = left - 1;
            await Task.Delay(1000);
        }
    }

    private void CancelCountdown_Click(object sender, RoutedEventArgs e)
    {
        Power.CancelShutdown();
        Disarm("Auto action cancelled.");
    }

    private void WatchSettings_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        if (sender == WatchBox) _speedWatch.Reset();
        ApplyWatcherSettings();
        PersistSettings();
        PaintWatchAction();
        if (_lastSnapshot is not null) UpdateAutoSummary(_lastSnapshot, _speedWatch.LastBps);
        else UpdateAutoSummary(null, _speedWatch.LastBps);
    }

    private void Persist_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        PersistSettings();
    }

    private void ApplyWatcherSettings()
    {
        if (_watcher is not null)
        {
            _watcher.IncludeQueued = QueuedBox.IsChecked == true;
            _watcher.RequireActivityFirst = SeenBox.IsChecked == true;
            _watcher.GraceSeconds = SelectedSeconds(GraceBox, 45);
        }
        _speedWatch.ThresholdBps = Traffic.Floors.FirstOrDefault(f => f.Label == FloorBox.SelectedItem?.ToString()).Bps;
        if (_speedWatch.ThresholdBps <= 0) _speedWatch.ThresholdBps = 200 * 1024;
        _speedWatch.HoldSeconds = SelectedSeconds(GraceBox, 45);
        _speedWatch.RequireBurst = SeenBox.IsChecked == true;
    }

    private void PersistSettings()
    {
        _settings.AutoAction = ActionKey();
        _settings.CountdownSeconds = SelectedSeconds(CountdownBox, 60);
        _settings.GraceSeconds = SelectedSeconds(GraceBox, 45);
        _settings.IncludeQueued = QueuedBox.IsChecked == true;
        _settings.RequireActivityFirst = SeenBox.IsChecked == true;
        _settings.BlockGameExes = GameExeBox.IsChecked == true;
        _settings.KillAfterBlock = KillAfterBox.IsChecked == true;
        _settings.RestartAfterUnblock = RestartAfterBox.IsChecked == true;
        _settings.DnsAdapter = SelectedDnsAdapter();
        _settings.DnsProvider = (DnsGrid.SelectedItem as DnsProvider)?.Name ?? _settings.DnsProvider;
        _settings.WatchTarget = WatchTargetKey();
        _settings.SpeedFloorKbps = (int)Math.Round(_speedWatch.ThresholdBps / 1024.0);
        _settings.Save();
    }

    private void RefreshAdmin()
    {
        var admin = SteamClient.IsAdmin();
        AdminChip.Text = admin ? "administrator" : "standard user";
        AdminChip.Foreground = Brush(admin ? "Green" : "Amber");
        ElevateButton.IsEnabled = !admin;
    }

    private async void RefreshFirewallAsync()
    {
        List<string> names;
        try { names = await Task.Run(Firewall.RuleNames); }
        catch { names = []; }
        var blocked = names.Count > 0;
        FirewallChip.Text = blocked ? "steam blocked" : "steam allowed";
        FirewallChip.Foreground = Brush(blocked ? "Red" : "Green");
        TileNetwork.Text = blocked ? "blocked" : "allowed";
        TileNetwork.Foreground = Brush(blocked ? "Red" : "Green");
        BlockHeadline.Text = blocked ? "Cut off" : "Online";
        BlockHeadline.Foreground = Brush(blocked ? "Red" : "Green");
        RuleCount.Text = blocked
            ? $"{names.Count} firewall rule(s) stopping Steam from going online."
            : "Steam can reach the internet.";
        _suppress = true;
        BlockSwitch.IsChecked = !blocked;
        _suppress = false;
    }

    private void RefreshProcesses()
    {
        var rows = SteamClient.RunningProcesses();
        var grouped = rows
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProcessRow
            {
                Name = g.Count() == 1 ? g.Key : $"{g.Key}  ×{g.Count()}",
                Pid = g.Count(),
                Bytes = g.Sum(r => r.Bytes),
                Memory = Format.Bytes(g.Sum(r => r.Bytes))
            })
            .OrderBy(r => r.Name.StartsWith("steam.exe", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(r => r.Name)
            .ToList();
        ProcGrid.ItemsSource = grouped;
        var running = rows.Count > 0;
        TileClient.Text = running ? "Running" : "Stopped";
        TileClient.Foreground = Brush(running ? "Green" : "Muted");
        ClientHint.Text = running
            ? $"{rows.Count} processes  ·  {Format.Bytes(rows.Sum(r => r.Bytes))}"
            : "Steam is not running";
    }

    private void RefreshProc_Click(object sender, RoutedEventArgs e) => RefreshProcesses();

    private async void RescanLibrary()
    {
        if (_steamRoot is null) return;
        var root = _steamRoot;
        _games = await Task.Run(() => SteamClient.ListGames(root));
        var total = _games.Sum(g => g.SizeBytes);
        LibSummary.Text = $"{_games.Count} games  ·  {Format.Bytes(total)}";
        TileLibrary.Text = Format.Bytes(total);
        RenderGames();
    }

    private void Rescan_Click(object sender, RoutedEventArgs e) => RescanLibrary();

    private void Search_Changed(object sender, TextChangedEventArgs e) => RenderGames();

    private void RenderGames()
    {
        var q = SearchBox.Text.Trim();
        SearchHint.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        IEnumerable<GameEntry> list = _games;
        if (q.Length > 0)
            list = _games.Where(g => g.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || g.AppId.Contains(q));
        GamesGrid.ItemsSource = list.OrderByDescending(g => g.SizeBytes).ToList();
    }

    private void Games_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (GamesGrid.SelectedItem is not GameEntry game || _steamRoot is null) return;
        SteamClient.OpenFolder(Path.Combine(game.Library, "steamapps", "common", game.InstallDir));
    }

    private async void Measure_Click(object sender, RoutedEventArgs e)
    {
        if (_steamRoot is null) return;
        var root = _steamRoot;
        var total = await Task.Run(() => CacheTargets.Sum(t => SteamClient.FolderSize(Path.Combine(new[] { root }.Concat(t.Parts).ToArray()))));
        StatusText.Text = $"Measured {Format.Bytes(total)} of cache and logs.";
    }

    private void ClearCache(string name)
    {
        var path = CachePath(name);
        if (path is null) return;
        if (MessageBox.Show($"Delete the contents of:\n{path}?", "Clear files", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;
        var removed = SteamClient.ClearPath(path);
        StatusText.Text = $"Cleared {removed} item(s) from {name}.";
        Measure_Click(this, new RoutedEventArgs());
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (_steamRoot is null) return;
        if (MessageBox.Show("Delete the contents of every cache and log folder listed?", "Clear all", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;
        var removed = CacheTargets.Sum(t => SteamClient.ClearPath(CachePath(t.Name)!));
        StatusText.Text = $"Cleared {removed} item(s).";
        Measure_Click(this, new RoutedEventArgs());
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        if (_steamRoot is null) return;
        StatusText.Text = "Backing up config and userdata…";
        try
        {
            var root = _steamRoot;
            var archive = await Task.Run(() => SteamClient.BackupConfig(root));
            StatusText.Text = "Backup saved to " + archive;
            MessageBox.Show(archive, "Backup complete");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Backup failed");
        }
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        if (_steamRoot is null) { MessageBox.Show("Could not find a Steam install."); return; }
        var tag = (sender as FrameworkElement)?.Tag as string ?? "";
        SteamClient.OpenFolder(string.IsNullOrEmpty(tag) ? _steamRoot : Path.Combine(_steamRoot, tag));
    }

    private void Backups_Click(object sender, RoutedEventArgs e)
        => SteamClient.OpenFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SteamStopperBackups"));

    private void Power_Click(object sender, RoutedEventArgs e)
    {
        var action = (sender as FrameworkElement)?.Tag as string ?? "";
        var label = Power.Actions.FirstOrDefault(a => a.Key == action).Label ?? action;
        if (MessageBox.Show($"{label} now?", "Steam Stopper", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        StatusText.Text = Power.Run(action, _steamRoot);
    }

    private void AbortShutdown_Click(object sender, RoutedEventArgs e)
        => StatusText.Text = Power.CancelShutdown() ? "Pending shutdown cancelled." : "No pending shutdown to cancel.";

    private void Elevate_Click(object sender, RoutedEventArgs e)
    {
        SteamClient.RelaunchAsAdmin();
        Close();
    }

    private bool NeedAdmin(string action = "A firewall change")
    {
        if (SteamClient.IsAdmin()) return true;
        if (MessageBox.Show($"{action} needs Administrator. Relaunch as admin now?", "Administrator required", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
        {
            SteamClient.RelaunchAsAdmin();
            Close();
        }
        return false;
    }

    private void RefreshDnsUi()
    {
        var keep = SelectedDnsAdapter();
        if (string.IsNullOrWhiteSpace(keep)) keep = _settings.DnsAdapter;
        _suppress = true;
        var adapters = DnsJumper.Adapters();
        DnsAdapterBox.Items.Clear();
        DnsAdapterBox.Items.Add(new DnsAdapterInfo
        {
            Name = DnsJumper.AllAdapters,
            Display = adapters.Count == 0 ? "No active adapter" : "All adapters with a gateway",
            Current = string.Join("  ·  ", adapters.Where(a => a.HasGateway).Select(a => a.Current).Distinct().Take(3))
        });
        foreach (var adapter in adapters)
            DnsAdapterBox.Items.Add(adapter);
        DnsAdapterBox.SelectedItem = DnsAdapterBox.Items.Cast<object>()
            .OfType<DnsAdapterInfo>()
            .FirstOrDefault(a => a.Name == keep)
            ?? adapters.FirstOrDefault(a => a.HasGateway)
            ?? adapters.FirstOrDefault()
            ?? DnsAdapterBox.Items[0];

        if (_dnsRows.Count == 0)
        {
            _dnsRows = DnsJumper.Rows();
            DnsGrid.ItemsSource = _dnsRows;
            var wanted = _settings.DnsProvider;
            DnsGrid.SelectedItem = _dnsRows.FirstOrDefault(r => r.Name == wanted) ?? _dnsRows.FirstOrDefault();
            FillDnsBoxesFromSelection();
        }
        _suppress = false;
        PaintDns();
    }

    private void PaintDns()
    {
        var adapter = SelectedAdapter();
        var summary = adapter?.Current;
        if (string.IsNullOrWhiteSpace(summary))
            summary = adapter?.Name == DnsJumper.AllAdapters ? "Automatic" : "—";
        DnsHeadline.Text = summary;
        DnsChip.Text = "dns: " + summary;
        DnsHint.Text = adapter?.Name == DnsJumper.AllAdapters
            ? "Applies to adapters that have a gateway."
            : "Select a DNS, then Apply. Jump pings the list and uses the fastest.";
    }

    private DnsAdapterInfo? SelectedAdapter()
        => DnsAdapterBox.SelectedItem as DnsAdapterInfo;

    private string SelectedDnsAdapter()
        => SelectedAdapter()?.Name ?? (_settings.DnsAdapter.Length > 0 ? _settings.DnsAdapter : "");

    private DnsProvider? SelectedDnsProvider()
        => DnsGrid.SelectedItem as DnsProvider;

    private void FillDnsBoxesFromSelection()
    {
        var row = SelectedDnsProvider();
        if (row is null) return;
        DnsPrimaryBox.Text = row.Primary;
        DnsSecondaryBox.Text = row.Secondary;
    }

    private void DnsAdapter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        PaintDns();
        PersistSettings();
    }

    private void DnsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        FillDnsBoxesFromSelection();
        var name = SelectedDnsProvider()?.Name;
        if (!string.IsNullOrWhiteSpace(name))
            _settings.DnsProvider = name;
    }

    private void DnsGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        FillDnsBoxesFromSelection();
        DnsApply_Click(sender, e);
    }

    private async void DnsApply_Click(object sender, RoutedEventArgs e)
    {
        if (_dnsWorking || !NeedAdmin("Changing DNS")) return;
        var row = SelectedDnsProvider();
        var primary = DnsPrimaryBox.Text.Trim();
        var secondary = DnsSecondaryBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(primary) && row is not null)
        {
            primary = row.Primary;
            secondary = row.Secondary;
            DnsPrimaryBox.Text = primary;
            DnsSecondaryBox.Text = secondary;
        }
        if (string.IsNullOrWhiteSpace(primary))
        {
            StatusText.Text = "Select a DNS in the list, or type a primary address.";
            return;
        }
        var adapter = SelectedAdapter();
        await RunDnsAsync("Setting DNS…", () => DnsJumper.Apply(adapter, primary, secondary));
    }

    private async void DnsFastest_Click(object sender, RoutedEventArgs e)
    {
        if (_dnsWorking) return;
        await PingDnsAsync();
        var best = DnsJumper.Fastest(_dnsRows);
        if (best is null)
        {
            StatusText.Text = "Could not reach any DNS server.";
            return;
        }
        DnsGrid.SelectedItem = best;
        FillDnsBoxesFromSelection();
        _settings.DnsProvider = best.Name;
        if (!NeedAdmin("Changing DNS")) return;
        var adapter = SelectedAdapter();
        await RunDnsAsync($"Jumping to {best.Name} ({best.PingText})…", () => DnsJumper.Apply(adapter, best));
    }

    private async void DnsRestore_Click(object sender, RoutedEventArgs e)
    {
        if (_dnsWorking || !NeedAdmin("Changing DNS")) return;
        var adapter = SelectedAdapter();
        await RunDnsAsync("Restoring automatic DNS…", () => DnsJumper.Restore(adapter));
    }

    private async void DnsFlush_Click(object sender, RoutedEventArgs e)
    {
        if (_dnsWorking) return;
        await RunDnsAsync("Flushing DNS cache…", () =>
        {
            DnsJumper.Flush();
            return "Flushed the DNS cache.";
        });
    }

    private async void DnsPing_Click(object sender, RoutedEventArgs e)
    {
        if (_dnsWorking) return;
        await PingDnsAsync();
        var best = DnsJumper.Fastest(_dnsRows);
        StatusText.Text = best is null
            ? "Ping finished. No DNS replied."
            : $"Fastest: {best.Name} at {best.PingText}.";
    }

    private async Task PingDnsAsync()
    {
        _dnsWorking = true;
        DnsBusy.Text = "Pinging DNS servers…";
        StatusText.Text = "Pinging DNS servers…";
        try
        {
            await DnsJumper.MeasureAsync(_dnsRows);
            DnsGrid.ItemsSource = null;
            DnsGrid.ItemsSource = _dnsRows;
            var best = DnsJumper.Fastest(_dnsRows);
            if (best is not null)
                DnsGrid.SelectedItem = best;
            FillDnsBoxesFromSelection();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ping failed: " + ex.Message;
        }
        finally
        {
            _dnsWorking = false;
            DnsBusy.Text = "";
        }
    }

    private async Task RunDnsAsync(string status, Func<string> work)
    {
        _dnsWorking = true;
        DnsBusy.Text = status;
        StatusText.Text = status;
        try
        {
            var result = await Task.Run(work);
            StatusText.Text = result;
            RefreshDnsUi();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            _dnsWorking = false;
            DnsBusy.Text = "";
        }
    }

    private async Task ScanCleanerAsync()
    {
        if (_toolsWorking) return;
        _toolsWorking = true;
        CleanBusy.Text = "Scanning…";
        CleanHeadline.Text = "Scanning";
        StatusText.Text = "Scanning junk files…";
        try
        {
            if (_cleanRows.Count == 0)
                _cleanRows = Cleaner.Catalog(_steamRoot);
            CleanGrid.ItemsSource = _cleanRows;
            var sizes = await Task.Run(() => Cleaner.Measure(_cleanRows));
            foreach (var item in _cleanRows)
                item.Bytes = sizes.GetValueOrDefault(item.Key);
            var total = _cleanRows.Where(i => i.Selected).Sum(i => i.Bytes);
            CleanHeadline.Text = Format.Bytes(total);
            CleanHint.Text = $"{_cleanRows.Count} targets. {Format.Bytes(_cleanRows.Sum(i => i.Bytes))} found. Clean only deletes junk folders, not games or documents.";
            StatusText.Text = $"Found {Format.Bytes(_cleanRows.Sum(i => i.Bytes))} that can be cleaned.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Scan failed: " + ex.Message;
        }
        finally
        {
            _toolsWorking = false;
            CleanBusy.Text = "";
        }
    }

    private void CleanScan_Click(object sender, RoutedEventArgs e) => _ = ScanCleanerAsync();

    private void CleanSelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _cleanRows) item.Selected = true;
        CleanHeadline.Text = Format.Bytes(_cleanRows.Sum(i => i.Bytes));
    }

    private void CleanSelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _cleanRows) item.Selected = false;
        CleanHeadline.Text = "0 B";
    }

    private async void CleanRun_Click(object sender, RoutedEventArgs e)
    {
        if (_toolsWorking) return;
        var picked = _cleanRows.Where(i => i.Selected).ToList();
        if (picked.Count == 0)
        {
            StatusText.Text = "Select at least one cleaner target.";
            return;
        }
        var size = Format.Bytes(picked.Sum(i => i.Bytes));
        if (MessageBox.Show($"Delete {size} from {picked.Count} selected target(s)? Locked files are skipped.", "Clean files", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;
        _toolsWorking = true;
        CleanBusy.Text = "Cleaning…";
        StatusText.Text = "Cleaning selected files…";
        try
        {
            var result = await Task.Run(() => Cleaner.Clean(picked));
            StatusText.Text = $"Removed {result.Removed} item(s), about {Format.Bytes(result.Bytes)}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Clean failed: " + ex.Message;
        }
        finally
        {
            _toolsWorking = false;
            CleanBusy.Text = "";
        }
        await ScanCleanerAsync();
    }

    private void RefreshApps()
    {
        _appRows = AppHub.Snapshot();
        BindApps();
    }

    private void BindApps()
    {
        IEnumerable<AppOffer> rows = _appRows;
        if (_appCategory != "All")
            rows = rows.Where(a => a.Category == _appCategory);
        var query = AppSearchBox.Text?.Trim() ?? "";
        if (query.Length > 0)
            rows = rows.Where(a =>
                a.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                a.Blurb.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                a.Category.Contains(query, StringComparison.OrdinalIgnoreCase));
        var list = rows.ToList();
        AppsList.ItemsSource = list;
        AppCount.Text = $"{_appRows.Count(a => a.Installed)} installed  ·  {list.Count} shown";
    }

    private void FillAppCategories()
    {
        foreach (var name in AppHub.Categories)
        {
            var button = new RadioButton
            {
                Content = name,
                Tag = name,
                GroupName = "AppCat",
                Style = (Style)FindResource("Segment"),
                IsChecked = name == "All"
            };
            button.Click += AppCategory_Click;
            AppCatBar.Children.Add(button);
        }
    }

    private void AppCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string key })
        {
            _appCategory = key;
            BindApps();
        }
    }

    private void AppSearch_Changed(object sender, TextChangedEventArgs e)
    {
        AppSearchHint.Visibility = string.IsNullOrWhiteSpace(AppSearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (_appRows.Count > 0) BindApps();
    }

    private void AppRefresh_Click(object sender, RoutedEventArgs e) => RefreshApps();

    private void AppCardAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AppOffer app })
        {
            if (app.Installed) _ = UninstallApp(app);
            else _ = InstallApp(app);
        }
    }

    private async Task InstallApp(AppOffer app)
    {
        if (_toolsWorking) return;
        _toolsWorking = true;
        AppBusy.Text = $"Installing {app.Name}…";
        StatusText.Text = $"Installing {app.Name}…";
        try
        {
            var result = await Task.Run(() => AppHub.Install(app));
            StatusText.Text = result;
            AppBusy.Text = result;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            _toolsWorking = false;
            RefreshApps();
        }
    }

    private async Task UninstallApp(AppOffer app)
    {
        if (_toolsWorking) return;
        if (MessageBox.Show($"Uninstall {app.Name} from this PC?", "Uninstall", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;
        _toolsWorking = true;
        AppBusy.Text = $"Uninstalling {app.Name}…";
        StatusText.Text = $"Uninstalling {app.Name}…";
        try
        {
            var result = await Task.Run(() => AppHub.Uninstall(app));
            StatusText.Text = result;
            AppBusy.Text = result;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            _toolsWorking = false;
            RefreshApps();
        }
    }

    private string? CachePath(string name)
    {
        if (_steamRoot is null) return null;
        var parts = CacheTargets.First(t => t.Name == name).Parts;
        return Path.Combine(new[] { _steamRoot }.Concat(parts).ToArray());
    }

    private string ActionKey()
    {
        var label = ActionBox.SelectedItem?.ToString();
        return Power.Actions.FirstOrDefault(a => a.Label == label).Key ?? "notify";
    }

    private string WatchLabel() => WatchBox.SelectedItem?.ToString() ?? "Steam";

    private string WatchTargetKey()
    {
        var label = WatchBox.SelectedItem?.ToString();
        return Traffic.Apps.FirstOrDefault(a => a.Label == label).Key ?? "steam";
    }

    private static int SelectedSeconds(ComboBox box, int fallback)
        => box.SelectedItem is string label && SecondsMap.TryGetValue(label, out var n) ? n : fallback;

    private Brush Brush(string key) => (Brush)FindResource(key);
}
