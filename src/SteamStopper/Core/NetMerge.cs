using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace SteamStopper.Core;

/// <summary>
/// Optional system-wide bonding. net-merge (github.com/net-merge/net-merge, MIT) puts a Wintun virtual adapter in
/// front of every app, TCP and UDP, and spreads their connections over the links. We drive its command line
/// instead of carrying our own virtual adapter and TCP/IP stack.
/// </summary>
public static class NetMerge
{
    private const string ZipUrl = "https://github.com/net-merge/net-merge/releases/latest/download/net-merge-portable.zip";
    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamStopper", "net-merge");
    private static string Exe => Path.Combine(Dir, "net-merge.exe");
    private static string Log => Path.Combine(Dir, "run.log");

    /// <summary>Download net-merge (with its signed wintun.dll) the first time it's needed.</summary>
    public static async Task Ensure(CancellationToken cancel = default)
    {
        if (File.Exists(Exe) && File.Exists(Path.Combine(Dir, "wintun.dll"))) return;
        Directory.CreateDirectory(Dir);
        var zip = Path.Combine(Dir, "net-merge.zip");
        using (var http = new HttpClient())
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SteamMultitool");
            await using var body = await http.GetStreamAsync(ZipUrl, cancel);
            await using var file = File.Create(zip);
            await body.CopyToAsync(file, cancel);
        }
        ZipFile.ExtractToDirectory(zip, Dir, overwriteFiles: true);
        File.Delete(zip);
        if (!File.Exists(Exe)) throw new IOException("The net-merge download has no net-merge.exe in it.");
    }

    /// <summary>Start bonding every app over these adapters. Null once it's on, else why it isn't.</summary>
    public static async Task<string?> Start(IReadOnlyList<BondLink> links)
    {
        if (Running()) return null;
        // Errors before logging starts (bad flag, no admin) only go to stderr.
        var psi = new ProcessStartInfo(Exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Dir, RedirectStandardError = true };
        foreach (var arg in new[] { "run", "-include", string.Join(",", links.Select(l => l.Name)), "-stats", "0", "-log", Log })
            psi.ArgumentList.Add(arg);
        // Fixed weights only when measured balancing is off; net-merge measures speeds itself otherwise.
        if (!BondLink.AutoBalance)
            foreach (var link in links)
            {
                psi.ArgumentList.Add("-weight");
                psi.ArgumentList.Add($"{link.Name}={link.Weight}");
            }
        try { File.Delete(Log); } catch (IOException) { }
        using var process = Process.Start(psi);
        if (process is null) return "Could not start net-merge.";
        // It brings its adapter up within seconds, or exits with the reason in its log.
        for (var i = 0; i < 60 && !process.HasExited; i++)
        {
            if (ReadLog().Contains("is ON")) return null;
            await Task.Delay(250);
        }
        if (!process.HasExited) return null; // still starting; Stop works either way
        var reason = (ReadLog() + "\n" + await process.StandardError.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
        return "net-merge stopped: " + (reason ?? $"exit code {process.ExitCode}");
    }

    /// <summary>Turn it off and remove its adapter. Only touches the copy this app started.</summary>
    public static void Stop()
    {
        if (!Running()) return;
        Run("off");
        if (!Running()) return;
        // It didn't answer: end it and remove the routes it left behind.
        foreach (var process in Ours())
            using (process)
                try { process.Kill(); process.WaitForExit(5000); } catch { }
        Run("cleanup");
    }

    public static bool Running()
    {
        var list = Ours();
        foreach (var process in list) process.Dispose();
        return list.Count > 0;
    }

    private static List<Process> Ours()
    {
        var mine = new List<Process>();
        foreach (var process in Process.GetProcessesByName("net-merge"))
        {
            string? path = null;
            try { path = process.MainModule?.FileName; } catch { }
            if (path is not null && path.StartsWith(Dir, StringComparison.OrdinalIgnoreCase)) mine.Add(process);
            else process.Dispose();
        }
        return mine;
    }

    private static void Run(string command)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Exe, command) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Dir });
            process?.WaitForExit(15000);
        }
        catch { }
    }

    private static string ReadLog()
    {
        try
        {
            using var stream = new FileStream(Log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new StreamReader(stream).ReadToEnd();
        }
        catch (IOException) { return ""; }
    }
}
