using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace SteamStopper.Core;

/// <summary>Health probe for bonded links: can each one still reach the internet, and how fast?</summary>
public static class LinkHealth
{
    // Two anycast addresses on 443, so a single blocked or down service doesn't mark a link dead.
    private static readonly IPEndPoint[] Targets = [new(IPAddress.Parse("1.1.1.1"), 443), new(IPAddress.Parse("8.8.8.8"), 443)];

    public static Task Probe(IEnumerable<BondLink> links) =>
        Task.WhenAll(links.Where(l => l.Selected).Select(async link => link.Probed(await Rtt(link.Address))));

    private static async Task<double?> Rtt(IPAddress source)
    {
        foreach (var target in Targets)
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                socket.Bind(new IPEndPoint(source, 0));
                using var timeout = new CancellationTokenSource(1500);
                var watch = Stopwatch.StartNew();
                await socket.ConnectAsync(target, timeout.Token);
                return watch.Elapsed.TotalMilliseconds;
            }
            catch { } // try the other address
        }
        return null;
    }
}

/// <summary>
/// DNS asked through one link's own DNS server, from that link's address. CDNs answer with the server
/// nearest to the asking provider, so each bonded link downloads from its own nearby server.
/// </summary>
public static class LinkDns
{
    private static readonly Dictionary<(IPAddress, string), (DateTime Until, IPAddress[] Addresses)> Cache = [];

    public static async Task<IPAddress[]> Resolve(string host, IPAddress server, IPAddress source, CancellationToken cancel)
    {
        lock (Cache)
            if (Cache.TryGetValue((server, host), out var hit) && hit.Until > DateTime.UtcNow) return hit.Addresses;
        using var udp = new UdpClient(new IPEndPoint(source, 0));
        var id = (ushort)Random.Shared.Next(ushort.MaxValue);
        await udp.SendAsync(Query(host, id), new IPEndPoint(server, 53), cancel);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(1500);
        var reply = await udp.ReceiveAsync(timeout.Token);
        var (addresses, ttl) = Parse(reply.Buffer, id);
        lock (Cache) Cache[(server, host)] = (DateTime.UtcNow.AddSeconds(Math.Clamp(ttl, 60, 3600)), addresses);
        return addresses;
    }

    /// <summary>A standard recursive query for the A records of a name.</summary>
    public static byte[] Query(string host, ushort id)
    {
        var bytes = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in host.TrimEnd('.').Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }
        bytes.AddRange([0, 0, 1, 0, 1]); // end of name, type A, class IN
        return [.. bytes];
    }

    /// <summary>The A records in a reply, and their shortest TTL. Empty for another id or an error reply.</summary>
    public static (IPAddress[] Addresses, int Ttl) Parse(byte[] reply, ushort id)
    {
        if (reply.Length < 12 || reply[0] != (byte)(id >> 8) || reply[1] != (byte)id || (reply[3] & 0x0F) != 0) return ([], 0);
        int questions = reply[4] << 8 | reply[5], answers = reply[6] << 8 | reply[7], at = 12;
        for (var q = 0; q < questions; q++) at = SkipName(reply, at) + 4;
        var found = new List<IPAddress>();
        var ttl = int.MaxValue;
        for (var a = 0; a < answers && at + 10 <= reply.Length; a++)
        {
            at = SkipName(reply, at);
            int type = reply[at] << 8 | reply[at + 1], length = reply[at + 8] << 8 | reply[at + 9];
            var recordTtl = reply[at + 4] << 24 | reply[at + 5] << 16 | reply[at + 6] << 8 | reply[at + 7];
            at += 10;
            if (type == 1 && length == 4 && at + 4 <= reply.Length)
            {
                found.Add(new IPAddress(reply.AsSpan(at, 4)));
                ttl = Math.Min(ttl, recordTtl);
            }
            at += length;
        }
        return ([.. found], found.Count == 0 ? 0 : ttl);
    }

    private static int SkipName(byte[] data, int at)
    {
        while (at < data.Length)
        {
            var length = data[at];
            if (length == 0) return at + 1;
            if ((length & 0xC0) == 0xC0) return at + 2; // pointer to a name earlier in the reply
            at += length + 1;
        }
        return at;
    }
}

/// <summary>Which program opened a TCP connection, from Windows' connection table.</summary>
public static class TcpOwner
{
    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);

    /// <summary>The process id behind a local connection from clientPort to serverPort, or null.</summary>
    public static int? PidOf(int clientPort, int serverPort)
    {
        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 5, 0); // AF_INET, TCP_TABLE_OWNER_PID_ALL
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, 2, 5, 0) != 0) return null;
            var rows = Marshal.ReadInt32(buffer);
            for (var i = 0; i < rows; i++)
            {
                var row = buffer + 4 + i * 24; // state, local addr, local port, remote addr, remote port, pid
                int Port(int offset) => (ushort)IPAddress.NetworkToHostOrder((short)Marshal.ReadInt32(row + offset));
                if (Port(8) == clientPort && Port(16) == serverPort) return Marshal.ReadInt32(row + 20);
            }
            return null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}

/// <summary>Windows settings that quietly cap download speed, and their fixes.</summary>
public static class NetTuning
{
    // Wireless adapter power saving in the power plan: 0 is maximum performance, 3 maximum saving.
    private const string WifiPower = "19cbb8fa-5279-450e-9fac-8a3d5fedd0c1 12bbebe6-58d6-4636-95bb-3217ef867c1a";

    /// <summary>TCP receive window auto-tuning for internet connections: Normal is right; Disabled or Restricted caps speed.</summary>
    public static string? AutoTuning() =>
        Run("powershell", "-NoProfile -Command \"(Get-NetTCPSetting -SettingName Internet).AutoTuningLevelLocal\"")?.Trim() is { Length: > 0 } level ? level : null;

    public static bool AutoTuningLimited(string? level) => level is not null && level is not ("Normal" or "Experimental" or "normal" or "experimental");

    public static void FixAutoTuning() => Run("netsh", "int tcp set global autotuninglevel=normal");

    /// <summary>The power plan's Wi-Fi saving level when plugged in, or null when it can't be read.</summary>
    public static int? WifiSaving() => ParsePowerIndex(Run("powercfg", $"/query SCHEME_CURRENT {WifiPower}") ?? "");

    /// <summary>The first "0x…" in powercfg's output is the plugged-in (AC) value; the labels are translated, the values aren't.</summary>
    public static int? ParsePowerIndex(string output) =>
        Regex.Match(output, @"0x([0-9a-fA-F]{8})") is { Success: true } m ? Convert.ToInt32(m.Groups[1].Value, 16) : null;

    /// <summary>Maximum Wi-Fi performance when plugged in; on battery the saving stays.</summary>
    public static void FixWifi()
    {
        Run("powercfg", $"/setacvalueindex SCHEME_CURRENT {WifiPower} 0");
        Run("powercfg", "/setactive SCHEME_CURRENT");
    }

    private static string? Run(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            return output;
        }
        catch { return null; } // not on this Windows
    }
}
