using System.Diagnostics;
using System.IO;

namespace SteamStopper.Core;

public sealed class DownloadJob
{
    public const int UpdateRequired = 2;
    public const int FilesMissing = 32;
    public const int UpdateRunning = 256;
    public const int UpdatePaused = 512;
    public const int UpdateStarted = 1024;
    public const int Validating = 65536;
    public const int AddingFiles = 131072;
    public const int Preallocating = 262144;
    public const int Downloading = 524288;
    public const int Staging = 1048576;
    public const int Committing = 2097152;
    public const int ActiveMask = UpdateRunning | UpdateStarted | Validating | AddingFiles | Preallocating | Downloading | Staging | Committing;

    public string AppId { get; init; } = "";
    public string Name { get; init; } = "";
    public long Downloaded { get; init; }
    public long Total { get; init; }
    public long Staged { get; init; }
    public int StateFlags { get; init; }
    public string Library { get; init; } = "";
    public long PendingBytes => Math.Max(0, Total - Downloaded);
    public double Percent => Total <= 0 ? 0 : Math.Min(1, Downloaded / (double)Total);
    public string ProgressText => Total > 0 ? $"{Percent * 100:0}%" : Status;
    public string RemainingText => Total > 0 ? Format.Bytes(PendingBytes) : "—";
    public string SizeText => Total > 0 ? $"{Format.Bytes(Downloaded)} / {Format.Bytes(Total)}" : "—";
    public bool IsPaused => (StateFlags & UpdatePaused) != 0;
    public bool IsActive => (StateFlags & ActiveMask) != 0;
    public string Status
    {
        get
        {
            var flags = StateFlags;
            if ((flags & Committing) != 0) return "Committing";
            if ((flags & Staging) != 0) return "Staging";
            if ((flags & Validating) != 0) return "Validating";
            if ((flags & Preallocating) != 0) return "Preallocating";
            if ((flags & (Downloading | AddingFiles)) != 0) return "Downloading";
            if (IsPaused) return "Paused";
            if ((flags & (UpdateRunning | UpdateStarted)) != 0) return "Updating";
            if ((flags & FilesMissing) != 0) return "Repairing";
            if ((flags & UpdateRequired) != 0) return "Queued";
            return "Pending";
        }
    }
}

public sealed class DownloadSnapshot
{
    public List<DownloadJob> Jobs { get; init; } = [];
    public long Downloaded { get; init; }
    public long Total { get; init; }
    public long Remaining { get; init; }
    public double SpeedBps { get; init; }
    public double? EtaSeconds { get; init; }
    public bool Active { get; init; }
    public bool SeenActivity { get; init; }
    public double IdleSeconds { get; init; }
    public bool Finished { get; init; }
    public double Percent => Total <= 0 ? 0 : Math.Min(1, Downloaded / (double)Total);
    public int ActiveCount => Jobs.Count(j => j.IsActive);
}

public sealed class DownloadWatcher
{
    private readonly Dictionary<string, long> _lastBytes = [];
    private readonly Queue<(long Ticks, long Delta)> _samples = [];
    private long? _idleSince;

    public DownloadWatcher(string steamRoot) => SteamRoot = steamRoot;

    public string SteamRoot { get; }
    public bool IncludeQueued { get; set; } = true;
    public double GraceSeconds { get; set; } = 45;
    public bool RequireActivityFirst { get; set; } = true;
    public bool SeenActivity { get; private set; }

    public void Reset()
    {
        _lastBytes.Clear();
        _samples.Clear();
        SeenActivity = false;
        _idleSince = null;
    }

    public static List<DownloadJob> ListJobs(string steamRoot, bool includeQueued)
    {
        var jobs = new List<DownloadJob>();
        foreach (var library in SteamClient.LibraryPaths(steamRoot))
        {
            var apps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                Dictionary<string, object> data;
                try { data = Vdf.Parse(File.ReadAllText(acf)); }
                catch { continue; }
                var app = data.TryGetValue("AppState", out var state) ? Vdf.AsObject(state) : data;
                var flags = (int)Vdf.GetLong(app, "StateFlags");
                var downloaded = Vdf.GetLong(app, "BytesDownloaded");
                var total = Vdf.GetLong(app, "BytesToDownload");
                var staged = Vdf.GetLong(app, "BytesStaged");
                var active = (flags & DownloadJob.ActiveMask) != 0;
                var pending = total > 0 && downloaded < total;
                var queued = (flags & (DownloadJob.UpdateRequired | DownloadJob.UpdatePaused | DownloadJob.FilesMissing)) != 0;
                if (!(active || pending || (includeQueued && queued))) continue;
                jobs.Add(new DownloadJob
                {
                    AppId = Vdf.GetString(app, "appid", Path.GetFileNameWithoutExtension(acf).Replace("appmanifest_", "")),
                    Name = Vdf.GetString(app, "name", Path.GetFileNameWithoutExtension(acf)),
                    Downloaded = downloaded,
                    Total = total,
                    Staged = staged,
                    StateFlags = flags,
                    Library = library
                });
            }
        }
        return jobs.OrderBy(j => j.IsActive ? 0 : 1).ThenBy(j => j.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public DownloadSnapshot Poll()
    {
        var now = Stopwatch.GetTimestamp();
        var jobs = ListJobs(SteamRoot, IncludeQueued);
        var current = jobs.ToDictionary(j => j.AppId, j => j.Downloaded + j.Staged);
        long delta = 0;
        foreach (var (appid, value) in current)
            delta += Math.Max(0, value - _lastBytes.GetValueOrDefault(appid, value));
        _lastBytes.Clear();
        foreach (var pair in current) _lastBytes[pair.Key] = pair.Value;

        _samples.Enqueue((now, delta));
        var window = TimeSpan.FromSeconds(25).Ticks;
        while (_samples.Count > 0 && now - _samples.Peek().Ticks > window)
            _samples.Dequeue();

        double speed = 0;
        if (_samples.Count >= 2)
        {
            var span = Stopwatch.GetElapsedTime(_samples.Peek().Ticks, now).TotalSeconds;
            if (span > 0) speed = _samples.Skip(1).Sum(s => s.Delta) / span;
        }

        var pending = jobs.Count > 0;
        var active = delta > 0 || jobs.Any(j => j.IsActive);
        if (active) SeenActivity = true;
        if (pending) _idleSince = null;
        else _idleSince ??= now;

        var idle = _idleSince is null ? 0 : Stopwatch.GetElapsedTime(_idleSince.Value, now).TotalSeconds;
        var remaining = jobs.Sum(j => j.PendingBytes);
        return new DownloadSnapshot
        {
            Jobs = jobs,
            Downloaded = jobs.Sum(j => j.Downloaded),
            Total = jobs.Sum(j => j.Total),
            Remaining = remaining,
            SpeedBps = speed,
            EtaSeconds = speed > 0 && remaining > 0 ? remaining / speed : null,
            Active = active,
            SeenActivity = SeenActivity,
            IdleSeconds = idle,
            Finished = !pending && idle >= GraceSeconds && (SeenActivity || !RequireActivityFirst)
        };
    }
}
