using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SteamStopper.Core;

public static class Format
{
    public static string Bytes(long num)
    {
        double value = num;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        foreach (var unit in units)
        {
            if (value < 1024 || unit == "TB")
                return unit == "B" ? $"{num} B" : $"{value:0.0} {unit}";
            value /= 1024;
        }
        return $"{num} B";
    }

    public static string Speed(double bps)
    {
        if (bps <= 0) return "0 KB/s";
        var kb = bps / 1024.0;
        if (kb < 10) return $"{kb:0.0} KB/s";
        if (kb < 1024) return $"{kb:0} KB/s";
        return $"{kb / 1024.0:0.0} MB/s";
    }

    public static string Duration(double? seconds)
    {
        if (seconds is null) return "--";
        var total = Math.Max(0, (int)seconds.Value);
        var hours = Math.DivRem(total, 3600, out var rest);
        var minutes = Math.DivRem(rest, 60, out var secs);
        if (hours > 0) return $"{hours}h {minutes:00}m";
        if (minutes > 0) return $"{minutes}m {secs:00}s";
        return $"{secs}s";
    }
}

public static class Power
{
    public static readonly (string Key, string Label)[] Actions =
    [
        ("notify", "Just notify me"),
        ("exit_steam", "Exit Steam"),
        ("block_internet", "Block Steam internet"),
        ("lock", "Lock Windows"),
        ("sleep", "Sleep"),
        ("hibernate", "Hibernate"),
        ("signout", "Sign out"),
        ("restart", "Restart PC"),
        ("shutdown", "Shut down PC"),
    ];

    [DllImport("user32.dll")] private static extern bool LockWorkStation();
    [DllImport("powrprof.dll", SetLastError = true)] private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    public static string Run(string action, string? steamRoot)
    {
        switch (action)
        {
            case "notify": return "Downloads finished.";
            case "exit_steam":
                var killed = SteamClient.KillSteam();
                return $"Downloads finished, Steam closed ({killed} process(es)).";
            case "block_internet":
                if (steamRoot is null) return "Downloads finished, but Steam path is unknown.";
                var count = Firewall.Block(steamRoot, false);
                SteamClient.KillSteam();
                return $"Downloads finished, blocked {count} Steam binary path(s).";
            case "lock":
                LockWorkStation();
                return "Downloads finished, workstation locked.";
            case "sleep":
                SetSuspendState(false, true, false);
                return "Downloads finished, entering sleep.";
            case "hibernate":
                SetSuspendState(true, true, false);
                return "Downloads finished, entering hibernate.";
            case "signout":
                RunShutdown("/l");
                return "Downloads finished, signing out.";
            case "restart":
                RunShutdown("/r /f /t 0");
                return "Downloads finished, restarting.";
            case "shutdown":
                RunShutdown("/s /f /t 0");
                return "Downloads finished, shutting down.";
            default:
                return "Unknown action: " + action;
        }
    }

    public static bool CancelShutdown()
    {
        using var proc = Process.Start(new ProcessStartInfo("shutdown", "/a") { CreateNoWindow = true, UseShellExecute = false });
        proc?.WaitForExit();
        return proc?.ExitCode == 0;
    }

    private static void RunShutdown(string args)
    {
        Process.Start(new ProcessStartInfo("shutdown", args) { CreateNoWindow = true, UseShellExecute = false });
    }
}

public sealed class AppSettings
{
    public string AutoAction { get; set; } = "shutdown";
    public int CountdownSeconds { get; set; } = 60;
    public int GraceSeconds { get; set; } = 45;
    public bool IncludeQueued { get; set; } = true;
    public bool RequireActivityFirst { get; set; } = true;
    public bool BlockGameExes { get; set; }
    public bool KillAfterBlock { get; set; } = true;
    public bool RestartAfterUnblock { get; set; } = true;
    public string DnsAdapter { get; set; } = "";
    public string DnsProvider { get; set; } = "Cloudflare";
    public string WatchTarget { get; set; } = "steam";
    public int SpeedFloorKbps { get; set; } = 200;

    private static string PathName =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamStopper", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(PathName))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(PathName)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathName)!);
            File.WriteAllText(PathName, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
