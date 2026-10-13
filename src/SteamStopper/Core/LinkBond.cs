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

    public string Name { get; init; } = "";
    public int Index { get; init; }
    public string Description { get; init; } = "";
    public string Kind { get; init; } = "";
    public IPAddress Address { get; init; } = IPAddress.None;
    public string Gateway { get; init; } = "";
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
    public string SpeedText => _bps <= 0 ? "—" : Format.Speed(_bps);
    public string TotalText => _bytesShown <= 0 ? "—" : Format.Bytes(_bytesShown);
    public double Bps => _bps;

    // The dark ComboBox template shows items via ToString, not DisplayMemberPath.
    public override string ToString() => Name;

    internal bool Failing => DateTime.UtcNow.Ticks < Interlocked.Read(ref FailUntilTicks);

    internal void Sample(double seconds)
    {
        var bytes = Interlocked.Read(ref ByteCount);
        _bps = seconds > 0 ? Math.Max(0, bytes - _lastBytes) / seconds : 0;
        _lastBytes = bytes;
        _bytesShown = bytes;
        _activeShown = Volatile.Read(ref ActiveCount);
        OnChanged(nameof(Active));
        OnChanged(nameof(SpeedText));
        OnChanged(nameof(TotalText));
    }

    internal void ResetCounters()
    {
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

    private async Task Serve(Socket client, CancellationToken cancel)
    {
        using var _ = client;
        client.NoDelay = true;
        Socket? upstream = null;
        BondLink? link = null;
        try
        {
            var (head, extra) = await ReadHead(client, cancel);
            if (head is null) return;
            var request = ParseRequest(head);
            if (request is null)
            {
                await Reply(client, "400 Bad Request", cancel);
                return;
            }

            (upstream, link) = await ConnectUpstream(request.Value.Host, request.Value.Port, cancel);
            if (upstream is null)
            {
                await Reply(client, "502 Bad Gateway", cancel);
                return;
            }

            if (request.Value.Tunnel)
            {
                await client.SendAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n"), SocketFlags.None, cancel);
            }
            else
            {
                await upstream.SendAsync(Encoding.Latin1.GetBytes(request.Value.Forward), SocketFlags.None, cancel);
            }
            if (extra.Length > 0)
                await upstream.SendAsync(extra, SocketFlags.None, cancel);

            await Pipe(client, upstream, link, cancel);
        }
        catch { }
        finally
        {
            if (link is not null) Interlocked.Decrement(ref link.ActiveCount);
            upstream?.Dispose();
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

        // One request per proxied connection: every request (each Steam content chunk) gets its
        // own pick of link instead of sticking to whichever link the first request landed on.
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
        forward.Append("Connection: close\r\n\r\n");
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

    private static async Task<(string? Head, byte[] Extra)> ReadHead(Socket client, CancellationToken cancel)
    {
        var buffer = new byte[8192];
        using var collected = new MemoryStream();
        while (collected.Length < MaxHead)
        {
            var read = await client.ReceiveAsync(buffer, SocketFlags.None, cancel);
            if (read <= 0) return (null, []);
            collected.Write(buffer, 0, read);
            var split = SplitHead(collected.GetBuffer(), (int)collected.Length);
            if (split is not null) return split.Value;
        }
        return (null, []);
    }

    private static (string?, byte[])? SplitHead(byte[] buffer, int length)
    {
        var data = buffer.AsSpan(0, length);
        var end = data.IndexOf("\r\n\r\n"u8);
        if (end < 0) return null;
        return (Encoding.Latin1.GetString(data[..end]), data[(end + 4)..].ToArray());
    }

    private static Task Reply(Socket client, string status, CancellationToken cancel)
        => client.SendAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"), SocketFlags.None, cancel).AsTask();

    private async Task<(Socket?, BondLink?)> ConnectUpstream(string host, int port, CancellationToken cancel)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal)) addresses = [literal];
        else
        {
            try { addresses = await Dns.GetHostAddressesAsync(host, cancel); }
            catch { return (null, null); }
        }

        var v4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        if (v4 is null || IPAddress.IsLoopback(v4))
        {
            // Local or IPv6-only target: let Windows route it normally.
            var target = addresses.FirstOrDefault();
            if (target is null) return (null, null);
            var plain = await TryConnect(target, port, null, cancel);
            return (plain, null);
        }

        foreach (var link in Ranked())
        {
            Interlocked.Increment(ref link.ActiveCount);
            var socket = await TryConnect(v4, port, link.Address, cancel);
            if (socket is not null)
            {
                socket.NoDelay = true;
                return (socket, link);
            }
            Interlocked.Decrement(ref link.ActiveCount);
            Interlocked.Exchange(ref link.FailUntilTicks, DateTime.UtcNow.Add(FailPenalty).Ticks);
        }
        return (null, null);
    }

    // Weighted least-connections, healthy links first so a dead phone hotspot only costs one retry.
    private List<BondLink> Ranked()
    {
        var chosen = _links.Where(l => l.Selected).ToList();
        if (chosen.Count == 0) chosen = _links.ToList();
        return chosen
            .OrderBy(l => l.Failing)
            .ThenBy(l => (Volatile.Read(ref l.ActiveCount) + 1.0) / l.Weight)
            .ThenBy(l => Interlocked.Read(ref l.ByteCount) / (double)l.Weight)
            .ToList();
    }

    private static async Task<Socket?> TryConnect(IPAddress target, int port, IPAddress? source, CancellationToken cancel)
    {
        var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            // Windows sends from the adapter that owns the bound source address (strong host model),
            // so binding is all it takes to push this connection out a specific link.
            if (source is not null) socket.Bind(new IPEndPoint(source, 0));
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
