using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SteamStopper.Core;

/// <summary>One game file in the library. Console keys are the tile names in <see cref="AppHub.Consoles"/>.</summary>
public sealed class Rom : INotifyPropertyChanged
{
    private string _console = "";
    private string? _cover;

    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
    public string Console
    {
        get => _console;
        set { _console = value; OnChanged(); OnChanged(nameof(Color)); OnChanged(nameof(ConsoleName)); }
    }
    public string Emulator { get; set; } = "";
    public string CustomCover { get; set; } = "";
    public DateTime Added { get; set; } = DateTime.Now;
    public DateTime? LastPlayed { get; set; }
    public double PlaySeconds { get; set; }

    /// <summary>Cover image on disk: the user's own, else a downloaded box art. Null shows the console colour.</summary>
    [JsonIgnore]
    public string? Cover
    {
        get => _cover;
        set { _cover = value; OnChanged(); }
    }
    [JsonIgnore] public ConsoleTile? Tile => AppHub.Consoles.FirstOrDefault(c => c.Name == Console);
    [JsonIgnore] public string Color => Tile?.Color ?? "#4A4A55";
    [JsonIgnore] public string ConsoleName => Tile?.Maker ?? "Unknown console";
    [JsonIgnore] public string Initials => string.Concat(Title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));
    [JsonIgnore] public string PlayTimeText => PlaySeconds < 60 ? (LastPlayed is null ? "Never played" : "Under a minute")
        : PlaySeconds < 90 ? "1 minute" : PlaySeconds < 3600 ? $"{PlaySeconds / 60:0} minutes" : $"{PlaySeconds / 3600:0.0} hours";
    [JsonIgnore] public string LastPlayedText => LastPlayed is not { } t ? "Never"
        : t.Date == DateTime.Today ? "Today" : t.Date == DateTime.Today.AddDays(-1) ? "Yesterday" : t.ToString("d MMM yyyy");
    // Under a cover on the Home shelf: when it was played, or its console when it never was.
    [JsonIgnore] public string ShelfText => LastPlayed is null ? ConsoleName : "Played " + (LastPlayed.Value.Date >= DateTime.Today.AddDays(-1) ? LastPlayedText.ToLowerInvariant() : LastPlayedText);
    [JsonIgnore] public bool Exists => File.Exists(Path) || Directory.Exists(Path);
    [JsonIgnore] public string SizeText => File.Exists(Path) ? Format.Bytes(new FileInfo(Path).Length) : Directory.Exists(Path) ? "Game folder" : "File missing";

    public void Refresh() => OnChanged(null);
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>The Games tab: folders of game files, what console each is, how to start it, and box art.</summary>
public static class RomLibrary
{
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".nsp"] = "SWITCH", [".xci"] = "SWITCH",
        // Switch 2 archive used by Lichii and Eglantine: base game plus updates in one file (magic "NUP\x01").
        [".nup"] = "SWITCH 2",
        [".3ds"] = "3DS", [".cci"] = "3DS", [".3dsx"] = "3DS",
        [".nds"] = "DS",
        [".wua"] = "WII U", [".wux"] = "WII U", [".wud"] = "WII U", [".rpx"] = "WII U",
        [".wbfs"] = "WII", [".rvz"] = "WII", [".gcz"] = "GAMECUBE", [".ciso"] = "GAMECUBE",
        [".gba"] = "GAME BOY", [".gbc"] = "GAME BOY", [".gb"] = "GAME BOY",
        [".nes"] = "NES · SNES", [".sfc"] = "NES · SNES", [".smc"] = "NES · SNES",
        [".n64"] = "N64", [".z64"] = "N64", [".v64"] = "N64",
        [".cso"] = "PSP", [".pbp"] = "PSP",
        [".cue"] = "PS1", [".chd"] = "PS1", [".m3u"] = "PS1",
        [".gdi"] = "DREAMCAST", [".cdi"] = "DREAMCAST",
        [".xex"] = "XBOX 360",
        [".zar"] = "PS5",
        [".iso"] = ""
    };

    // How each emulator opens a game from the command line. {rom} is the full path.
    // Batch flags make the emulator close when the game does, so play time stops counting.
    public static readonly Dictionary<string, string> LaunchArgs = new()
    {
        ["DuckStation"] = "-batch -- \"{rom}\"",
        ["PCSX2"] = "-batch -- \"{rom}\"",
        ["PPSSPP"] = "\"{rom}\"",
        ["Eden"] = "-g \"{rom}\"",
        ["Lichii"] = "-g \"{rom}\"",
        ["Eglantine"] = "-g \"{rom}\"",
        ["Ryujinx"] = "\"{rom}\"",
        ["Azahar"] = "\"{rom}\"",
        ["melonDS"] = "\"{rom}\"",
        ["Cemu"] = "-g \"{rom}\"",
        ["Dolphin"] = "-b -e \"{rom}\"",
        ["xemu"] = "-dvd_path \"{rom}\"",
        ["Xenia Canary"] = "\"{rom}\"",
        ["mGBA"] = "\"{rom}\"",
        ["Mesen"] = "\"{rom}\"",
        ["Project64"] = "\"{rom}\"",
        ["ares"] = "\"{rom}\"",
        ["KytyPS5"] = "--game \"{rom}\"",
        ["redream"] = "\"{rom}\""
    };

    // Emulators whose games start from a different exe than the one Play opens.
    public static readonly Dictionary<string, string> GameExe = new() { ["KytyPS5"] = "kyty_emulator.exe" };

    // libretro-thumbnails folders per console, tried in order; Switch and Wii U use GameTDB below.
    private static readonly Dictionary<string, string[]> ThumbSystems = new()
    {
        ["PS1"] = ["Sony - PlayStation"], ["PS2"] = ["Sony - PlayStation 2"], ["PSP"] = ["Sony - PlayStation Portable"],
        ["GAMECUBE"] = ["Nintendo - GameCube"], ["WII"] = ["Nintendo - Wii"], ["DS"] = ["Nintendo - Nintendo DS"],
        ["3DS"] = ["Nintendo - Nintendo 3DS", "Nintendo - Nintendo 3DS (Digital)"], ["N64"] = ["Nintendo - Nintendo 64"],
        ["DREAMCAST"] = ["Sega - Dreamcast"], ["XBOX"] = ["Microsoft - Xbox"], ["XBOX 360"] = ["Microsoft - Xbox 360"],
        ["NES · SNES"] = ["Nintendo - Super Nintendo Entertainment System", "Nintendo - Nintendo Entertainment System"],
        ["GAME BOY"] = ["Nintendo - Game Boy Advance", "Nintendo - Game Boy Color", "Nintendo - Game Boy"]
    };

    // GameTDB (database zip, art folder) for consoles libretro has no box art for. Art is named by GameTDB's own game id.
    private static readonly Dictionary<string, (string Db, string Art)> TdbSystems = new()
    {
        ["SWITCH"] = ("switchtdb", "switch"), ["WII U"] = ("wiiutdb", "wiiu")
    };

    private static string Dir => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamStopper");
    private static string StorePath => System.IO.Path.Combine(Dir, "games.json");
    public static string CoverDir => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamStopper", "Covers");

    public sealed class Store
    {
        public List<string> Folders { get; set; } = [];
        public List<Rom> Games { get; set; } = [];
    }

    public static Store Load()
    {
        try
        {
            if (File.Exists(StorePath))
                return JsonSerializer.Deserialize<Store>(File.ReadAllText(StorePath)) ?? new Store();
        }
        catch { }
        return new Store();
    }

    public static void Save(Store store)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = StorePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, StorePath, true);
        }
        catch { }
    }

    /// <summary>Add every game file under the folders that isn't in the library yet. Returns how many were added.</summary>
    public static int Scan(Store store)
    {
        var known = new HashSet<string>(store.Games.Select(g => g.Path), StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var folder in store.Folders.Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }); }
            catch { continue; }
            foreach (var file in files)
            {
                // PS5 dumps are folders: eboot.bin next to sce_sys\param.json (PS4 uses param.sfo instead).
                if (System.IO.Path.GetFileName(file).Equals("eboot.bin", StringComparison.OrdinalIgnoreCase)
                    && System.IO.Path.GetDirectoryName(file) is { } game && !known.Contains(game)
                    && File.Exists(System.IO.Path.Combine(game, "sce_sys", "param.json")))
                {
                    store.Games.Add(new Rom { Path = game, Title = Ps5Title(game), Console = "PS5" });
                    known.Add(game);
                    added++;
                    continue;
                }
                if (known.Contains(file) || ConsoleOf(file) is not { } console) continue;
                store.Games.Add(new Rom { Path = file, Title = TitleOf(file), Console = console });
                known.Add(file);
                added++;
            }
        }
        return added;
    }

    /// <summary>The game's name from param.json in its default language, else the folder name.</summary>
    public static string Ps5Title(string folder)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(folder, "sce_sys", "param.json")));
            var local = doc.RootElement.GetProperty("localizedParameters");
            var language = local.GetProperty("defaultLanguage").GetString() ?? "en-US";
            if (local.GetProperty(language).GetProperty("titleName").GetString() is { Length: > 0 } name) return name;
        }
        catch { }
        return System.IO.Path.GetFileName(folder.TrimEnd('\\', '/'));
    }

    /// <summary>Console for a game file, "" when it is a game but the console is unclear, null when it isn't a game.</summary>
    public static string? ConsoleOf(string file)
    {
        if (!ByExtension.TryGetValue(System.IO.Path.GetExtension(file), out var console)) return null;
        // .nup is also used by unrelated firmware updaters; only NUP-format archives are games.
        if (console == "SWITCH 2") return HasMagic(file, "NUP\x01"u8) ? console : null;
        // Multi-disc .m3u lists and loose .cue sheets: one entry per game, not per disc track.
        if (console == "PS1" && !file.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase) && InM3u(file)) return null;
        return console.Length > 0 && console is not ("WII" or "GAMECUBE") ? console : Sniff(file) ?? console;
    }

    private static bool InM3u(string cue)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(cue)!;
            var name = System.IO.Path.GetFileName(cue);
            return Directory.EnumerateFiles(dir, "*.m3u").Any(m => File.ReadLines(m).Any(l => l.Trim().Equals(name, StringComparison.OrdinalIgnoreCase)));
        }
        catch { return false; }
    }

    /// <summary>Read a disc image's header to tell PS2, PSP, GameCube, Wii and Xbox apart.</summary>
    public static string? Sniff(string file)
    {
        try
        {
            using var s = File.OpenRead(file);
            var head = Read(s, 0, 1024);
            // Nintendo disc magic; RVZ and WBFS keep a copy of the disc header near the start.
            if (Find(head, [0xC2, 0x33, 0x9F, 0x3D])) return "GAMECUBE";
            if (Find(head, [0x5D, 0x1C, 0x9E, 0xA3])) return "WII";
            var volume = Encoding.ASCII.GetString(Read(s, 0x8000, 64));
            if (volume.Contains("PSP GAME")) return "PSP";
            if (volume.Contains("PLAYSTATION")) return "PS2";
            const string xbox = "MICROSOFT*XBOX*MEDIA";
            if (Encoding.ASCII.GetString(Read(s, 0x10000, 20)) == xbox) return "XBOX";
            // Xbox 360 discs put the game partition after a video partition.
            foreach (var offset in new long[] { 0xFDA0000, 0x2080000, 0x18300000 })
                if (Encoding.ASCII.GetString(Read(s, offset + 0x10000, 20)) == xbox) return "XBOX 360";
        }
        catch { }
        return null;
    }

    private static bool HasMagic(string file, ReadOnlySpan<byte> magic)
    {
        try
        {
            using var s = File.OpenRead(file);
            return Read(s, 0, magic.Length).AsSpan().SequenceEqual(magic);
        }
        catch { return false; }
    }

    private static byte[] Read(Stream s, long offset, int count)
    {
        if (offset + count > s.Length) return [];
        s.Position = offset;
        var buffer = new byte[count];
        s.ReadExactly(buffer);
        return buffer;
    }

    private static bool Find(byte[] data, byte[] magic) => data.AsSpan().IndexOf(magic) >= 0;

    /// <summary>"Super Mario 64 (USA) [!].z64" becomes "Super Mario 64".</summary>
    public static string TitleOf(string file)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(file);
        var clean = Regex.Replace(name, @"\s*[\(\[][^\)\]]*[\)\]]", "").Replace('_', ' ').Trim();
        // No-Intro moves articles back: "Legend of Zelda, The - Ocarina of Time" reads "The Legend of Zelda - Ocarina of Time".
        clean = Regex.Replace(clean, @"^(.+?), (The|A|An)\b(.*)$", "$2 $1$3");
        return clean.Length > 0 ? clean : name;
    }

    public static string Arguments(string emulator, string rom) =>
        LaunchArgs.TryGetValue(emulator, out var template) ? template.Replace("{rom}", rom) : $"\"{rom}\"";

    /// <summary>Local box art for a game, downloading it from libretro-thumbnails or GameTDB the first time.</summary>
    public static async Task<string?> CoverFor(Rom rom, HttpClient http)
    {
        if (rom.CustomCover.Length > 0 && File.Exists(rom.CustomCover)) return rom.CustomCover;
        // PS5 folder dumps carry their own art.
        if (Directory.Exists(rom.Path) && System.IO.Path.Combine(rom.Path, "sce_sys", "icon0.png") is var icon && File.Exists(icon)) return icon;
        var hasThumbs = ThumbSystems.TryGetValue(rom.Console, out var systems);
        var hasTdb = TdbSystems.TryGetValue(rom.Console, out var tdb);
        if (!hasThumbs && !hasTdb) return null;
        var file = System.IO.Path.GetFileNameWithoutExtension(rom.Path);
        var cachedBase = System.IO.Path.Combine(CoverDir, Safe($"{rom.Console} - {file}"));
        if (new[] { ".png", ".jpg" }.Select(e => cachedBase + e).FirstOrDefault(File.Exists) is { } hit) return hit;
        var miss = cachedBase + ".png.none";
        if (File.Exists(miss)) return null;
        try
        {
            Directory.CreateDirectory(CoverDir);
            var urls = new List<string>();
            foreach (var system in systems ?? [])
                if (BestMatch(file, rom.Title, await IndexOf(system, http)) is { } name)
                    urls.Add($"https://thumbnails.libretro.com/{Uri.EscapeDataString(system)}/Named_Boxarts/{Uri.EscapeDataString(name)}.png");
            if (hasTdb && TdbPick(file, rom.Title, await TdbIndexOf(tdb.Db, http)) is { } game)
                urls.Add($"https://art.gametdb.com/{tdb.Art}/coverM/{game.Region}/{game.Id}.jpg");
            foreach (var url in urls)
            {
                using var response = await http.GetAsync(url);
                if (!response.IsSuccessStatusCode) continue;
                var cached = cachedBase + System.IO.Path.GetExtension(url);
                await File.WriteAllBytesAsync(cached, await response.Content.ReadAsByteArrayAsync());
                return cached;
            }
            // Remember misses so every visit doesn't ask again.
            File.WriteAllText(miss, "");
        }
        catch { }
        return null;
    }

    private static readonly Dictionary<string, Task<ILookup<string, string>>> TdbIndexes = [];

    /// <summary>GameTDB's games for a console as "name|id|art region" lines, keyed by title, kept on disk for a month.</summary>
    private static Task<ILookup<string, string>> TdbIndexOf(string db, HttpClient http)
    {
        lock (TdbIndexes)
        {
            if (!TdbIndexes.TryGetValue(db, out var task) || task.IsFaulted) TdbIndexes[db] = task = Fetch();
            return task;
        }

        async Task<ILookup<string, string>> Fetch()
        {
            var path = System.IO.Path.Combine(CoverDir, db + ".index");
            if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-30))
            {
                using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(await http.GetByteArrayAsync($"https://www.gametdb.com/{db}.zip?LANG=EN")));
                using var reader = new StreamReader(zip.Entries[0].Open());
                await File.WriteAllLinesAsync(path, TdbLines(await reader.ReadToEndAsync()));
            }
            return TdbByKey(await File.ReadAllLinesAsync(path));
        }
    }

    /// <summary>"name|id|art region" for every game in a GameTDB xml.</summary>
    public static IEnumerable<string> TdbLines(string xml) =>
        Regex.Matches(xml, @"<game name=""([^""]+)"">\s*<id>([^<]+)</id>\s*<type>[^<]*</type>\s*<region>([^<]+)</region>")
            .Select(m => $"{System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Replace('|', ' ')}|{m.Groups[2].Value}|{ArtRegion(m.Groups[3].Value)}");

    // Regions are lists like "USA,EUR" or "ALL"; art folders are US, EN (Europe and worldwide) and JA.
    private static string ArtRegion(string regions) =>
        regions.Contains("USA") ? "US" : regions.Contains("ALL") || regions.Contains("EUR") || !regions.StartsWith("JPN") ? "EN" : "JA";

    public static ILookup<string, string> TdbByKey(IEnumerable<string> lines) => lines.ToLookup(l => Key(l.Split('|')[0]));

    /// <summary>The GameTDB game for a file, the US release first.</summary>
    public static (string Id, string Region)? TdbPick(string file, string title, ILookup<string, string> games) =>
        games[Key(file)].Concat(games[Key(title)])
            .Select(l => l.Split('|'))
            .OrderBy(p => p[2] == "US" ? 0 : p[2] == "EN" ? 1 : 2)
            .Select(p => ((string, string)?)(p[1], p[2]))
            .FirstOrDefault();

    private static readonly Dictionary<string, Task<ILookup<string, string>>> Indexes = [];

    /// <summary>Every box art name libretro has for a system, from its folder listing, kept on disk for a week.</summary>
    private static Task<ILookup<string, string>> IndexOf(string system, HttpClient http)
    {
        lock (Indexes)
        {
            if (!Indexes.TryGetValue(system, out var task) || task.IsFaulted) Indexes[system] = task = Fetch();
            return task;
        }

        async Task<ILookup<string, string>> Fetch()
        {
            var path = System.IO.Path.Combine(CoverDir, Safe(system) + ".index");
            if (File.Exists(path) && File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddDays(-7)) return ByKey(await File.ReadAllLinesAsync(path));
            var html = await http.GetStringAsync($"https://thumbnails.libretro.com/{Uri.EscapeDataString(system)}/Named_Boxarts/");
            var names = Regex.Matches(html, @"href=""([^""?/]+)\.png""").Select(m => Uri.UnescapeDataString(m.Groups[1].Value)).ToArray();
            await File.WriteAllLinesAsync(path, names);
            return ByKey(names);
        }
    }

    /// <summary>The libretro name for a game: the exact file name, else the same title with any region, the USA/Europe release first.</summary>
    public static string? BestMatch(string file, string title, ILookup<string, string> names)
    {
        // libretro names art after the No-Intro/Redump file name, with these characters swapped for _.
        var exact = Regex.Replace(file, @"[&*/:`<>?\\|""]", "_");
        if (names[Key(exact)].Contains(exact)) return exact;
        return names[Key(file)].Concat(names[Key(title)])
            .OrderBy(n => n.Contains("(Beta") || n.Contains("(Proto") || n.Contains("(Demo") ? 1 : 0)
            .ThenBy(n => n.Contains("(USA") ? 0 : n.Contains("(World") ? 1 : n.Contains("(Europe") ? 2 : 3)
            .FirstOrDefault();
    }

    public static ILookup<string, string> ByKey(IEnumerable<string> names) => names.ToLookup(Key);

    // "Legend of Zelda, The - Ocarina of Time (USA) (Rev 1)" and "The_Legend_of_Zelda_Ocarina_of_Time" both become "legendofzeldaocarinaoftime".
    public static string Key(string name) =>
        Regex.Replace(Regex.Replace(TitleOf(name + ".x").ToLowerInvariant().Replace("&", "and").Replace('_', ' '), @"\bthe\b", ""), @"[^a-z0-9]", "");

    private static string Safe(string name) => string.Concat(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
