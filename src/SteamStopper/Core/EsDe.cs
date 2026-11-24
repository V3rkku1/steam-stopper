using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace SteamStopper.Core;

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

    /// <summary>Install the theme once (ES-DE's own Theme downloader updates it), select it, start ES-DE fullscreen.</summary>
    public static async Task Launch(string exe)
    {
        var data = DataDir(exe);
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
