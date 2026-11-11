using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace SteamStopper.Core;

/// <summary>
/// Zip backups of emulator and PC port save folders. Each backup holds the folders it found plus a
/// manifest of where they came from, so Restore puts every file back where the emulator looks for it.
/// </summary>
public static class SaveBackup
{
    // Where each emulator keeps saves. {app} is its install folder, the rest are the usual Windows folders.
    // Several are listed per emulator: portable mode keeps them beside the exe, a normal install in AppData.
    public static readonly Dictionary<string, string[]> Places = new()
    {
        ["DuckStation"] = [@"{app}\memcards", @"{app}\savestates", @"{docs}\DuckStation\memcards", @"{docs}\DuckStation\savestates"],
        ["PCSX2"] = [@"{app}\memcards", @"{app}\sstates", @"{docs}\PCSX2\memcards", @"{docs}\PCSX2\sstates"],
        ["RPCS3"] = [@"{app}\dev_hdd0\home\00000001\savedata"],
        ["shadPS4"] = [@"{app}\user\savedata", @"{appdata}\shadPS4\savedata"],
        ["PPSSPP"] = [@"{app}\memstick\PSP\SAVEDATA", @"{docs}\PPSSPP\PSP\SAVEDATA"],
        ["Vita3K"] = [@"{app}\ux0\user\00\savedata", @"{appdata}\Vita3K\Vita3K\ux0\user\00\savedata"],
        ["Eden"] = [@"{app}\user\nand\user\save", @"{appdata}\eden\nand\user\save"],
        ["Ryujinx"] = [@"{app}\portable\bis\user\save", @"{appdata}\Ryujinx\bis\user\save"],
        ["Lichii"] = [@"{app}\user\nand\user\save", @"{appdata}\lichii\nand\user\save"],
        ["Eglantine"] = [@"{app}\user\nand\user\save", @"{appdata}\eglantine\nand\user\save"],
        ["Azahar"] = [@"{app}\user\sdmc", @"{appdata}\Azahar\sdmc"],
        ["Cemu"] = [@"{app}\mlc01\usr\save", @"{appdata}\Cemu\mlc01\usr\save"],
        ["Dolphin"] = [@"{docs}\Dolphin Emulator\GC", @"{docs}\Dolphin Emulator\Wii\title", @"{appdata}\Dolphin Emulator\GC", @"{appdata}\Dolphin Emulator\Wii\title"],
        ["Xenia Canary"] = [@"{app}\content", @"{docs}\Xenia\content"],
        ["Project64"] = [@"{app}\Save"],
        ["Hookshot"] = [@"{local}\hookshot", @"{home}\Games\Hookshot\user"],
        ["Dusklight"] = [@"{appdata}\TwilitRealm\Dusk"],
        ["Zelda64Recomp"] = [@"{local}\Zelda64Recompiled\saves"],
        ["Ship of Harkinian"] = [@"{app}\Save", @"{appdata}\com.shipofharkinian.soh\Save"],
        ["2Ship2Harkinian"] = [@"{app}\saves", @"{appdata}\com.2ship2harkinian.2s2h\saves"],
        ["Starship"] = [@"{app}\saves", @"{appdata}\com.harbourmasters.starship\saves"]
    };

    // Disc images and shader caches can sit in the same folders; a save file is never this big.
    private const long MaxFile = 512L << 20;
    private const int Keep = 10;
    private const string Manifest = "steamstopper-backup.json";

    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Steam Stopper Backups");

    public static string Resolve(string place, string appDir) => place
        .Replace("{app}", appDir)
        .Replace("{docs}", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))
        .Replace("{appdata}", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))
        .Replace("{local}", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
        .Replace("{home}", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
        .Replace('\\', Path.DirectorySeparatorChar);

    /// <summary>The save folders that exist for an emulator, as (place template, folder on disk).</summary>
    public static List<(string Place, string Dir)> Found(string emulator, string appDir) =>
        Places.TryGetValue(emulator, out var places)
            ? places.Select(p => (Place: p, Dir: Resolve(p, appDir)))
                .Where(p => Directory.Exists(p.Dir) && Directory.EnumerateFileSystemEntries(p.Dir).Any())
                .ToList()
            : [];

    public static string FolderFor(string emulator) => Path.Combine(Root, string.Concat(emulator.Split(Path.GetInvalidFileNameChars())));

    public static List<string> Backups(string emulator) =>
        Directory.Exists(FolderFor(emulator))
            ? Directory.GetFiles(FolderFor(emulator), "*.zip").OrderByDescending(File.GetLastWriteTimeUtc).ToList()
            : [];

    /// <summary>Zip every save folder found into Documents\Steam Stopper Backups\name. Returns the zip, or null when there was nothing.</summary>
    public static string? Create(string emulator, string appDir, string? folder = null)
    {
        var found = Found(emulator, appDir);
        if (found.Count == 0) return null;
        folder ??= FolderFor(emulator);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{Path.GetFileName(folder)} {DateTime.Now:yyyy-MM-dd HHmmss}.zip");
        var tmp = path + ".tmp";
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            for (var i = 0; i < found.Count; i++)
            {
                foreach (var file in Directory.EnumerateFiles(found[i].Dir, "*", SearchOption.AllDirectories))
                {
                    if (new FileInfo(file).Length > MaxFile) continue;
                    var name = $"{i}/{Path.GetRelativePath(found[i].Dir, file).Replace('\\', '/')}";
                    // Open shared: an emulator left running still has its save open.
                    using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var target = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                    source.CopyTo(target);
                }
            }
            using var writer = new StreamWriter(zip.CreateEntry(Manifest).Open());
            writer.Write(JsonSerializer.Serialize(found.Select(f => f.Place).ToArray()));
        }
        File.Move(tmp, path, true);
        // Keep the newest few, so a backup every session doesn't fill the disk.
        foreach (var old in Directory.GetFiles(folder, "*.zip").OrderByDescending(File.GetLastWriteTimeUtc).Skip(Keep))
            try { File.Delete(old); } catch { }
        return path;
    }

    /// <summary>
    /// Put a backup's files back where they came from. The current saves are backed up first, into a
    /// "before restore" zip, so a wrong restore can be undone. Files the backup doesn't hold are left alone.
    /// </summary>
    public static int Restore(string emulator, string appDir, string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var manifest = zip.GetEntry(Manifest) ?? throw new InvalidDataException("This zip isn't a Steam Stopper save backup.");
        string[] places;
        using (var reader = new StreamReader(manifest.Open()))
            places = JsonSerializer.Deserialize<string[]>(reader.ReadToEnd()) ?? [];
        Create(emulator, appDir, Path.Combine(FolderFor(emulator), "Before restore"));
        var count = 0;
        foreach (var entry in zip.Entries)
        {
            var slash = entry.FullName.IndexOf('/');
            if (slash < 0 || entry.Name.Length == 0 || !int.TryParse(entry.FullName[..slash], out var i) || i >= places.Length) continue;
            var root = Path.GetFullPath(Resolve(places[i], appDir));
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName[(slash + 1)..]));
            // A crafted zip must not write outside the save folder ("../../Windows/...").
            if (!target.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
            count++;
        }
        return count;
    }
}
