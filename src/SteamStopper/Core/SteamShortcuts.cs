using System.IO;
using System.Text;

namespace SteamStopper.Core;

public sealed record SteamGame(string Name, string Exe, string Args, string? Cover, string Group);

/// <summary>
/// Adds library games to Steam as non-Steam games: userdata\&lt;user&gt;\config\shortcuts.vdf (binary VDF)
/// plus box art in config\grid. Entries this app made carry a "Steam Multitool" tag (or the earlier "Steam Stopper"), so a later sync replaces
/// them and leaves the user's own shortcuts alone. Steam must be closed while the file is written.
/// </summary>
public static class SteamShortcuts
{
    public const string Tag = "Steam Multitool";
    // The app's earlier name; shortcuts tagged with it are ours too and get replaced.
    private const string OldTag = "Steam Stopper";
    private const byte Map = 0, Text = 1, Int = 2, Float = 3, UInt64 = 7, End = 8;

    /// <summary>Folder of the Steam account that logged in last (from config\loginusers.vdf), or the newest userdata folder.</summary>
    public static string? UserConfig(string steamRoot)
    {
        var userdata = Path.Combine(steamRoot, "userdata");
        try
        {
            var users = Vdf.AsObject(Vdf.Parse(File.ReadAllText(Path.Combine(steamRoot, "config", "loginusers.vdf"))).GetValueOrDefault("users"));
            foreach (var (id, value) in users)
                if (Vdf.GetString(Vdf.AsObject(value), "MostRecent") == "1" && ulong.TryParse(id, out var steamId))
                {
                    // userdata folders are named by account id, the low 32 bits of the SteamID64.
                    var dir = Path.Combine(userdata, (steamId & 0xFFFFFFFF).ToString());
                    if (Directory.Exists(dir)) return Path.Combine(dir, "config");
                }
        }
        catch { }
        if (!Directory.Exists(userdata)) return null;
        return Directory.EnumerateDirectories(userdata).Where(d => Path.GetFileName(d) != "0")
            .OrderByDescending(Directory.GetLastWriteTimeUtc).Select(d => Path.Combine(d, "config")).FirstOrDefault();
    }

    /// <summary>Replace this app's shortcuts with the given games. Returns how many were written.</summary>
    public static int Sync(string config, IReadOnlyList<SteamGame> games)
    {
        Directory.CreateDirectory(config);
        var path = Path.Combine(config, "shortcuts.vdf");
        var root = File.Exists(path) ? Read(File.ReadAllBytes(path)) : [];
        var old = root.TryGetValue("shortcuts", out var s) && s is Dictionary<string, object> m ? m : [];

        var kept = old.Values.OfType<Dictionary<string, object>>().Where(e => !HasTag(e)).ToList();
        var grid = Path.Combine(config, "grid");
        foreach (var game in games)
        {
            var exe = Quote(game.Exe);
            var id = AppId(exe, game.Name);
            kept.Add(new Dictionary<string, object>
            {
                ["appid"] = unchecked((int)id),
                ["AppName"] = game.Name,
                ["Exe"] = exe,
                ["StartDir"] = Quote(Path.GetDirectoryName(game.Exe) ?? ""),
                ["icon"] = "",
                ["ShortcutPath"] = "",
                ["LaunchOptions"] = game.Args,
                ["IsHidden"] = 0,
                ["AllowDesktopConfig"] = 1,
                ["AllowOverlay"] = 1,
                ["OpenVR"] = 0,
                ["Devkit"] = 0,
                ["DevkitGameID"] = "",
                ["DevkitOverrideAppID"] = 0,
                ["LastPlayTime"] = 0,
                ["FlatpakAppID"] = "",
                ["tags"] = new Dictionary<string, object> { ["0"] = Tag, ["1"] = game.Group }
            });
            // Portrait library art is <appid>p.<ext>, named by the unsigned id.
            if (game.Cover is { } cover && File.Exists(cover))
            {
                try
                {
                    Directory.CreateDirectory(grid);
                    File.Copy(cover, Path.Combine(grid, $"{id}p{Path.GetExtension(cover)}"), true);
                }
                catch { }
            }
        }

        var shortcuts = new Dictionary<string, object>();
        for (var i = 0; i < kept.Count; i++) shortcuts[i.ToString()] = kept[i];
        root["shortcuts"] = shortcuts;
        if (File.Exists(path)) File.Copy(path, path + ".bak", true);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, Write(root));
        File.Move(tmp, path, true);
        return games.Count;
    }

    private static bool HasTag(Dictionary<string, object> entry) =>
        entry.TryGetValue("tags", out var t) && t is Dictionary<string, object> tags && (tags.Values.Contains(Tag) || tags.Values.Contains(OldTag));

    private static string Quote(string path) => $"\"{path}\"";

    /// <summary>Steam's id for a shortcut: CRC32 of exe + name with the top bit set. Box art files use it.</summary>
    public static uint AppId(string quotedExe, string name) => Crc32(Encoding.UTF8.GetBytes(quotedExe + name)) | 0x80000000;

    public static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    public static Dictionary<string, object> Read(byte[] data)
    {
        var pos = 0;
        return ReadMap(data, ref pos);
    }

    private static Dictionary<string, object> ReadMap(byte[] data, ref int pos)
    {
        var map = new Dictionary<string, object>();
        while (pos < data.Length)
        {
            var type = data[pos++];
            if (type == End) break;
            var key = ReadString(data, ref pos);
            map[key] = type switch
            {
                Map => ReadMap(data, ref pos),
                Text => ReadString(data, ref pos),
                Int => Take(data, ref pos, 4, BitConverter.ToInt32),
                Float => Take(data, ref pos, 4, BitConverter.ToSingle),
                UInt64 => Take(data, ref pos, 8, BitConverter.ToUInt64),
                _ => throw new InvalidDataException($"shortcuts.vdf has an unknown value type {type}; leaving it untouched.")
            };
        }
        return map;
    }

    private static object Take<T>(byte[] data, ref int pos, int size, Func<byte[], int, T> read)
    {
        var value = read(data, pos)!;
        pos += size;
        return value;
    }

    private static string ReadString(byte[] data, ref int pos)
    {
        var end = Array.IndexOf(data, (byte)0, pos);
        if (end < 0) throw new InvalidDataException("shortcuts.vdf is cut off.");
        var text = Encoding.UTF8.GetString(data, pos, end - pos);
        pos = end + 1;
        return text;
    }

    public static byte[] Write(Dictionary<string, object> root)
    {
        using var stream = new MemoryStream();
        WriteMap(stream, root);
        return stream.ToArray();
    }

    private static void WriteMap(Stream s, Dictionary<string, object> map)
    {
        foreach (var (key, value) in map)
        {
            var (type, bytes) = value switch
            {
                Dictionary<string, object> => (Map, Array.Empty<byte>()),
                string text => (Text, [.. Encoding.UTF8.GetBytes(text), 0]),
                int number => (Int, BitConverter.GetBytes(number)),
                float number => (Float, BitConverter.GetBytes(number)),
                ulong number => (UInt64, BitConverter.GetBytes(number)),
                _ => throw new InvalidDataException($"Can't write {value?.GetType().Name} to shortcuts.vdf.")
            };
            s.WriteByte(type);
            s.Write([.. Encoding.UTF8.GetBytes(key), 0]);
            if (value is Dictionary<string, object> child) WriteMap(s, child);
            else s.Write(bytes);
        }
        s.WriteByte(End);
    }
}
