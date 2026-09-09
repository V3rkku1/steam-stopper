using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.IO;

namespace SteamStopper.Core;

public sealed class CleanerTarget : INotifyPropertyChanged
{
    private bool _selected = true;
    private long _bytes;

    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string Hint { get; init; } = "";
    public bool Selected
    {
        get => _selected;
        set { _selected = value; OnChanged(); }
    }
    public long Bytes
    {
        get => _bytes;
        set { _bytes = value; OnChanged(); OnChanged(nameof(SizeText)); }
    }
    public string SizeText => Bytes <= 0 ? "0 B" : Format.Bytes(Bytes);
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class Cleaner
{
    private const uint RecycleNoConfirm = 0x00000007;

    [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? root, uint flags);

    public static List<CleanerTarget> Catalog(string? steamRoot)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var list = new List<CleanerTarget>
        {
            Item("user-temp", "User temp", "Temporary files for your account", [Path.GetTempPath()]),
            Item("win-temp", "Windows temp", "C:\\Windows\\Temp", [Path.Combine(windows, "Temp")]),
            Item("recycle", "Recycle Bin", "Empty the Recycle Bin", []),
            Item("shader", "DirectX shader cache", "D3DSCache GPU shader leftovers", [Path.Combine(local, "D3DSCache")]),
            Item("thumbs", "Thumbnail cache", "Explorer thumbnail databases", [Path.Combine(local, "Microsoft", "Windows", "Explorer")]),
            Item("wer", "Error reports", "Windows Error Reporting", [Path.Combine(local, "Microsoft", "Windows", "WER")]),
            Item("dumps", "Crash dumps", "Local crash dump files", [Path.Combine(local, "CrashDumps")]),
            Item("do-cache", "Delivery Optimization", "Windows update delivery cache", [
                Path.Combine(windows, "SoftwareDistribution", "DeliveryOptimization", "Cache")
            ]),
            Item("nvidia", "NVIDIA shader cache", "NVIDIA DX/GL cache", [
                Path.Combine(local, "NVIDIA", "DXCache"),
                Path.Combine(local, "NVIDIA", "GLCache")
            ]),
            Item("chrome", "Chrome cache", "Google Chrome cache", [
                Path.Combine(local, "Google", "Chrome", "User Data", "Default", "Cache"),
                Path.Combine(local, "Google", "Chrome", "User Data", "Default", "Code Cache")
            ]),
            Item("edge", "Edge cache", "Microsoft Edge cache", [
                Path.Combine(local, "Microsoft", "Edge", "User Data", "Default", "Cache"),
                Path.Combine(local, "Microsoft", "Edge", "User Data", "Default", "Code Cache")
            ])
        };
        if (!string.IsNullOrWhiteSpace(steamRoot))
        {
            list.Add(Item("steam-html", "Steam HTML cache", "Steam web helper cache", [Path.Combine(steamRoot, "config", "htmlcache")]));
            list.Add(Item("steam-logs", "Steam logs", "Steam client logs", [Path.Combine(steamRoot, "logs")]));
            list.Add(Item("steam-dumps", "Steam dumps", "Steam crash dumps", [Path.Combine(steamRoot, "dumps")]));
            list.Add(Item("steam-temp", "Steam temp", "steamapps temp folder", [Path.Combine(steamRoot, "steamapps", "temp")]));
        }
        return list;
    }

    public static Dictionary<string, long> Measure(IEnumerable<CleanerTarget> items)
    {
        var map = new Dictionary<string, long>();
        foreach (var item in items)
        {
            if (item.Key == "recycle") map[item.Key] = RecycleSize();
            else if (item.Key == "thumbs") map[item.Key] = ThumbSize();
            else map[item.Key] = Paths(item.Key).Sum(SteamClient.FolderSize);
        }
        return map;
    }

    public static (int Removed, long Bytes) Clean(IEnumerable<CleanerTarget> items)
    {
        var selected = items.Where(i => i.Selected).ToList();
        var before = selected.Sum(i => i.Bytes);
        var removed = 0;
        foreach (var item in selected)
        {
            if (item.Key == "recycle")
            {
                try { SHEmptyRecycleBin(IntPtr.Zero, null, RecycleNoConfirm); removed++; } catch { }
                continue;
            }
            if (item.Key == "thumbs")
            {
                removed += ClearThumbs();
                continue;
            }
            foreach (var path in Paths(item.Key))
                removed += SteamClient.ClearPath(path);
        }
        return (removed, before);
    }

    private static CleanerTarget Item(string key, string name, string hint, string[] paths)
    {
        _paths[key] = paths;
        return new CleanerTarget { Key = key, Name = name, Hint = hint, Selected = key is not "chrome" and not "edge" };
    }

    private static readonly Dictionary<string, string[]> _paths = [];

    private static string[] Paths(string key) => _paths.GetValueOrDefault(key) ?? [];

    private static long RecycleSize()
    {
        long total = 0;
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;
                var bin = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin");
                if (Directory.Exists(bin)) total += SteamClient.FolderSize(bin);
            }
            catch { }
        }
        return total;
    }

    private static long ThumbSize()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Explorer");
        if (!Directory.Exists(dir)) return 0;
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "thumbcache_*.db"))
                try { total += new FileInfo(file).Length; } catch { }
        }
        catch { }
        return total;
    }

    private static int ClearThumbs()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Explorer");
        if (!Directory.Exists(dir)) return 0;
        var n = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "thumbcache_*.db"))
            {
                try { File.Delete(file); n++; } catch { }
            }
        }
        catch { }
        return n;
    }
}
