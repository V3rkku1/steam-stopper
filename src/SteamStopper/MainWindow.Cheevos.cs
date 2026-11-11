using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using SteamStopper.Core;

namespace SteamStopper;

// RetroAchievements: login in Settings, progress and badges on each game's page.
public partial class MainWindow
{
    private static readonly HttpClient CheevoHttp = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly Dictionary<string, (DateTime At, CheevoGame? Game)> _cheevoCache = [];
    private int _cheevoRun;

    static MainWindow() => CheevoHttp.DefaultRequestHeaders.UserAgent.ParseAdd("SteamStopper");

    private string RaKey()
    {
        if (_settings.RaKeyProtected.Length == 0) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(_settings.RaKeyProtected), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; } // saved by another Windows user or damaged
    }

    private bool RaConnected => _settings.RaUser.Length > 0 && RaKey().Length > 0;

    private void InitCheevos()
    {
        RaUserBox.Text = _settings.RaUser;
        RaStatus.Text = RaConnected ? $"Connected as {_settings.RaUser}." : "Not connected.";
    }

    private async void RaSave_Click(object sender, RoutedEventArgs e)
    {
        var user = RaUserBox.Text.Trim();
        var key = RaKeyBox.Password.Trim();
        if (key.Length == 0) key = RaKey(); // only the username changed
        if (user.Length == 0 || key.Length == 0)
        {
            RaStatus.Text = "Enter your username and Web API key.";
            return;
        }
        RaStatus.Text = "Checking…";
        try
        {
            // Any authenticated call works as a check; the profile is the smallest.
            await CheevoHttp.GetStringAsync($"https://retroachievements.org/API/API_GetUserProfile.php?u={Uri.EscapeDataString(user)}&z={Uri.EscapeDataString(user)}&y={Uri.EscapeDataString(key)}");
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            RaStatus.Text = "RetroAchievements didn't accept that username and key.";
            return;
        }
        catch (Exception ex)
        {
            RaStatus.Text = "Couldn't reach RetroAchievements: " + ex.Message;
            return;
        }
        _settings.RaUser = user;
        _settings.RaKeyProtected = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser));
        _settings.Save();
        RaKeyBox.Clear();
        _cheevoCache.Clear();
        RaStatus.Text = $"Connected as {user}. Your achievements show on each game's page.";
    }

    private void RaClear_Click(object sender, RoutedEventArgs e)
    {
        _settings.RaUser = _settings.RaKeyProtected = "";
        _settings.Save();
        RaUserBox.Clear();
        RaKeyBox.Clear();
        _cheevoCache.Clear();
        RaStatus.Text = "Disconnected. The key is removed from this PC.";
    }

    private void RaKeyPage_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://retroachievements.org/settings") { UseShellExecute = true })?.Dispose();

    /// <summary>Fill the achievements section for the selected game; quiet when not connected or no set exists.</summary>
    private async void ShowCheevos(Rom? rom)
    {
        var run = ++_cheevoRun;
        GameCheevos.Visibility = Visibility.Collapsed;
        if (rom is null || !RaConnected || !Cheevos.Consoles.ContainsKey(rom.Console)) return;
        GameCheevos.Visibility = Visibility.Visible;
        GameCheevosText.Text = "Loading your achievements…";
        GameCheevosBar.Visibility = Visibility.Collapsed;
        GameCheevosList.ItemsSource = null;
        GameCheevosLink.Visibility = Visibility.Collapsed;
        CheevoGame? game;
        try
        {
            if (_cheevoCache.TryGetValue(rom.Path, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(5)) game = hit.Game;
            else
            {
                var user = _settings.RaUser;
                var key = RaKey();
                var id = await Cheevos.FindGame(rom.Console, System.IO.Path.GetFileNameWithoutExtension(rom.Path), rom.Title, user, key, CheevoHttp, RomLibrary.CoverDir);
                game = id is { } found ? await Cheevos.Progress(found, user, key, CheevoHttp) : null;
                _cheevoCache[rom.Path] = (DateTime.UtcNow, game);
            }
        }
        catch (Exception ex)
        {
            if (run == _cheevoRun) GameCheevosText.Text = "Couldn't load achievements: " + ex.Message;
            return;
        }
        if (run != _cheevoRun) return; // another game was picked meanwhile
        if (game is null || game.Total == 0)
        {
            GameCheevosText.Text = "RetroAchievements has no achievements for this game.";
            return;
        }
        GameCheevosText.Text = $"{game.Earned} of {game.Total} earned  ·  {game.Share:P0}";
        GameCheevosBar.Visibility = Visibility.Visible;
        GameCheevosBar.Value = game.Share;
        GameCheevosList.ItemsSource = game.List;
        GameCheevosLink.Tag = game.Url;
        GameCheevosLink.Visibility = Visibility.Visible;
    }

    private void GameCheevosLink_Click(object sender, RoutedEventArgs e)
    {
        if (GameCheevosLink.Tag is string url) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
    }
}
