using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamStopper.Core;

public static class Updater
{
    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "2.1.0";

    public static bool IsInstalled =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, ".installed"));

    public static string? FeedUrl()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "FeedUrl.txt");
            if (!File.Exists(path)) return null;
            foreach (var line in File.ReadAllLines(path))
            {
                var text = line.Trim();
                if (text.Length == 0 || text.StartsWith('#')) continue;
                return text;
            }
        }
        catch { }
        return null;
    }

    public static async Task<(string Message, string? ApplyScript)> CheckAsync(Action<string>? status = null)
    {
        if (!IsInstalled)
            return ($"Steam Stopper {Version}", null);

        var feed = FeedUrl();
        if (string.IsNullOrWhiteSpace(feed))
            return ($"Steam Stopper {Version}", null);

        if (!TryGitHub(feed, out var owner, out var repo))
            return ($"Steam Stopper {Version}", null);

        try
        {
            status?.Invoke("Checking for updates…");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SteamStopper/" + Version);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            var json = await http.GetStringAsync($"https://api.github.com/repos/{owner}/{repo}/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var tag = doc.RootElement.GetProperty("tag_name").GetString()?.Trim().TrimStart('v');
            if (!System.Version.TryParse(tag, out var remote) || !System.Version.TryParse(Version, out var local) || remote <= local)
                return ($"Steam Stopper {Version} is up to date.", null);

            string? zipUrl = null;
            string? fallback = null;
            foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Contains("portable", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Setup", StringComparison.OrdinalIgnoreCase))
                    continue;
                var url = asset.GetProperty("browser_download_url").GetString();
                if (name.Equals("SteamStopper.zip", StringComparison.OrdinalIgnoreCase))
                {
                    zipUrl = url;
                    break;
                }
                fallback ??= url;
            }
            zipUrl ??= fallback;
            if (string.IsNullOrWhiteSpace(zipUrl))
                return ($"Update {remote} exists but has no zip attached.", null);

            status?.Invoke($"Downloading {remote}…");
            var tempRoot = Path.Combine(Path.GetTempPath(), "SteamStopperUpdate");
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
            Directory.CreateDirectory(tempRoot);
            var zipPath = Path.Combine(tempRoot, "update.zip");
            using (var data = await http.GetStreamAsync(zipUrl))
            using (var file = File.Create(zipPath))
                await data.CopyToAsync(file);
            ZipFile.ExtractToDirectory(zipPath, Path.Combine(tempRoot, "files"), true);

            var dest = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var script = Path.Combine(tempRoot, "apply.cmd");
            File.WriteAllText(script, $"""
@echo off
timeout /t 2 /nobreak >nul
xcopy /E /Y /Q "{Path.Combine(tempRoot, "files")}\*" "{dest}\" >nul
start "" "{Path.Combine(dest, "SteamStopper.exe")}"
""");
            return ($"Restarting into {remote}…", script);
        }
        catch (Exception ex)
        {
            return ("Update check skipped: " + ex.Message, null);
        }
    }

    private static bool TryGitHub(string feed, out string owner, out string repo)
    {
        owner = "";
        repo = "";
        var match = Regex.Match(feed.Trim().TrimEnd('/'), @"github\.com/([^/]+)/([^/]+)", RegexOptions.IgnoreCase);
        if (!match.Success) return false;
        owner = match.Groups[1].Value;
        repo = match.Groups[2].Value.Replace(".git", "");
        return owner.Length > 0 && repo.Length > 0;
    }
}
