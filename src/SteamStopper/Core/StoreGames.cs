using System.IO;
using System.Text.Json;

namespace SteamStopper.Core;

/// <summary>
/// PC games installed through Steam, the Epic Games Launcher and Heroic, read from each launcher's own files.
/// They join the Games library with a launcher link as their path; the launcher starts them.
/// </summary>
public static class StoreGames
{
    /// <summary>Steam games fully installed in these libraries. Covers come from Steam's own cache when it has them.</summary>
    public static List<Rom> Steam(IEnumerable<string> libraries, string? steamRoot)
    {
        var games = new List<Rom>();
        foreach (var library in libraries)
        {
            var apps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                try
                {
                    var data = Vdf.Parse(File.ReadAllText(acf));
                    var app = data.TryGetValue("AppState", out var state) ? Vdf.AsObject(state) : data;
                    var id = Vdf.GetString(app, "appid");
                    // 4 = fully installed; 228980 is Steamworks Common Redistributables, not a game.
                    if (id.Length == 0 || id == "228980" || (Vdf.GetLong(app, "StateFlags") & 4) == 0) continue;
                    games.Add(new Rom { Path = $"steam://rungameid/{id}", Title = Vdf.GetString(app, "name", id), Console = "STEAM", ArtUrl = SteamArt(steamRoot, id) });
                }
                catch { } // a manifest Steam is rewriting right now
            }
        }
        return games;
    }

    // Steam keeps library art as <id>_library_600x900.jpg, or in a folder per game since 2024.
    private static string SteamArt(string? root, string id)
    {
        if (root is not null)
        {
            var cache = Path.Combine(root, "appcache", "librarycache");
            foreach (var local in new[] { Path.Combine(cache, $"{id}_library_600x900.jpg"), Path.Combine(cache, id, "library_600x900.jpg") })
                if (File.Exists(local)) return local;
        }
        return $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{id}/library_600x900.jpg";
    }

    public static string EpicManifests =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");

    /// <summary>Games the Epic Games Launcher installed, from its .item manifests. DLC and half-finished installs are left out.</summary>
    public static List<Rom> Epic(string manifests)
    {
        var games = new List<Rom>();
        if (!Directory.Exists(manifests)) return games;
        foreach (var item in Directory.EnumerateFiles(manifests, "*.item"))
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(item));
                var m = json.RootElement;
                string Text(string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
                var app = Text("AppName");
                if (app.Length == 0 || Text("MainGameAppName") is { Length: > 0 } main && main != app) continue;
                if (m.TryGetProperty("bIsIncompleteInstall", out var incomplete) && incomplete.ValueKind == JsonValueKind.True) continue;
                games.Add(new Rom
                {
                    Path = $"com.epicgames.launcher://apps/{Uri.EscapeDataString(Text("CatalogNamespace"))}%3A{Uri.EscapeDataString(Text("CatalogItemId"))}%3A{Uri.EscapeDataString(app)}?action=launch&silent=true",
                    Title = Text("DisplayName") is { Length: > 0 } name ? name : app,
                    Console = "EPIC"
                });
            }
            catch { }
        }
        return games;
    }

    public static string HeroicFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "heroic");

    /// <summary>Games Heroic installed: Epic (legendary), GOG and added apps. Names and square art come from Heroic's library cache.</summary>
    public static List<Rom> Heroic(string heroic)
    {
        var games = new List<Rom>();
        if (!Directory.Exists(heroic)) return games;

        // app_name -> (title, art) from the library caches Heroic keeps for each store.
        var known = new Dictionary<string, (string Title, string Art)>();
        foreach (var (file, list) in new[] { ("legendary_library.json", "library"), ("gog_library.json", "games") })
            foreach (var game in Items(Path.Combine(heroic, "store_cache", file), list))
                known[Str(game, "app_name")] = (Str(game, "title"), Str(game, "art_square"));

        Rom Link(string app, string runner, string title, string art)
        {
            known.TryGetValue(app, out var cached);
            return new()
            {
                Path = $"heroic://launch?appName={Uri.EscapeDataString(app)}&runner={runner}",
                Title = title is { Length: > 0 } ? title : cached.Title is { Length: > 0 } ? cached.Title : app,
                Console = "HEROIC",
                ArtUrl = art is { Length: > 0 } ? art : cached.Art ?? ""
            };
        }

        // Epic through legendary: { "<app>": { "title": …, "is_dlc": … }, … }
        if (Read(Path.Combine(heroic, "legendaryConfig", "legendary", "installed.json")) is { ValueKind: JsonValueKind.Object } legendary)
            foreach (var game in legendary.EnumerateObject())
                if (!(game.Value.TryGetProperty("is_dlc", out var dlc) && dlc.ValueKind == JsonValueKind.True))
                    games.Add(Link(game.Name, "legendary", Str(game.Value, "title"), ""));

        // GOG: { "installed": [ { "appName": … } ] }
        foreach (var game in Items(Path.Combine(heroic, "gog_store", "installed.json"), "installed"))
            if (Str(game, "appName") is { Length: > 0 } app) games.Add(Link(app, "gog", "", ""));

        // Apps added to Heroic by hand: { "games": [ { "app_name", "title", "is_installed", "art_square" } ] }
        foreach (var game in Items(Path.Combine(heroic, "sideload_apps", "library.json"), "games"))
            if (game.TryGetProperty("is_installed", out var on) && on.ValueKind == JsonValueKind.True && Str(game, "app_name") is { Length: > 0 } app)
                games.Add(Link(app, "sideload", Str(game, "title"), Str(game, "art_square")));

        return games;
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static JsonElement? Read(string file)
    {
        try { return File.Exists(file) ? JsonDocument.Parse(File.ReadAllText(file)).RootElement.Clone() : null; }
        catch { return null; }
    }

    private static IEnumerable<JsonElement> Items(string file, string list) =>
        Read(file) is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty(list, out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().ToList() : [];
}
