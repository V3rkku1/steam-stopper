using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace SteamStopper.Core;

public enum FetchState { Queued, Connecting, Downloading, Paused, Done, Failed }

public sealed class FetchSegment
{
    public long Pos { get; set; }
    public long End { get; set; }
    internal bool Busy;
}

/// <summary>One file download. Its progress lives next to the file in "name.part.json", so it survives a restart.</summary>
public sealed class FetchJob : INotifyPropertyChanged
{
    internal readonly object Gate = new();
    private readonly Dictionary<string, long> _linkBytes = [];
    private Dictionary<string, long> _lastLinkBytes = [];
    private readonly Dictionary<string, double> _linkBps = [];
    private long _received;
    private long _lastReceived;
    private double _bps;
    private string _via = "";
    private FetchState _state;
    private string _error = "";

    public string Url { get; set; } = "";
    public string ResolvedUrl { get; set; } = "";
    public string Folder { get; set; } = "";
    public string FileName { get; set; } = "";
    public long Total { get; set; } = -1;
    public bool Ranged { get; set; }
    public string Validator { get; set; } = "";
    public bool Probed { get; set; }
    public List<FetchSegment> Segments { get; set; } = [];

    [JsonIgnore] public string FinalPath => Path.Combine(Folder, FileName);
    [JsonIgnore] public string PartPath => FinalPath + ".part";
    [JsonIgnore] public string StatePath => FinalPath + ".part.json";

    [JsonIgnore] public FetchState State => _state;
    [JsonIgnore] public string Error => _error;
    [JsonIgnore] public long Received => Interlocked.Read(ref _received);
    [JsonIgnore] public string Name => FileName.Length > 0 ? FileName : Url;
    [JsonIgnore] public string Hint => _error.Length > 0 ? $"{Url}\n{_error}" : Url;
    [JsonIgnore] public double Percent => Total > 0 ? Math.Min(1, Received / (double)Total) : _state == FetchState.Done ? 1 : 0;
    [JsonIgnore] public string ProgressText => Total > 0 ? $"{Percent * 100:0}%" : "—";
    [JsonIgnore] public string SizeText => Total > 0 ? $"{Format.Bytes(Received)} / {Format.Bytes(Total)}" : Received > 0 ? Format.Bytes(Received) : "—";
    [JsonIgnore] public double Bps => _bps;
    [JsonIgnore] public string SpeedText => _state == FetchState.Downloading ? Format.Speed(_bps) : "—";
    [JsonIgnore] public string EtaText => _state == FetchState.Downloading && Total > 0 && _bps > 1024
        ? Format.Duration((Total - Received) / _bps) : "—";
    [JsonIgnore] public string Detail => _state switch
    {
        FetchState.Failed => $"Failed · {_error}",
        FetchState.Done => $"Done · {Format.Bytes(Received)}",
        FetchState.Downloading when _via.Length > 0 => $"{SizeText} · {_via}",
        FetchState.Connecting => "Connecting…",
        _ => $"{_state} · {SizeText}"
    };
    [JsonIgnore] public bool Running => _state is FetchState.Connecting or FetchState.Downloading;
    [JsonIgnore] public bool CanResume => _state is FetchState.Paused or FetchState.Failed;

    internal void SetState(FetchState state, string error = "")
    {
        _state = state;
        _error = error;
        if (state != FetchState.Downloading) { _bps = 0; _via = ""; }
        Notify();
    }

    internal void NoteError(string error) => _error = error;

    internal void ResetProgress()
    {
        lock (Gate)
            Interlocked.Exchange(ref _received, Ranged && Total > 0
                ? Total - Segments.Sum(s => Math.Max(0, s.End - s.Pos))
                : Segments.Sum(s => s.Pos));
        _lastReceived = Received;
    }

    internal void Add(long bytes, string link)
    {
        Interlocked.Add(ref _received, bytes);
        lock (Gate) _linkBytes[link] = _linkBytes.GetValueOrDefault(link) + bytes;
    }

    /// <summary>Hand a worker a piece of the file: a free segment, or the back half of the biggest one in flight.</summary>
    internal FetchSegment? Take(long minSplit)
    {
        lock (Gate)
        {
            var free = Segments.FirstOrDefault(s => !s.Busy && s.Pos < s.End);
            if (free is not null) { free.Busy = true; return free; }
            if (!Ranged) return null;
            var big = Segments.Where(s => s.Busy).MaxBy(s => s.End - s.Pos);
            if (big is null || big.End - big.Pos < 2 * minSplit) return null;
            var mid = big.Pos + (big.End - big.Pos) / 2;
            var tail = new FetchSegment { Pos = mid, End = big.End, Busy = true };
            big.End = mid;
            Segments.Add(tail);
            return tail;
        }
    }

    internal void Release(FetchSegment segment)
    {
        lock (Gate) segment.Busy = false;
    }

    internal bool Complete
    {
        get { lock (Gate) return Segments.All(s => s.Pos >= s.End); }
    }

    /// <summary>Refresh speed and progress columns. Call from the UI thread about once a second.</summary>
    public void Sample(double seconds)
    {
        var now = Received;
        var instant = seconds > 0 ? Math.Max(0, now - _lastReceived) / seconds : 0;
        _lastReceived = now;
        // Smooth a little so the speed and ETA columns don't jump around every second.
        _bps = _bps <= 0 ? instant : _bps * 0.6 + instant * 0.4;
        Dictionary<string, long> bytes;
        int conns;
        lock (Gate)
        {
            bytes = new Dictionary<string, long>(_linkBytes);
            conns = Segments.Count(s => s.Busy);
        }
        if (_state == FetchState.Downloading)
        {
            // Smoothed like the total: data arrives in chunks, so a slower link (or the speed limit) can
            // have a second with nothing in it, and an unsmoothed link would blink out of the list.
            foreach (var (link, total) in bytes)
            {
                var linkNow = seconds > 0 ? Math.Max(0, total - _lastLinkBytes.GetValueOrDefault(link)) / seconds : 0;
                _linkBps[link] = _linkBps.TryGetValue(link, out var was) && was > 0 ? was * 0.6 + linkNow * 0.4 : linkNow;
            }
            var parts = _linkBps.Where(p => p.Value >= 1024).Select(p => $"{p.Key} {Format.Speed(p.Value)}");
            _via = $"{conns} conn · " + string.Join(" · ", parts);
        }
        _lastLinkBytes = bytes;
        Notify();
    }

    public void Save()
    {
        if (!Probed || _state == FetchState.Done) return;
        try
        {
            lock (Gate)
            {
                var tmp = StatePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this));
                File.Move(tmp, StatePath, true);
            }
        }
        catch { }
    }

    public static FetchJob? Load(string statePath)
    {
        try
        {
            var job = JsonSerializer.Deserialize<FetchJob>(File.ReadAllText(statePath));
            if (job is null || !File.Exists(job.PartPath)) return null;
            job.Segments.RemoveAll(s => s.Pos >= s.End);
            // A server without range support can't continue where it stopped.
            if (!job.Ranged) job.Segments = [new FetchSegment { Pos = 0, End = job.Total > 0 ? job.Total : long.MaxValue }];
            job.ResetProgress();
            job._state = FetchState.Paused;
            return job;
        }
        catch { return null; }
    }

    /// <summary>Delete the unfinished file and its progress. Finished files are kept.</summary>
    public void DeleteLeftovers()
    {
        if (_state == FetchState.Done || !Probed) return;
        try { File.Delete(PartPath); } catch { }
        try { File.Delete(StatePath); } catch { }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? _ = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}

internal sealed class FatalFetchException(string message) : Exception(message);

/// <summary>
/// Segmented HTTP downloader. Each connection is pinned to one of the bonded links (by binding
/// its source address, like <see cref="LinkBonder"/>), so one file downloads over all of them.
/// Fast links finish their piece first and then split the biggest piece still in flight.
/// </summary>
public sealed class Fetcher(Func<IReadOnlyList<BondLink>> links)
{
    private const long MinSplit = 1 << 20;
    private const int MaxFailures = 25;
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36";
    private static readonly TimeSpan Stall = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FailPenalty = TimeSpan.FromSeconds(20);
    private readonly Dictionary<string, HttpClient> _clients = [];
    private readonly object _pickGate = new();
    private readonly object _limitGate = new();
    // When the limit lets the next byte through, in seconds on the Stopwatch clock.
    private double _nextFree;

    /// <summary>Speed limit for all downloads together, bytes per second; 0 is no limit.</summary>
    public long LimitBps { get; set; }

    public async Task Run(FetchJob job, int connections, CancellationToken cancel)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        try
        {
            job.SetState(FetchState.Connecting);
            if (!job.Probed)
            {
                await Probe(job, cancel);
                job.Save();
            }
            PrepareFile(job);
            job.SetState(FetchState.Downloading);

            Exception? fatal = null;
            var workers = Enumerable.Range(0, job.Ranged ? Math.Max(1, connections) : 1).Select(_ => Task.Run(async () =>
            {
                try { await Worker(job, stop.Token); }
                catch (Exception ex)
                {
                    // The others stop with a cancellation; keep the error that actually ended the download.
                    if (ex is not OperationCanceledException || !stop.IsCancellationRequested)
                        Interlocked.CompareExchange(ref fatal, ex, null);
                    stop.Cancel();
                }
            }, CancellationToken.None));
            await Task.WhenAll(workers);
            cancel.ThrowIfCancellationRequested();
            if (fatal is not null) throw fatal;
            if (!job.Complete) throw new IOException("Download stopped before the end of the file.");

            var final = UniquePath(job.FinalPath, ignorePart: true);
            File.Move(job.PartPath, final);
            try { File.Delete(job.StatePath); } catch { }
            job.FileName = Path.GetFileName(final);
            job.Total = job.Received;
            job.SetState(FetchState.Done);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            job.SetState(FetchState.Paused);
            job.Save();
        }
        catch (Exception ex)
        {
            job.SetState(FetchState.Failed, Describe(ex));
            job.Save();
        }
    }

    private async Task Probe(FetchJob job, CancellationToken cancel)
    {
        if (!Uri.TryCreate(job.Url.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new FatalFetchException("Not an http:// or https:// link.");
        // Try each link once, so a dead hotspot doesn't fail the download before it starts.
        for (var attempt = 0; ; attempt++)
        {
            var link = Pick();
            try
            {
                await ProbeVia(job, uri, link, cancel);
                return;
            }
            catch (Exception ex) when (ex is not FatalFetchException && !cancel.IsCancellationRequested &&
                                       link is not null && attempt < links().Count(l => l.Selected))
            {
                Interlocked.Exchange(ref link.FailUntilTicks, DateTime.UtcNow.Add(FailPenalty).Ticks);
            }
            finally { Unpick(link); }
        }
    }

    private async Task ProbeVia(FetchJob job, Uri uri, BondLink? link, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Range = new RangeHeaderValue(0, null);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(Stall);
        using var response = await ClientFor(link).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw new FatalFetchException($"Server answered {(int)response.StatusCode} {response.ReasonPhrase}.");

        var final = response.RequestMessage?.RequestUri ?? uri;
        var range = response.Content.Headers.ContentRange;
        job.ResolvedUrl = final.ToString();
        job.Ranged = response.StatusCode == HttpStatusCode.PartialContent && range?.Length is not null && range.From == 0;
        job.Total = job.Ranged ? range!.Length!.Value : response.Content.Headers.ContentLength ?? -1;
        job.Validator = response.Headers.ETag is { IsWeak: false } etag ? etag.Tag
            : response.Content.Headers.LastModified?.ToString("R") ?? "";
        job.FileName = Reserve(job.Folder, NameFor(response, final));
        job.Segments = [new FetchSegment { Pos = 0, End = job.Total >= 0 ? job.Total : long.MaxValue }];
        job.Probed = true;
        job.ResetProgress();
    }

    private static void PrepareFile(FetchJob job)
    {
        Directory.CreateDirectory(job.Folder);
        if (job.Ranged && File.Exists(job.PartPath) && new FileInfo(job.PartPath).Length == Math.Max(0, job.Total)) return;
        using var file = new FileStream(job.PartPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        if (job.Total <= 0) return;
        // Sparse, so writing near the end of a big file doesn't make NTFS zero-fill everything before it first.
        MarkSparse(file.SafeFileHandle);
        file.SetLength(job.Total);
    }

    private async Task Worker(FetchJob job, CancellationToken cancel)
    {
        using var file = File.OpenHandle(job.PartPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, FileOptions.Asynchronous);
        var buffer = new byte[256 * 1024];
        var failures = 0;
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            var segment = job.Take(MinSplit);
            if (segment is null) return;
            var link = Pick();
            try
            {
                await Pull(job, segment, link, file, buffer, cancel);
                failures = 0;
            }
            catch (Exception ex) when (ex is not FatalFetchException && !cancel.IsCancellationRequested)
            {
                if (link is not null) Interlocked.Exchange(ref link.FailUntilTicks, DateTime.UtcNow.Add(FailPenalty).Ticks);
                job.NoteError(Describe(ex));
                if (++failures >= MaxFailures) throw;
                if (!job.Ranged)
                {
                    // No ranges: the only way to retry is from the start.
                    lock (job.Gate) segment.Pos = 0;
                    job.ResetProgress();
                }
                job.Release(segment);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(15, failures)), cancel);
                continue;
            }
            finally { Unpick(link); }
            job.Release(segment);
        }
    }

    private async Task Pull(FetchJob job, FetchSegment segment, BondLink? link, SafeFileHandle file, byte[] buffer, CancellationToken cancel)
    {
        long start, end;
        lock (job.Gate) (start, end) = (segment.Pos, segment.End);
        string address;
        lock (job.Gate) address = job.ResolvedUrl;
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        if (job.Ranged)
        {
            request.Headers.Range = new RangeHeaderValue(start, end - 1);
            if (job.Validator.StartsWith('"'))
                request.Headers.IfRange = new RangeConditionHeaderValue(job.Validator);
            else if (DateTimeOffset.TryParse(job.Validator, out var date))
                request.Headers.IfRange = new RangeConditionHeaderValue(date);
        }

        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        stall.CancelAfter(Stall);
        using var response = await ClientFor(link).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token);
        var code = (int)response.StatusCode;
        if (code is 401 or 403 or 410)
        {
            // GitHub and most CDNs redirect to signed links that expire after minutes. If this request used such
            // a link, go back to the one the user gave and let its redirect hand out a fresh address. A refusal
            // of the original link itself is final.
            if (address != job.Url)
            {
                lock (job.Gate) job.ResolvedUrl = job.Url;
                throw new HttpRequestException($"Server answered {code}; asking the original link for a fresh address.");
            }
        }
        if (code is 401 or 403 or 404 or 410 or 416)
            throw new FatalFetchException($"Server answered {code} {response.ReasonPhrase}. The link may have expired.");
        if (job.Ranged)
        {
            if (response.StatusCode == HttpStatusCode.OK)
                throw new FatalFetchException("The file changed on the server or it stopped allowing resume. Remove it and add the link again.");
            if (response.StatusCode != HttpStatusCode.PartialContent)
                throw new HttpRequestException($"Server answered {code} {response.ReasonPhrase}.");
            if (response.Content.Headers.ContentRange?.From != start)
                throw new HttpRequestException("Server sent the wrong part of the file.");
        }
        else response.EnsureSuccessStatusCode();

        var name = link?.Name ?? "Default";
        await using var body = await response.Content.ReadAsStreamAsync(stall.Token);
        while (true)
        {
            stall.CancelAfter(Stall);
            var read = await body.ReadAsync(buffer, stall.Token);
            if (read == 0) break;
            long offset;
            int take;
            lock (job.Gate)
            {
                offset = segment.Pos;
                take = (int)Math.Min(read, segment.End - offset);
            }
            if (take <= 0) return;
            await RandomAccess.WriteAsync(file, buffer.AsMemory(0, take), offset, cancel);
            long gained;
            bool done;
            lock (job.Gate)
            {
                // Another worker may have taken the back half meanwhile; only count bytes still ours.
                var newPos = Math.Min(offset + take, segment.End);
                gained = Math.Max(0, newPos - segment.Pos);
                segment.Pos = Math.Max(segment.Pos, newPos);
                done = segment.Pos >= segment.End;
            }
            job.Add(gained, name);
            if (link is not null) Interlocked.Add(ref link.ByteCount, gained);
            if (done) return;
            await Throttle(take, cancel);
        }

        lock (job.Gate)
        {
            if (segment.End == long.MaxValue)
            {
                // Unknown size: the server closing the stream is the end of the file.
                segment.End = segment.Pos;
                job.Total = segment.Pos;
                return;
            }
            if (segment.Pos < segment.End) throw new IOException("Connection closed early.");
        }
    }

    /// <summary>Hold a worker back when all workers together are ahead of the speed limit.</summary>
    private async Task Throttle(int bytes, CancellationToken cancel)
    {
        var limit = LimitBps;
        if (limit <= 0) return;
        double wait;
        lock (_limitGate)
        {
            var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            // Idle time earns at most a tenth of a second of burst, so a pause isn't followed by a spike.
            _nextFree = Math.Max(_nextFree, now - 0.1) + bytes / (double)limit;
            wait = _nextFree - now;
        }
        // Under the 20 s stall timeout, so a slow limit never looks like a dead connection.
        if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(Math.Min(wait, 10)), cancel);
    }

    // Weighted least-connections over the links ticked on the Bond page, healthy ones first.
    private BondLink? Pick()
    {
        lock (_pickGate)
        {
            var link = links().Where(l => l.Selected)
                // When every link failed lately, retry the one that failed longest ago.
                .OrderBy(l => l.Failing ? Interlocked.Read(ref l.FailUntilTicks) : 0)
                .ThenBy(l => (Volatile.Read(ref l.ActiveCount) + 1.0) / l.Weight)
                .FirstOrDefault();
            if (link is not null) Interlocked.Increment(ref link.ActiveCount);
            return link;
        }
    }

    private static void Unpick(BondLink? link)
    {
        if (link is not null) Interlocked.Decrement(ref link.ActiveCount);
    }

    private HttpClient ClientFor(BondLink? link)
    {
        var key = link?.Address.ToString() ?? "";
        lock (_clients)
        {
            if (!_clients.TryGetValue(key, out var client))
                _clients[key] = client = Create(link?.Address);
            return client;
        }
    }

    private static HttpClient Create(IPAddress? source)
    {
        var handler = new SocketsHttpHandler
        {
            // The system proxy may be the bonder itself; bound sockets already pick the link.
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            MaxConnectionsPerServer = 64
        };
        if (source is not null)
        {
            handler.ConnectCallback = async (context, cancel) =>
            {
                var host = context.DnsEndPoint.Host;
                var targets = IPAddress.TryParse(host, out var literal)
                    ? [literal]
                    : await IPv4Of(host, cancel);
                if (targets.Length == 0)
                {
                    // IPv6-only host: the links are bound by IPv4 address, so let Windows route this one.
                    var plain = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try { await plain.ConnectAsync(context.DnsEndPoint, cancel); return new NetworkStream(plain, ownsSocket: true); }
                    catch { plain.Dispose(); throw; }
                }
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    // Windows sends from the adapter that owns the bound source address (strong host model).
                    socket.Bind(new IPEndPoint(source, 0));
                    await socket.ConnectAsync(targets, context.DnsEndPoint.Port, cancel);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            };
        }
        // HTTP/1.1 (the default) so every piece gets its own TCP connection; HTTP/2 would squeeze them onto one link.
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return http;
    }

    private static async Task<IPAddress[]> IPv4Of(string host, CancellationToken cancel)
    {
        try { return await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cancel); }
        catch (SocketException) { return []; }
    }

    private static string NameFor(HttpResponseMessage response, Uri final)
    {
        var disposition = response.Content.Headers.ContentDisposition;
        var name = disposition?.FileNameStar ?? disposition?.FileName?.Trim('"');
        if (string.IsNullOrWhiteSpace(name))
            name = Uri.UnescapeDataString(final.Segments.LastOrDefault() ?? "").Trim('/');
        name = string.Concat(Path.GetFileName(name ?? "").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim(' ', '.');
        return name.Length == 0 ? "download" : name;
    }

    // Claim "name.part" atomically, so two downloads of the same link can't pick the same file.
    private static string Reserve(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        while (true)
        {
            var path = UniquePath(Path.Combine(folder, name), ignorePart: false);
            try
            {
                new FileStream(path + ".part", FileMode.CreateNew).Dispose();
                return Path.GetFileName(path);
            }
            catch (IOException) when (File.Exists(path + ".part")) { }
        }
    }

    private static string UniquePath(string path, bool ignorePart)
    {
        bool Taken(string p) => File.Exists(p) || (!ignorePart && (File.Exists(p + ".part") || File.Exists(p + ".part.json")));
        if (!Taken(path)) return path;
        var folder = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({i}){ext}");
            if (!Taken(candidate)) return candidate;
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        OperationCanceledException => "Connection stalled.",
        HttpRequestException { InnerException: SocketException s } => s.Message,
        _ => ex.Message
    };

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr inBuffer, uint inSize,
        IntPtr outBuffer, uint outSize, out uint returned, IntPtr overlapped);

    private static void MarkSparse(SafeFileHandle file)
    {
        const uint FsctlSetSparse = 0x000900C4;
        if (!OperatingSystem.IsWindows()) return;
        try { DeviceIoControl(file, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero); } catch { }
    }
}
