using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SteamStopper.Core;

public sealed class AppOffer
{
    public string Name { get; init; } = "";
    public string Icon { get; init; } = "";
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
            return Name.Length > 0 ? Name[..1].ToUpperInvariant() : "";
        }
    }
    public string ActionLabel => Installed ? "Uninstall" : "Install";
    // Bundled logo (Assets/apps/<Icon>.png); cards fall back to the coloured initial when empty.
    public string? IconUri => Icon.Length > 0 ? $"pack://application:,,,/Assets/apps/{Icon}.png" : null;
}

public sealed class LeftoverItem : System.ComponentModel.INotifyPropertyChanged
{
    private bool _selected = true;
    public string App { get; init; } = "";
    public string Path { get; init; } = "";
    public long Bytes { get; init; }
    public string SizeText => Format.Bytes(Bytes);
    public bool Selected
    {
        get => _selected;
        set { _selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); }
    }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public static class AppHub
{
    public static readonly string[] Categories = ["All", "Chat", "Gaming", "Web", "Remote", "Media", "Office", "Tools", "Security", "Dev"];

    public static readonly AppOffer[] Catalog =
    [
        new() { Name = "Discord", Icon = "discord", Id = "Discord.Discord", Match = "Discord", Category = "Chat", Color = "#5865F2", Blurb = "Voice, text, and communities.", Url = "https://discord.com/api/download?platform=win", Silent = "-s" },
        new() { Name = "Telegram", Icon = "telegram", Id = "Telegram.TelegramDesktop", Match = "Telegram", Category = "Chat", Color = "#2AABEE", Blurb = "Fast messaging app." },
        new() { Name = "Signal", Icon = "signal", Id = "OpenWhisperSystems.Signal", Match = "Signal", Category = "Chat", Color = "#3A76F0", Blurb = "Private messaging." },
        new() { Name = "Slack", Icon = "slack", Id = "SlackTechnologies.Slack", Match = "Slack", Category = "Chat", Color = "#4A154B", Blurb = "Team chat and channels." },
        new() { Name = "Zoom", Icon = "zoom", Id = "Zoom.Zoom", Match = "Zoom", Category = "Chat", Color = "#0B5CFF", Blurb = "Video meetings and calls." },
        new() { Name = "Thunderbird", Icon = "thunderbird", Id = "Mozilla.Thunderbird", Match = "Mozilla Thunderbird", Category = "Chat", Color = "#0A84FF", Blurb = "Free email client." },

        new() { Name = "Steam", Icon = "steam", Id = "Valve.Steam", Match = "Steam", Category = "Gaming", Color = "#1B2838", Blurb = "Valve game store and launcher.", Url = "https://cdn.cloudflare.steamstatic.com/client/installer/SteamSetup.exe", Silent = "/S" },
        new() { Name = "Epic Games", Icon = "epicgames", Id = "EpicGames.EpicGamesLauncher", Match = "Epic Games Launcher", Category = "Gaming", Color = "#2A2A2A", Blurb = "Epic Games Store launcher." },
        new() { Name = "EA app", Icon = "eaapp", Id = "ElectronicArts.EADesktop", Match = "EA app", Category = "Gaming", Color = "#FF4747", Blurb = "EA games launcher." },
        new() { Name = "Ubisoft Connect", Icon = "ubisoftconnect", Id = "Ubisoft.Connect", Match = "Ubisoft Connect", Category = "Gaming", Color = "#0070FF", Blurb = "Ubisoft games launcher." },
        new() { Name = "Battle.net", Icon = "battlenet", Id = "Blizzard.BattleNet", Match = "Battle.net", Category = "Gaming", Color = "#148EFF", Blurb = "Blizzard games launcher." },
        new() { Name = "GOG Galaxy", Icon = "goggalaxy", Id = "GOG.Galaxy", Match = "GOG GALAXY", Category = "Gaming", Color = "#86328A", Blurb = "DRM-free games and launcher." },
        new() { Name = "Playnite", Icon = "playnite", Id = "Playnite.Playnite", Match = "Playnite", Category = "Gaming", Color = "#FF4E42", Blurb = "All your game libraries in one place." },
        new() { Name = "CurseForge", Icon = "curseforge", Id = "Overwolf.CurseForge", Match = "CurseForge", Category = "Gaming", Color = "#F16436", Blurb = "Mod manager for many games." },
        new() { Name = "Content Manager", Icon = "contentmanager", Kind = "zip", Match = "Content Manager", Category = "Gaming", Color = "#E10600", Blurb = "Assetto Corsa launcher and content tool.", Url = "https://acstuff.ru/app/latest.zip", Detect = ContentManagerPaths() },

        new() { Name = "Google Chrome", Icon = "googlechrome", Id = "Google.Chrome", Match = "Google Chrome", Category = "Web", Color = "#4285F4", Blurb = "Google web browser.", Url = "https://dl.google.com/chrome/install/latest/chrome_installer.exe", Silent = "/silent /install" },
        new() { Name = "Mozilla Firefox", Icon = "mozillafirefox", Id = "Mozilla.Firefox", Match = "Mozilla Firefox", Category = "Web", Color = "#FF7139", Blurb = "Independent web browser." },
        new() { Name = "Brave", Icon = "brave", Id = "Brave.Brave", Match = "Brave", Category = "Web", Color = "#FB542B", Blurb = "Private browser with ad blocking." },
        new() { Name = "Opera GX", Icon = "operagx", Id = "Opera.OperaGX", Match = "Opera GX", Category = "Web", Color = "#FA1E4E", Blurb = "Browser built for gamers." },

        new() { Name = "AnyDesk", Icon = "anydesk", Id = "AnyDesk.AnyDesk", Match = "AnyDesk", Category = "Remote", Color = "#EF443B", Blurb = "Remote desktop support.", Url = "https://download.anydesk.com/AnyDesk.exe", Silent = "--install" },
        new() { Name = "RustDesk", Icon = "rustdesk", Id = "RustDesk.RustDesk", Match = "RustDesk", Category = "Remote", Color = "#F8C301", Blurb = "Open-source remote desktop." },
        new() { Name = "TeamViewer", Icon = "teamviewer", Id = "TeamViewer.TeamViewer", Match = "TeamViewer", Category = "Remote", Color = "#0E8EE9", Blurb = "Remote access and support." },
        new() { Name = "Parsec", Icon = "parsec", Id = "Parsec.Parsec", Match = "Parsec", Category = "Remote", Color = "#B4FF39", Blurb = "Low-latency game streaming." },

        new() { Name = "Spotify", Icon = "spotify", Id = "Spotify.Spotify", Match = "Spotify", Category = "Media", Color = "#1DB954", Blurb = "Music streaming.", Url = "https://download.scdn.co/SpotifySetup.exe", Silent = "/silent" },
        new() { Name = "VLC", Icon = "vlc", Id = "VideoLAN.VLC", Match = "VLC media player", Category = "Media", Color = "#E85E00", Blurb = "Plays almost any video." },
        new() { Name = "OBS Studio", Icon = "obsstudio", Id = "OBSProject.OBSStudio", Match = "OBS Studio", Category = "Media", Color = "#302E31", Blurb = "Streaming and recording." },
        new() { Name = "HandBrake", Icon = "handbrake", Id = "HandBrake.HandBrake", Match = "HandBrake", Category = "Media", Color = "#E8A317", Blurb = "Convert video files." },
        new() { Name = "Audacity", Icon = "audacity", Id = "Audacity.Audacity", Match = "Audacity", Category = "Media", Color = "#0000CC", Blurb = "Record and edit audio." },
        new() { Name = "GIMP", Icon = "gimp", Id = "GIMP.GIMP.3", Match = "GIMP", Category = "Media", Color = "#5C5543", Blurb = "Free image editor." },
        new() { Name = "Krita", Icon = "krita", Id = "KDE.Krita", Match = "Krita", Category = "Media", Color = "#3BABFF", Blurb = "Digital painting." },
        new() { Name = "Blender", Icon = "blender", Id = "BlenderFoundation.Blender", Match = "Blender", Category = "Media", Color = "#E87D0D", Blurb = "3D modelling and animation." },
        new() { Name = "Paint.NET", Icon = "paintnet", Id = "dotPDN.PaintDotNet", Match = "paint.net", Category = "Media", Color = "#2B5797", Blurb = "Simple, fast photo editor." },

        new() { Name = "LibreOffice", Icon = "libreoffice", Id = "TheDocumentFoundation.LibreOffice", Match = "LibreOffice", Category = "Office", Color = "#18A303", Blurb = "Free office suite." },
        new() { Name = "Obsidian", Icon = "obsidian", Id = "Obsidian.Obsidian", Match = "Obsidian", Category = "Office", Color = "#7C3AED", Blurb = "Notes in plain Markdown files." },
        new() { Name = "Notion", Icon = "notion", Id = "Notion.Notion", Match = "Notion", Category = "Office", Color = "#191919", Blurb = "Notes, docs and tasks." },

        new() { Name = "7-Zip", Icon = "7zip", Id = "7zip.7zip", Match = "7-Zip", Category = "Tools", Color = "#111", Blurb = "Fast file archiver.", Url = "https://www.7-zip.org/a/7z2501-x64.exe", Silent = "/S" },
        new() { Name = "WinRAR", Icon = "winrar", Id = "RARLab.WinRAR", Match = "WinRAR", Category = "Tools", Color = "#6B2D8C", Blurb = "RAR and ZIP archiver." },
        new() { Name = "Notepad++", Icon = "notepad", Id = "Notepad++.Notepad++", Match = "Notepad++", Category = "Tools", Color = "#90C040", Blurb = "Lightweight code editor.", Url = "https://github.com/notepad-plus-plus/notepad-plus-plus/releases/download/v8.8.5/npp.8.8.5.Installer.x64.exe", Silent = "/S" },
        new() { Name = "PowerToys", Icon = "powertoys", Id = "Microsoft.PowerToys", Match = "PowerToys", Category = "Tools", Color = "#F9C117", Blurb = "Windows extras from Microsoft." },
        new() { Name = "Everything", Icon = "everything", Id = "voidtools.Everything", Match = "Everything", Category = "Tools", Color = "#4CAF50", Blurb = "Instant file search." },
        new() { Name = "ShareX", Icon = "sharex", Id = "ShareX.ShareX", Match = "ShareX", Category = "Tools", Color = "#2885C7", Blurb = "Screenshots and recording." },
        new() { Name = "qBittorrent", Icon = "qbittorrent", Id = "qBittorrent.qBittorrent", Match = "qBittorrent", Category = "Tools", Color = "#3B85C8", Blurb = "Open-source torrent client." },
        new() { Name = "Revo Uninstaller", Icon = "revouninstaller", Id = "RevoUninstaller.RevoUninstaller", Match = "Revo Uninstaller", Category = "Tools", Color = "#C62828", Blurb = "Remove apps and their leftovers." },
        new() { Name = "CPU-Z", Icon = "cpuz", Id = "CPUID.CPU-Z", Match = "CPU-Z", Category = "Tools", Color = "#F39C12", Blurb = "CPU and board info." },
        new() { Name = "GPU-Z", Icon = "gpuz", Id = "TechPowerUp.GPU-Z", Match = "GPU-Z", Category = "Tools", Color = "#B71C1C", Blurb = "Graphics card info." },
        new() { Name = "HWiNFO", Icon = "hwinfo", Id = "REALiX.HWiNFO", Match = "HWiNFO", Category = "Tools", Color = "#1565C0", Blurb = "Hardware sensors and temps." },
        new() { Name = "CrystalDiskInfo", Icon = "crystaldiskinfo", Id = "CrystalDewWorld.CrystalDiskInfo", Match = "CrystalDiskInfo", Category = "Tools", Color = "#29B6F6", Blurb = "Drive health and temperature." },
        new() { Name = "MSI Afterburner", Icon = "msiafterburner", Id = "Guru3D.Afterburner", Match = "MSI Afterburner", Category = "Tools", Color = "#FF5400", Blurb = "GPU overclock and overlay." },

        new() { Name = "Bitwarden", Icon = "bitwarden", Id = "Bitwarden.Bitwarden", Match = "Bitwarden", Category = "Security", Color = "#175DDC", Blurb = "Open-source password manager." },
        new() { Name = "Malwarebytes", Icon = "malwarebytes", Id = "Malwarebytes.Malwarebytes", Match = "Malwarebytes", Category = "Security", Color = "#0D3ECC", Blurb = "Malware scanner and removal." },
        new() { Name = "Proton VPN", Icon = "protonvpn", Id = "Proton.ProtonVPN", Match = "Proton VPN", Category = "Security", Color = "#6D4AFF", Blurb = "Free, no-logs VPN." },

        new() { Name = "Visual Studio Code", Icon = "visualstudiocode", Id = "Microsoft.VisualStudioCode", Match = "Visual Studio Code", Category = "Dev", Color = "#007ACC", Blurb = "Editor for code and configs." },
        new() { Name = "Git", Icon = "git", Id = "Git.Git", Match = "Git", Category = "Dev", Color = "#F05032", Blurb = "Version control." },
        new() { Name = "Python 3", Icon = "python3", Id = "Python.Python.3.12", Match = "Python 3", Category = "Dev", Color = "#3776AB", Blurb = "Python runtime." },
        new() { Name = "Node.js", Icon = "nodejs", Id = "OpenJS.NodeJS.LTS", Match = "Node.js", Category = "Dev", Color = "#5FA04E", Blurb = "JavaScript runtime (LTS)." },
        new() { Name = "PowerShell 7", Icon = "powershell", Id = "Microsoft.PowerShell", Match = "PowerShell 7", Category = "Dev", Color = "#2671BE", Blurb = "Modern cross-platform shell." }
    ];

    // Folders apps leave behind under names that differ from the app name. Keyed by AppOffer.Icon.
    private static readonly Dictionary<string, string[]> KnownLeftovers = new()
    {
        ["telegram"] = [@"%AppData%\Telegram Desktop"],
        ["signal"] = [@"%LocalAppData%\signal-desktop-updater"],
        ["thunderbird"] = [@"%AppData%\Thunderbird", @"%LocalAppData%\Thunderbird"],
        ["epicgames"] = [@"%LocalAppData%\EpicGamesLauncher", @"%ProgramData%\Epic\EpicGamesLauncher"],
        ["eaapp"] = [@"%LocalAppData%\Electronic Arts\EA Desktop", @"%ProgramData%\EA Desktop", @"%ProgramFiles%\Electronic Arts\EA Desktop"],
        ["ubisoftconnect"] = [@"%LocalAppData%\Ubisoft Game Launcher"],
        ["battlenet"] = [@"%ProgramData%\Blizzard Entertainment\Battle.net", @"%ProgramData%\Battle.net_components"],
        ["goggalaxy"] = [@"%ProgramData%\GOG.com\Galaxy", @"%LocalAppData%\GOG.com\Galaxy"],
        ["contentmanager"] = [@"%LocalAppData%\AcTools Content Manager"],
        ["googlechrome"] = [@"%LocalAppData%\Google\Chrome"],
        ["mozillafirefox"] = [@"%AppData%\Mozilla\Firefox", @"%LocalAppData%\Mozilla\Firefox"],
        ["brave"] = [@"%LocalAppData%\BraveSoftware"],
        ["operagx"] = [@"%AppData%\Opera Software\Opera GX Stable", @"%LocalAppData%\Opera Software\Opera GX Stable", @"%LocalAppData%\Programs\Opera GX"],
        ["obsstudio"] = [@"%AppData%\obs-studio"],
        ["vlc"] = [@"%AppData%\vlc"],
        ["blender"] = [@"%AppData%\Blender Foundation"],
        ["paintnet"] = [@"%AppData%\paint.net", @"%LocalAppData%\paint.net"],
        ["powertoys"] = [@"%LocalAppData%\Microsoft\PowerToys"],
        ["msiafterburner"] = [@"%ProgramFiles(x86)%\MSI Afterburner", @"%ProgramFiles(x86)%\RivaTuner Statistics Server"],
        ["protonvpn"] = [@"%LocalAppData%\ProtonVPN", @"%ProgramData%\ProtonVPN", @"%LocalAppData%\Proton\Proton VPN"],
        ["visualstudiocode"] = [@"%AppData%\Code", @"%LocalAppData%\Programs\Microsoft VS Code", @"%UserProfile%\.vscode"],
        ["nodejs"] = [@"%ProgramFiles%\nodejs", @"%AppData%\npm", @"%AppData%\npm-cache", @"%LocalAppData%\npm-cache"],
        ["powershell"] = [@"%ProgramFiles%\PowerShell\7"],
        ["python3"] = [@"%LocalAppData%\Programs\Python\Python312"],
        ["revouninstaller"] = [@"%ProgramFiles%\VS Revo Group", @"%ProgramData%\VS Revo Group"],
        ["hwinfo"] = [@"%ProgramFiles%\HWiNFO64"],
        ["cpuz"] = [@"%ProgramFiles%\CPUID\CPU-Z"],
        ["crystaldiskinfo"] = [@"%ProgramFiles%\CrystalDiskInfo"],
    };

    // Leftovers larger than this start unticked: they may hold games, mail or projects.
    public const long LargeLeftoverBytes = 2L * 1024 * 1024 * 1024;

    private static readonly EnumerationOptions Recursive = new() { RecurseSubdirectories = true, IgnoreInaccessible = true };

    private static string[] LeftoverRoots()
    {
        static string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var local = F(Environment.SpecialFolder.LocalApplicationData);
        return new[]
        {
            F(Environment.SpecialFolder.ApplicationData), local, Path.Combine(local, "Programs"),
            Path.Combine(Path.GetDirectoryName(local) ?? local, "LocalLow"),
            F(Environment.SpecialFolder.CommonApplicationData),
            F(Environment.SpecialFolder.ProgramFiles), F(Environment.SpecialFolder.ProgramFilesX86)
        }.Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Existing folders that an uninstalled app left behind. Exact folder-name matches only, never fuzzy.</summary>
    public static List<LeftoverItem> FindLeftovers(AppOffer app)
    {
        var roots = LeftoverRoots();
        var names = new[] { app.Name, app.Match, app.Name.Replace(" ", "") }
            .Where(n => n.Trim().Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var candidates = roots.SelectMany(root => names.Select(name => Path.Combine(root, name)))
            .Concat(KnownLeftovers.GetValueOrDefault(app.Icon, []).Select(Environment.ExpandEnvironmentVariables))
            .Concat(app.Kind == "zip" ? app.Detect.Select(p => Path.GetDirectoryName(p) ?? "") : []);

        var items = new List<LeftoverItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in candidates)
        {
            string full;
            try { full = Path.GetFullPath(raw).TrimEnd(Path.DirectorySeparatorChar); }
            catch { continue; }
            if (!seen.Add(full) || !Directory.Exists(full) || !IsSafeLeftover(full, roots)) continue;
            items.Add(new LeftoverItem { App = app.Name, Path = full, Bytes = FolderBytes(full) });
        }
        foreach (var item in items)
            item.Selected = item.Bytes < LargeLeftoverBytes;
        return items;
    }

    private static bool IsSafeLeftover(string path, string[] roots)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var protectedDirs = roots.Append(profile)
            .Append(Environment.GetFolderPath(Environment.SpecialFolder.Windows))
            .Append(Path.GetPathRoot(path) ?? "");
        if (protectedDirs.Any(p => string.Equals(p.TrimEnd('\\'), path, StringComparison.OrdinalIgnoreCase))) return false;
        // Only folders inside a known root (or the profile, for dot-folders like .vscode).
        var parentOk = roots.Append(profile).Any(r => path.StartsWith(r.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
        if (!parentOk) return false;
        // Never this app, and never a Steam library (an old Steam folder can still hold games).
        if (AppContext.BaseDirectory.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase)) return false;
        try { if (Directory.Exists(Path.Combine(path, "steamapps"))) return false; }
        catch { return false; }
        return true;
    }

    private static long FolderBytes(string path)
    {
        long total = 0;
        try
        {
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", Recursive))
                try { total += file.Length; } catch { }
        }
        catch { }
        return total;
    }

    /// <summary>Catalog apps that are not installed but still have folders on disk.</summary>
    public static List<LeftoverItem> FindAllLeftovers(IEnumerable<AppOffer> apps)
        => apps.Where(a => !a.Installed).SelectMany(FindLeftovers).ToList();

    public static (int Removed, int Failed, long Bytes) DeleteLeftovers(IEnumerable<LeftoverItem> items)
    {
        int removed = 0, failed = 0;
        long bytes = 0;
        foreach (var item in items.Where(i => i.Selected))
        {
            try
            {
                var info = new DirectoryInfo(item.Path);
                if (!info.Exists) continue;
                // A junction/symlink: remove the link itself, never what it points to.
                if (info.LinkTarget is not null) info.Delete();
                else info.Delete(true);
                removed++;
                bytes += item.Bytes;
            }
            catch (IOException) { failed++; }
            catch (UnauthorizedAccessException) { failed++; }
        }
        return (removed, failed, bytes);
    }

    public static bool StillInstalled(AppOffer app)
    {
        var installed = InstalledNames();
        return app.Detect.Any(File.Exists) || installed.Any(name => NameMatches(name, app.Match));
    }

    public static List<AppOffer> Snapshot()
    {
        var installed = InstalledNames();
        return Catalog.Select(app =>
        {
            var hit = app.Detect.Any(File.Exists)
                || installed.Any(name => NameMatches(name, app.Match));
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
        Icon = app.Icon,
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
            try { File.Delete(zipPath); } catch { }
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
            try { File.Delete(dest); } catch { }
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
                    if (string.IsNullOrWhiteSpace(display) || !NameMatches(display, match))
                        continue;
                    var cmd = sub?.GetValue("QuietUninstallString") as string
                        ?? sub?.GetValue("UninstallString") as string;
                    if (!string.IsNullOrWhiteSpace(cmd)) return cmd;
                }
            }
        }
        return null;
    }

    // Whole-word match, so "Git" skips "GitHub Desktop" and "Steam" skips "Steamworks" and this app itself.
    internal static bool NameMatches(string displayName, string match)
        => !string.IsNullOrWhiteSpace(match)
           && !displayName.StartsWith("Steam Stopper", StringComparison.OrdinalIgnoreCase)
           && Regex.IsMatch(displayName, $@"(?<![A-Za-z0-9]){Regex.Escape(match)}(?![A-Za-z0-9])", RegexOptions.IgnoreCase);

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
            var stderr = proc.StandardError.ReadToEndAsync();
            var text = proc.StandardOutput.ReadToEnd() + stderr.Result;
            proc.WaitForExit(15 * 60 * 1000);
            return (proc.ExitCode, text);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
