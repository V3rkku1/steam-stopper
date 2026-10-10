using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SteamStopper.Core;

public sealed class OrganizeItem
{
    public string Source { get; init; } = "";
    public string Name => Path.GetFileName(Source);
    public string Category { get; init; } = "";
    public long Bytes { get; init; }
    public string SizeText => Format.Bytes(Bytes);
}

public static class Organizer
{
    public static readonly (string Folder, string[] Extensions)[] Rules =
    [
        ("Installers", [".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".appinstaller"]),
        ("Archives", [".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".iso", ".img", ".cab"]),
        ("Documents", [".pdf", ".doc", ".docx", ".odt", ".rtf", ".txt", ".md", ".xls", ".xlsx", ".csv", ".ods",
                       ".ppt", ".pptx", ".odp", ".epub", ".pages", ".numbers", ".key"]),
        ("Images", [".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".svg", ".heic", ".heif", ".ico", ".tif", ".tiff",
                    ".psd", ".avif", ".raw", ".cr2", ".nef", ".dng"]),
        ("Videos", [".mp4", ".mkv", ".mov", ".avi", ".webm", ".wmv", ".flv", ".m4v", ".mpg", ".mpeg", ".3gp"]),
        ("Audio", [".mp3", ".wav", ".flac", ".m4a", ".ogg", ".aac", ".opus", ".wma", ".aiff", ".mid", ".midi"]),
        ("Code", [".py", ".js", ".ts", ".json", ".xml", ".html", ".htm", ".css", ".cs", ".cpp", ".c", ".h", ".java",
                  ".ps1", ".bat", ".cmd", ".sh", ".yml", ".yaml", ".ini", ".cfg", ".toml", ".reg", ".sql", ".lua"]),
        ("Fonts", [".ttf", ".otf", ".woff", ".woff2"]),
        ("Torrents", [".torrent"]),
    ];

    public const string OtherFolder = "Other";

    // Browsers and download managers write these while a download is still running.
    private static readonly HashSet<string> Unfinished = new(StringComparer.OrdinalIgnoreCase)
    {
        ".crdownload", ".part", ".partial", ".opdownload", ".download", ".tmp", ".!qb", ".!ut", ".aria2"
    };

    private static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(2);

    private static string LogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamStopper", "organize-undo.json");

    public static bool CanUndo => File.Exists(LogPath);

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    // The user can move Downloads to another drive, so ask the shell instead of assuming %UserProfile%\Downloads.
    public static string DownloadsFolder()
    {
        var downloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        if (SHGetKnownFolderPath(downloads, 0, IntPtr.Zero, out var ptr) == 0)
        {
            try { return Marshal.PtrToStringUni(ptr) ?? Fallback(); }
            finally { Marshal.FreeCoTaskMem(ptr); }
        }
        return Fallback();

        static string Fallback() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    public static string CategoryFor(string file)
    {
        var ext = Path.GetExtension(file);
        foreach (var (folder, extensions) in Rules)
            if (extensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return folder;
        return OtherFolder;
    }

    /// <summary>Top-level files only; folders already in the Downloads folder are left as they are.</summary>
    public static List<OrganizeItem> Plan(string folder)
    {
        var items = new List<OrganizeItem>();
        if (!Directory.Exists(folder)) return items;
        var now = DateTime.Now;
        foreach (var path in Directory.EnumerateFiles(folder))
        {
            try
            {
                var info = new FileInfo(path);
                if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                if (Unfinished.Contains(info.Extension)) continue;
                if (info.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                if (now - info.LastWriteTime < RecentWindow) continue;
                items.Add(new OrganizeItem { Source = path, Category = CategoryFor(path), Bytes = info.Length });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return items.OrderBy(i => i.Category).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static (int Moved, int Skipped) Run(string folder, IEnumerable<OrganizeItem> items)
    {
        var moves = new List<Move>();
        var skipped = 0;
        foreach (var item in items)
        {
            try
            {
                if (!File.Exists(item.Source)) { skipped++; continue; }
                var dir = Path.Combine(folder, item.Category);
                Directory.CreateDirectory(dir);
                var target = UniquePath(Path.Combine(dir, Path.GetFileName(item.Source)));
                File.Move(item.Source, target);
                moves.Add(new Move(item.Source, target));
            }
            catch (IOException) { skipped++; }
            catch (UnauthorizedAccessException) { skipped++; }
        }
        if (moves.Count > 0) SaveLog(folder, moves);
        return (moves.Count, skipped);
    }

    public static (int Restored, int Skipped) Undo()
    {
        if (!File.Exists(LogPath)) return (0, 0);
        UndoLog? log;
        try { log = JsonSerializer.Deserialize<UndoLog>(File.ReadAllText(LogPath)); }
        catch (JsonException) { log = null; }
        if (log is null) { File.Delete(LogPath); return (0, 0); }

        int restored = 0, skipped = 0;
        foreach (var move in Enumerable.Reverse(log.Moves))
        {
            try
            {
                // Never overwrite: a new file with the old name may have been downloaded since.
                if (!File.Exists(move.To) || File.Exists(move.From)) { skipped++; continue; }
                File.Move(move.To, move.From);
                restored++;
            }
            catch (IOException) { skipped++; }
            catch (UnauthorizedAccessException) { skipped++; }
        }
        foreach (var dir in log.Moves.Select(m => Path.GetDirectoryName(m.To)).Distinct())
        {
            try
            {
                if (dir is not null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        File.Delete(LogPath);
        return (restored, skipped);
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static void SaveLog(string folder, List<Move> moves)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        File.WriteAllText(LogPath, JsonSerializer.Serialize(new UndoLog { Folder = folder, Moves = moves }));
    }

    public sealed record Move(string From, string To);

    public sealed class UndoLog
    {
        public string Folder { get; set; } = "";
        public List<Move> Moves { get; set; } = [];
    }
}
