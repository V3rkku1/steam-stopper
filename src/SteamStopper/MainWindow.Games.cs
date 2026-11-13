using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SteamStopper.Core;

namespace SteamStopper;

// Games tab: a Steam-style library of the user's own game files, started in the matching emulator.
public partial class MainWindow
{
    private RomLibrary.Store? _library;
    private ICollectionView? _gamesView;
    private List<AppOffer> _emuInstalled = [];
    private bool _emuGamesScanned;

    private List<string> EmulatorGameFolders() => _emuInstalled.SelectMany(e => RomLibrary.EmulatorGameFolders(e.Name, e.LaunchPath!)).ToList();
    private bool _gamesFilling;
    private readonly Dictionary<Rom, Process> _playing = [];
    private static readonly HttpClient CoverHttp = new() { Timeout = TimeSpan.FromSeconds(60) }; // art folder listings are a few MB

    private void RefreshGamesUi()
    {
        if (_library is null)
        {
            _library = RomLibrary.Load();
            _gamesView = CollectionViewSource.GetDefaultView(_library.Games);
            _gamesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Rom.ConsoleName)));
            _gamesView.SortDescriptions.Add(new SortDescription(nameof(Rom.ConsoleName), ListSortDirection.Ascending));
            _gamesView.SortDescriptions.Add(new SortDescription(nameof(Rom.Title), ListSortDirection.Ascending));
            _gamesView.Filter = o => o is Rom r && (GameSearchBox.Text.Trim() is not { Length: > 0 } q
                || r.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || r.ConsoleName.Contains(q, StringComparison.OrdinalIgnoreCase));
            GamesList.ItemsSource = _gamesView;
            GameConsoleBox.ItemsSource = AppHub.Consoles.Where(c => c.Name is not ("ALL-IN-ONE" or "RECOMP")).Select(c => c.Maker).ToList();
            _ = LoadCoversAsync(_library.Games.ToList());
            WatchLibrary();
        }
        _ = SyncStoreGames();
        // Emulators may have been installed or removed on the Emulators tab since last time.
        _emuInstalled = AppHub.Snapshot(AppHub.Emulators).Where(e => e.LaunchPath is not null).ToList();
        // Games installed inside RPCS3, shadPS4 or Vita3K since last time; once per run, the folder watcher covers the rest.
        if (!_emuGamesScanned && EmulatorGameFolders().Count > 0) { _emuGamesScanned = true; _ = AddToLibrary([], [], quiet: true); }
        _ = CheckEmulatorUpdates();
        PaintGames();
        ShowGame(GamesList.SelectedItem as Rom);
    }

    private void PaintGames()
    {
        if (_library is null) return;
        _gamesView?.Refresh();
        GamesCount.Text = _library.Games.Count == 0 ? "No games yet"
            : $"{_library.Games.Count} games  ·  {_library.Folders.Count} folder{(_library.Folders.Count == 1 ? "" : "s")}";
        if (GamesList.SelectedItem is null && _gamesView?.Cast<Rom>().FirstOrDefault() is { } first) GamesList.SelectedItem = first;
        GamesEmpty.Visibility = _library.Games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // The card radius minus its 1px border. Bottom corners round too but fade into the card colour there.
    private void GameHero_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ((Grid)sender).Clip = new System.Windows.Media.RectangleGeometry(new Rect(e.NewSize),
            ((CornerRadius)FindResource("CardRadius")).TopLeft - 1, ((CornerRadius)FindResource("CardRadius")).TopLeft - 1);

    private void GameSearch_Changed(object sender, TextChangedEventArgs e)
    {
        GameSearchHint.Visibility = GameSearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _gamesView?.Refresh();
    }

    private void GamesList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowGame(GamesList.SelectedItem as Rom);

    private void ShowGame(Rom? rom)
    {
        GameDetail.Visibility = rom is null ? Visibility.Collapsed : Visibility.Visible;
        GameDetail.DataContext = rom;
        if (rom is null) return;
        _gamesFilling = true;
        GameConsoleBox.SelectedItem = rom.Tile?.Maker;
        // A store game's launcher is fixed, so there's no console to pick.
        GameConsolePanel.Visibility = rom.IsLink ? Visibility.Hidden : Visibility.Visible;
        GameOpenFolderButton.Visibility = GameSavesButton.Visibility = rom.IsLink ? Visibility.Collapsed : Visibility.Visible;
        var choices = EmulatorsFor(rom);
        GameEmuBox.ItemsSource = choices.Select(e => e.Name).ToList();
        GameEmuBox.SelectedItem = choices.FirstOrDefault()?.Name;
        GameEmuBox.IsEnabled = choices.Count > 1;
        GameEmuPanel.Visibility = choices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _gamesFilling = false;
        PaintPlay(rom);
        ShowCheevos(rom);
    }

    /// <summary>Installed emulators that can open this game, the one the user picked first.</summary>
    private List<AppOffer> EmulatorsFor(Rom rom)
    {
        var wanted = rom.Tile?.Emulators ?? [];
        return _emuInstalled
            .Where(e => wanted.Contains(e.Name) && RomLibrary.LaunchArgs.ContainsKey(e.Name))
            .OrderBy(e => e.Name == rom.Emulator ? -1 : Array.IndexOf(wanted, e.Name))
            .ToList();
    }

    private void PaintPlay(Rom rom)
    {
        if (GameDetail.DataContext != rom) return; // console mode can play a game whose page isn't open
        var running = _playing.ContainsKey(rom) || _unpackRom == rom;
        var hasEmu = rom.IsLink || EmulatorsFor(rom).Count > 0;
        GamePlayButton.Content = running ? "STOP" : hasEmu ? "PLAY" : "GET EMULATOR";
        // Tag switches the button to Steam's blue Stop look, or blue with a download arrow for Get Emulator.
        GamePlayButton.Tag = running ? "Stop" : hasEmu ? null : "Get";
        GamePlayButton.IsEnabled = running || (rom.Exists && rom.Tile is not null);
        GameHint.Text = !rom.Exists ? "The game file is gone. Remove it or rescan."
            : rom.Tile is null ? "Pick the console this game is for below."
            : !hasEmu ? $"No emulator for {rom.ConsoleName} is installed yet. Get Emulator opens the Emulators tab."
            : "";
    }

    private async void GamePlay_Click(object sender, RoutedEventArgs e)
    {
        if (GameDetail.DataContext is Rom rom) await Play(rom);
    }

    private void ConsoleMode_Click(object sender, RoutedEventArgs e)
    {
        if (OwnedWindows.OfType<ConsoleMode>().FirstOrDefault() is { } open) { open.Activate(); return; }
        RefreshGamesUi(); // loads the library, its covers and the installed emulators
        ConsoleMode.Open(this, _library!.Games, Play, rom => _playing.ContainsKey(rom) || _unpackRom == rom, () => StatusText.Text);
    }

    /// <summary>Play, or stop, a game: from its page or from console mode.</summary>
    internal async Task Play(Rom rom)
    {
        if (_library is null) return;
        if (_unpackRom == rom)
        {
            _unpackStop?.Cancel();
            return;
        }
        if (_playing.TryGetValue(rom, out var playing))
        {
            StatusText.Text = $"Stopping {rom.Title}…";
            if (GameDetail.DataContext == rom) GamePlayButton.IsEnabled = false;
            if (!await Task.Run(() => Stop(playing)))
            {
                PaintPlay(rom);
                StatusText.Text = $"Could not stop {rom.Title}. Close the emulator window yourself.";
            }
            return;
        }
        if (RomLibrary.IsPs5Image(rom.Path) || RomLibrary.IsPs4Package(rom))
        {
            // Ask for the emulator before a long unpack, not after it.
            if (EmulatorsFor(rom).Count == 0)
            {
                OpenEmulatorsFor(rom.Tile);
                StatusText.Text = $"No emulator for {rom.ConsoleName} is installed yet. Get one on the Emulators tab.";
                return;
            }
            if (!await Unpack(rom)) return;
        }
        if (rom.IsLink)
        {
            // Steam, Epic and Heroic start their own games (and count play time themselves).
            try { Process.Start(new ProcessStartInfo(rom.Path) { UseShellExecute = true })?.Dispose(); }
            catch (Exception ex)
            {
                StatusText.Text = $"Could not open {rom.ConsoleName} for {rom.Title}: {ex.Message}";
                return;
            }
            rom.LastPlayed = DateTime.Now;
            RomLibrary.Save(_library);
            rom.Refresh();
            StatusText.Text = $"Starting {rom.Title} in {rom.ConsoleName}.";
            return;
        }
        if (LaunchFor(rom) is not { } launch)
        {
            OpenEmulatorsFor(rom.Tile);
            StatusText.Text = $"No emulator for {rom.ConsoleName} is installed yet. Get one on the Emulators tab.";
            return;
        }
        var (emu, exe, args) = launch;
        Process process;
        try
        {
            // Started directly (not through explorer) so play time can follow the process; it inherits admin from this app.
            process = Process.Start(new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe)!
            }) ?? throw new InvalidOperationException("Windows did not start it.");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not start {emu.Name}: {ex.Message}";
            return;
        }
        var started = DateTime.Now;
        rom.LastPlayed = started;
        _playing[rom] = process;
        RomLibrary.Save(_library);
        rom.Refresh();
        PaintPlay(rom);
        StatusText.Text = $"Playing {rom.Title} in {emu.Name}.";
        List<string> boosted = [];
        if (_settings.GameBoost && _settings.GameBoostApps.Count > 0)
        {
            var names = _settings.GameBoostApps.ToList();
            var gamePid = process.Id;
            boosted = await Task.Run(() => GameBoost.Close(names, gamePid));
            if (boosted.Count > 0)
                StatusText.Text = $"Playing {rom.Title} in {emu.Name}. Closed {string.Join(", ", boosted.Select(Path.GetFileNameWithoutExtension))} until you stop.";
        }
        var exited = process.WaitForExitAsync();
        _ = WatchCheevos(exited);
        using (process) await exited;
        if (boosted.Count > 0) _ = Task.Run(() => GameBoost.Reopen(boosted));
        _playing.Remove(rom);
        rom.PlaySeconds += (DateTime.Now - started).TotalSeconds;
        if (_settings.Theme == "game") ApplyTheme();
        RomLibrary.Save(_library);
        rom.Refresh();
        if (GameDetail.DataContext == rom) PaintPlay(rom);
        StatusText.Text = $"Played {rom.Title} for {Format.Duration((DateTime.Now - started).TotalSeconds)}.";
    }

    // The one game being unpacked (a PS5 image or a PS4 .pkg); Play on it again stops it.
    private Rom? _unpackRom;
    private CancellationTokenSource? _unpackStop;

    private static bool ForgeAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited && p.ProcessName.StartsWith("ps5-dump-forge", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Unpack a PS5 image with PS5 Dump Forge, or extract a PS4 .pkg with Orbis, into a game folder; then point the game at it.
    /// KytyPS5 and shadPS4 only run folders.
    /// </summary>
    private async Task<bool> Unpack(Rom rom)
    {
        if (_unpackRom is { } busy)
        {
            StatusText.Text = $"Wait for {busy.Title} to finish unpacking, or press Stop on it.";
            return false;
        }
        var ps4 = RomLibrary.IsPs4Package(rom);
        var dir = RomLibrary.UnpackedDir(rom.Path);
        var eboot = Path.Combine(dir, "eboot.bin");
        // Forge publishes the folder whole; Orbis writes file by file, so only its finished mark counts.
        if (ps4 ? !File.Exists(Path.Combine(dir, RomLibrary.UnpackedMark)) : !File.Exists(eboot))
        {
            var tool = ps4 ? "Orbis" : "PS5 Dump Forge";
            var cli = EmuStore.Find(tool)?.Exe is { } app ? ps4 ? app : Path.Combine(Path.GetDirectoryName(app)!, "ps5-dump-forge.exe") : null;
            if (cli is null || !File.Exists(cli))
            {
                OpenEmulatorsFor(rom.Tile);
                StatusText.Text = ps4 ? "Install Orbis to play PS4 .pkg games. Play extracts them for shadPS4."
                    : "Install PS5 Dump Forge to play .ffpfs games. Play unpacks them for KytyPS5.";
                return false;
            }
            _unpackRom = rom;
            _unpackStop = new CancellationTokenSource();
            PaintPlay(rom);
            try
            {
                // Forge can't resume, but one started before the app closed keeps going: wait for it instead of starting over.
                if (!ps4 && RomLibrary.UnpackLeftovers(dir, ForgeAlive) is { } pid)
                {
                    StatusText.Text = $"Still unpacking {rom.Title} from before…";
                    using var earlier = Process.GetProcessById(pid);
                    using (_unpackStop.Token.Register(() => { try { earlier.Kill(entireProcessTree: true); } catch { } }))
                        await earlier.WaitForExitAsync(_unpackStop.Token);
                }
                else if (!await RunUnpacker(cli, rom, dir, ps4, _unpackStop.Token)) return false;
                if (!File.Exists(eboot))
                {
                    StatusText.Text = $"Unpacking {rom.Title} didn't finish. Press Play to try again.";
                    return false;
                }
                if (ps4) File.WriteAllText(Path.Combine(dir, RomLibrary.UnpackedMark), "");
            }
            catch (OperationCanceledException)
            {
                if (!ps4)
                {
                    // Forge was killed mid-write: clear its half-written folder now instead of next time.
                    await Task.Delay(500);
                    RomLibrary.UnpackLeftovers(dir, ForgeAlive);
                }
                StatusText.Text = ps4 ? $"Stopped extracting {rom.Title}. Play carries on from where it stopped."
                    : $"Stopped unpacking {rom.Title}. Unpacking starts over next time.";
                return false;
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Could not unpack {rom.Title}: {ex.Message}";
                return false;
            }
            finally
            {
                _unpackRom = null;
                _unpackStop.Dispose();
                _unpackStop = null;
                if (GameDetail.DataContext is Rom shown) PaintPlay(shown);
            }
        }
        // The image or package stays; the folder is what plays from now on.
        rom.Path = dir;
        RomLibrary.Save(_library!);
        rom.Refresh();
        return true;
    }

    /// <summary>Run the unpacker, showing percent, speed and time left. False (with the reason shown) when it fails.</summary>
    private async Task<bool> RunUnpacker(string cli, Rom rom, string dir, bool ps4, CancellationToken stop)
    {
        StatusText.Text = $"Unpacking {rom.Title}…";
        var start = new ProcessStartInfo(cli) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        // --resume: Orbis keeps the files a stopped extract already wrote and copies the rest.
        string[] args = ps4 ? ["extract", rom.Path, "-o", dir, "--resume"] : ["convert", rom.Path, "--to", "folder", "-o", dir];
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows did not start it.");
        using var kill = stop.Register(() => { try { process.Kill(entireProcessTree: true); } catch { } });
        var errors = process.StandardError.ReadToEndAsync();
        var clock = Stopwatch.StartNew();
        void Show(ulong done, ulong total, double bps) =>
            StatusText.Text = $"Unpacking {rom.Title}…  {done * 100 / total}%  ·  {Format.Speed(bps)}"
                + (bps > 0 && done > 0 ? $"  ·  {Format.Duration((total - done) / bps)} left" : "");
        if (ps4)
        {
            // Orbis prints only phase names when it isn't in a console, so progress is the folder's size against the package's.
            var output = process.StandardOutput.ReadToEndAsync();
            var total = (ulong)Math.Max(1, new FileInfo(rom.Path).Length);
            var before = await Task.Run(() => RomLibrary.FolderSize(dir));
            var exited = process.WaitForExitAsync();
            while (await Task.WhenAny(exited, Task.Delay(2000)) != exited)
            {
                var size = await Task.Run(() => RomLibrary.FolderSize(dir));
                Show(Math.Min(size, total * 99 / 100), total, (size - Math.Min(before, size)) / Math.Max(clock.Elapsed.TotalSeconds, 0.001));
            }
            await output;
        }
        else
            while (await process.StandardOutput.ReadLineAsync() is { } line)
                if (RomLibrary.UnpackProgress(line) is var (done, total))
                    Show(done, total, done / Math.Max(clock.Elapsed.TotalSeconds, 0.001));
        await process.WaitForExitAsync();
        stop.ThrowIfCancellationRequested();
        // Orbis exit 3: some entries were skipped; the game folder can still be complete, so eboot.bin decides.
        if (process.ExitCode == 0 || ps4 && process.ExitCode == 3) return true;
        var why = (await errors).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        StatusText.Text = $"Could not unpack {rom.Title}: {why ?? $"{(ps4 ? "Orbis" : "PS5 Dump Forge")} failed."}";
        return false;
    }

    /// <summary>Ask the emulator to close like its X button would, so it can save; kill it if it doesn't within 5 seconds.</summary>
    private static bool Stop(Process process)
    {
        try
        {
            if (process.HasExited) return true;
            if (process.CloseMainWindow() && process.WaitForExit(5000)) return true;
            process.Kill(entireProcessTree: true);
            return process.WaitForExit(5000);
        }
        catch (InvalidOperationException) { return true; } // exited meanwhile
        catch { return false; }
    }

    /// <summary>The emulator, exe and arguments that start this game, or null when no emulator for it is installed.</summary>
    private (AppOffer Emu, string Exe, string Args)? LaunchFor(Rom rom)
    {
        var emu = EmulatorsFor(rom).FirstOrDefault();
        if (emu?.LaunchPath is not { } exe) return null;
        // KytyPS5's Play button opens its launcher; games start in its emulator exe next to it.
        if (RomLibrary.GameExe.TryGetValue(emu.Name, out var gameExe) && Path.Combine(Path.GetDirectoryName(exe)!, gameExe) is var direct && File.Exists(direct))
            exe = direct;
        return (emu, exe, RomLibrary.Arguments(emu.Name, rom.Path, rom.Console));
    }

    private async void GamesToSteam_Click(object sender, RoutedEventArgs e)
    {
        if (_library is null) return;
        if (_steamRoot is null || SteamShortcuts.UserConfig(_steamRoot) is not { } config)
        {
            StatusText.Text = "Steam or a Steam login wasn't found on this PC.";
            return;
        }
        // .pkg/.vpk shortcuts would only open the emulator.
        var games = _library.Games.Where(g => g.Exists && g.Tile is not null && !RomLibrary.InstallOnly(g.Path))
            .Select(g => (Rom: g, Launch: LaunchFor(g)))
            .Where(x => x.Launch is not null)
            .Select(x => new SteamGame(x.Rom.Title, x.Launch!.Value.Exe, x.Launch.Value.Args, x.Rom.Cover, x.Rom.ConsoleName))
            .ToList();
        var skipped = _library.Games.Count - games.Count;
        var running = SteamClient.IsRunning();
        var message = $"Add {games.Count} game(s) to Steam as non-Steam games, with their box art?"
            + (skipped > 0 ? $"\n\n{skipped} game(s) are skipped: no emulator for them is installed, or they are .pkg/.vpk installers." : "")
            + "\n\nGames added earlier by Steam Multitool are replaced, so this also removes ones you took off the library. Your other non-Steam games stay."
            + (running ? "\n\nSteam will close and reopen to pick them up." : "");
        if (Dialog.Show(message, "Add to Steam", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        StatusText.Text = "Adding games to Steam…";
        try
        {
            var root = _steamRoot;
            var count = await Task.Run(() =>
            {
                // Steam keeps shortcuts in memory and rewrites the file on exit, so it has to be closed first.
                if (running) CloseSteam(root);
                var written = SteamShortcuts.Sync(config, games);
                if (running) SteamClient.Launch(root, offline: false);
                return written;
            });
            StatusText.Text = $"Added {count} game(s) to Steam. They're in your Steam library under Non-Steam.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not add games to Steam: " + ex.Message;
        }
    }

    private static void CloseSteam(string steamRoot)
    {
        // Ask Steam to exit cleanly first, so it saves what it has; kill it only if it hangs.
        try { Process.Start(new ProcessStartInfo(Path.Combine(steamRoot, "steam.exe"), "-shutdown") { UseShellExecute = true })?.Dispose(); }
        catch { }
        var until = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < until && SteamClient.IsRunning()) Thread.Sleep(300);
        if (SteamClient.IsRunning()) SteamClient.KillSteam();
        until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until && SteamClient.IsRunning()) Thread.Sleep(200);
    }

    private void OpenEmulatorsFor(ConsoleTile? tile)
    {
        GoTo("emulators");
        _emuConsole = tile;
        BindApps();
    }


    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T hit) yield return hit;
            foreach (var deeper in FindVisualChildren<T>(child)) yield return deeper;
        }
    }

    private void GameEmu_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_gamesFilling || _library is null || GameDetail.DataContext is not Rom rom || GameEmuBox.SelectedItem is not string name) return;
        rom.Emulator = name;
        RomLibrary.Save(_library);
    }

    private void GameConsole_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_gamesFilling || _library is null || GameDetail.DataContext is not Rom rom || GameConsoleBox.SelectedItem is not string maker) return;
        var tile = AppHub.Consoles.FirstOrDefault(c => c.Maker == maker);
        if (tile is null || tile.Name == rom.Console) return;
        rom.Console = tile.Name;
        rom.Emulator = "";
        RomLibrary.Save(_library);
        PaintGames();
        GamesList.SelectedItem = rom;
        ShowGame(rom);
        _ = LoadCoversAsync([rom]);
    }

    private async void GamesAdd_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose folders with your games", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        await AddToLibrary(dialog.FolderNames, []);
    }

    private async void GamesRescan_Click(object sender, RoutedEventArgs e) => await AddToLibrary([], []);

    private async void Games_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        await AddToLibrary(paths.Where(Directory.Exists), paths.Where(File.Exists));
    }

    /// <summary>Remember new folders, add single files, then scan every folder for games not in the list yet.</summary>
    /// <param name="quiet">From the folder watcher: say nothing unless something was found, and keep the selection.</param>
    private async Task AddToLibrary(IEnumerable<string> folders, IEnumerable<string> files, bool quiet = false)
    {
        if (_library is null) RefreshGamesUi();
        var store = _library!;
        var newFolder = false;
        foreach (var folder in folders)
            if (!store.Folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) { store.Folders.Add(folder); newFolder = true; }
        if (newFolder) WatchLibrary();
        var before = store.Games.Count;
        var extra = EmulatorGameFolders();
        if (!quiet) StatusText.Text = "Looking for games…";
        // Scan into a copy off the UI thread; the list the UI shows is only touched here.
        var scanned = await Task.Run(() =>
        {
            var copy = new RomLibrary.Store { Folders = store.Folders.ToList(), Games = store.Games.ToList() };
            foreach (var file in files)
                if (RomLibrary.ConsoleOf(file) is { } console && !copy.Games.Any(g => g.Path.Equals(file, StringComparison.OrdinalIgnoreCase)))
                    copy.Games.Add(new Rom { Path = file, Title = RomLibrary.TitleOf(file), Console = console });
            RomLibrary.Scan(copy, extra);
            return copy.Games.Skip(before).ToList();
        });
        store.Games.AddRange(scanned);
        foreach (var package in RomLibrary.InstalledPackages(store.Games))
        {
            store.Games.Remove(package);
            scanned.Remove(package);
        }
        RomLibrary.Save(store);
        PaintGames();
        if (!quiet || scanned.Count > 0)
            StatusText.Text = scanned.Count switch
            {
                0 => "No new games found.",
                1 => $"Added {(quiet ? scanned[0].Title + " from your games folder" : "1 game")}.",
                _ => $"Added {scanned.Count} games{(quiet ? " from your games folders" : "")}."
            };
        if (scanned.Count > 0)
        {
            if (!quiet) GamesList.SelectedItem = scanned[0];
            _ = LoadCoversAsync(scanned);
            if (PageHome.IsVisible) RefreshHome();
        }
    }

    private readonly List<FileSystemWatcher> _libraryWatchers = [];
    private CancellationTokenSource? _libraryScanWait;

    /// <summary>Watch the library folders, so a game dropped or copied into one shows up without Rescan.</summary>
    private void WatchLibrary()
    {
        foreach (var old in _libraryWatchers) old.Dispose();
        _libraryWatchers.Clear();
        foreach (var folder in _library!.Folders.Where(Directory.Exists))
        {
            try
            {
                var watcher = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024
                };
                watcher.Created += OnLibraryChanged;
                watcher.Changed += OnLibraryChanged;
                watcher.Renamed += (s, e) => OnLibraryChanged(s, e);
                watcher.EnableRaisingEvents = true;
                _libraryWatchers.Add(watcher);
            }
            catch { } // a drive that went away or a folder Windows won't watch: Rescan still works
        }
    }

    private void OnLibraryChanged(object sender, FileSystemEventArgs e)
    {
        // Saves, screenshots and the like in the same folders aren't games; a new folder may hold some.
        if (!RomLibrary.LooksLikeGame(e.FullPath) && !Directory.Exists(e.FullPath)) return;
        Dispatcher.BeginInvoke(async () =>
        {
            // A big copy keeps changing the file; scan once it has been still for 5 seconds.
            _libraryScanWait?.Cancel();
            var wait = _libraryScanWait = new CancellationTokenSource();
            try { await Task.Delay(TimeSpan.FromSeconds(5), wait.Token); }
            catch (OperationCanceledException) { return; }
            await AddToLibrary([], [], quiet: true);
        });
    }

    /// <summary>Steam, Epic and Heroic games: add new installs and drop ones that were uninstalled.</summary>
    private bool _syncingStores;

    private async Task SyncStoreGames()
    {
        // One at a time: two overlapping syncs would both add the same new games.
        if (_library is null || _syncingStores) return;
        _syncingStores = true;
        try { await SyncStoreGamesCore(); }
        finally { _syncingStores = false; }
    }

    private async Task SyncStoreGamesCore()
    {
        if (_library is null) return;
        var root = _steamRoot;
        List<Rom> found;
        try
        {
            found = await Task.Run(() => StoreGames.Steam(root is null ? [] : SteamClient.LibraryPaths(root), root)
                .Concat(StoreGames.Epic(StoreGames.EpicManifests))
                .Concat(StoreGames.Heroic(StoreGames.HeroicFolder)).ToList());
        }
        catch { return; } // a launcher's files mid-write; next visit tries again
        var paths = found.Select(g => g.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gone = _library.Games.Where(g => g.IsLink && !paths.Contains(g.Path)).ToList();
        var have = _library.Games.Select(g => g.Path).Concat(_library.HiddenLinks).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = found.Where(g => have.Add(g.Path)).ToList();
        if (gone.Count + added.Count == 0) return;
        foreach (var game in gone) _library.Games.Remove(game);
        _library.Games.AddRange(added);
        RomLibrary.Save(_library);
        PaintGames();
        _ = LoadCoversAsync(added);
    }

    /// <summary>Fill in box art one game at a time, so a big library doesn't flood the art server.</summary>
    private async Task LoadCoversAsync(List<Rom> roms)
    {
        var renamed = false;
        foreach (var rom in roms)
        {
            if (await RomLibrary.NameFromId(rom, CoverHttp) is { } name) { rom.Title = name; renamed = true; }
            if (rom.Cover is not null) continue;
            rom.Cover = await RomLibrary.CoverFor(rom, CoverHttp);
        }
        if (renamed && _library is not null) { RomLibrary.Save(_library); _gamesView?.Refresh(); }
        // Covers can arrive after start; the game theme waits for them.
        if (_settings.Theme == "game") ApplyTheme();
    }

    private void GameOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (GameDetail.DataContext is Rom rom && rom.Exists && !rom.IsLink)
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{rom.Path}\"") { UseShellExecute = true });
    }

    private void GameCover_Click(object sender, RoutedEventArgs e)
    {
        if (GameDetail.DataContext is not Rom rom || _library is null) return;
        if (CoverPicker.Show(this, rom, CoverHttp) is not { } path) return;
        rom.CustomCover = path;
        rom.Cover = path;
        RomLibrary.Save(_library);
    }

    private void GameRename_Click(object sender, RoutedEventArgs e)
    {
        if (GameDetail.DataContext is not Rom rom || _library is null) return;
        if (Dialog.Ask(this, "The name shown in your library and on Steam. Covers are searched by it too.", "Rename game", rom.Title) is not { } name) return;
        rom.Title = name;
        RomLibrary.Save(_library);
        _gamesView?.Refresh();
    }

    private void GamesFixCovers_Click(object sender, RoutedEventArgs e)
    {
        if (_library is null) return;
        var missing = _library.Games.Where(g => g.Cover is null && g.Exists && RomLibrary.HasCoverSource(g.Console)).ToList();
        if (missing.Count == 0)
        {
            StatusText.Text = "Every game that has a cover source already has a cover.";
            return;
        }
        var stop = false;
        var set = 0;
        for (var i = 0; i < missing.Count && !stop; i++)
        {
            if (CoverPicker.Show(this, missing[i], CoverHttp, $"{i + 1} of {missing.Count}", () => stop = true) is not { } path) continue;
            missing[i].CustomCover = path;
            missing[i].Cover = path;
            set++;
        }
        RomLibrary.Save(_library);
        StatusText.Text = $"Set {set} cover(s).";
    }

    private void GameRemove_Click(object sender, RoutedEventArgs e)
    {
        if (GameDetail.DataContext is not Rom rom || _library is null) return;
        _library.Games.Remove(rom);
        if (rom.IsLink) _library.HiddenLinks.Add(rom.Path);
        RomLibrary.Save(_library);
        GamesList.SelectedItem = null;
        PaintGames();
        StatusText.Text = rom.IsLink
            ? $"Removed {rom.Title} from the library. It stays installed in {rom.ConsoleName}."
            : $"Removed {rom.Title} from the library. The file is still on disk; Rescan would add it back.";
    }
}
