using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamStopper.Core;

public sealed record UpdateInfo(Version Version, string ZipUrl, string ZipName, string? ShaUrl, string PageUrl, string Notes);

/// <summary>
/// Updates from GitHub releases. The new build is downloaded and checked first; then the new version's own exe
/// is started with --install-update, waits for this app (and its watchdog) to exit, swaps the files with a
/// backup of each one it replaces, rolls back if anything fails, and starts the result.
/// </summary>
public static class Updater
{
    public const string InstallArg = "--install-update";
    private const string DefaultFeed = "https://github.com/V3rkku1/steam-stopper";

    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "2.5.8";

    // Installed copies (zip or MSI) carry a .installed marker; the portable build is one self-contained exe.
    public static bool IsInstalled =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, ".installed"));

    private static string TempRoot => Path.Combine(Path.GetTempPath(), "SteamStopperUpdate");

    public static string FeedUrl()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "FeedUrl.txt");
            if (File.Exists(path))
                foreach (var line in File.ReadAllLines(path))
                {
                    var text = line.Trim();
                    if (text.Length > 0 && !text.StartsWith('#')) return text;
                }
        }
        catch { }
        // The portable zip has no FeedUrl.txt; it should still find updates.
        return DefaultFeed;
    }

    /// <summary>The newest release if it is newer than this build, else null. Throws on network errors.</summary>
    public static async Task<UpdateInfo?> FindAsync(CancellationToken cancel)
    {
        var match = Regex.Match(FeedUrl().TrimEnd('/'), @"github\.com/([^/]+)/([^/]+)", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SteamStopper/" + Version);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        var json = await http.GetStringAsync(
            $"https://api.github.com/repos/{match.Groups[1].Value}/{match.Groups[2].Value.Replace(".git", "")}/releases/latest", cancel);
        return Pick(json, System.Version.Parse(Version), IsInstalled);
    }

    public static UpdateInfo? Pick(string json, Version current, bool installed)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!System.Version.TryParse(root.GetProperty("tag_name").GetString()?.Trim().TrimStart('v'), out var remote) || remote <= current)
            return null;
        var assets = root.GetProperty("assets").EnumerateArray()
            .Select(a => (Name: a.GetProperty("name").GetString() ?? "", Url: a.GetProperty("browser_download_url").GetString() ?? ""))
            .ToList();
        // Installed copies take the app zip; the portable exe takes the portable zip (one self-contained exe).
        var wanted = installed ? "SteamStopper.zip" : "SteamStopper-portable.zip";
        var zip = assets.FirstOrDefault(a => a.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase));
        if (zip.Url is not { Length: > 0 }) return null;
        var sha = assets.FirstOrDefault(a => a.Name.Equals(wanted + ".sha256", StringComparison.OrdinalIgnoreCase));
        return new UpdateInfo(remote, zip.Url, zip.Name, sha.Url is { Length: > 0 } ? sha.Url : null,
            root.TryGetProperty("html_url", out var page) ? page.GetString() ?? "" : "",
            root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "");
    }

    /// <summary>Check the download against the release's .sha256 file ("hash" or "hash  name").</summary>
    public static bool Verify(string file, string shaText)
    {
        var expected = shaText.Trim().Split((char[])[' ', '\t', '*'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Download, verify and unpack the update. Returns the folder holding the new files.</summary>
    public static async Task<string> PrepareAsync(UpdateInfo info, Fetcher fetcher, Action<FetchJob> started, CancellationToken cancel)
    {
        var root = Path.Combine(TempRoot, info.Version.ToString());
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        var job = new FetchJob { Url = info.ZipUrl, Folder = root };
        started(job);
        await fetcher.Run(job, 4, cancel);
        cancel.ThrowIfCancellationRequested();
        if (job.State != FetchState.Done) throw new IOException(job.Error.Length > 0 ? job.Error : "The download stopped.");
        if (info.ShaUrl is not null)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SteamStopper/" + Version);
            if (!Verify(job.FinalPath, await http.GetStringAsync(info.ShaUrl, cancel)))
                throw new IOException("The download is damaged (checksum mismatch). Nothing was changed.");
        }
        var files = Path.Combine(root, "files");
        ZipFile.ExtractToDirectory(job.FinalPath, files, true);
        if (!File.Exists(Path.Combine(files, "SteamStopper.exe")))
            throw new IOException("The update has no SteamStopper.exe. Nothing was changed.");
        return files;
    }

    /// <summary>Hand over to the new version's exe, which installs itself once this process has exited.</summary>
    public static void Launch(string files)
    {
        var psi = new ProcessStartInfo(Path.Combine(files, "SteamStopper.exe")) { UseShellExecute = false, WorkingDirectory = files };
        psi.ArgumentList.Add(InstallArg);
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        psi.ArgumentList.Add(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        Process.Start(psi)?.Dispose();
    }

    /// <summary>
    /// Runs inside the new version (from the temp folder): copy its files over dest. Every replaced file is
    /// moved to dest\.update-backup first, so a failure puts the old version back. Returns null on success.
    /// </summary>
    public static string? Install(int oldPid, string source, string dest, bool start = true)
    {
        var backup = Path.Combine(dest, ".update-backup");
        var replaced = new List<string>();
        var added = new List<string>();
        try
        {
            WaitForExit(oldPid, dest);
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(source, file);
                // The portable exe often lives in Downloads next to the user's own files; never touch a README there.
                if (rel.Equals("README.txt", StringComparison.OrdinalIgnoreCase)) continue;
                var target = Path.Combine(dest, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    var saved = Path.Combine(backup, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                    Retry(() => File.Move(target, saved));
                    replaced.Add(rel);
                }
                else added.Add(rel);
                File.Copy(file, target);
            }
            try { Directory.Delete(backup, true); } catch { }
            if (start) StartApp(dest);
            return null;
        }
        catch (Exception ex)
        {
            foreach (var rel in added)
                try { File.Delete(Path.Combine(dest, rel)); } catch { }
            foreach (var rel in replaced)
                try { File.Move(Path.Combine(backup, rel), Path.Combine(dest, rel), true); } catch { }
            if (start) StartApp(dest);
            return ex.Message;
        }
    }

    /// <summary>Wait for the old app, then for its watchdog (also SteamStopper.exe, still cleaning up), which holds the exe open.</summary>
    private static void WaitForExit(int pid, string dest)
    {
        try
        {
            using var old = Process.GetProcessById(pid);
            if (!old.WaitForExit(60_000)) throw new IOException("Steam Multitool did not close, so it could not be updated.");
        }
        catch (ArgumentException) { } // Already gone.
        var until = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var others = Process.GetProcessesByName("SteamStopper")
                .Where(p => p.Id != Environment.ProcessId && RunsFrom(p, dest)).ToList();
            if (others.Count == 0) return;
            foreach (var p in others)
            {
                // The watchdog normally finishes within seconds; past the deadline it is stuck.
                if (DateTime.UtcNow > until) try { p.Kill(); } catch { }
                p.Dispose();
            }
            Thread.Sleep(300);
        }
    }

    private static bool RunsFrom(Process p, string dest)
    {
        try { return p.MainModule?.FileName?.StartsWith(dest, StringComparison.OrdinalIgnoreCase) == true; }
        catch { return false; }
    }

    // Antivirus scanners briefly lock freshly closed exes.
    private static void Retry(Action action)
    {
        for (var i = 0; ; i++)
        {
            try { action(); return; }
            catch (IOException) when (i < 20) { Thread.Sleep(250); }
            catch (UnauthorizedAccessException) when (i < 20) { Thread.Sleep(250); }
        }
    }

    private static void StartApp(string dest)
    {
        try { Process.Start(new ProcessStartInfo(Path.Combine(dest, "SteamStopper.exe")) { UseShellExecute = false, WorkingDirectory = dest })?.Dispose(); }
        catch { }
    }

    /// <summary>Delete leftover downloads once a new version is running from its real folder.</summary>
    public static void CleanTemp()
    {
        if (AppContext.BaseDirectory.StartsWith(TempRoot, StringComparison.OrdinalIgnoreCase)) return;
        try { if (Directory.Exists(TempRoot)) Directory.Delete(TempRoot, true); } catch { }
    }
}
