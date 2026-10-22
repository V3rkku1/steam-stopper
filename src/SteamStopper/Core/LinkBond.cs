using System.ComponentModel;
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
    internal long Picks;

    public string Name { get; init; } = "";
    public int Index { get; set; }
    public string Description { get; init; } = "";
    public string Kind { get; init; } = "";
    public IPAddress Address { get; set; } = IPAddress.None;
    public string Gateway { get; set; } = "";
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
    public string SpeedText => Failing ? "can't connect" : _bps <= 0 ? "—" : Format.Speed(_bps);
    public string TotalText => _bytesShown <= 0 ? "—" : Format.Bytes(_bytesShown);
    public double Bps => _bps;

    // The dark ComboBox template shows items via ToString, not DisplayMemberPath.
    public override string ToString() => Name;

    internal bool Failing => DateTime.UtcNow.Ticks < Interlocked.Read(ref FailUntilTicks);

    internal void Sample(double seconds)
    {
        var bytes = Interlocked.Read(ref ByteCount);
        var raw = seconds > 0 ? Math.Max(0, bytes - _lastBytes) / seconds : 0;
        // Smooth over a few seconds so the speed doesn't jump between 0 and double each tick.
        _bps = _bps * 0.6 + raw * 0.4;
        _lastBytes = bytes;
        _bytesShown = bytes;
        _activeShown = Volatile.Read(ref ActiveCount);
        OnChanged(nameof(Active));
        OnChanged(nameof(SpeedText));
        OnChanged(nameof(TotalText));
    }

    // The adapter got a new IP (phone tether reconnected, DHCP renew): send from the new one.
    internal void Moved(BondLink fresh)
    {
        Address = fresh.Address;
        Gateway = fresh.Gateway;
        Index = fresh.Index;
        Interlocked.Exchange(ref FailUntilTicks, 0);
        OnChanged(nameof(Detail));
        OnChanged(nameof(Hint));
    }

    internal void ResetCounters()
    {
        Interlocked.Exchange(ref ByteCount, 0);
        Interlocked.Exchange(ref FailUntilTicks, 0);
        Interlocked.Exchange(ref Picks, 0);
        _lastBytes = 0;
        _bps = 0;
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
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan FailPenalty = TimeSpan.FromSeconds(20);
    // A connection with no traffic either way this long is dead (link dropped, NAT expired);
    // closing it makes Steam retry the chunk on a fresh connection instead of hanging.
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(45);
    // HTTPS tunnels also carry quiet keep-alive sockets (Steam login, Discord's ~41s heartbeat).
    private static readonly TimeSpan TunnelIdleTimeout = TimeSpan.FromSeconds(90);
    // Servers drop idle keep-alive connections after ~60s; reuse well inside that.
    private static readonly TimeSpan PoolIdle = TimeSpan.FromSeconds(20);

    private readonly object _gate = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _stop;
    private List<BondLink> _links = [];
    private DateTime _lastSample = DateTime.UtcNow;
    // Idle keep-alive upstream connections, by link + its address + target host.
    private readonly Dictionary<string, Stack<(Wire Wire, DateTime At)>> _pool = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IPAddress[]> _dns = new();

    public bool Running => _listener is not null;
    public int Port { get; private set; }
    public IReadOnlyList<BondLink> Links => _links;

    public static List<BondLink> Discover()
    {
        var list = new List<BondLink>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
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
            _ = WatchLinks(_stop.Token);
            return chosen.Count == 1
                ? $"Pinned to {chosen[0].Name} through 127.0.0.1:{port}."
                : $"Bonding {chosen.Count} links through 127.0.0.1:{port}.";
        }
    }

    public void Stop()
    {
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
        foreach (var link in _links) link.Sample(seconds);
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

    private async Task WatchLinks(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), cancel); }
            catch { break; }
            try
            {
                var found = Discover();
                foreach (var link in _links)
                {
                    var fresh = found.FirstOrDefault(f => f.Name == link.Name);
                    if (fresh is not null && (!fresh.Address.Equals(link.Address) || fresh.Index != link.Index)) link.Moved(fresh);
                }
            }
            catch { }
            Sweep(all: false);
        }
    }

    private async Task Serve(Socket client, CancellationToken cancel)
    {
        using var _ = client;
        client.NoDelay = true;
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        idle.CancelAfter(IdleTimeout);
        var from = new Wire(client, null, "") { Idle = idle };
        try
        {
            // Keep Steam's connection open across requests; each request still picks its own link.
            while (await from.ReadHead() is { } head)
            {
                var request = ParseRequest(head);
                if (request is null)
                {
                    await Reply(client, "400 Bad Request", idle.Token);
                    return;
                }
                if (request.Value.Tunnel)
                {
                    // The tunnel keeps its own idle timer; this one stops being refreshed once piping starts.
                    await Tunnel(from, request.Value, cancel);
                    return;
                }
                if (!await Forward(from, request.Value, idle)) return;
            }
        }
        catch { }
    }

    private async Task Tunnel(Wire client, ProxyRequest request, CancellationToken cancel)
    {
        var (upstream, link) = await Open(request.Host, request.Port, pooled: false, cancel);
        if (upstream is null)
        {
            await Reply(client.Socket, "502 Bad Gateway", cancel);
            return;
        }
        try
        {
            await client.Socket.SendAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n"), SocketFlags.None, cancel);
            await Send(upstream.Socket, client.TakeBuffered(), cancel);
            await Pipe(client.Socket, upstream.Socket, link, cancel);
        }
        finally
        {
            if (link is not null) Interlocked.Decrement(ref link.ActiveCount);
            upstream.Socket.Dispose();
        }
    }

    /// <summary>Send one request upstream and stream its response back. True when Steam's connection can carry another.</summary>
    private async Task<bool> Forward(Wire client, ProxyRequest request, CancellationTokenSource idle)
    {
        while (true)
        {
            var (upstream, link) = await Open(request.Host, request.Port, pooled: true, idle.Token);
            if (upstream is null)
            {
                await Reply(client.Socket, "502 Bad Gateway", idle.Token);
                return false;
            }
            var reusable = false;
            try
            {
                upstream.Idle = idle;
                string? head = null;
                try
                {
                    await Send(upstream.Socket, Encoding.Latin1.GetBytes(request.Forward), idle.Token);
                    await client.CopyBody(upstream.Socket, request.BodyLength);
                    head = await upstream.ReadHead();
                }
                catch when (upstream.Reused && !idle.IsCancellationRequested) { }
                // A pooled connection the server closed while it sat idle: retry on a fresh one.
                // Only safe without a request body, which was already consumed from Steam.
                if (head is null && upstream.Reused && request.BodyLength == 0) continue;
                if (head is null)
                {
                    await Reply(client.Socket, "502 Bad Gateway", idle.Token);
                    return false;
                }

                var response = ParseResponse(head);
                while (response.Status is >= 100 and < 200)
                {
                    await Send(client.Socket, Encoding.Latin1.GetBytes(head + "\r\n\r\n"), idle.Token);
                    head = await upstream.ReadHead() ?? throw new IOException("closed after 1xx");
                    response = ParseResponse(head);
                }
                var noBody = request.Method == "HEAD" || response.Status is 204 or 304;
                var framed = noBody || response.Length >= 0 || response.Chunked;
                var keep = framed && !request.Close;
                await Send(client.Socket, Encoding.Latin1.GetBytes(
                    StripHop(head) + (keep ? "Connection: keep-alive" : "Connection: close") + "\r\n\r\n"), idle.Token);

                if (noBody) { }
                else if (response.Chunked) await upstream.CopyChunked(client.Socket);
                else if (response.Length >= 0) await upstream.CopyExact(client.Socket, response.Length);
                else await upstream.CopyToEnd(client.Socket);

                reusable = framed && !response.Close;
                return keep;
            }
            finally
            {
                if (link is not null) Interlocked.Decrement(ref link.ActiveCount);
                if (reusable) Return(upstream);
                else upstream.Socket.Dispose();
            }
        }
    }

    private readonly record struct ProxyRequest(bool Tunnel, string Method, string Host, int Port, string Forward, long BodyLength, bool Close);
    private readonly record struct ProxyResponse(int Status, long Length, bool Chunked, bool Close);

    private static string? Header(string[] lines, string name)
    {
        foreach (var line in lines.Skip(1))
        {
            var parts = line.Split(':', 2);
            if (parts.Length == 2 && parts[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) return parts[1].Trim();
        }
        return null;
    }

    private static bool Has(string? value, string token) => value?.Contains(token, StringComparison.OrdinalIgnoreCase) == true;

    // Drop hop-by-hop headers; the caller appends its own Connection header and the blank line.
    private static string StripHop(string head)
    {
        var lines = head.Split("\r\n");
        var text = new StringBuilder(lines[0]).Append("\r\n");
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0) continue;
            var name = line.Split(':', 2)[0].Trim();
            if (name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase))
                continue;
            text.Append(line).Append("\r\n");
        }
        return text.ToString();
    }

    private static ProxyResponse ParseResponse(string head)
    {
        var lines = head.Split("\r\n");
        var first = lines[0].Split(' ', 3);
        var status = first.Length >= 2 && int.TryParse(first[1], out var code) ? code : 0;
        var chunked = Has(Header(lines, "Transfer-Encoding"), "chunked");
        var length = !chunked && long.TryParse(Header(lines, "Content-Length"), out var n) ? n : -1;
        var close = Has(Header(lines, "Connection"), "close") || first[0] == "HTTP/1.0";
        return new ProxyResponse(status, length, chunked, close);
    }

    private static ProxyRequest? ParseRequest(string head)
    {
        var lines = head.Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length != 3) return null;
        var (method, target, version) = (first[0], first[1], first[2]);

        if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            var (host, port) = SplitHostPort(target, 443);
            return host is null ? null : new ProxyRequest(true, method, host, port, "", 0, true);
        }

        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp)
            return null;

        var chunked = Has(Header(lines, "Transfer-Encoding"), "chunked");
        var body = chunked ? -1 : long.TryParse(Header(lines, "Content-Length"), out var n) ? n : 0;
        var close = version == "HTTP/1.0" || Has(Header(lines, "Proxy-Connection") ?? Header(lines, "Connection"), "close");
        var forward = StripHop($"{method} {uri.PathAndQuery} {version}\r\n" + string.Join("\r\n", lines.Skip(1)))
                      + "Connection: keep-alive\r\n\r\n";
        return new ProxyRequest(false, method.ToUpperInvariant(), uri.IdnHost, uri.Port, forward, body, close);
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

    private static async Task Send(Socket to, ReadOnlyMemory<byte> data, CancellationToken cancel)
    {
        var sent = 0;
        while (sent < data.Length)
            sent += await to.SendAsync(data[sent..], SocketFlags.None, cancel);
    }

    private static string PoolKey(BondLink link, string host, int port) => $"{link.Name}|{link.Address}|{host}:{port}";

    /// <summary>Pick a link and hand back an idle connection on it, or a new one. Counts as active on that link.</summary>
    private async Task<(Wire?, BondLink?)> Open(string host, int port, bool pooled, CancellationToken cancel)
    {
        IPAddress[]? addresses = null;
        IPAddress? v4 = null;
        foreach (var link in Ranked())
        {
            Interlocked.Increment(ref link.ActiveCount);
            Interlocked.Increment(ref link.Picks);
            var key = PoolKey(link, host, port);
            if (pooled && Take(key) is { } idle) return (idle, link);

            if (addresses is null)
            {
                if (IPAddress.TryParse(host, out var literal)) addresses = [literal];
                else
                {
                    try { _dns[host] = addresses = await Dns.GetHostAddressesAsync(host, cancel); }
                    // DNS goes out the main link; when that link is down, reuse the last answer so the others keep working.
                    catch { addresses = _dns.GetValueOrDefault(host) ?? []; }
                }
                v4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
            }
            if (v4 is null)
            {
                Interlocked.Decrement(ref link.ActiveCount);
                break;
            }
            var socket = await TryConnect(v4, port, link.Address, cancel, link.Index);
            if (socket is not null)
            {
                socket.NoDelay = true;
                return (new Wire(socket, link, key), link);
            }
            Interlocked.Decrement(ref link.ActiveCount);
            Interlocked.Exchange(ref link.FailUntilTicks, DateTime.UtcNow.Add(FailPenalty).Ticks);
        }

        // Local or IPv6-only target: let Windows route it normally.
        var target = v4 is null ? addresses?.FirstOrDefault() : null;
        if (target is null) return (null, null);
        var plain = await TryConnect(target, port, null, cancel);
        return plain is null ? (null, null) : (new Wire(plain, null, ""), null);
    }

    private Wire? Take(string key)
    {
        lock (_pool)
        {
            if (!_pool.TryGetValue(key, out var stack)) return null;
            while (stack.TryPop(out var item))
            {
                // Readable while idle means the server closed it (or sent junk): not reusable.
                var alive = DateTime.UtcNow - item.At < PoolIdle && !item.Wire.Socket.Poll(0, SelectMode.SelectRead);
                if (alive)
                {
                    item.Wire.Reused = true;
                    return item.Wire;
                }
                item.Wire.Socket.Dispose();
            }
            return null;
        }
    }

    private void Return(Wire wire)
    {
        wire.Idle = null;
        if (wire.Key.Length == 0 || !Running)
        {
            wire.Socket.Dispose();
            return;
        }
        lock (_pool)
        {
            if (!_pool.TryGetValue(wire.Key, out var stack)) _pool[wire.Key] = stack = new();
            stack.Push((wire, DateTime.UtcNow));
        }
    }

    private void Sweep(bool all)
    {
        lock (_pool)
        {
            foreach (var key in _pool.Keys.ToList())
            {
                var keep = _pool[key].Where(i => !all && DateTime.UtcNow - i.At < PoolIdle).Reverse().ToList();
                foreach (var gone in _pool[key].Except(keep)) gone.Wire.Socket.Dispose();
                if (keep.Count == 0) _pool.Remove(key);
                else _pool[key] = new Stack<(Wire, DateTime)>(keep);
            }
        }
    }

    // Weighted least-connections, healthy links first so a dead phone hotspot only costs one retry.
    // Ties go round-robin by requests handed out (weighted). Not by speed: the first link to get
    // going won every tie and the others sat idle. Not by total bytes: that fed the slow link.
    private List<BondLink> Ranked()
    {
        var chosen = _links.Where(l => l.Selected).ToList();
        if (chosen.Count == 0) chosen = _links.ToList();
        return chosen
            .OrderBy(l => l.Failing)
            .ThenBy(l => (Volatile.Read(ref l.ActiveCount) + 1.0) / l.Weight)
            .ThenBy(l => Interlocked.Read(ref l.Picks) / (double)l.Weight)
            .ToList();
    }

    // IP_UNICAST_IF: IPPROTO_IP option 31, interface index in network byte order.
    private const SocketOptionName UnicastInterface = (SocketOptionName)31;

    private static async Task<Socket?> TryConnect(IPAddress target, int port, IPAddress? source, CancellationToken cancel, int index = 0)
    {
        var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            if (source is not null) socket.Bind(new IPEndPoint(source, 0));
            // Binding alone doesn't pick the adapter on Windows: with two default gateways the packet can
            // still leave through the main one (wrong source IP, so it never connects). Force the adapter.
            if (index > 0 && OperatingSystem.IsWindows())
                socket.SetSocketOption(SocketOptionLevel.IP, UnicastInterface, IPAddress.HostToNetworkOrder(index));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(ConnectTimeout);
            await socket.ConnectAsync(new IPEndPoint(target, port), timeout.Token);
            return socket;
        }
        catch
        {
            socket.Dispose();
            return null;
        }
    }

    private static async Task Pipe(Socket client, Socket upstream, BondLink? link, CancellationToken cancel)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        idle.CancelAfter(TunnelIdleTimeout);
        var up = Copy(client, upstream, null, idle);
        var down = Copy(upstream, client, link, idle);
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

    // link is set only for the download direction, so the speed column shows download speed.
    private static async Task Copy(Socket from, Socket to, BondLink? link, CancellationTokenSource idle)
    {
        var token = idle.Token;
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var read = await from.ReceiveAsync(buffer, SocketFlags.None, token);
                if (read <= 0) break;
                if (link is not null) Interlocked.Add(ref link.ByteCount, read);
                try { idle.CancelAfter(TunnelIdleTimeout); } catch (ObjectDisposedException) { }
                var sent = 0;
                while (sent < read)
                    sent += await to.SendAsync(buffer.AsMemory(sent, read - sent), SocketFlags.None, token);
            }
        }
        catch { }
    }
}

/// <summary>A socket with a read buffer, for parsing HTTP framing without losing bytes past a boundary.</summary>
internal sealed class Wire(Socket socket, BondLink? link, string key)
{
    private readonly byte[] _buffer = new byte[64 * 1024];
    private int _start, _end;

    public Socket Socket => socket;
    public string Key => key;
    public bool Reused { get; set; }
    public CancellationTokenSource? Idle { get; set; }
    private CancellationToken Token => Idle?.Token ?? CancellationToken.None;

    private async Task<bool> Fill()
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }
        if (_end == _buffer.Length) return false;
        var read = await socket.ReceiveAsync(_buffer.AsMemory(_end), SocketFlags.None, Token);
        if (read <= 0) return false;
        _end += read;
        // Only upstream wires carry a link, so this counts download bytes.
        if (link is not null) Interlocked.Add(ref link.ByteCount, read);
        try { Idle?.CancelAfter(LinkBonder.IdleTimeout); } catch (ObjectDisposedException) { }
        return true;
    }

    public ReadOnlyMemory<byte> TakeBuffered()
    {
        var data = _buffer.AsMemory(_start, _end - _start).ToArray();
        _start = _end = 0;
        return data;
    }

    private async Task<string?> ReadUntil(byte[] marker)
    {
        while (true)
        {
            var at = _buffer.AsSpan(_start, _end - _start).IndexOf(marker);
            if (at >= 0)
            {
                var text = Encoding.Latin1.GetString(_buffer, _start, at);
                _start += at + marker.Length;
                return text;
            }
            if (!await Fill()) return null;
        }
    }

    /// <summary>The request or status line plus headers, without the blank line. Null when the peer closed.</summary>
    public Task<string?> ReadHead() => ReadUntil("\r\n\r\n"u8.ToArray());

    private async Task Pass(Socket to, long count)
    {
        while (count > 0)
        {
            if (_start == _end && !await Fill()) throw new IOException("closed mid-body");
            var take = (int)Math.Min(count, _end - _start);
            var sent = 0;
            while (sent < take)
                sent += await to.SendAsync(_buffer.AsMemory(_start + sent, take - sent), SocketFlags.None, Token);
            _start += take;
            count -= take;
        }
    }

    public Task CopyExact(Socket to, long count) => Pass(to, count);

    /// <summary>Request body: a length, or -1 for chunked.</summary>
    public Task CopyBody(Socket to, long length) => length < 0 ? CopyChunked(to) : Pass(to, length);

    public async Task CopyChunked(Socket to)
    {
        var crlf = "\r\n"u8.ToArray();
        while (true)
        {
            var line = await ReadUntil(crlf) ?? throw new IOException("closed mid-chunk");
            await to.SendAsync(Encoding.Latin1.GetBytes(line + "\r\n"), SocketFlags.None, Token);
            var size = Convert.ToInt64(line.Split(';')[0].Trim(), 16);
            if (size > 0)
            {
                await Pass(to, size + 2);
                continue;
            }
            // Trailers end with an empty line.
            while (await ReadUntil(crlf) is { } trailer)
            {
                await to.SendAsync(Encoding.Latin1.GetBytes(trailer + "\r\n"), SocketFlags.None, Token);
                if (trailer.Length == 0) return;
            }
            throw new IOException("closed in trailers");
        }
    }

    public async Task CopyToEnd(Socket to)
    {
        while (true)
        {
            if (_start < _end) await Pass(to, _end - _start);
            if (!await Fill()) return;
        }
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
