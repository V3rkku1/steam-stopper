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
    [JsonIgnore] public string SizeText => File.Exists(Path) ? Format.Bytes(new FileInfo(Path).Length) : "File missing";

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
        // Lichii's Switch 2 archive: base game plus updates in one file (magic "NUP\x01").
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
        ["redream"] = "\"{rom}\""
    };

    // libretro-thumbnails folder per console; consoles missing here get the colour cover.
    private static readonly Dictionary<string, string> ThumbSystems = new()
    {
        ["PS1"] = "Sony - PlayStation", ["PS2"] = "Sony - PlayStation 2", ["PSP"] = "Sony - PlayStation Portable",
        ["GAMECUBE"] = "Nintendo - GameCube", ["WII"] = "Nintendo - Wii", ["DS"] = "Nintendo - Nintendo DS",
        ["3DS"] = "Nintendo - Nintendo 3DS", ["N64"] = "Nintendo - Nintendo 64", ["DREAMCAST"] = "Sega - Dreamcast",
        ["XBOX"] = "Microsoft - Xbox", ["NES · SNES"] = "Nintendo - Super Nintendo Entertainment System",
        ["GAME BOY"] = "Nintendo - Game Boy Advance"
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
                if (known.Contains(file) || ConsoleOf(file) is not { } console) continue;
                store.Games.Add(new Rom { Path = file, Title = TitleOf(file), Console = console });
                known.Add(file);
                added++;
            }
        }
        return added;
    }

    /// <summary>Console for a game file, "" when it is a game but the console is unclear, null when it isn't a game.</summary>
    public static string? ConsoleOf(string file)
    {
        if (!ByExtension.TryGetValue(System.IO.Path.GetExtension(file), out var console)) return null;
        // .nup is also used by unrelated firmware updaters; only Lichii archives are games.
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
        return clean.Length > 0 ? clean : name;
    }

    public static string Arguments(string emulator, string rom) =>
        LaunchArgs.TryGetValue(emulator, out var template) ? template.Replace("{rom}", rom) : $"\"{rom}\"";

    /// <summary>Local box art for a game, downloading it from libretro-thumbnails the first time.</summary>
    public static async Task<string?> CoverFor(Rom rom, HttpClient http)
    {
        if (rom.CustomCover.Length > 0 && File.Exists(rom.CustomCover)) return rom.CustomCover;
        if (!ThumbSystems.TryGetValue(rom.Console, out var system)) return null;
        // libretro names art after the No-Intro/Redump file name, with these characters swapped for _.
        var name = Regex.Replace(System.IO.Path.GetFileNameWithoutExtension(rom.Path), @"[&*/:`<>?\\|""]", "_");
        var cached = System.IO.Path.Combine(CoverDir, $"{system} - {name}.png".Replace('/', '_'));
        if (File.Exists(cached)) return cached;
        var miss = cached + ".none";
        if (File.Exists(miss)) return null;
        try
        {
            Directory.CreateDirectory(CoverDir);
            var url = $"https://thumbnails.libretro.com/{Uri.EscapeDataString(system)}/Named_Boxarts/{Uri.EscapeDataString(name)}.png";
            using var response = await http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                // Remember misses so every visit doesn't ask again.
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound) File.WriteAllText(miss, "");
                return null;
            }
            await File.WriteAllBytesAsync(cached, await response.Content.ReadAsByteArrayAsync());
            return cached;
        }
        catch { return null; }
    }
}
