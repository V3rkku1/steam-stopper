using System.Windows;
using System.Windows.Controls;
using SteamStopper.Core;

namespace SteamStopper;

// Home page: a summary of the other pages, built from the state they already keep. Nothing here is fetched on its own.
public partial class MainWindow
{
    private void RefreshHome()
    {
        // The shelf needs the Games library, which otherwise loads on the first visit to that tab.
        if (_library is null) RefreshGamesUi();
        var shelf = _library!.Games
            .OrderByDescending(g => g.LastPlayed ?? DateTime.MinValue)
            .ThenByDescending(g => g.Added)
            .Take(8)
            .ToList();
        HomeShelf.ItemsSource = shelf;
        HomeShelfEmpty.Visibility = shelf.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HomeShelfTitle.Text = shelf.Any(g => g.LastPlayed is not null) ? "CONTINUE PLAYING" : "RECENTLY ADDED";
        PaintHome();
    }

    /// <summary>Repaint the summary texts; cheap enough for every poll tick while Home is open.</summary>
    private void PaintHome()
    {
        var hour = DateTime.Now.Hour;
        HomeGreeting.Text = hour < 5 ? "Good night" : hour < 12 ? "Good morning" : hour < 18 ? "Good afternoon" : "Good evening";

        var steamRunning = TileClient.Text == "Running";
        HomeSteam.Text = _steamBlocked ? "Cut off" : "Online";
        HomeSteam.Foreground = Brush(_steamBlocked ? "Red" : "Green");
        HomeSteamHint.Text = $"{(steamRunning ? "Running" : "Not running")}  ·  downloads {TileDownloads.Text}";
        HomeBlockButton.Content = _steamBlocked ? "Let Steam online" : "Block Steam";

        var running = _fetchJobs.Where(j => j.Running).ToList();
        var queued = _fetchJobs.Count(j => j.State == FetchState.Queued);
        var sized = _fetchJobs.Where(j => (j.Running || j.State == FetchState.Queued) && j.Total > 0).ToList();
        HomeFetch.Text = running.Count > 0 ? Format.Speed(running.Sum(j => j.Bps)) : "Idle";
        HomeFetch.Foreground = Brush(running.Count > 0 ? "Green" : "Muted");
        HomeFetchHint.Text = running.Count + queued == 0
            ? (_fetchJobs.Count(j => j.State == FetchState.Done) is var done and > 0 ? $"{done} finished" : "Paste a link to download it fast")
            : $"{running.Count} downloading{(queued > 0 ? $"  ·  {queued} queued" : "")}";
        HomeFetchBar.Visibility = sized.Count > 0 ? Visibility.Visible : Visibility.Hidden;
        if (sized.Count > 0) HomeFetchBar.Value = sized.Sum(j => j.Received) / (double)sized.Sum(j => j.Total);

        var emuUpdates = AppHub.Emulators.Count(e => UpdateFor(e.Name) is not null);
        HomeUpdates.Text = _update is not null ? $"{_update.Version} ready" : emuUpdates > 0 ? $"{emuUpdates} emulator update{(emuUpdates == 1 ? "" : "s")}" : "Up to date";
        HomeUpdates.Foreground = Brush(_update is not null || emuUpdates > 0 ? "Amber" : "Text");
        HomeUpdatesHint.Text = UpdateState.Text;

        var games = _library?.Games.Count ?? 0;
        HomeSummary.Text = string.Join("  ·  ", new[]
        {
            _steamBlocked ? "Steam is cut off from the internet" : "Steam can go online",
            running.Count > 0 ? $"{running.Count} file{(running.Count == 1 ? "" : "s")} downloading" : null,
            games > 0 ? $"{games} game{(games == 1 ? "" : "s")} in your library" : null
        }.Where(p => p is not null));
    }

    private void HomeGo(string key)
    {
        if (FindNav(key) is not { } nav) return;
        nav.IsChecked = true;
        Nav_Click(nav, new RoutedEventArgs());
    }

    private void HomeGo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string key }) HomeGo(key);
    }

    private void HomeGames_Click(object sender, RoutedEventArgs e) => HomeGo("games");

    private void HomeGame_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: Rom rom }) return;
        HomeGo("games");
        // Clear the search so the game is in the list to select.
        GameSearchBox.Clear();
        GamesList.SelectedItem = rom;
        GamesList.ScrollIntoView(rom);
    }

    private async void HomeBlock_Click(object sender, RoutedEventArgs e)
    {
        HomeBlockButton.IsEnabled = false;
        try { await (_steamBlocked ? UnblockAsync() : BlockAsync()); }
        finally { HomeBlockButton.IsEnabled = true; }
        PaintHome();
    }
}
