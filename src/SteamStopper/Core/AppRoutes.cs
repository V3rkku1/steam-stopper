using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;

namespace SteamStopper.Core;

public sealed class RouteApp
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string[] Processes { get; init; } = [];
    // Destination ranges sent out the app's link with a plain Windows route. Catches the app's
    // UDP traffic (Discord voice) that a proxy can't carry.
    public string[] Networks { get; init; } = [];
    public Func<string?> Find { get; init; } = () => null;
    public Func<string, int?, ProcessStartInfo> Launch { get; init; } = (_, _) => new ProcessStartInfo();
}

public sealed class RouteRule : INotifyPropertyChanged
{
    public const string Main = "Main connection";
    private string _link = Main;
    private string _status = "";

    public RouteApp App { get; init; } = new();
    public string Name => App.Name;
    public string? Exe { get; init; }
    public bool Installed => Exe is not null;
    public List<string> LinkChoices { get; init; } = [Main];

    public string Link
    {
        get => _link;
        set { _link = value ?? Main; OnChanged(); }
    }

    public string Status
    {
        get => _status;
        set { _status = value; OnChanged(); }
    }

    // Set while the app runs pinned to a link.
    internal LinkBonder? Proxy;
    internal BondLink? Pinned;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Per-app internet choice without touching the app itself: the main link carries everything by
/// default (games like Valorant, so anti-cheat never sees anything injected), and chosen apps are
/// restarted with a proxy pinned to another link plus routes for their voice servers.
/// </summary>
public static class AppRoutes
{
    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string Programs => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static string Programs86 => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

    public static readonly RouteApp[] Catalog =
    [
        new()
        {
            Key = "discord",
            Name = "Discord",
            Processes = ["Discord"],
            // Discord's own address block (AS49544), where its voice servers live.
            Networks = ["66.22.192.0/18"],
            Find = () => FirstFile(Path.Combine(Local, "Discord", "Update.exe")),
            Launch = (exe, port) => Squirrel(exe, "Discord.exe", port)
        },
        Chromium("chrome", "Google Chrome", "chrome",
            Path.Combine(Programs, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Programs86, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Local, "Google", "Chrome", "Application", "chrome.exe")),
        Chromium("edge", "Microsoft Edge", "msedge",
            Path.Combine(Programs86, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Programs, "Microsoft", "Edge", "Application", "msedge.exe")),
        Chromium("brave", "Brave", "brave",
            Path.Combine(Programs, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
            Path.Combine(Local, "BraveSoftware", "Brave-Browser", "Application", "brave.exe")),
        Chromium("opera", "Opera GX", "opera",
            Path.Combine(Local, "Programs", "Opera GX", "opera.exe")),
        Chromium("spotify", "Spotify", "Spotify",
            Path.Combine(Roaming, "Spotify", "Spotify.exe"))
    ];

    private static RouteApp Chromium(string key, string name, string process, params string[] paths) => new()
    {
        Key = key,
        Name = name,
        Processes = [process],
        Find = () => FirstFile(paths),
        Launch = (exe, port) =>
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            if (port is not null) psi.ArgumentList.Add($"--proxy-server=http://127.0.0.1:{port}");
            return psi;
        }
    };

    private static ProcessStartInfo Squirrel(string updater, string exe, int? port)
    {
        var psi = new ProcessStartInfo(updater) { UseShellExecute = false };
        psi.ArgumentList.Add("--processStart");
        psi.ArgumentList.Add(exe);
        if (port is not null)
        {
            psi.ArgumentList.Add("--process-start-args");
            psi.ArgumentList.Add($"--proxy-server=http://127.0.0.1:{port}");
        }
        return psi;
    }

    private static string? FirstFile(params string[] paths) => paths.FirstOrDefault(File.Exists);

    public static bool IsRunning(RouteApp app)
    {
        foreach (var name in app.Processes)
        {
            var list = Process.GetProcessesByName(name);
            var any = list.Length > 0;
            foreach (var p in list) p.Dispose();
            if (any) return true;
        }
        return false;
    }

    /// <summary>Close the app and start it again, through the pinned proxy or (port null) normally.</summary>
    public static void Restart(RouteApp app, string exe, int? port)
    {
        foreach (var name in app.Processes)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    try { p.Kill(true); } catch { }
                }
            }
        }
        var until = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < until && IsRunning(app))
            Thread.Sleep(200);
        Thread.Sleep(400);
        Process.Start(app.Launch(exe, port))?.Dispose();
    }

    public static string? AddRoutes(RouteApp app, BondLink link)
    {
        foreach (var net in app.Networks)
        {
            Netsh.Run(["interface", "ipv4", "delete", "route", "prefix=" + net, "interface=" + link.Index, "store=active"]);
            // store=active: gone after a reboot, so a crash can't leave a stray route behind for good.
            var error = Netsh.Run(["interface", "ipv4", "add", "route", "prefix=" + net, "interface=" + link.Index,
                "nexthop=" + link.Gateway, "metric=1", "store=active"]);
            if (error is not null) return error;
        }
        return null;
    }

    public static void RemoveRoutes(RouteApp app, BondLink link)
    {
        foreach (var net in app.Networks)
            Netsh.Run(["interface", "ipv4", "delete", "route", "prefix=" + net, "interface=" + link.Index,
                "nexthop=" + link.Gateway, "store=active"]);
    }

    /// <summary>
    /// Make one link the default for everything that isn't pinned: lowest interface metric wins
    /// in Windows' route choice.
    /// </summary>
    public static string? SetMain(BondLink main, IEnumerable<BondLink> all)
    {
        foreach (var link in all)
        {
            var metric = link.Name == main.Name ? 5 : 50;
            var error = Powershell($"Set-NetIPInterface -InterfaceIndex {link.Index} -AddressFamily IPv4 -InterfaceMetric {metric} -ErrorAction Stop");
            if (error is not null) return $"{link.Name}: {error}";
        }
        return null;
    }

    public static void RestoreMain(IEnumerable<string> adapterNames)
    {
        foreach (var name in adapterNames)
        {
            var safe = name.Replace("'", "''");
            Powershell($"Set-NetIPInterface -InterfaceAlias '{safe}' -AddressFamily IPv4 -AutomaticMetric Enabled -ErrorAction Stop");
        }
    }

    private static string? Powershell(string command)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(command);
            using var proc = Process.Start(psi);
            if (proc is null) return "Could not start PowerShell.";
            var stderr = proc.StandardError.ReadToEndAsync();
            proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(15000);
            var error = stderr.Result.Trim();
            return proc.ExitCode == 0 ? null : (error.Length > 0 ? error.Split('\n')[0].Trim() : $"exit code {proc.ExitCode}");
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}

internal static class Netsh
{
    public static string? Run(string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var proc = Process.Start(psi);
            if (proc is null) return "Could not start netsh.";
            var stderr = proc.StandardError.ReadToEndAsync();
            var output = (proc.StandardOutput.ReadToEnd() + " " + stderr.Result).Trim();
            proc.WaitForExit(8000);
            return proc.ExitCode == 0 ? null : (output.Length > 0 ? output : $"netsh exit code {proc.ExitCode}");
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
