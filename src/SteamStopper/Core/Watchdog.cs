using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace SteamStopper.Core;

/// <summary>
/// Puts the internet back when Steam Stopper dies without closing normally (crash, Task Manager, power button).
/// Bonding points the Windows proxy at this process and routed apps run through proxies inside it,
/// so once it is gone browsers and Discord can't connect. A second copy of the app ("--watchdog pid")
/// waits for this one to exit and undoes both. A normal exit cleans up first, leaving it nothing to do.
/// </summary>
public static class Watchdog
{
    public const string Arg = "--watchdog";
    private static readonly object Gate = new();
    private static bool _started;

    private static string RoutedPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamStopper", "routed-apps.json");

    /// <summary>Start the watchdog once, before the first change that would break the internet if we died.</summary>
    public static void Ensure()
    {
        lock (Gate)
        {
            if (_started || Environment.ProcessPath is not { } exe) return;
            try
            {
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add(Arg);
                psi.ArgumentList.Add(Environment.ProcessId.ToString());
                Process.Start(psi)?.Dispose();
                _started = true;
            }
            catch { }
        }
    }

    /// <summary>The watchdog process: wait for the app to exit, then repair what it left behind.</summary>
    public static void Run(int pid)
    {
        try
        {
            using var app = Process.GetProcessById(pid);
            app.WaitForExit();
        }
        catch (ArgumentException) { } // Already gone.
        Recover();
    }

    /// <summary>Restore the proxy setting and restart routed apps without their dead proxy. Safe when nothing is left over.</summary>
    public static void Recover()
    {
        try { SystemProxy.Restore(); } catch { }
        foreach (var (key, exe) in Routed())
        {
            var app = AppRoutes.Catalog.FirstOrDefault(a => a.Key == key);
            try
            {
                if (app is not null && File.Exists(exe) && AppRoutes.IsRunning(app))
                    AppRoutes.Restart(app, exe, null);
            }
            catch { }
        }
        lock (Gate) try { File.Delete(RoutedPath); } catch { }
    }

    /// <summary>Remember (exe set) or forget (exe null) an app that was restarted through one of our proxies.</summary>
    public static void TrackRouted(string key, string? exe)
    {
        lock (Gate)
        {
            var routed = Routed();
            if (exe is null) routed.Remove(key);
            else routed[key] = exe;
            try
            {
                if (routed.Count == 0) File.Delete(RoutedPath);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(RoutedPath)!);
                    File.WriteAllText(RoutedPath, JsonSerializer.Serialize(routed));
                }
            }
            catch { }
        }
    }

    private static Dictionary<string, string> Routed()
    {
        try
        {
            if (File.Exists(RoutedPath))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(RoutedPath)) ?? [];
        }
        catch { }
        return [];
    }
}
