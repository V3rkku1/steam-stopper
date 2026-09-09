using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SteamStopper.Core;

public sealed class GameEntry
{
    public string AppId { get; init; } = "";
    public string Name { get; init; } = "";
    public long SizeBytes { get; init; }
    public string SizeText => Format.Bytes(SizeBytes);
    public string InstallDir { get; init; } = "";
    public string Library { get; init; } = "";
}

public sealed class ProcessRow
{
    public string Name { get; init; } = "";
    public int Pid { get; init; }
    public long Bytes { get; init; }
    public string Memory { get; init; } = "";
}

public static class SteamClient
{
    public static readonly string[] ProcessNames =
    [
        "steam", "steamwebhelper", "steamerrorreporter", "gameoverlayui", "streaming_client"
    ];

    public static readonly string[] ClientExes =
    [
        "steam.exe", "steamwebhelper.exe", "steamerrorreporter.exe", "GameOverlayUI.exe", "streaming_client.exe"
    ];

    public static bool IsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static void RelaunchAsAdmin()
    {
        var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(exe)) return;
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
    }

    public static string? FindRoot()
    {
        foreach (var (hive, path, name) in new (RegistryKey, string, string)[]
        {
            (Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            (Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
        })
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                var value = key?.GetValue(name) as string;
                if (string.IsNullOrWhiteSpace(value)) continue;
                var dir = value.Replace('/', '\\');
                if (Directory.Exists(dir)) return dir;
            }
            catch (IOException) { }
        }

        foreach (var candidate in new[]
        {
            @"C:\Program Files (x86)\Steam",
            @"C:\Program Files\Steam",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam")
        })
        {
            if (File.Exists(Path.Combine(candidate, "steam.exe"))) return candidate;
        }
        return null;
    }

    public static IReadOnlyList<string> LibraryPaths(string steamRoot)
    {
        var paths = new List<string> { steamRoot };
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) return paths;

        Dictionary<string, object> data;
        try { data = Vdf.Parse(File.ReadAllText(vdf)); }
        catch { return paths; }

        var folders = data.TryGetValue("libraryfolders", out var nested) ? Vdf.AsObject(nested) : data;
        foreach (var pair in folders)
        {
            if (pair.Key.StartsWith('_')) continue;
            string? raw = pair.Value is Dictionary<string, object> obj
                ? Vdf.GetString(obj, "path")
                : Convert.ToString(pair.Value);
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var lib = raw.Replace(@"\\", @"\");
            if (Directory.Exists(lib) && !paths.Contains(lib, StringComparer.OrdinalIgnoreCase))
                paths.Add(lib);
        }
        return paths;
    }

    public static List<GameEntry> ListGames(string steamRoot)
    {
        var games = new List<GameEntry>();
        foreach (var library in LibraryPaths(steamRoot))
        {
            var apps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                try
                {
                    var data = Vdf.Parse(File.ReadAllText(acf));
                    var app = data.TryGetValue("AppState", out var state) ? Vdf.AsObject(state) : data;
                    games.Add(new GameEntry
                    {
                        AppId = Vdf.GetString(app, "appid", Path.GetFileNameWithoutExtension(acf).Replace("appmanifest_", "")),
                        Name = Vdf.GetString(app, "name", Path.GetFileNameWithoutExtension(acf)),
                        SizeBytes = Vdf.GetLong(app, "SizeOnDisk"),
                        InstallDir = Vdf.GetString(app, "installdir"),
                        Library = library
                    });
                }
                catch { }
            }
        }
        return games.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static List<ProcessRow> RunningProcesses()
    {
        var wanted = new HashSet<string>(ProcessNames, StringComparer.OrdinalIgnoreCase);
        var rows = new List<ProcessRow>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (!wanted.Contains(process.ProcessName)) continue;
                rows.Add(new ProcessRow
                {
                    Name = process.ProcessName + ".exe",
                    Pid = process.Id,
                    Bytes = process.WorkingSet64,
                    Memory = Format.Bytes(process.WorkingSet64)
                });
            }
            catch { }
            finally { process.Dispose(); }
        }
        return rows.OrderBy(r => r.Name).ThenBy(r => r.Pid).ToList();
    }

    public static int KillSteam()
    {
        var killed = 0;
        foreach (var name in ProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try { process.Kill(true); killed++; }
                catch { }
                finally { process.Dispose(); }
            }
        }
        return killed;
    }

    public static void Launch(string steamRoot, bool offline)
    {
        var exe = Path.Combine(steamRoot, "steam.exe");
        var info = new ProcessStartInfo(exe) { WorkingDirectory = steamRoot, UseShellExecute = true };
        if (offline) info.Arguments = "-offline";
        Process.Start(info);
    }

    public static bool IsRunning()
    {
        foreach (var name in ProcessNames)
        {
            Process[] list;
            try { list = Process.GetProcessesByName(name); }
            catch { continue; }
            var any = list.Length > 0;
            foreach (var process in list) process.Dispose();
            if (any) return true;
        }
        return false;
    }

    public static void Restart(string steamRoot, bool offline = false)
    {
        KillSteam();
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until && IsRunning())
            Thread.Sleep(200);
        Thread.Sleep(500);
        Launch(steamRoot, offline);
    }

    public static bool SetOfflinePreference(string steamRoot, bool enabled)
    {
        var config = Path.Combine(steamRoot, "config", "loginusers.vdf");
        if (!File.Exists(config)) return false;
        var text = File.ReadAllText(config);
        var replacement = $"\"WantsOfflineMode\"\t\t\"{(enabled ? "1" : "0")}\"";
        var updated = Regex.Replace(text, "\"WantsOfflineMode\"\\s*\"\\d+\"", replacement);
        if (updated == text)
            updated = new Regex("(\"MostRecent\"\\s*\"\\d+\")").Replace(text, replacement + "\n\t\t$1", 1);
        File.WriteAllText(config, updated);
        return true;
    }

    public static IEnumerable<string> ClientExePaths(string steamRoot)
        => ClientExes.Select(name => Path.Combine(steamRoot, name)).Where(File.Exists);

    public static IEnumerable<string> GameExePaths(string steamRoot, int limit = 400)
    {
        var count = 0;
        foreach (var library in LibraryPaths(steamRoot))
        {
            var common = Path.Combine(library, "steamapps", "common");
            if (!Directory.Exists(common)) continue;
            foreach (var exe in Directory.EnumerateFiles(common, "*.exe", SearchOption.AllDirectories))
            {
                yield return exe;
                if (++count >= limit) yield break;
            }
        }
    }

    public static long FolderSize(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path)) return 0;
        if (File.Exists(path)) return new FileInfo(path).Length;
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; } catch { }
            }
        }
        catch { }
        return total;
    }

    public static int ClearPath(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path)) return 0;
        if (File.Exists(path))
        {
            File.Delete(path);
            return 1;
        }
        var removed = 0;
        foreach (var child in Directory.GetFileSystemEntries(path))
        {
            try
            {
                if (Directory.Exists(child)) Directory.Delete(child, true);
                else File.Delete(child);
                removed++;
            }
            catch { }
        }
        return removed;
    }

    public static string BackupConfig(string steamRoot)
    {
        var destDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SteamStopperBackups");
        Directory.CreateDirectory(destDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var staging = Path.Combine(destDir, "_staging-" + stamp);
        Directory.CreateDirectory(staging);
        var config = Path.Combine(steamRoot, "config");
        var userdata = Path.Combine(steamRoot, "userdata");
        if (Directory.Exists(config)) CopyDir(config, Path.Combine(staging, "config"));
        if (Directory.Exists(userdata)) CopyDir(userdata, Path.Combine(staging, "userdata"));
        var zip = Path.Combine(destDir, $"steam-config-{stamp}.zip");
        if (File.Exists(zip)) File.Delete(zip);
        ZipFile.CreateFromDirectory(staging, zip);
        Directory.Delete(staging, true);
        return zip;
    }

    public static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private static void CopyDir(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(source, dest));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, dest), true);
    }
}
