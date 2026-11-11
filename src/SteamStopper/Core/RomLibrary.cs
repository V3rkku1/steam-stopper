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
    private string _title = "";

    public string Path { get; set; } = "";
    public string Title
    {
        get => _title;
        set { _title = value; OnChanged(); OnChanged(nameof(Initials)); }
    }
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
        [".nsp"] = "SWITCH", [".xci"] = "SWITCH", [".nro"] = "SWITCH",
        // Switch 2 archive used by Lichii and Eglantine: base game plus updates in one file (magic "NUP\x01").
        [".nup"] = "SWITCH 2",
        [".3ds"] = "3DS", [".cci"] = "3DS", [".3dsx"] = "3DS", [".cxi"] = "3DS",
        [".nds"] = "DS",
        [".wua"] = "WII U", [".wux"] = "WII U", [".wud"] = "WII U", [".rpx"] = "WII U",
        [".wbfs"] = "WII", [".rvz"] = "WII", [".wad"] = "WII", [".gcz"] = "GAMECUBE", [".ciso"] = "GAMECUBE", [".gcm"] = "GAMECUBE",
        [".gba"] = "GAME BOY", [".gbc"] = "GAME BOY", [".gb"] = "GAME BOY",
        [".nes"] = "NES · SNES", [".sfc"] = "NES · SNES", [".smc"] = "NES · SNES", [".fds"] = "NES · SNES",
        [".n64"] = "N64", [".z64"] = "N64", [".v64"] = "N64",
        [".cso"] = "PSP", [".pbp"] = "PSP",
        [".cue"] = "PS1", [".chd"] = "PS1", [".m3u"] = "PS1",
        [".gdi"] = "DREAMCAST", [".cdi"] = "DREAMCAST",
        [".xex"] = "XBOX 360",
        [".zar"] = "PS5",
        [".vpk"] = "VITA",
        // PS3, PS4 and Vita share .pkg; Sniff reads which from its header.
        [".pkg"] = "",
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
        // RPCS3 boots a game folder (PS3_GAME\USRDIR\EBOOT.BIN inside); shadPS4 boots the folder's eboot.bin.
        ["RPCS3"] = "--no-gui \"{rom}\"",
        ["shadPS4"] = "-g \"{rom}\\eboot.bin\"",
        // Vita3K runs an installed game by its title id (the folder name under ux0\app).
        ["Vita3K"] = "-r {id}",
        ["RetroArch"] = "-L \"cores\\{core}_libretro.dll\" \"{rom}\"",
        ["redream"] = "\"{rom}\""
    };

    // Emulators whose games start from a different exe than the one Play opens.
    public static readonly Dictionary<string, string> GameExe = new() { ["KytyPS5"] = "kyty_emulator.exe" };

    // libretro-thumbnails folders per console, tried in order; Switch and Wii U use GameTDB below.
    private static readonly Dictionary<string, string[]> ThumbSystems = new()
    {
        ["PS1"] = ["Sony - PlayStation"], ["PS2"] = ["Sony - PlayStation 2"], ["PSP"] = ["Sony - PlayStation Portable"],
        ["GAMECUBE"] = ["Nintendo - GameCube"], ["WII"] = ["Nintendo - Wii"], ["DS"] = ["Nintendo - Nintendo DS", "Nintendo - Nintendo DSi"],
        ["WII U"] = ["Nintendo - Wii U"], ["PS4"] = ["Sony - PlayStation 4"], ["ARCADE"] = ["MAME", "FBNeo - Arcade Games"],
        ["3DS"] = ["Nintendo - Nintendo 3DS", "Nintendo - Nintendo 3DS (Digital)"], ["N64"] = ["Nintendo - Nintendo 64"],
        ["DREAMCAST"] = ["Sega - Dreamcast"], ["PS3"] = ["Sony - PlayStation 3"], ["VITA"] = ["Sony - PlayStation Vita"], ["XBOX"] = ["Microsoft - Xbox"], ["XBOX 360"] = ["Microsoft - Xbox 360"],
        ["NES · SNES"] = ["Nintendo - Super Nintendo Entertainment System", "Nintendo - Nintendo Entertainment System", "Nintendo - Family Computer Disk System"],
        ["GAME BOY"] = ["Nintendo - Game Boy Advance", "Nintendo - Game Boy Color", "Nintendo - Game Boy"]
    };

    // GameTDB (database zip, art folder) for consoles libretro has little or no box art for. Art is named by GameTDB's own game id.
    private static readonly Dictionary<string, (string Db, string Art)> TdbSystems = new()
    {
        ["SWITCH"] = ("switchtdb", "switch"), ["WII U"] = ("wiiutdb", "wiiu"), ["PS3"] = ("ps3tdb", "ps3")
    };

    // Nintendo's eShop search for consoles neither libretro nor GameTDB covers yet (code from its playable_on_txt field).
    private static readonly Dictionary<string, string> EshopSystems = new() { ["SWITCH 2"] = "BEE" };

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
    /// <param name="extra">More folders to look in that aren't the user's, such as where emulators install games.</param>
    public static int Scan(Store store, IEnumerable<string>? extra = null)
    {
        var known = new HashSet<string>(store.Games.Select(g => g.Path), StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var folder in store.Folders.Concat(extra ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }); }
            catch { continue; }
            foreach (var file in files)
            {
                if (FolderGame(file) is { } dump)
                {
                    if (known.Add(dump.Path)) { store.Games.Add(dump); added++; }
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

    /// <summary>A PS3, PS4 or PS5 folder dump this eboot.bin starts, else null. The game's path is its folder.</summary>
    public static Rom? FolderGame(string eboot)
    {
        if (!System.IO.Path.GetFileName(eboot).Equals("eboot.bin", StringComparison.OrdinalIgnoreCase) || System.IO.Path.GetDirectoryName(eboot) is not { } dir) return null;
        string P(params string[] parts) => System.IO.Path.Combine([dir, .. parts]);
        if (File.Exists(P("sce_sys", "param.json"))) return new Rom { Path = dir, Title = Ps5Title(dir), Console = "PS5" };
        // PS4 and Vita both keep sce_sys\param.sfo; Vita title ids start with PCS.
        if (File.Exists(P("sce_sys", "param.sfo")))
            return new Rom { Path = dir, Title = SfoTitle(P("sce_sys", "param.sfo")) ?? FolderName(dir),
                Console = (SfoValue(P("sce_sys", "param.sfo"), "TITLE_ID") ?? FolderName(dir)).StartsWith("PCS") ? "VITA" : "PS4" };
        // PS3: disc dumps keep USRDIR inside PS3_GAME, installed PSN games keep it next to PARAM.SFO.
        if (!System.IO.Path.GetFileName(dir).Equals("USRDIR", StringComparison.OrdinalIgnoreCase) || System.IO.Path.GetDirectoryName(dir) is not { } parent) return null;
        var sfo = System.IO.Path.Combine(parent, "PARAM.SFO");
        // Updates and DLC in RPCS3's game folder are game data (GD), not games.
        if (!File.Exists(sfo) || SfoValue(sfo, "CATEGORY") == "GD") return null;
        var root = System.IO.Path.GetFileName(parent).Equals("PS3_GAME", StringComparison.OrdinalIgnoreCase) ? System.IO.Path.GetDirectoryName(parent) ?? parent : parent;
        return new Rom { Path = root, Title = SfoTitle(sfo) ?? FolderName(root), Console = "PS3" };
    }

    private static string FolderName(string folder) => System.IO.Path.GetFileName(folder.TrimEnd('\\', '/'));

    /// <summary>TITLE from a PARAM.SFO (PS3, PS4, Vita, PSP), or null.</summary>
    public static string? SfoTitle(string sfo) => SfoValue(sfo, "TITLE")?.Replace('\n', ' ').Trim() is { Length: > 0 } t ? t : null;

    /// <summary>A text value from a PARAM.SFO, or null.</summary>
    public static string? SfoValue(string sfo, string name)
    {
        try
        {
            var b = File.ReadAllBytes(sfo);
            if (!b.AsSpan().StartsWith("\0PSF"u8)) return null;
            int U32(int at) => BitConverter.ToInt32(b, at);
            int keys = U32(8), data = U32(12), count = U32(16);
            for (var i = 0; i < count; i++)
            {
                var e = 20 + i * 16;
                var key = keys + BitConverter.ToUInt16(b, e);
                if (Encoding.ASCII.GetString(b, key, Array.IndexOf(b, (byte)0, key) - key) != name) continue;
                return Encoding.UTF8.GetString(b, data + U32(e + 12), U32(e + 4)).TrimEnd('\0');
            }
        }
        catch { }
        return null;
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
        return FolderName(folder);
    }

    /// <summary>Cheap check for the folder watcher: could this path be (part of) a game? No file is opened.</summary>
    public static bool LooksLikeGame(string path) =>
        ByExtension.ContainsKey(System.IO.Path.GetExtension(path)) || System.IO.Path.GetFileName(path).Equals("eboot.bin", StringComparison.OrdinalIgnoreCase);

    /// <summary>Console for a game file, "" when it is a game but the console is unclear, null when it isn't a game.</summary>
    public static string? ConsoleOf(string file)
    {
        if (!ByExtension.TryGetValue(System.IO.Path.GetExtension(file), out var console)) return null;
        // .nup is also used by unrelated firmware updaters; only NUP-format archives are games.
        if (console == "SWITCH 2") return HasMagic(file, "NUP\x01"u8) ? console : null;
        // Multi-disc .m3u lists and loose .cue sheets: one entry per game, not per disc track.
        if (console == "PS1" && !file.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase) && InM3u(file)) return null;
        // .chd, .cue and .m3u are PS1 unless a folder says otherwise; telling them apart by content means decompressing.
        if (console == "PS1" && FolderHint(file) is { } hinted) return hinted;
        return console.Length > 0 && console is not ("WII" or "GAMECUBE") ? console : Sniff(file) ?? console;
    }

    private static readonly Dictionary<string, string> FolderHints = new()
    {
        ["ps2"] = "PS2", ["playstation2"] = "PS2", ["sonyplaystation2"] = "PS2",
        ["dreamcast"] = "DREAMCAST", ["dc"] = "DREAMCAST", ["segadreamcast"] = "DREAMCAST",
        ["psp"] = "PSP", ["playstationportable"] = "PSP"
    };

    private static string? FolderHint(string file) =>
        System.IO.Path.GetDirectoryName(file)?.Split('\\', '/').Reverse()
            .Select(d => FolderHints.GetValueOrDefault(Regex.Replace(d.ToLowerInvariant(), "[^a-z0-9]", ""))).FirstOrDefault(c => c is not null);

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
            // PS4 packages start "\x7FCNT"; PS3, PSP and Vita ones "\x7FPKG" with a platform word at 6 (1 = PS3)
            // and a content id at 0x30 whose title id starts "PCS" for Vita games.
            if (head.AsSpan().StartsWith("\u007FCNT"u8)) return "PS4";
            if (head.AsSpan().StartsWith("\u007FPKG"u8))
                return head[7] == 1 ? "PS3" : Encoding.ASCII.GetString(head, 0x37, 3) == "PCS" ? "VITA" : null;
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

    // RetroArch core per console, started from its cores folder (the game starts in RetroArch's own folder).
    private static readonly Dictionary<string, string> RetroCores = new()
    {
        ["PS1"] = "swanstation", ["PSP"] = "ppsspp", ["N64"] = "mupen64plus_next", ["GAME BOY"] = "mgba",
        ["NES · SNES"] = "snes9x", ["DREAMCAST"] = "flycast", ["ARCADE"] = "fbneo"
    };

    /// <summary>.pkg and .vpk are installers, not bootable games.</summary>
    public static bool InstallOnly(string path) => path.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase);

    // Installers start the emulator alone so its File menu can install them.
    public static string Arguments(string emulator, string rom, string console = "") =>
        InstallOnly(rom) ? "" :
        emulator == "RetroArch" ? RetroCores.TryGetValue(console, out var core)
            ? LaunchArgs["RetroArch"].Replace("{core}", rom.EndsWith(".nes", StringComparison.OrdinalIgnoreCase) || rom.EndsWith(".fds", StringComparison.OrdinalIgnoreCase) ? "mesen" : core).Replace("{rom}", rom)
            : $"\"{rom}\"" :
        LaunchArgs.TryGetValue(emulator, out var template) ? template.Replace("{rom}", rom).Replace("{id}", FolderName(rom)) : $"\"{rom}\"";

    /// <summary>Where an emulator keeps the games it installed from .pkg files, so they join the library by themselves.</summary>
    public static IEnumerable<string> EmulatorGameFolders(string emulator, string exe)
    {
        var dir = System.IO.Path.GetDirectoryName(exe) ?? "";
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        IEnumerable<string> found = emulator switch
        {
            "RPCS3" => [System.IO.Path.Combine(dir, "dev_hdd0", "game")],
            // Vita3K's pref-path in config.yml, else next to the exe or in AppData.
            "Vita3K" => new[] { Setting(System.IO.Path.Combine(dir, "config.yml"), @"^pref-path:\s*['""]?([^'""\r\n]+)").FirstOrDefault(), dir, System.IO.Path.Combine(appData, "Vita3K", "Vita3K") }
                .OfType<string>().Select(p => System.IO.Path.Combine(p, "ux0", "app")),
            // shadPS4 lists its install folders in config.toml (portable user folder, else AppData).
            "shadPS4" => new[] { System.IO.Path.Combine(dir, "user", "config.toml"), System.IO.Path.Combine(appData, "shadPS4", "config.toml") }
                .SelectMany(c => Setting(c, @"^installDirs?\s*=\s*(\[[^\]]*\]|""[^""]*"")"))
                .SelectMany(v => Regex.Matches(v, @"""((?:[^""\\]|\\.)*)""").Select(m => m.Groups[1].Value.Replace(@"\\", @"\"))),
            _ => []
        };
        return found.Where(p => p.Length > 0 && Directory.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> Setting(string file, string pattern)
    {
        try { return File.Exists(file) ? Regex.Matches(File.ReadAllText(file), pattern, RegexOptions.Multiline).Select(m => m.Groups[1].Value.Trim()).ToList() : []; }
        catch { return []; }
    }

    /// <summary>.pkg files whose game is installed: a game folder with the same title id. The folder is the one that plays.</summary>
    public static List<Rom> InstalledPackages(IEnumerable<Rom> games)
    {
        var list = games.ToList();
        var installed = list.Where(g => Directory.Exists(g.Path)).Select(g => TitleId(g.Path)).OfType<string>().ToHashSet();
        return installed.Count == 0 ? [] : list.Where(g => InstallOnly(g.Path) && TitleId(g.Path) is { } id && installed.Contains(id)).ToList();
    }

    /// <summary>Local box art for a game, downloading it from libretro-thumbnails or GameTDB the first time.</summary>
    public static async Task<string?> CoverFor(Rom rom, HttpClient http)
    {
        if (rom.CustomCover.Length > 0 && File.Exists(rom.CustomCover)) return rom.CustomCover;
        // PS3, PS4 and PS5 folder dumps carry their own art.
        if (Directory.Exists(rom.Path) && new[] { @"sce_sys\icon0.png", @"PS3_GAME\ICON0.PNG", "ICON0.PNG" }
            .Select(i => System.IO.Path.Combine(rom.Path, i)).FirstOrDefault(File.Exists) is { } icon) return icon;
        var hasThumbs = ThumbSystems.TryGetValue(rom.Console, out var systems);
        var hasTdb = TdbSystems.TryGetValue(rom.Console, out var tdb);
        if (!hasThumbs && !hasTdb && !EshopSystems.ContainsKey(rom.Console)) return null;
        var file = System.IO.Path.GetFileNameWithoutExtension(rom.Path);
        var cachedBase = System.IO.Path.Combine(CoverDir, Safe($"{rom.Console} - {file}"));
        if (new[] { ".png", ".jpg" }.Select(e => cachedBase + e).FirstOrDefault(File.Exists) is { } hit) return hit;
        var miss = cachedBase + ".png.none";
        // A miss is retried after a week, or sooner once an update may have added a source.
        if (File.Exists(miss) && File.GetLastWriteTimeUtc(miss) is var missed && missed > DateTime.UtcNow.AddDays(-7)
            && (Environment.ProcessPath is not { } exe || missed > File.GetLastWriteTimeUtc(exe))) return null;
        try
        {
            Directory.CreateDirectory(CoverDir);
            var urls = new List<string>();
            foreach (var system in systems ?? [])
                if (BestMatch(file, rom.Title, await IndexOf(system, http)) is { } name)
                    urls.Add($"https://thumbnails.libretro.com/{Uri.EscapeDataString(system)}/Named_Boxarts/{Uri.EscapeDataString(name)}.png");
            if (hasTdb && TdbPick(file, rom.Title, await TdbIndexOf(tdb.Db, http), TitleId(rom.Path)) is { } game)
                urls.AddRange(TdbUrls(tdb.Art, game.Id, game.Region));
            // No exact name: take a near one only when it starts with every word of a title at least two words long
            // ("Pokemon Emerald" finds "Pokemon - Emerald Version"), so a short name never grabs another game's art.
            if (urls.Count == 0 && Words(rom.Title).Length >= 2 && (await SearchCovers(rom.Console, rom.Title, http, 1)) is [{ Rank: <= 1 } near])
                urls.AddRange(near.Urls);
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

    // GameTDB keeps some art only in the full-size folder or under another region, so fall back to those.
    private static string[] TdbUrls(string art, string id, string region) =>
        (from kind in new[] { "coverM", "cover" } from r in new[] { region, "US", "EN" }.Distinct() select $"https://art.gametdb.com/{art}/{kind}/{r}/{id}.jpg").ToArray();

    /// <summary>One box art the cover search found. Urls are tried in order; Rank 0 is the exact name, 1 starts with the search words, 2 holds them.</summary>
    public sealed record CoverChoice(string Name, string Source, string[] Urls, int Rank);

    public static bool HasCoverSource(string console) => ThumbSystems.ContainsKey(console) || TdbSystems.ContainsKey(console) || EshopSystems.ContainsKey(console);

    /// <summary>Every box art for this console whose name holds the search words (or GameTDB id), best match and USA release first.</summary>
    public static async Task<List<CoverChoice>> SearchCovers(string console, string query, HttpClient http, int max = 60)
    {
        var words = Words(query);
        if (words.Length == 0) return [];
        var key = Key(query);
        var found = new List<CoverChoice>();
        foreach (var system in ThumbSystems.GetValueOrDefault(console) ?? [])
        {
            ILookup<string, string> index;
            try { index = await IndexOf(system, http); }
            catch { continue; } // one listing down shouldn't hide the others
            foreach (var name in index.SelectMany(g => g))
                if (Rank(name, key, words) is { } rank)
                    found.Add(new(name, system, [$"https://thumbnails.libretro.com/{Uri.EscapeDataString(system)}/Named_Boxarts/{Uri.EscapeDataString(name)}.png"], rank));
        }
        if (TdbSystems.TryGetValue(console, out var tdb))
        {
            try
            {
                foreach (var p in (await TdbIndexOf(tdb.Db, http)).SelectMany(g => g).Distinct().Select(l => l.Split('|')))
                    if ((Key(p[1]) == key ? 0 : Rank(p[0], key, words)) is { } rank)
                        found.Add(new(p[0], "GameTDB", TdbUrls(tdb.Art, p[1], p[2]), rank));
            }
            catch { }
        }
        if (EshopSystems.TryGetValue(console, out var code))
        {
            try
            {
                var url = $"https://searching.nintendo-europe.com/en/select?wt=json&rows=100&fl=title,image_url,image_url_sq_s&fq=type:GAME%20AND%20playable_on_txt:{code}&q={Uri.EscapeDataString(string.Join(' ', words))}";
                using var json = JsonDocument.Parse(await http.GetStringAsync(url));
                foreach (var doc in json.RootElement.GetProperty("response").GetProperty("docs").EnumerateArray())
                {
                    var name = doc.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    // Box packshot first, square banner as the fallback.
                    var urls = new[] { "image_url", "image_url_sq_s" }.Select(f => doc.TryGetProperty(f, out var u) ? u.GetString() : null).OfType<string>().ToArray();
                    if (urls.Length > 0 && Rank(name, key, words) is { } rank) found.Add(new(name, "Nintendo eShop", urls, rank));
                }
            }
            catch { }
        }
        return found.OrderBy(c => c.Rank).ThenBy(c => ReleaseOrder(c.Name)).ThenBy(c => c.Name.Length).Take(max).ToList();
    }

    private static int? Rank(string name, string key, string[] words)
    {
        if (Key(name) == key) return 0;
        var have = Words(name);
        if (have.Length >= words.Length && have.Take(words.Length).SequenceEqual(words)) return 1;
        return words.All(w => have.Any(h => h.StartsWith(w, StringComparison.Ordinal))) ? 2 : null;
    }

    private static int ReleaseOrder(string n) =>
        (n.Contains("(Beta") || n.Contains("(Proto") || n.Contains("(Demo") ? 10 : 0)
        + (n.Contains("(USA") || n.Contains("(EN") ? 0 : n.Contains("(World") ? 1 : n.Contains("(Europe") ? 2 : 3);

    // "Legend of Zelda, The - Ocarina of Time (USA)" becomes legend, of, zelda, ocarina, of, time.
    public static string[] Words(string name) =>
        Regex.Split(TitleOf(name + ".x").ToLowerInvariant().Replace("&", " and "), "[^a-z0-9]+").Where(w => w.Length > 0 && w != "the").ToArray();

    /// <summary>The first of a choice's urls that answers, as image bytes.</summary>
    public static async Task<byte[]?> CoverBytes(CoverChoice choice, HttpClient http)
    {
        foreach (var url in choice.Urls)
        {
            try
            {
                using var response = await http.GetAsync(url);
                if (response.IsSuccessStatusCode) return await response.Content.ReadAsByteArrayAsync();
            }
            catch { }
        }
        return null;
    }

    /// <summary>Save the picked art next to the other covers and return its path. Each pick gets its own file, so the shown one is never overwritten.</summary>
    public static async Task<string?> SaveChosenCover(Rom rom, CoverChoice choice, HttpClient http)
    {
        if (await CoverBytes(choice, http) is not { } bytes) return null;
        Directory.CreateDirectory(CoverDir);
        var path = System.IO.Path.Combine(CoverDir, Safe($"{rom.Console} - {System.IO.Path.GetFileNameWithoutExtension(rom.Path)} - {choice.Name}") + (bytes is [0x89, ..] ? ".png" : ".jpg"));
        await File.WriteAllBytesAsync(path, bytes);
        return path;
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
        Regex.Matches(xml, @"<game name=""([^""]+)"">\s*<id>([^<]+)</id>\s*<type>[^<]*</type>\s*(?:<region>([^<]*)</region>|<region\s*/>)")
            .Select(m => $"{System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Replace('|', ' ')}|{m.Groups[2].Value}|{ArtRegion(m.Groups[3].Value)}");

    // Regions are lists like "USA,EUR" or "ALL" (PS3: NTSC-U, NTSC-J, PAL, or none); art folders are US, EN (Europe and worldwide) and JA.
    private static string ArtRegion(string regions) =>
        regions.Contains("USA") || regions.Contains("NTSC-U") ? "US" : regions.StartsWith("JPN") || regions.StartsWith("NTSC-J") ? "JA" : "EN";

    // Each game is found by its name and by its id.
    public static ILookup<string, string> TdbByKey(IEnumerable<string> lines) =>
        lines.SelectMany(l => l.Split('|') is var p ? new[] { (Key(p[0]), l), (Key(p[1]), l) } : []).ToLookup(x => x.Item1, x => x.Item2);

    /// <summary>The GameTDB game for a file: its own id first, else by name with the US release first.</summary>
    public static (string Id, string Region)? TdbPick(string file, string title, ILookup<string, string> games, string? id = null) =>
        (id is null ? [] : games[Key(id)]).Concat(games[Key(file)]).Concat(games[Key(title)])
            .Select(l => l.Split('|'))
            .OrderBy(p => p[1] == id ? 0 : 1)
            .ThenBy(p => p[2] == "US" ? 0 : p[2] == "EN" ? 1 : 2)
            .Select(p => ((string, string)?)(p[1], p[2]))
            .FirstOrDefault();

    /// <summary>
    /// The real name for a game titled after its id (a .pkg named "UP9000-NPUA80490_00-…" or a folder named "NPUA80490"),
    /// from GameTDB. Null when the title isn't the id, so a name the user typed is kept.
    /// </summary>
    public static async Task<string?> NameFromId(Rom rom, HttpClient http)
    {
        if (TitleId(rom.Path) is not { } id || !rom.Title.Contains(id) || !TdbSystems.TryGetValue(rom.Console, out var tdb)) return null;
        try
        {
            return (await TdbIndexOf(tdb.Db, http))[Key(id)].Select(l => l.Split('|')).FirstOrDefault(p => p[1] == id) is { } game
                ? TitleOf(System.Net.WebUtility.HtmlDecode(game[0]) + ".x") : null;
        }
        catch { return null; }
    }

    /// <summary>A PS3 title id like NPUA80490 from the file or folder name, else from a .pkg's content id.</summary>
    public static string? TitleId(string path)
    {
        if (Regex.Match(System.IO.Path.GetFileName(path), @"[A-Z]{4}\d{5}") is { Success: true } m) return m.Value;
        if (!path.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var s = File.OpenRead(path);
            var id = Encoding.ASCII.GetString(Read(s, 0x37, 9));
            return Regex.IsMatch(id, @"^[A-Z]{4}\d{5}$") ? id : null;
        }
        catch { return null; }
    }

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
