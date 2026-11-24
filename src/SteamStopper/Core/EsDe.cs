using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SteamStopper.Core;

/// <summary>A library game for ES-DE: what starts it (an emulator and its arguments, or a launcher link) and its box art.</summary>
public sealed record EsGame(string Console, string Title, string Exe, string Args, string? Cover);

/// <summary>
/// Console mode through ES-DE with the PS5 Menu theme (github.com/InitialDin/ps5-menu-es-de, CC BY-NC-SA).
/// The theme is downloaded into the user's own ES-DE, never shipped with this app, because of its licence.
/// </summary>
public static class EsDe
{
    public const string ThemeName = "ps5-menu-es-de";
    private const string ZipUrl = "https://github.com/InitialDin/ps5-menu-es-de/archive/refs/heads/main.zip";

    /// <summary>ES-DE keeps its data next to the exe when portable.txt is there, else in %USERPROFILE%\ES-DE.</summary>
    public static string DataDir(string exe) => File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "portable.txt"))
        ? Path.Combine(Path.GetDirectoryName(exe)!, "ES-DE")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ES-DE");

    // Our console keys to ES-DE platform and theme names. Our systems are named "sm-<platform>" so the user's own
    // ES-DE systems and ROM folder stay as they are.
    private static readonly Dictionary<string, string> Platforms = new()
    {
        ["PS1"] = "psx", ["PS2"] = "ps2", ["PS3"] = "ps3", ["PS4"] = "ps4", ["PS5"] = "ps5", ["PSP"] = "psp", ["VITA"] = "psvita",
        ["SWITCH"] = "switch", ["SWITCH 2"] = "switch", ["3DS"] = "n3ds", ["DS"] = "nds", ["WII U"] = "wiiu", ["WII"] = "wii",
        ["GAMECUBE"] = "gc", ["N64"] = "n64", ["GAME BOY"] = "gba", ["NES · SNES"] = "snes", ["XBOX"] = "xbox", ["XBOX 360"] = "xbox360",
        ["DREAMCAST"] = "dreamcast", ["ARCADE"] = "arcade", ["RECOMP"] = "ports", ["STEAM"] = "steam", ["EPIC"] = "epic", ["HEROIC"] = "windows"
    };
    private const string Prefix = "sm-";

    /// <summary>
    /// Put the library in ES-DE: a .bat per game under <paramref name="roms"/>\&lt;system&gt; that starts it the way Play does,
    /// our systems in custom_systems\es_systems.xml (the user's own entries kept), and covers in ES-DE's media folder.
    /// Everything of ours is rebuilt, so games taken off the library disappear. Returns how many games were written.
    /// </summary>
    public static int Sync(string data, string roms, IEnumerable<EsGame> games)
    {
        if (Directory.Exists(roms)) Directory.Delete(roms, true);
        var media = MediaDir(data);
        var systems = new Dictionary<string, string>(); // system name -> full name
        var count = 0;
        foreach (var game in games)
        {
            if (!Platforms.TryGetValue(game.Console, out var platform)) continue;
            var system = Prefix + platform;
            systems[system] = AppHub.Consoles.Concat(AppHub.Stores).FirstOrDefault(c => c.Name == game.Console)?.Maker ?? game.Console;
            var dir = Directory.CreateDirectory(Path.Combine(roms, system)).FullName;
            var name = FileName(game.Title);
            for (var n = 2; File.Exists(Path.Combine(dir, name + ".bat")); n++) name = $"{FileName(game.Title)} ({n})";
            // UTF-8 without BOM plus chcp 65001, so paths outside the ANSI code page survive; % is doubled for cmd.
            File.WriteAllText(Path.Combine(dir, name + ".bat"), Batch(game), new UTF8Encoding(false));
            var covers = Path.Combine(media, system, "covers");
            foreach (var old in Directory.Exists(covers) ? Directory.GetFiles(covers, name + ".*") : []) File.Delete(old);
            if (game.Cover is { } cover && File.Exists(cover))
            {
                Directory.CreateDirectory(covers);
                File.Copy(cover, Path.Combine(covers, name + Path.GetExtension(cover)), true);
            }
            count++;
        }

        var file = Path.Combine(data, "custom_systems", "es_systems.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var doc = File.Exists(file) ? XDocument.Load(file) : new XDocument(new XElement("systemList"));
        doc.Root!.Elements("system").Where(e => ((string?)e.Element("name"))?.StartsWith(Prefix) == true).Remove();
        foreach (var (system, full) in systems)
        {
            var platform = system[Prefix.Length..];
            doc.Root.Add(new XElement("system",
                new XElement("name", system),
                new XElement("fullname", full),
                new XElement("path", Path.Combine(roms, system)),
                new XElement("extension", ".bat .BAT"),
                // Same command as ES-DE's own Windows system; %HIDEWINDOW% hides the cmd window.
                new XElement("command", new XAttribute("label", "Steam Multitool"), "%HIDEWINDOW% %ESCAPESPECIALS% %ROM%"),
                new XElement("platform", platform),
                new XElement("theme", platform)));
        }
        doc.Save(file);
        return count;
    }

    public static string Batch(EsGame game)
    {
        static string Esc(string s) => s.Replace("%", "%%");
        return game.Exe.Contains("://")
            ? $"@chcp 65001 >nul\r\n@start \"\" \"{Esc(game.Exe)}\"\r\n"
            : $"@chcp 65001 >nul\r\n@cd /d \"{Esc(Path.GetDirectoryName(game.Exe) ?? "")}\"\r\n@\"{Esc(game.Exe)}\" {Esc(game.Args)}\r\n";
    }

    private static string FileName(string title)
    {
        var name = string.Concat(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '<' or '>' or ':' or '"' or '|' or '?' or '*' or '\\' or '/' ? '_' : c)).Trim(' ', '.');
        return name.Length > 0 ? name : "Game";
    }

    /// <summary>Where ES-DE looks for covers: its MediaDirectory setting, else downloaded_media.</summary>
    private static string MediaDir(string data)
    {
        var settings = Path.Combine(data, "settings", "es_settings.xml");
        var custom = File.Exists(settings) ? Regex.Match(File.ReadAllText(settings), "<string name=\"MediaDirectory\" value=\"([^\"]+)\"").Groups[1].Value : "";
        return custom.Length > 0 ? custom : Path.Combine(data, "downloaded_media");
    }

    /// <summary>Install the theme once (ES-DE's own Theme downloader updates it), select it, start ES-DE fullscreen.</summary>
    public static async Task Launch(string exe, IEnumerable<EsGame> games)
    {
        var data = DataDir(exe);
        Sync(data, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamStopper", "ES-DE roms"), games);
        var theme = Path.Combine(data, "themes", ThemeName);
        if (!Directory.Exists(theme))
        {
            var themes = Path.GetDirectoryName(theme)!;
            var temp = Path.Combine(themes, ThemeName + ".download");
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
            using (var http = new HttpClient())
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("SteamMultitool");
                using var zip = new ZipArchive(new MemoryStream(await http.GetByteArrayAsync(ZipUrl)));
                zip.ExtractToDirectory(temp);
            }
            // The archive holds one folder, ps5-menu-es-de-main; its name must match the theme name ES-DE stores.
            Directory.Move(Directory.GetDirectories(temp).Single(), theme);
            Directory.Delete(temp, true);
        }
        // ES-DE writes its settings on exit, so only change them while it's closed.
        if (Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)).Length == 0)
        {
            var file = Path.Combine(data, "settings", "es_settings.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, SelectTheme(File.Exists(file) ? File.ReadAllText(file) : ""));
        }
        // ES-DE starts fullscreen by default.
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
    }

    /// <summary>es_settings.xml has no root element, so edit the one line instead of parsing it as XML.</summary>
    public static string SelectTheme(string xml)
    {
        // ES-DE 3 calls the setting Theme, older builds ThemeSet; keep whichever name the file uses.
        var existing = new Regex("(<string name=\"Theme(?:Set)?\" value=\")[^\"]*(\")");
        if (existing.IsMatch(xml)) return existing.Replace(xml, "${1}" + ThemeName + "${2}", 1);
        var line = $"<string name=\"Theme\" value=\"{ThemeName}\" />";
        return xml.Length == 0 ? $"<?xml version=\"1.0\"?>\n{line}\n" : xml.TrimEnd() + "\n" + line + "\n";
    }
}
