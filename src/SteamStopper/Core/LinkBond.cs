using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace SteamStopper.Core;

public sealed class BondLink : INotifyPropertyChanged
{
    private bool _selected = true;
    private int _weight = 1;
    private int _activeShown;
    private double _bps;
    private long _bytesShown;
    private long _lastBytes;
    internal int ActiveCount;
    internal long ByteCount;
    internal long FailUntilTicks;
    // Best recent speed, falling slowly, as the link's capacity for auto-balance.
    private double _peak;
    private bool _idle;
    private long _adapterBase = -1;
    private int _misses;
    // Weight times measured capacity: what Bond and Fetch share connections by.
    internal double Share = 1;

    /// <summary>Balance links by their measured speed (on by default); off uses only the manual weight.</summary>
    public static bool AutoBalance { get; set; } = true;

    public string Name { get; init; } = "";
    public int Index { get; init; }
    public string Description { get; init; } = "";
    public string Kind { get; init; } = "";
    // Settable: a link that reconnects can come back with a new DHCP address.
    public IPAddress Address { get; internal set; } = IPAddress.None;
    public string Gateway { get; init; } = "";
    public IPAddress[] DnsServers { get; internal set; } = [];
    /// <summary>Last round trip to the internet over this link, from the health probe.</summary>
    public double? RttMs { get; private set; }
    /// <summary>The health probe couldn't get out over this link twice in a row.</summary>
    public bool Down { get; private set; }
    public string Detail => $"{Address}  ·  {Kind}";
    public string Hint => $"{Description}\nIP {Address}, gateway {Gateway}";
    public int[] WeightChoices { get; } = [1, 2, 3, 4];

    public bool Selected
    {
        get => _selected;
        set { _selected = value; OnChanged(); }
    }

    // Share of new connections relative to the other links: 2 gets twice as many as 1.
    public int Weight
    {
        get => _weight;
        set { _weight = Math.Clamp(value, 1, 4); OnChanged(); }
    }

    public int Active => _activeShown;
    public string SpeedText => Down ? "offline" : _bps <= 0 ? "—" : Format.Speed(_bps);
    public string TotalText => _bytesShown <= 0 ? "—" : Format.Bytes(_bytesShown);
    public double Bps => _bps;

    // The dark ComboBox template shows items via ToString, not DisplayMemberPath.
    public override string ToString() => Name;

    internal bool Failing => DateTime.UtcNow.Ticks < Interlocked.Read(ref FailUntilTicks);

    internal void Sample(double seconds)
    {
        var bytes = Interlocked.Read(ref ByteCount);
        // Smoothed so a second without a finished chunk doesn't show the link as idle ("—").
        var now = seconds > 0 ? Math.Max(0, bytes - _lastBytes) / seconds : 0;
        _bps = seconds <= 0 ? 0 : _bps > 0 ? _bps * 0.6 + now * 0.4 : now;
        // Capacity: grows within seconds, falls over half a minute, and holds while the link is idle,
        // so one burst or one quiet second doesn't flip which link is preferred (as in net-merge).
        _idle = _bps < MeasuredBps || _bps < 0.05 * _peak;
        if (seconds <= 0) _peak = 0;
        else if (!_idle) _peak = _peak == 0 ? _bps : _peak + (_bps - _peak) * (1 - Math.Exp(-seconds / (_bps > _peak ? 2 : 30)));
        _lastBytes = bytes;
        _bytesShown = bytes;
        _activeShown = Volatile.Read(ref ActiveCount);
        OnChanged(nameof(Active));
        OnChanged(nameof(SpeedText));
        OnChanged(nameof(TotalText));
    }

    private const double MeasuredBps = 50_000;

    /// <summary>Recompute each link's share: its weight, times its speed against the fastest link when auto-balance is on.</summary>
    public static void Rebalance(IEnumerable<BondLink> links)
    {
        var list = links.Where(l => l.Selected && !l.Down).ToList();
        var fastest = list.Count == 0 ? 0 : list.Max(l => l._peak);
        // An idle link's low estimate creeps back up (never past half the fastest), so it gets tried again.
        foreach (var link in list.Where(l => l._idle && l._peak > 0))
            link._peak = Math.Min(link._peak * 1.03, Math.Max(link._peak, 0.5 * fastest));
        // Until every link has carried real traffic, or with auto off, only the weight counts.
        var measured = list.Count > 1 && list.All(l => l._peak >= MeasuredBps);
        foreach (var link in links)
            link.Share = link.Weight * (AutoBalance && measured ? Math.Clamp(link._peak / fastest, 0.05, 1) : 1);
    }

    /// <summary>
    /// Send this socket out of this link: bind its address, and also force the adapter with IP_UNICAST_IF,
    /// since binding alone isn't enough where Windows uses the weak host model (Mobile Hotspot and connection
    /// sharing turn it on). Idea from net-merge.
    /// </summary>
    internal void Pin(Socket socket)
    {
        socket.Bind(new IPEndPoint(Address, 0));
        // IPv4 wants the interface index in network byte order; 31 = IP_UNICAST_IF, which .NET doesn't name.
        try { if (Index > 0) socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31, IPAddress.HostToNetworkOrder(Index)); }
        catch (SocketException) { } // the bind still picks the link in the usual strong host model
    }

    /// <summary>
    /// Did this fail because the link itself is broken? Only then is the link sidelined; a slow or refusing
    /// server would fail on every link, and blaming each link in turn left none to use.
    /// </summary>
    internal static bool IsLinkFault(SocketError error)
        => error is SocketError.NetworkDown or SocketError.NetworkUnreachable or SocketError.AddressNotAvailable;

    internal static bool IsLinkFault(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
            if (ex is SocketException socket) return IsLinkFault(socket.SocketErrorCode);
        return false;
    }

    /// <summary>Record a health probe: two misses in a row take the link out of use until it answers again.</summary>
    internal void Probed(double? rttMs)
    {
        if (rttMs is { } ms)
        {
            RttMs = RttMs is { } was ? was * 0.7 + ms * 0.3 : ms;
            _misses = 0;
            if (Down) { Down = false; Interlocked.Exchange(ref FailUntilTicks, 0); }
        }
        else if (++_misses >= 2)
        {
            Down = true;
            // Kept failing while down; the next probe (2 s) renews it.
            Interlocked.Exchange(ref FailUntilTicks, DateTime.UtcNow.AddSeconds(10).Ticks);
        }
        OnChanged(nameof(SpeedText));
    }

    /// <summary>Count the adapter's traffic by any app from Windows' counters (every-app mode has no proxy to count it).</summary>
    internal void CountAdapter(NetworkInterface[] nics)
    {
        try
        {
            if (nics.FirstOrDefault(n => n.Name == Name)?.GetIPStatistics() is not { } stats) return;
            var total = stats.BytesReceived + stats.BytesSent;
            if (_adapterBase < 0) _adapterBase = total;
            Interlocked.Exchange(ref ByteCount, Math.Max(0, total - _adapterBase));
        }
        catch (NetworkInformationException) { }
    }

    internal void ResetCounters()
    {
        _adapterBase = -1;
        Interlocked.Exchange(ref ByteCount, 0);
        Interlocked.Exchange(ref FailUntilTicks, 0);
        _lastBytes = 0;
        Sample(0);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Local HTTP proxy that hands each new outgoing connection to a different internet link
/// (Ethernet, Wi-Fi, phone tethering...). Steam opens many parallel connections to its
/// content servers, so spreading them adds the links' bandwidth together.
/// </summary>
public sealed class LinkBonder
{
    public const int DefaultPort = 8899;
    private const int MaxHead = 64 * 1024;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan FailPenalty = TimeSpan.FromSeconds(20);

    private readonly object _gate = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _stop;
    private List<BondLink> _links = [];
    private DateTime _lastSample = DateTime.UtcNow;

    private bool _everyApp;

    public bool Running => _listener is not null || _everyApp;
    public int Port { get; private set; }
    public IReadOnlyList<BondLink> Links => _links;

    public static List<BondLink> Discover()
    {
        var list = new List<BondLink>();
        NetworkInterface[] nics;
        // Throws "Request not supported" on some systems (Wine, broken network stacks); show no links instead of an error.
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { return list; }
        foreach (var nic in nics)
        {
            try
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var props = nic.GetIPProperties();
                var gateway = props.GatewayAddresses
                    .Select(g => g.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork &&
                                         !IPAddress.IsLoopback(a) && !a.Equals(IPAddress.Any));
                if (gateway is null) continue;
                var address = props.UnicastAddresses
                    .Select(u => u.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork &&
                                         !IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254."));
                if (address is null) continue;
                list.Add(new BondLink
                {
                    DnsServers = props.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray(),
                    Name = nic.Name,
                    Index = props.GetIPv4Properties()?.Index ?? 0,
                    Description = nic.Description,
                    Kind = KindOf(nic),
                    Address = address,
                    Gateway = gateway.ToString()
                });
            }
            catch { }
        }
        return list.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string KindOf(NetworkInterface nic)
    {
        var text = (nic.Description + " " + nic.Name).ToLowerInvariant();
        if (text.Contains("rndis") || text.Contains("ncm") || text.Contains("tether") ||
            text.Contains("android") || text.Contains("iphone") || text.Contains("apple mobile"))
            return "Phone (USB)";
        if (text.Contains("bluetooth")) return "Bluetooth";
        return nic.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Wireless80211 => "Wi-Fi",
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => "Ethernet",
            NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => "Mobile broadband",
            NetworkInterfaceType.Ppp => "PPP",
            _ => nic.NetworkInterfaceType.ToString()
        };
    }

    public string Start(List<BondLink> links, int port, int minLinks = 2)
    {
        lock (_gate)
        {
            if (Running) return $"Bonding already running on port {Port}.";
            var chosen = links.Where(l => l.Selected).ToList();
            if (chosen.Count < minLinks)
                return "Pick at least two internet connections to bond.";
            if (port is < 1024 or > 65535)
                return "Pick a port between 1024 and 65535.";
            var listener = new TcpListener(IPAddress.Loopback, port);
            try { listener.Start(512); }
            catch (SocketException ex) { return $"Port {port} is busy: {ex.Message}"; }
            foreach (var link in links) link.ResetCounters();
            _links = links;
            _listener = listener;
            _stop = new CancellationTokenSource();
            Port = port;
            _lastSample = DateTime.UtcNow;
            _ = AcceptLoop(listener, _stop.Token);
            return chosen.Count == 1
                ? $"Pinned to {chosen[0].Name} through 127.0.0.1:{port}."
                : $"Bonding {chosen.Count} links through 127.0.0.1:{port}.";
        }
    }

    /// <summary>Bond every app, not only Steam and browsers, through net-merge's virtual adapter.</summary>
    public async Task<string> StartEveryApp(List<BondLink> links)
    {
        if (Running) return "Bonding is already running.";
        var chosen = links.Where(l => l.Selected).ToList();
        if (chosen.Count < 2) return "Pick at least two internet connections to bond.";
        await NetMerge.Ensure();
        if (await NetMerge.Start(chosen) is { } error) return error;
        foreach (var link in links) link.ResetCounters();
        _links = links;
        _lastSample = DateTime.UtcNow;
        _everyApp = true;
        return $"Bonding {chosen.Count} links for every app.";
    }

    public void Stop()
    {
        if (_everyApp)
        {
            NetMerge.Stop();
            _everyApp = false;
        }
        lock (_gate)
        {
            try { _stop?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            _stop?.Dispose();
            _stop = null;
            _listener = null;
        }
        Sweep(all: true);
    }

    /// <summary>Refresh the per-link speed columns. Call from the UI thread about once a second.</summary>
    public double Sample()
    {
        var now = DateTime.UtcNow;
        var seconds = (now - _lastSample).TotalSeconds;
        _lastSample = now;
        if (_everyApp)
        {
            NetworkInterface[] nics;
            try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
            catch (NetworkInformationException) { nics = []; }
            foreach (var link in _links.Where(l => l.Selected)) link.CountAdapter(nics);
        }
        foreach (var link in _links) link.Sample(seconds);
        BondLink.Rebalance(_links);
        Sweep();
        return _links.Sum(l => l.Bps);
    }

    private async Task AcceptLoop(TcpListener listener, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            Socket client;
            try { client = await listener.AcceptSocketAsync(cancel); }
            catch { break; }
            _ = Task.Run(() => Serve(client, cancel), CancellationToken.None);
        }
    }

    private async Task Serve(Socket client, CancellationToken cancel)
    {
        using var _ = client;
        client.NoDelay = true;
        var wire = new Wire(client);
        try
        {
            var head = await wire.ReadHead(cancel);
            if (head is null) return;
            var request = ParseRequest(head);
            if (request is null)
            {
                await Reply(client, "400 Bad Request", cancel);
                return;
            }
            var latency = LatencyApp(client);
            if (!request.Value.Tunnel)
            {
                await ServeHttp(wire, head, request.Value, latency, cancel);
                return;
            }

            var (upstream, link, _) = await ConnectUpstream(request.Value.Host, request.Value.Port, latency, false, cancel);
            if (upstream is null)
            {
                await Reply(client, "502 Bad Gateway", cancel);
                return;
            }
            try
            {
                await Send(client, "HTTP/1.1 200 Connection established\r\n\r\n", cancel);
                await SendAll(upstream, wire.TakeBuffered(), cancel);
                await Pipe(client, upstream, link, cancel);
            }
            finally
            {
                if (link is not null) Interlocked.Decrement(ref link.ActiveCount);
                upstream.Dispose();
            }
        }
        catch { }
    }

    /// <summary>
    /// Plain HTTP (Steam's content downloads). Each request still picks its link afresh, but the connection to
    /// the server is kept and reused by the next request on that link, so a chunk doesn't pay for a new TCP
    /// handshake and slow start every time.
    /// </summary>
    private async Task ServeHttp(Wire client, string head, ProxyRequest request, bool latency, CancellationToken cancel)
    {
        while (true)
        {
            var lines = head.Split("\r\n");
            var keepClient = KeepsAlive(lines[0].Split(' ')[^1], Header(lines, "Proxy-Connection") ?? Header(lines, "Connection"));
            var body = Has(Header(lines, "Transfer-Encoding"), "chunked") ? -1
                : long.TryParse(Header(lines, "Content-Length"), out var length) ? length : 0;
            var method = lines[0].Split(' ')[0];
            if (!await Exchange(client, request, method, body, latency, cancel) || !keepClient) return;

            var next = await client.ReadHead(cancel);
            if (next is null) return;
            if (ParseRequest(next) is not { Tunnel: false } parsed)
            {
                await Reply(client.Socket, "400 Bad Request", cancel);
                return;
            }
            (head, request) = (next, parsed);
        }
    }

    /// <summary>One request and its response. True when the client connection can carry another request.</summary>
    private async Task<bool> Exchange(Wire client, ProxyRequest request, string method, long body, bool latency, CancellationToken cancel)
    {
        for (var attempt = 0; ; attempt++)
        {
            var (upstream, link, reused) = await ConnectUpstream(request.Host, request.Port, latency, true, cancel);
            if (upstream is null)
            {
                await Reply(client.Socket, "502 Bad Gateway", cancel);
                return false;
            }
            var server = new Wire(upstream);
            var answered = false;
            var keep = false;
            try
            {
                await Send(upstream, request.Forward, cancel);
                if (body != 0 && !await client.RelayBody(upstream, body, null, cancel)) return false;
                var head = await server.ReadHead(cancel) ?? throw new IOException("The server closed the connection.");
                var status = Status(head);
                // Interim answers (100 Continue) go straight through; the real one follows.
                while (status is >= 100 and < 200 and not 101)
                {
                    answered = true;
                    await Send(client.Socket, head + "\r\n\r\n", cancel);
                    head = await server.ReadHead(cancel) ?? throw new IOException("The server closed the connection.");
                    status = Status(head);
                }
                answered = true;
                if (status == 101)
                {
                    // Switching protocols (WebSocket): from here on it's a plain tunnel.
                    await Send(client.Socket, head + "\r\n\r\n", cancel);
                    await SendAll(client.Socket, server.TakeBuffered(), cancel);
                    await SendAll(upstream, client.TakeBuffered(), cancel);
                    await Pipe(client.Socket, upstream, link, cancel);
                    return false;
                }

                var lines = head.Split("\r\n");
                // -1 chunked, -2 until the server closes, else the exact length.
                var length = method.Equals("HEAD", StringComparison.OrdinalIgnoreCase) || status is 204 or 304 ? 0
                    : Has(Header(lines, "Transfer-Encoding"), "chunked") ? -1
                    : long.TryParse(Header(lines, "Content-Length"), out var n) ? n : -2;
                var reply = new StringBuilder();
                foreach (var line in lines)
                {
                    var name = line.Split(':', 2)[0].Trim();
                    if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase))
                        continue;
                    reply.Append(line).Append("\r\n");
                }
                reply.Append(length == -2 ? "Connection: close\r\n\r\n" : "Connection: keep-alive\r\n\r\n");
                await Send(client.Socket, reply.ToString(), cancel);
                if (length == -2)
                {
                    await server.Relay(client.Socket, long.MaxValue, link, cancel);
                    return false;
                }
                if (!await server.RelayBody(client.Socket, length, link, cancel)) return false;
                keep = link is not null && KeepsAlive(lines[0].Split(' ')[0], Header(lines, "Connection")) && server.Empty;
                return true;
            }
            // A kept connection the server had already closed: ask again on a fresh one, while nothing reached the client yet.
            catch (Exception) when (reused && attempt == 0 && body == 0 && !answered && !cancel.IsCancellationRequested)
            {
                continue;
            }
            finally
            {
                if (link is not null) Interlocked.Decrement(ref link.ActiveCount);
                if (keep) Park(link!, request.Host, request.Port, upstream);
                else upstream.Dispose();
            }
        }
    }

    private static bool KeepsAlive(string version, string? connection)
        => version.Equals("HTTP/1.1", StringComparison.OrdinalIgnoreCase) ? !Has(connection, "close") : Has(connection, "keep-alive");

    private static int Status(string head)
    {
        var parts = head.Split(' ', 3);
        return parts.Length > 1 && int.TryParse(parts[1], out var code) ? code : 0;
    }

    private static string? Header(string[] lines, string name)
    {
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0 && line.AsSpan(0, colon).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return line[(colon + 1)..].Trim();
        }
        return null;
    }

    private static bool Has(string? value, string token) => value?.Contains(token, StringComparison.OrdinalIgnoreCase) == true;

    private static async Task SendAll(Socket to, ReadOnlyMemory<byte> data, CancellationToken cancel)
    {
        while (data.Length > 0)
            data = data[await to.SendAsync(data, SocketFlags.None, cancel)..];
    }

    private static Task Send(Socket to, string text, CancellationToken cancel) => SendAll(to, Encoding.Latin1.GetBytes(text), cancel);

    // Idle server connections by link and server, reused for that link's next request to the same server.
    private readonly Dictionary<(BondLink, string, int), Stack<(Socket Socket, long Since)>> _parked = [];
    private const int ParkMs = 15_000;

    private void Park(BondLink link, string host, int port, Socket socket)
    {
        lock (_parked)
        {
            if (!_parked.TryGetValue((link, host, port), out var stack)) _parked[(link, host, port)] = stack = new();
            if (stack.Count < 32) { stack.Push((socket, Environment.TickCount64)); return; }
        }
        socket.Dispose();
    }

    private Socket? Unpark(BondLink link, string host, int port)
    {
        lock (_parked)
        {
            if (!_parked.TryGetValue((link, host, port), out var stack)) return null;
            while (stack.TryPop(out var idle))
            {
                // Readable while idle means the server closed it.
                if (Environment.TickCount64 - idle.Since < ParkMs && !idle.Socket.Poll(0, SelectMode.SelectRead)) return idle.Socket;
                idle.Socket.Dispose();
            }
            return null;
        }
    }

    /// <summary>Close idle server connections older than the park time, or all of them.</summary>
    private void Sweep(bool all = false)
    {
        lock (_parked)
        {
            foreach (var (key, stack) in _parked.ToList())
            {
                var fresh = stack.Where(i => !all && Environment.TickCount64 - i.Since < ParkMs).Reverse().ToList();
                foreach (var stale in stack.Except(fresh)) stale.Socket.Dispose();
                if (fresh.Count == 0) _parked.Remove(key);
                else _parked[key] = new Stack<(Socket, long)>(fresh);
            }
        }
    }

    private readonly record struct ProxyRequest(bool Tunnel, string Host, int Port, string Forward);

    private static ProxyRequest? ParseRequest(string head)
    {
        var lines = head.Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length != 3) return null;
        var (method, target, version) = (first[0], first[1], first[2]);

        if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            var (host, port) = SplitHostPort(target, 443);
            return host is null ? null : new ProxyRequest(true, host, port, "");
        }

        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp)
            return null;

        // Every request (each Steam content chunk) gets its own pick of link; see ServeHttp.
        var forward = new StringBuilder();
        forward.Append(method).Append(' ').Append(uri.PathAndQuery).Append(' ').Append(version).Append("\r\n");
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0) continue;
            var name = line.Split(':', 2)[0].Trim();
            if (name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase))
                continue;
            forward.Append(line).Append("\r\n");
        }
        forward.Append("Connection: keep-alive\r\n\r\n");
        return new ProxyRequest(false, uri.IdnHost, uri.Port, forward.ToString());
    }

    private static (string? Host, int Port) SplitHostPort(string target, int fallback)
    {
        if (target.StartsWith('['))
        {
            var close = target.IndexOf(']');
            if (close < 0) return (null, 0);
            var host = target[1..close];
            var rest = target[(close + 1)..];
            return (host, rest.StartsWith(':') && int.TryParse(rest[1..], out var p) ? p : fallback);
        }
        var colon = target.LastIndexOf(':');
        if (colon < 0) return (target, fallback);
        return int.TryParse(target[(colon + 1)..], out var port) ? (target[..colon], port) : (null, 0);
    }

    private static Task Reply(Socket client, string status, CancellationToken cancel)
        => client.SendAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"), SocketFlags.None, cancel).AsTask();

    /// <summary>Keep games, emulators and voice chat on the one quickest link instead of spreading them (on by default).</summary>
    public bool KeepGamesOnOneLink { get; set; } = true;

    private static readonly string[] VoiceApps = ["discord", "discordptb", "discordcanary", "teamspeak", "ts3client_win64", "mumble"];
    private readonly Dictionary<int, bool> _latencyPids = [];

    /// <summary>Is the program behind this proxy connection a game, emulator or voice app? Downloaders and browsers aren't.</summary>
    private bool LatencyApp(Socket client)
    {
        if (!KeepGamesOnOneLink || client.RemoteEndPoint is not IPEndPoint remote || TcpOwner.PidOf(remote.Port, Port) is not { } pid) return false;
        lock (_latencyPids)
            if (_latencyPids.TryGetValue(pid, out var known)) return known;
        var latency = false;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            var path = process.MainModule?.FileName ?? "";
            latency = VoiceApps.Contains(process.ProcessName.ToLowerInvariant())
                || path.Contains(@"\steamapps\common\", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(EmuStore.Root, StringComparison.OrdinalIgnoreCase);
        }
        catch { } // exited or protected: treat as a normal app
        lock (_latencyPids) _latencyPids[pid] = latency;
        return latency;
    }

    private async Task<(Socket?, BondLink?, bool Reused)> ConnectUpstream(string host, int port, bool latency, bool reuse, CancellationToken cancel)
    {
        var links = Ranked();
        // Latency apps: the quickest healthy link first, the rest only as fallbacks.
        if (latency) links = [.. links.OrderBy(l => l.Down).ThenBy(l => l.RttMs ?? double.MaxValue)];
        if (reuse && links.FirstOrDefault() is { Failing: false } first && Unpark(first, host, port) is { } idle)
        {
            Interlocked.Increment(ref first.ActiveCount);
            return (idle, first, true);
        }

        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal)) addresses = [literal];
        else
        {
            // Windows may still ask the DNS of a link that just dropped: give it 3 s, then ask the live links' own DNS.
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            wait.CancelAfter(3000);
            try { addresses = await Dns.GetHostAddressesAsync(host, wait.Token); }
            catch when (!cancel.IsCancellationRequested) { addresses = await ResolveOverLinks(host, cancel); }
            if (addresses.Length == 0) return (null, null, false);
        }

        var v4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        if (v4 is null || IPAddress.IsLoopback(v4))
        {
            // Local or IPv6-only target: let Windows route it normally.
            var target = addresses.FirstOrDefault();
            if (target is null) return (null, null, false);
            var (plain, _) = await TryConnect(target, port, null, cancel);
            return (plain, null, false);
        }

        foreach (var link in links)
        {
            Interlocked.Increment(ref link.ActiveCount);
            // Ask this link's own DNS, so a CDN hands it the server nearest to its provider.
            var target = v4;
            if (literal is null && link.DnsServers.FirstOrDefault() is { } dns)
                try { target = (await LinkDns.Resolve(host, dns, link.Address, cancel)).FirstOrDefault() ?? v4; }
                catch when (!cancel.IsCancellationRequested) { } // that DNS didn't answer: use Windows' answer
            var (socket, error) = await TryConnect(target, port, link, cancel);
            if (socket is not null)
            {
                socket.NoDelay = true;
                return (socket, link, false);
            }
            Interlocked.Decrement(ref link.ActiveCount);
            if (BondLink.IsLinkFault(error))
                Interlocked.Exchange(ref link.FailUntilTicks, DateTime.UtcNow.Add(FailPenalty).Ticks);
            // The server itself said no; every other link would hear the same.
            if (error == SocketError.ConnectionRefused) break;
        }
        return (null, null, false);
    }

    private async Task<IPAddress[]> ResolveOverLinks(string host, CancellationToken cancel)
    {
        foreach (var link in Ranked().Where(l => !l.Down))
            foreach (var dns in link.DnsServers.Take(1))
                try { if (await LinkDns.Resolve(host, dns, link.Address, cancel) is { Length: > 0 } found) return found; }
                catch when (!cancel.IsCancellationRequested) { } // try the next link
        return [];
    }

    // Weighted least-connections, healthy links first so a dead phone hotspot only costs one retry.
    private List<BondLink> Ranked()
    {
        var chosen = _links.Where(l => l.Selected).ToList();
        if (chosen.Count == 0) chosen = _links.ToList();
        return chosen
            // When every link failed lately, retry the one that failed longest ago, not the heaviest.
            .OrderBy(l => l.Failing ? Interlocked.Read(ref l.FailUntilTicks) : 0)
            .ThenBy(l => (Volatile.Read(ref l.ActiveCount) + 1.0) / l.Share)
            .ThenBy(l => Interlocked.Read(ref l.ByteCount) / l.Share)
            .ToList();
    }

    private static async Task<(Socket?, SocketError)> TryConnect(IPAddress target, int port, BondLink? link, CancellationToken cancel)
    {
        var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            link?.Pin(socket);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(ConnectTimeout);
            await socket.ConnectAsync(new IPEndPoint(target, port), timeout.Token);
            return (socket, SocketError.Success);
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            return (null, ex.SocketErrorCode);
        }
        catch
        {
            socket.Dispose();
            return (null, SocketError.TimedOut);
        }
    }

    /// <summary>A socket read through a buffer, so HTTP heads and exact-length bodies can be taken off it.</summary>
    private sealed class Wire(Socket socket)
    {
        private static readonly byte[] HeadEnd = "\r\n\r\n"u8.ToArray(), LineEnd = "\r\n"u8.ToArray();
        private readonly byte[] _buffer = new byte[MaxHead];
        private int _start, _end;

        public Socket Socket { get; } = socket;
        public bool Empty => _start == _end;

        private async Task<bool> Fill(CancellationToken cancel)
        {
            if (_start == _end) _start = _end = 0;
            else if (_end == _buffer.Length)
            {
                if (_start == 0) return false; // a head bigger than the buffer
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                (_end, _start) = (_end - _start, 0);
            }
            var read = await Socket.ReceiveAsync(_buffer.AsMemory(_end), SocketFlags.None, cancel);
            if (read <= 0) return false;
            _end += read;
            return true;
        }

        private async Task<string?> ReadUntil(byte[] end, CancellationToken cancel)
        {
            while (true)
            {
                var at = _buffer.AsSpan(_start, _end - _start).IndexOf(end);
                if (at >= 0)
                {
                    var text = Encoding.Latin1.GetString(_buffer, _start, at);
                    _start += at + end.Length;
                    return text;
                }
                if (!await Fill(cancel)) return null;
            }
        }

        /// <summary>The head of a request or response, without its blank line; null if the stream ended first.</summary>
        public Task<string?> ReadHead(CancellationToken cancel) => ReadUntil(HeadEnd, cancel);

        public ReadOnlyMemory<byte> TakeBuffered()
        {
            var rest = _buffer.AsMemory(_start, _end - _start).ToArray();
            _start = _end = 0;
            return rest;
        }

        /// <summary>Pass on exactly count bytes, or (long.MaxValue) everything until the stream ends.</summary>
        public async Task<bool> Relay(Socket to, long count, BondLink? link, CancellationToken cancel)
        {
            var toEnd = count == long.MaxValue;
            while (count > 0)
            {
                if (_start == _end && !await Fill(cancel)) return toEnd;
                var n = (int)Math.Min(count, _end - _start);
                await SendAll(to, _buffer.AsMemory(_start, n), cancel);
                _start += n;
                if (!toEnd) count -= n;
                if (link is not null) Interlocked.Add(ref link.ByteCount, n);
            }
            return true;
        }

        /// <summary>Pass on a body: length bytes, or (-1) chunked up to its last chunk and trailers.</summary>
        public async Task<bool> RelayBody(Socket to, long length, BondLink? link, CancellationToken cancel)
        {
            if (length >= 0) return await Relay(to, length, link, cancel);
            while (true)
            {
                var line = await ReadUntil(LineEnd, cancel);
                if (line is null) return false;
                await Send(to, line + "\r\n", cancel);
                var size = line.Split(';')[0].Trim();
                if (!long.TryParse(size, NumberStyles.HexNumber, null, out var bytes) || bytes < 0) return false;
                if (bytes == 0) break;
                if (!await Relay(to, bytes + 2, link, cancel)) return false;
            }
            while (true)
            {
                var line = await ReadUntil(LineEnd, cancel);
                if (line is null) return false;
                await Send(to, line + "\r\n", cancel);
                if (line.Length == 0) return true;
            }
        }
    }

    private static async Task Pipe(Socket client, Socket upstream, BondLink? link, CancellationToken cancel)
    {
        var up = Copy(client, upstream, link, cancel);
        var down = Copy(upstream, client, link, cancel);
        if (await Task.WhenAny(up, down) == up)
        {
            // Client finished sending; keep streaming the response until the server closes.
            try { upstream.Shutdown(SocketShutdown.Send); } catch { }
        }
        await down;
        // Close gracefully: closing with a read still pending can reset the connection before
        // the client has read the tail of the response.
        try { client.Shutdown(SocketShutdown.Send); } catch { }
        try { await Task.WhenAny(up, Task.Delay(TimeSpan.FromSeconds(5), cancel)); } catch { }
    }

    private static async Task Copy(Socket from, Socket to, BondLink? link, CancellationToken cancel)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var read = await from.ReceiveAsync(buffer, SocketFlags.None, cancel);
                if (read <= 0) break;
                if (link is not null) Interlocked.Add(ref link.ByteCount, read);
                var sent = 0;
                while (sent < read)
                    sent += await to.SendAsync(buffer.AsMemory(sent, read - sent), SocketFlags.None, cancel);
            }
        }
        catch { }
    }
}

/// <summary>
/// Points the Windows proxy setting (which Steam and browsers follow) at the local bonder,
/// and puts the old setting back afterwards, even after a crash.
/// </summary>
public static class SystemProxy
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const int OptionSettingsChanged = 39;
    private const int OptionRefresh = 37;

    private sealed class Backup
    {
        public int Enable { get; set; }
        public string? Server { get; set; }
        public string? Override { get; set; }
    }

    private static string BackupPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamStopper", "proxy-backup.json");

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr handle, int option, IntPtr buffer, int length);

    public static bool PointsAt(int port)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key);
        return key?.GetValue("ProxyEnable") is int and not 0 &&
               string.Equals(key?.GetValue("ProxyServer") as string, Address(port), StringComparison.OrdinalIgnoreCase);
    }

    public static void Enable(int port)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        if (!File.Exists(BackupPath))
        {
            var backup = new Backup
            {
                Enable = key.GetValue("ProxyEnable") as int? ?? 0,
                Server = key.GetValue("ProxyServer") as string,
                Override = key.GetValue("ProxyOverride") as string
            };
            Directory.CreateDirectory(Path.GetDirectoryName(BackupPath)!);
            File.WriteAllText(BackupPath, JsonSerializer.Serialize(backup));
        }
        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", Address(port), RegistryValueKind.String);
        key.SetValue("ProxyOverride", "<local>;localhost;127.*;10.*;192.168.*", RegistryValueKind.String);
        Notify();
    }

    /// <summary>Restore the proxy setting saved by <see cref="Enable"/>. Safe to call when nothing was changed.</summary>
    public static bool Restore()
    {
        if (!File.Exists(BackupPath)) return false;
        try
        {
            var backup = JsonSerializer.Deserialize<Backup>(File.ReadAllText(BackupPath)) ?? new Backup();
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            key.SetValue("ProxyEnable", backup.Enable, RegistryValueKind.DWord);
            Put(key, "ProxyServer", backup.Server);
            Put(key, "ProxyOverride", backup.Override);
            Notify();
        }
        catch { }
        try { File.Delete(BackupPath); } catch { }
        return true;
    }

    private static void Put(RegistryKey key, string name, string? value)
    {
        if (value is null) key.DeleteValue(name, false);
        else key.SetValue(name, value, RegistryValueKind.String);
    }

    private static string Address(int port) => $"127.0.0.1:{port}";

    private static void Notify()
    {
        InternetSetOption(IntPtr.Zero, OptionSettingsChanged, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, OptionRefresh, IntPtr.Zero, 0);
    }
}
