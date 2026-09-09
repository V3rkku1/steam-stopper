using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using Microsoft.Win32;

namespace SteamStopper.Core;

public sealed class AppOffer
{
    public string Name { get; init; } = "";
    public string Id { get; init; } = "";
    public string Match { get; init; } = "";
    public string Url { get; init; } = "";
    public string Silent { get; init; } = "/S";
    public string Category { get; init; } = "Tools";
    public string Blurb { get; init; } = "";
    public string Kind { get; init; } = "winget";
    public string Color { get; init; } = "#3A3A3A";
    public string[] Detect { get; init; } = [];
    public bool Installed { get; set; }
    public string State { get; set; } = "Not installed";
    public string Initial
    {
        get
        {
            var parts = Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) return string.Concat(char.ToUpper(parts[0][0]), char.ToUpper(parts[1][0]));
            return Name.Length >= 2 ? Name[..2].ToUpperInvariant() : Name.ToUpperInvariant();
        }
    }
    public string ActionLabel => Installed ? "Uninstall" : "Install";
}

public static class AppHub
{
    public static readonly string[] Categories = ["All", "Chat", "Remote", "Gaming", "Tools", "Media", "Dev"];

    public static readonly AppOffer[] Catalog =
    [
        new() { Name = "Discord", Id = "Discord.Discord", Match = "Discord", Category = "Chat", Color = "#5865F2", Blurb = "Voice, text, and communities.", Url = "https://discord.com/api/download?platform=win", Silent = "-s" },
        new() { Name = "Telegram", Id = "Telegram.TelegramDesktop", Match = "Telegram", Category = "Chat", Color = "#2AABEE", Blurb = "Fast messaging app." },
        new() { Name = "Signal", Id = "OpenWhisperSystems.Signal", Match = "Signal", Category = "Chat", Color = "#3A76F0", Blurb = "Private messaging." },
        new() { Name = "Steam", Id = "Valve.Steam", Match = "Steam", Category = "Gaming", Color = "#1B2838", Blurb = "Valve game store and launcher.", Url = "https://cdn.cloudflare.steamstatic.com/client/installer/SteamSetup.exe", Silent = "/S" },
        new() { Name = "Epic Games", Id = "EpicGames.EpicGamesLauncher", Match = "Epic Games Launcher", Category = "Gaming", Color = "#2A2A2A", Blurb = "Epic Games Store launcher." },
        new() { Name = "Ubisoft Connect", Id = "Ubisoft.Connect", Match = "Ubisoft Connect", Category = "Gaming", Color = "#0070FF", Blurb = "Ubisoft games launcher." },
        new() { Name = "Content Manager", Kind = "zip", Match = "Content Manager", Category = "Gaming", Color = "#E10600", Blurb = "Assetto Corsa launcher and content tool.", Url = "https://acstuff.ru/app/latest.zip", Detect = ContentManagerPaths() },
        new() { Name = "AnyDesk", Id = "AnyDesk.AnyDesk", Match = "AnyDesk", Category = "Remote", Color = "#EF443B", Blurb = "Remote desktop support.", Url = "https://download.anydesk.com/AnyDesk.exe", Silent = "--install" },
        new() { Name = "RustDesk", Id = "RustDesk.RustDesk", Match = "RustDesk", Category = "Remote", Color = "#F8C301", Blurb = "Open-source remote desktop." },
        new() { Name = "Parsec", Id = "Parsec.Parsec", Match = "Parsec", Category = "Remote", Color = "#B4FF39", Blurb = "Low-latency game streaming." },
        new() { Name = "7-Zip", Id = "7zip.7zip", Match = "7-Zip", Category = "Tools", Color = "#111", Blurb = "Fast file archiver.", Url = "https://www.7-zip.org/a/7z2501-x64.exe", Silent = "/S" },
        new() { Name = "Notepad++", Id = "Notepad++.Notepad++", Match = "Notepad++", Category = "Tools", Color = "#90C040", Blurb = "Lightweight code editor.", Url = "https://github.com/notepad-plus-plus/notepad-plus-plus/releases/latest/download/npp.8.8.5.Installer.x64.exe", Silent = "/S" },
        new() { Name = "PowerToys", Id = "Microsoft.PowerToys", Match = "PowerToys", Category = "Tools", Color = "#F9C117", Blurb = "Windows extras from Microsoft." },
        new() { Name = "Everything", Id = "voidtools.Everything", Match = "Everything", Category = "Tools", Color = "#4CAF50", Blurb = "Instant file search." },
        new() { Name = "ShareX", Id = "ShareX.ShareX", Match = "ShareX", Category = "Tools", Color = "#2885C7", Blurb = "Screenshots and recording." },
        new() { Name = "qBittorrent", Id = "qBittorrent.qBittorrent", Match = "qBittorrent", Category = "Tools", Color = "#3B85C8", Blurb = "Open-source torrent client." },
        new() { Name = "CPU-Z", Id = "CPUID.CPU-Z", Match = "CPU-Z", Category = "Tools", Color = "#F39C12", Blurb = "CPU and board info." },
        new() { Name = "MSI Afterburner", Id = "Guru3D.Afterburner", Match = "MSI Afterburner", Category = "Tools", Color = "#FF5400", Blurb = "GPU overclock and overlay." },
        new() { Name = "Google Chrome", Id = "Google.Chrome", Match = "Google Chrome", Category = "Media", Color = "#4285F4", Blurb = "Google web browser.", Url = "https://dl.google.com/chrome/install/latest/chrome_installer.exe", Silent = "/silent /install" },
        new() { Name = "Mozilla Firefox", Id = "Mozilla.Firefox", Match = "Mozilla Firefox", Category = "Media", Color = "#FF7139", Blurb = "Independent web browser." },
        new() { Name = "VLC", Id = "VideoLAN.VLC", Match = "VLC media player", Category = "Media", Color = "#E85E00", Blurb = "Plays almost any video." },
        new() { Name = "Spotify", Id = "Spotify.Spotify", Match = "Spotify", Category = "Media", Color = "#1DB954", Blurb = "Music streaming.", Url = "https://download.scdn.co/SpotifySetup.exe", Silent = "/silent" },
        new() { Name = "OBS Studio", Id = "OBSProject.OBSStudio", Match = "OBS Studio", Category = "Media", Color = "#302E31", Blurb = "Streaming and recording." },
        new() { Name = "HandBrake", Id = "HandBrake.HandBrake", Match = "HandBrake", Category = "Media", Color = "#E8A317", Blurb = "Convert video files." },
        new() { Name = "Visual Studio Code", Id = "Microsoft.VisualStudioCode", Match = "Visual Studio Code", Category = "Dev", Color = "#007ACC", Blurb = "Editor for code and configs." },
        new() { Name = "Git", Id = "Git.Git", Match = "Git", Category = "Dev", Color = "#F05032", Blurb = "Version control." },
        new() { Name = "Python 3", Id = "Python.Python.3.12", Match = "Python 3", Category = "Dev", Color = "#3776AB", Blurb = "Python runtime." }
    ];

    public static List<AppOffer> Snapshot()
    {
        var installed = InstalledNames();
        return Catalog.Select(app =>
        {
            var hit = app.Detect.Any(File.Exists)
                || (!string.IsNullOrWhiteSpace(app.Match) && installed.Any(name => name.Contains(app.Match, StringComparison.OrdinalIgnoreCase)));
            return Copy(app, hit);
        }).ToList();
    }

    public static string Install(AppOffer app)
    {
        if (app.Kind == "zip")
            return InstallZip(app);
        var winget = FindWinget();
        if (winget is not null && !string.IsNullOrWhiteSpace(app.Id))
        {
            var result = Run(winget, $"install --id {app.Id} -e --accept-package-agreements --accept-source-agreements --disable-interactivity --silent");
            if (result.Exit == 0) return $"Installed {app.Name}.";
            if (!string.IsNullOrWhiteSpace(app.Url))
            {
                var download = DownloadAndRun(app);
                if (download is not null) return download;
            }
            return $"Could not install {app.Name}. {Trim(result.Text)}";
        }
        if (string.IsNullOrWhiteSpace(app.Url))
            return "winget is not available, and this app has no direct download.";
        return DownloadAndRun(app) ?? $"Could not download {app.Name}.";
    }

    public static string Uninstall(AppOffer app)
    {
        if (app.Kind == "zip")
            return UninstallZip(app);
        var winget = FindWinget();
        if (winget is not null && !string.IsNullOrWhiteSpace(app.Id))
        {
            var result = Run(winget, $"uninstall --id {app.Id} -e --accept-source-agreements --disable-interactivity --silent");
            if (result.Exit == 0) return $"Uninstalled {app.Name}.";
        }
        var cmd = UninstallCommand(app.Match);
        if (cmd is null) return $"Could not find an uninstaller for {app.Name}.";
        var (file, args) = SplitCommand(cmd);
        var done = Run(file, args);
        return done.Exit == 0 ? $"Uninstalled {app.Name}." : $"Uninstall finished with code {done.Exit}. {Trim(done.Text)}";
    }

    private static AppOffer Copy(AppOffer app, bool hit) => new()
    {
        Name = app.Name,
        Id = app.Id,
        Match = app.Match,
        Url = app.Url,
        Silent = app.Silent,
        Category = app.Category,
        Blurb = app.Blurb,
        Kind = app.Kind,
        Color = app.Color,
        Detect = app.Detect,
        Installed = hit,
        State = hit ? "Installed" : "Not installed"
    };

    private static string[] ContentManagerPaths()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return
        [
            Path.Combine(local, "Programs", "Content Manager", "Content Manager.exe"),
            Path.Combine(programs, "Content Manager", "Content Manager.exe"),
            Path.Combine(local, "AcTools Content Manager", "Content Manager.exe")
        ];
    }

    private static string InstallZip(AppOffer app)
    {
        try
        {
            var destDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Content Manager");
            var zipPath = Path.Combine(Path.GetTempPath(), "SteamStopper", "installers", "ContentManager.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(8) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("SteamStopper/2.0");
                using var response = http.GetAsync(app.Url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                response.EnsureSuccessStatusCode();
                using var input = response.Content.ReadAsStream();
                using var output = File.Create(zipPath);
                input.CopyTo(output);
            }
            if (Directory.Exists(destDir)) Directory.Delete(destDir, true);
            Directory.CreateDirectory(destDir);
            ZipFile.ExtractToDirectory(zipPath, destDir, true);
            var exe = Directory.EnumerateFiles(destDir, "Content Manager.exe", SearchOption.AllDirectories).FirstOrDefault()
                ?? Directory.EnumerateFiles(destDir, "*.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (exe is null)
            {
                Process.Start(new ProcessStartInfo("https://acstuff.club/app/") { UseShellExecute = true });
                return "Downloaded Content Manager, but no exe was in the zip. Opened the official page.";
            }
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
            return "Installed Content Manager. It will find Assetto Corsa on first launch.";
        }
        catch
        {
            Process.Start(new ProcessStartInfo("https://acstuff.club/app/") { UseShellExecute = true });
            return "Could not download Content Manager automatically. Opened the official page.";
        }
    }

    private static string UninstallZip(AppOffer app)
    {
        var removed = 0;
        foreach (var path in app.Detect.Where(File.Exists))
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (dir is not null && Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                    removed++;
                }
            }
            catch { }
        }
        return removed > 0 ? $"Removed {app.Name}." : $"Could not find {app.Name} files to remove.";
    }

    private static string? DownloadAndRun(AppOffer app)
    {
        try
        {
            var folder = Path.Combine(Path.GetTempPath(), "SteamStopper", "installers");
            Directory.CreateDirectory(folder);
            var dest = Path.Combine(folder, SafeName(app.Name) + ".exe");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SteamStopper/2.0");
            using var response = http.GetAsync(app.Url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            using (var input = response.Content.ReadAsStream())
            using (var output = File.Create(dest))
                input.CopyTo(output);
            var run = Run(dest, app.Silent);
            return run.Exit == 0 ? $"Installed {app.Name}." : $"Installer for {app.Name} exited {run.Exit}.";
        }
        catch (Exception ex)
        {
            return $"Download failed for {app.Name}: {ex.Message}";
        }
    }

    private static string? FindWinget()
    {
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "winget.exe");
        if (File.Exists(local)) return local;
        var result = Run("where", "winget");
        var line = result.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return File.Exists(line) ? line : null;
    }

    private static HashSet<string> InstalledNames()
    {
        string[] roots =
        [
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        ];
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var root in roots)
            {
                using var key = hive.OpenSubKey(root);
                if (key is null) continue;
                foreach (var name in key.GetSubKeyNames())
                {
                    using var sub = key.OpenSubKey(name);
                    var display = sub?.GetValue("DisplayName") as string;
                    if (!string.IsNullOrWhiteSpace(display)) names.Add(display);
                }
            }
        }
        return names;
    }

    private static string? UninstallCommand(string match)
    {
        string[] roots =
        [
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        ];
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var root in roots)
            {
                using var key = hive.OpenSubKey(root);
                if (key is null) continue;
                foreach (var name in key.GetSubKeyNames())
                {
                    using var sub = key.OpenSubKey(name);
                    var display = sub?.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(display) || !display.Contains(match, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var cmd = sub?.GetValue("QuietUninstallString") as string
                        ?? sub?.GetValue("UninstallString") as string;
                    if (!string.IsNullOrWhiteSpace(cmd)) return cmd;
                }
            }
        }
        return null;
    }

    private static (string File, string Args) SplitCommand(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end > 1)
                return (command[1..end], command[(end + 1)..].Trim());
        }
        var space = command.IndexOf(' ');
        return space < 0 ? (command, "") : (command[..space], command[(space + 1)..]);
    }

    private static string SafeName(string name)
        => string.Concat(name.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_'));

    private static string Trim(string text)
    {
        var line = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(s => s.Trim().Length > 0);
        return line?.Trim() ?? "";
    }

    private static (int Exit, string Text) Run(string file, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return (-1, "Could not start process.");
            var text = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            proc.WaitForExit(15 * 60 * 1000);
            return (proc.ExitCode, text);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
