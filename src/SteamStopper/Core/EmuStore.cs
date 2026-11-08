using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamStopper.Core;

/// <summary>Where an emulator publishes Windows builds. Assets are tried in order; Exe picks the program inside.</summary>
/// <param name="Host">"github", or the base URL of a GitLab server.</param>
public sealed record EmuSource(string Repo, string[] Assets, string Exe, string Host = "github");

public sealed record EmuRelease(string Url, string File, string Version);

/// <summary>
/// Portable emulator installs: download the latest release archive, unpack it under
/// %LOCALAPPDATA%\SteamStopper\Emulators\name, and remember which exe to start.
/// </summary>
public static class EmuStore
{
    // Debug symbols, ARM builds and installers sit next to the portable archives on most release pages.
    // libretro: RetroArch cores (Azahar ships one next to its own app), not standalone emulators.
    private static readonly Regex SkipAsset = new(@"pdb|symbols|debug|dbg|arm64|aarch64|installer|setup|libretro|\.sig$|\.sha", RegexOptions.IgnoreCase);
    // Helper programs that ship beside the real emulator exe.
    private static readonly Regex SkipExe = new(@"unins|updat|crash|setup|redist|helper|dxwebsetup|elevat|7z|createdump", RegexOptions.IgnoreCase);
    private static readonly object Gate = new();

    public static string Root =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamStopper", "Emulators");

    private static string ManifestPath => Path.Combine(Root, "installed.json");

    public static string FolderFor(string name) => Path.Combine(Root, string.Concat(name.Split(Path.GetInvalidFileNameChars())));

    /// <summary>Newest release (pre-releases included, drafts skipped) that has a Windows build matching the source.</summary>
    /// <param name="pgo">Prefer builds with "pgo" in the name (profile-guided, usually faster); false skips them.</param>
    public static async Task<EmuRelease?> FindLatest(EmuSource source, bool pgo, CancellationToken cancel)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SteamStopper");
        // Only set in CI, where anonymous lookups share an hourly limit with every other job on the runner.
        if (source.Host == "github" && Environment.GetEnvironmentVariable("GITHUB_TOKEN") is { Length: > 0 } token)
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (source.Host == "github")
        {
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            var api = $"https://api.github.com/repos/{source.Repo}/releases?per_page=";
            string json;
            // Repos with many big daily releases (Eglantine) can time out on GitHub's side; a smaller page usually works.
            try { json = await http.GetStringAsync(api + 15, cancel); }
            catch (HttpRequestException ex) when ((int?)ex.StatusCode >= 500) { json = await http.GetStringAsync(api + 3, cancel); }
            return PickGitHub(json, source, pgo);
        }
        var host = source.Host.TrimEnd('/');
        try
        {
            return PickGitLab(await http.GetStringAsync($"{host}/api/v4/projects/{Uri.EscapeDataString(source.Repo)}/releases?per_page=15", cancel), source, pgo);
        }
        catch (HttpRequestException)
        {
            // Not GitLab after all: Forgejo/Gitea serve GitHub-shaped release JSON under /api/v1.
            return PickGitHub(await http.GetStringAsync($"{host}/api/v1/repos/{source.Repo}/releases?limit=15", cancel), source, pgo);
        }
    }

    public static EmuRelease? PickGitHub(string json, EmuSource source, bool pgo = true)
    {
        using var doc = JsonDocument.Parse(json);
        var releases = doc.RootElement.EnumerateArray()
            .Where(r => !(r.TryGetProperty("draft", out var d) && d.GetBoolean()))
            .Select(r => (Tag: r.GetProperty("tag_name").GetString() ?? "",
                Files: r.GetProperty("assets").EnumerateArray()
                    .Select(a => (Name: a.GetProperty("name").GetString() ?? "", Url: a.GetProperty("browser_download_url").GetString() ?? ""))
                    .ToList()))
            .ToList();
        return Pick(releases, source, pgo);
    }

    public static EmuRelease? PickGitLab(string json, EmuSource source, bool pgo = true)
    {
        using var doc = JsonDocument.Parse(json);
        var releases = doc.RootElement.EnumerateArray()
            .Select(r => (Tag: r.GetProperty("tag_name").GetString() ?? "",
                Files: r.TryGetProperty("assets", out var assets) && assets.TryGetProperty("links", out var links)
                    ? links.EnumerateArray()
                        .Select(l => (Name: l.GetProperty("name").GetString() ?? "",
                            Url: (l.TryGetProperty("direct_asset_url", out var direct) ? direct.GetString() : null) ?? l.GetProperty("url").GetString() ?? ""))
                        .ToList()
                    : []))
            .ToList();
        return Pick(releases, source, pgo);
    }

    private static EmuRelease? Pick(List<(string Tag, List<(string Name, string Url)> Files)> releases, EmuSource source, bool pgo)
    {
        foreach (var (tag, files) in releases)
        {
            foreach (var pattern in source.Assets)
            {
                var hit = files
                    .Where(f => !SkipAsset.IsMatch(f.Name) && Regex.IsMatch(f.Name, pattern, RegexOptions.IgnoreCase))
                    .Where(f => pgo || !IsPgo(f.Name))
                    .OrderBy(f => pgo && IsPgo(f.Name) ? 0 : 1)
                    .FirstOrDefault();
                if (hit.Url is { Length: > 0 }) return new EmuRelease(hit.Url, hit.Name, tag);
            }
        }
        return null;
    }

    /// <summary>Unpack into dest, overwriting older files but keeping anything the user added (keys, saves, configs).</summary>
    public static void Extract(string archive, string dest)
    {
        Directory.CreateDirectory(dest);
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archive, dest, overwriteFiles: true);
            return;
        }
        // .7z: Windows 11's tar.exe reads it (libarchive); otherwise use an installed 7-Zip.
        var sevenZip = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe")
        }.FirstOrDefault(File.Exists);
        var tar = Path.Combine(Environment.SystemDirectory, "tar.exe");
        var tried = new List<string>();
        if (File.Exists(tar))
        {
            if (Run(tar, ["-xf", archive, "-C", dest]) == 0) return;
            tried.Add("Windows tar");
        }
        if (sevenZip is not null)
        {
            if (Run(sevenZip, ["x", "-y", "-o" + dest, archive]) == 0) return;
            tried.Add("7-Zip");
        }
        throw new IOException(tried.Count == 0
            ? "This emulator ships as a .7z file. Install 7-Zip from the Apps tab, then try again."
            : $"Could not unpack the .7z file ({string.Join(", ", tried)} failed). Install 7-Zip from the Apps tab, then try again.");
    }

    private static int Run(string file, string[] args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p is null) return -1;
        // Drain both pipes so a chatty extractor can't block on a full buffer.
        var drain = Task.WhenAll(p.StandardOutput.ReadToEndAsync(), p.StandardError.ReadToEndAsync());
        p.WaitForExit();
        drain.Wait();
        return p.ExitCode;
    }

    /// <summary>The emulator's own exe: matches the hint, isn't a helper, and sits closest to the top.</summary>
    public static string? FindExe(string dir, string hint)
    {
        if (!Directory.Exists(dir)) return null;
        var exes = Directory.EnumerateFiles(dir, "*.exe", SearchOption.AllDirectories)
            .Where(f => !SkipExe.IsMatch(Path.GetFileName(f)))
            .Select(f => new FileInfo(f))
            .ToList();
        var hinted = exes.Where(f => Regex.IsMatch(f.Name, hint, RegexOptions.IgnoreCase)).ToList();
        return (hinted.Count > 0 ? hinted : exes)
            .OrderBy(f => f.FullName.Count(c => c == Path.DirectorySeparatorChar))
            .ThenByDescending(f => f.Length)
            .FirstOrDefault()?.FullName;
    }

    public static bool IsPgo(string file) => Regex.IsMatch(file, @"(?<![a-z])pgo(?![a-z])", RegexOptions.IgnoreCase);

    /// <param name="File">Release file it came from; tells PGO and standard builds of one version apart.</param>
    public sealed record Entry(string Exe, string Version, string File = "");

    public static Dictionary<string, Entry> Installed()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(ManifestPath))
                    return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(ManifestPath)) ?? [];
            }
            catch { }
            return [];
        }
    }

    /// <summary>The recorded exe for a portable install, if it is still on disk.</summary>
    public static Entry? Find(string name) =>
        Installed().TryGetValue(name, out var e) && File.Exists(e.Exe) ? e : null;

    public static void Record(string name, Entry? entry)
    {
        lock (Gate)
        {
            var all = Installed();
            if (entry is null) all.Remove(name);
            else all[name] = entry;
            Directory.CreateDirectory(Root);
            File.WriteAllText(ManifestPath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    /// <summary>Delete a portable install and its record.</summary>
    public static void Remove(string name)
    {
        var dir = FolderFor(name);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Record(name, null);
    }
}
