using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace SteamStopper.Core;

public sealed record Cheevo(string Title, string Description, int Points, string Badge, bool Earned)
{
    public string BadgeUrl => $"https://media.retroachievements.org/Badge/{Badge}{(Earned ? "" : "_lock")}.png";
    public string Tip => $"{Title} ({Points} pts){Environment.NewLine}{Description}{(Earned ? "" : Environment.NewLine + "Not earned yet")}";
}

public sealed record CheevoGame(int Id, string Title, int Total, int Earned, List<Cheevo> List)
{
    public string Url => $"https://retroachievements.org/game/{Id}";
    public double Share => Total == 0 ? 0 : Earned / (double)Total;
}

/// <summary>
/// RetroAchievements: find a library game in RetroAchievements' game list by title, then read the
/// player's progress with their username and Web API key (retroachievements.org/settings).
/// </summary>
public static class Cheevos
{
    // RetroAchievements console ids for each console tile. Several where one tile covers more systems.
    public static readonly Dictionary<string, int[]> Consoles = new()
    {
        ["PS1"] = [12], ["PS2"] = [21], ["PSP"] = [41], ["N64"] = [2], ["DS"] = [18], ["GAMECUBE"] = [16],
        ["DREAMCAST"] = [40], ["GAME BOY"] = [5, 6, 4], ["NES · SNES"] = [3, 7]
    };

    private const string Api = "https://retroachievements.org/API/";

    private static string Auth(string user, string key) => $"z={Uri.EscapeDataString(user)}&y={Uri.EscapeDataString(key)}";

    /// <summary>The RetroAchievements game id for a title, from the console's game list (cached on disk for a week).</summary>
    public static async Task<int?> FindGame(string console, string file, string title, string user, string key, HttpClient http, string cacheDir)
    {
        if (!Consoles.TryGetValue(console, out var ids)) return null;
        foreach (var id in ids)
        {
            var path = Path.Combine(cacheDir, $"ra-{id}.json");
            string json;
            if (File.Exists(path) && File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddDays(-7)) json = await File.ReadAllTextAsync(path);
            else
            {
                json = await http.GetStringAsync($"{Api}API_GetGameList.php?i={id}&f=1&{Auth(user, key)}");
                Directory.CreateDirectory(cacheDir);
                await File.WriteAllTextAsync(path, json);
            }
            if (Match(json, file, title) is { } game) return game;
        }
        return null;
    }

    /// <summary>The game in a GetGameList answer with the same title, the main set before hacks, subsets and demos.</summary>
    public static int? Match(string gameListJson, string file, string title)
    {
        var keys = new[] { RomLibrary.Key(file), RomLibrary.Key(title) };
        using var doc = JsonDocument.Parse(gameListJson);
        return doc.RootElement.EnumerateArray()
            .Select(g => (Id: g.GetProperty("ID").GetInt32(), Title: g.GetProperty("Title").GetString() ?? ""))
            .Where(g => keys.Contains(RomLibrary.Key(g.Title)))
            .OrderBy(g => g.Title.Contains('~') || g.Title.Contains("[Subset") ? 1 : 0)
            .Select(g => (int?)g.Id)
            .FirstOrDefault();
    }

    public static async Task<CheevoGame> Progress(int gameId, string user, string key, HttpClient http) =>
        ParseProgress(await http.GetStringAsync($"{Api}API_GetGameInfoAndUserProgress.php?g={gameId}&u={Uri.EscapeDataString(user)}&{Auth(user, key)}"));

    public static CheevoGame ParseProgress(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var list = root.TryGetProperty("Achievements", out var all) && all.ValueKind == JsonValueKind.Object
            ? all.EnumerateObject().Select(p => p.Value).Select(a => new Cheevo(
                    a.GetProperty("Title").GetString() ?? "",
                    a.TryGetProperty("Description", out var d) ? d.GetString() ?? "" : "",
                    a.TryGetProperty("Points", out var pts) && pts.ValueKind == JsonValueKind.Number ? pts.GetInt32() : 0,
                    a.TryGetProperty("BadgeName", out var b) ? b.GetString() ?? "" : "",
                    a.TryGetProperty("DateEarned", out var date) && date.ValueKind == JsonValueKind.String))
                // Earned first, like the site; then by points.
                .OrderByDescending(c => c.Earned).ThenByDescending(c => c.Points).ToList()
            : [];
        return new CheevoGame(root.GetProperty("ID").GetInt32(), root.GetProperty("Title").GetString() ?? "", list.Count, list.Count(c => c.Earned), list);
    }
}
