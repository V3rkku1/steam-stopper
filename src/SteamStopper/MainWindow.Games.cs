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
    private bool _gamesFilling;
    private readonly HashSet<Rom> _playing = [];
    private static readonly HttpClient CoverHttp = new() { Timeout = TimeSpan.FromSeconds(15) };

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
            GameConsoleBox.ItemsSource = AppHub.Consoles.Where(c => c.Name != "ALL-IN-ONE").Select(c => c.Maker).ToList();
            _ = LoadCoversAsync(_library.Games.ToList());
        }
        // Emulators may have been installed or removed on the Emulators tab since last time.
        _emuInstalled = AppHub.Snapshot(AppHub.Emulators).Where(e => e.LaunchPath is not null).ToList();
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
        var choices = EmulatorsFor(rom);
        GameEmuBox.ItemsSource = choices.Select(e => e.Name).ToList();
        GameEmuBox.SelectedItem = choices.FirstOrDefault()?.Name;
        GameEmuBox.IsEnabled = choices.Count > 1;
        _gamesFilling = false;
        PaintPlay(rom);
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
        var running = _playing.Contains(rom);
        var hasEmu = EmulatorsFor(rom).Count > 0;
        GamePlayButton.Content = running ? "RUNNING" : hasEmu ? "PLAY" : "GET EMULATOR";
        GamePlayButton.IsEnabled = !running && rom.Exists && rom.Tile is not null;
        GameHint.Text = !rom.Exists ? "The game file is gone. Remove it or rescan."
            : rom.Tile is null ? "Pick the console this game is for below."
            : !hasEmu ? $"No emulator for {rom.ConsoleName} is installed yet. Get Emulator opens the Emulators tab."
            : "";
    }

    private async void GamePlay_Click(object sender, RoutedEventArgs e)
    {
        if (GameDetail.DataContext is not Rom rom || _library is null) return;
        if (LaunchFor(rom) is not { } launch)
        {
            OpenEmulatorsFor(rom.Tile);
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
        _playing.Add(rom);
        RomLibrary.Save(_library);
        rom.Refresh();
        PaintPlay(rom);
        StatusText.Text = $"Playing {rom.Title} in {emu.Name}.";
        using (process) await process.WaitForExitAsync();
        _playing.Remove(rom);
        rom.PlaySeconds += (DateTime.Now - started).TotalSeconds;
        RomLibrary.Save(_library);
        rom.Refresh();
        if (GameDetail.DataContext == rom) PaintPlay(rom);
        StatusText.Text = $"Played {rom.Title} for {Format.Duration((DateTime.Now - started).TotalSeconds)}.";
    }

    /// <summary>The emulator, exe and arguments that start this game, or null when no emulator for it is installed.</summary>
    private (AppOffer Emu, string Exe, string Args)? LaunchFor(Rom rom)
    {
        var emu = EmulatorsFor(rom).FirstOrDefault();
        if (emu?.LaunchPath is not { } exe) return null;
        // KytyPS5's Play button opens its launcher; games start in its emulator exe next to it.
        if (RomLibrary.GameExe.TryGetValue(emu.Name, out var gameExe) && Path.Combine(Path.GetDirectoryName(exe)!, gameExe) is var direct && File.Exists(direct))
            exe = direct;
        return (emu, exe, RomLibrary.Arguments(emu.Name, rom.Path));
    }

    private async void GamesToSteam_Click(object sender, RoutedEventArgs e)
    {
        if (_library is null) return;
        if (_steamRoot is null || SteamShortcuts.UserConfig(_steamRoot) is not { } config)
        {
            StatusText.Text = "Steam or a Steam login wasn't found on this PC.";
            return;
        }
        var games = _library.Games.Where(g => g.Exists && g.Tile is not null)
            .Select(g => (Rom: g, Launch: LaunchFor(g)))
            .Where(x => x.Launch is not null)
            .Select(x => new SteamGame(x.Rom.Title, x.Launch!.Value.Exe, x.Launch.Value.Args, x.Rom.Cover, x.Rom.ConsoleName))
            .ToList();
        var skipped = _library.Games.Count - games.Count;
        var running = SteamClient.IsRunning();
        var message = $"Add {games.Count} game(s) to Steam as non-Steam games, with their box art?"
            + (skipped > 0 ? $"\n\n{skipped} game(s) are skipped because no emulator for them is installed." : "")
            + "\n\nGames added earlier by Steam Stopper are replaced, so this also removes ones you took off the library. Your other non-Steam games stay."
            + (running ? "\n\nSteam will close and reopen to pick them up." : "");
        if (MessageBox.Show(message, "Add to Steam", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
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
        var nav = FindNav("emulators");
        if (nav is null) return;
        nav.IsChecked = true;
        Nav_Click(nav, new RoutedEventArgs());
        _emuConsole = tile;
        BindApps();
    }

    private RadioButton? FindNav(string key) =>
        FindVisualChildren<RadioButton>(this).FirstOrDefault(r => r.Tag as string == key && r.Style == FindResource("NavItem"));

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
    private async Task AddToLibrary(IEnumerable<string> folders, IEnumerable<string> files)
    {
        if (_library is null) RefreshGamesUi();
        var store = _library!;
        foreach (var folder in folders)
            if (!store.Folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) store.Folders.Add(folder);
        var before = store.Games.Count;
        StatusText.Text = "Looking for games…";
        // Scan into a copy off the UI thread; the list the UI shows is only touched here.
        var scanned = await Task.Run(() =>
        {
            var copy = new RomLibrary.Store { Folders = store.Folders.ToList(), Games = store.Games.ToList() };
            foreach (var file in files)
                if (RomLibrary.ConsoleOf(file) is { } console && !copy.Games.Any(g => g.Path.Equals(file, StringComparison.OrdinalIgnoreCase)))
                    copy.Games.Add(new Rom { Path = file, Title = RomLibrary.TitleOf(file), Console = console });
            RomLibrary.Scan(copy);
            return copy.Games.Skip(before).ToList();
        });
        store.Games.AddRange(scanned);
        RomLibrary.Save(store);
        PaintGames();
        StatusText.Text = scanned.Count switch
        {
            0 => "No new games found.",
            1 => "Added 1 game.",
            _ => $"Added {scanned.Count} games."
        };
        if (scanned.Count > 0)
        {
            GamesList.SelectedItem = scanned[0];
            _ = LoadCoversAsync(scanned);
        }
    }

    /// <summary>Fill in box art one game at a time, so a big library doesn't flood the art server.</summary>
    private async Task LoadCoversAsync(List<Rom> roms)
    {
        foreach (var rom in roms)
        {
            if (rom.Cover is not null) continue;
            rom.Cover = await RomLibrary.CoverFor(rom, CoverHttp);
        }
    }

    private void GameOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (GameDetail.DataContext is Rom rom && rom.Exists)
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{rom.Path}\"") { UseShellExecute = true });
    }

    private void GameCover_Click(object sender, RoutedEventArgs e)
    {
        if (GameDetail.DataContext is not Rom rom || _library is null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Choose a cover image", Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp" };
        if (dialog.ShowDialog(this) != true) return;
        rom.CustomCover = dialog.FileName;
        rom.Cover = dialog.FileName;
        RomLibrary.Save(_library);
    }

    private void GameRemove_Click(object sender, RoutedEventArgs e)
    {
        if (GameDetail.DataContext is not Rom rom || _library is null) return;
        _library.Games.Remove(rom);
        RomLibrary.Save(_library);
        GamesList.SelectedItem = null;
        PaintGames();
        StatusText.Text = $"Removed {rom.Title} from the library. The file is still on disk; Rescan would add it back.";
    }
}
