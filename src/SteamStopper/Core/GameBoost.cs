using System.Diagnostics;
using System.IO;

namespace SteamStopper.Core;

/// <summary>Close chosen background apps while a game runs and start them again after, for more CPU, memory and bandwidth.</summary>
public static class GameBoost
{
    // Launchers and sync apps that idle in the background; a starting point the user edits in Settings.
    public static readonly string[] Suggested =
        ["EpicGamesLauncher", "EADesktop", "upc", "Battle.net", "GalaxyClient", "OneDrive", "ms-teams", "Spotify"];

    /// <summary>Close every running process with one of these names (without .exe). Returns the programs to reopen.</summary>
    public static List<string> Close(IEnumerable<string> names, int keepPid)
    {
        var closed = new List<string>();
        var self = Environment.ProcessId;
        foreach (var name in names.Select(n => Path.GetFileNameWithoutExtension(n.Trim())).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    if (p.Id == self || p.Id == keepPid) continue;
                    try
                    {
                        var path = p.MainModule?.FileName;
                        // Ask nicely first so it can save; only force it if it ignores that.
                        if (!p.CloseMainWindow() || !p.WaitForExit(3000)) p.Kill(entireProcessTree: true);
                        if (path is not null && !closed.Contains(path, StringComparer.OrdinalIgnoreCase)) closed.Add(path);
                    }
                    catch { } // exited meanwhile, or a process we may not touch
                }
            }
        }
        return closed;
    }

    /// <summary>Start the closed programs again, as the normal user (through explorer), unless they already came back.</summary>
    public static void Reopen(IEnumerable<string> programs)
    {
        foreach (var program in programs)
        {
            if (!File.Exists(program) || Process.GetProcessesByName(Path.GetFileNameWithoutExtension(program)).Length > 0) continue;
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{program}\"") { UseShellExecute = false })?.Dispose(); }
            catch { }
        }
    }
}
