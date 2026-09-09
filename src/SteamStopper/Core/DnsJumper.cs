using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Management;

namespace SteamStopper.Core;

public sealed class DnsProvider
{
    public string Name { get; init; } = "";
    public string Primary { get; init; } = "";
    public string Secondary { get; init; } = "";
    public string? PrimaryV6 { get; init; }
    public string? SecondaryV6 { get; init; }
    public string Servers => string.IsNullOrWhiteSpace(Secondary) ? Primary : $"{Primary}  ·  {Secondary}";
    public string PingText { get; set; } = "—";
    public long? Rtt { get; set; }
}

public sealed class DnsAdapterInfo
{
    public string Name { get; init; } = "";
    public int Index { get; init; }
    public string Display { get; init; } = "";
    public string Current { get; init; } = "Automatic";
    public bool HasGateway { get; init; }
    public override string ToString() => Display;
}

public static class DnsJumper
{
    public const string AllAdapters = "*";

    public static readonly DnsProvider[] Providers =
    [
        new() { Name = "Cloudflare", Primary = "1.1.1.1", Secondary = "1.0.0.1" },
        new() { Name = "Cloudflare security", Primary = "1.1.1.2", Secondary = "1.0.0.2" },
        new() { Name = "Google", Primary = "8.8.8.8", Secondary = "8.8.4.4" },
        new() { Name = "Quad9", Primary = "9.9.9.9", Secondary = "149.112.112.112" },
        new() { Name = "OpenDNS", Primary = "208.67.222.222", Secondary = "208.67.220.220" },
        new() { Name = "AdGuard", Primary = "94.140.14.14", Secondary = "94.140.15.15" },
        new() { Name = "CleanBrowsing", Primary = "185.228.168.9", Secondary = "185.228.169.9" },
        new() { Name = "Control D", Primary = "76.76.2.0", Secondary = "76.76.10.0" },
        new() { Name = "Comodo", Primary = "8.26.56.26", Secondary = "8.20.247.20" },
        new() { Name = "Verisign", Primary = "64.6.64.6", Secondary = "64.6.65.6" },
        new() { Name = "Yandex", Primary = "77.88.8.8", Secondary = "77.88.8.1" },
        new() { Name = "Level3", Primary = "4.2.2.1", Secondary = "4.2.2.2" }
    ];

    public static List<DnsProvider> Rows()
        => Providers.Select(p => new DnsProvider
        {
            Name = p.Name,
            Primary = p.Primary,
            Secondary = p.Secondary
        }).ToList();

    public static List<DnsAdapterInfo> Adapters()
    {
        var list = new List<DnsAdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var index = nic.GetIPProperties().GetIPv4Properties()?.Index ?? 0;
                if (index <= 0) continue;
                var hasGateway = nic.GetIPProperties().GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(g.Address));
                var dns = CurrentDns(nic);
                list.Add(new DnsAdapterInfo
                {
                    Name = nic.Name,
                    Index = index,
                    HasGateway = hasGateway,
                    Current = dns,
                    Display = $"{nic.Name}  ·  {dns}"
                });
            }
            catch { }
        }
        return list
            .OrderByDescending(a => a.HasGateway)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string Apply(DnsAdapterInfo? adapter, DnsProvider provider)
        => Apply(adapter, provider.Primary, provider.Secondary);

    public static string Apply(DnsAdapterInfo? adapter, string primary, string secondary)
    {
        if (!IPAddress.TryParse(primary.Trim(), out var primaryIp) || primaryIp.AddressFamily != AddressFamily.InterNetwork)
            return "Primary DNS must be an IPv4 address.";
        var servers = new List<string> { primaryIp.ToString() };
        if (!string.IsNullOrWhiteSpace(secondary))
        {
            if (!IPAddress.TryParse(secondary.Trim(), out var secondaryIp) || secondaryIp.AddressFamily != AddressFamily.InterNetwork)
                return "Secondary DNS must be an IPv4 address.";
            servers.Add(secondaryIp.ToString());
        }

        var targets = Targets(adapter);
        if (targets.Count == 0) return "No active network adapter found.";

        var failed = new List<string>();
        var ok = 0;
        foreach (var target in targets)
        {
            var error = SetServers(target, servers.ToArray());
            if (error is null) ok++;
            else failed.Add($"{target.Name}: {error}");
        }
        Flush();
        if (ok == 0)
            return "Could not set DNS. " + string.Join(" ", failed);
        var text = $"Set {string.Join(" / ", servers)} on {ok} adapter(s).";
        if (failed.Count > 0) text += " Some adapters failed: " + string.Join(" ", failed);
        return text;
    }

    public static string Restore(DnsAdapterInfo? adapter)
    {
        var targets = Targets(adapter);
        if (targets.Count == 0) return "No active network adapter found.";
        var failed = new List<string>();
        var ok = 0;
        foreach (var target in targets)
        {
            var error = SetServers(target, null);
            if (error is null) ok++;
            else failed.Add($"{target.Name}: {error}");
        }
        Flush();
        if (ok == 0)
            return "Could not restore automatic DNS. " + string.Join(" ", failed);
        return $"Restored automatic DNS on {ok} adapter(s).";
    }

    public static void Flush()
    {
        using var proc = Process.Start(new ProcessStartInfo("ipconfig", "/flushdns")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        proc?.WaitForExit(4000);
    }

    public static async Task MeasureAsync(IReadOnlyList<DnsProvider> rows, CancellationToken cancel = default)
    {
        await Task.WhenAll(rows.Select(async row =>
        {
            cancel.ThrowIfCancellationRequested();
            var ms = await RttAsync(row.Primary, cancel);
            row.Rtt = ms;
            row.PingText = ms is null ? "timeout" : $"{ms} ms";
        }));
    }

    public static DnsProvider? Fastest(IEnumerable<DnsProvider> rows)
        => rows.Where(r => r.Rtt is > 0).OrderBy(r => r.Rtt).FirstOrDefault();

    private static List<DnsAdapterInfo> Targets(DnsAdapterInfo? adapter)
    {
        if (adapter is not null && adapter.Name != AllAdapters && adapter.Index > 0)
            return [adapter];
        var adapters = Adapters();
        if (adapter is null || adapter.Name == AllAdapters)
        {
            var gated = adapters.Where(a => a.HasGateway).ToList();
            return gated.Count > 0 ? gated : adapters;
        }
        var match = adapters.Where(a => a.Index == adapter.Index || a.Name == adapter.Name).ToList();
        return match.Count > 0 ? match : adapters;
    }

    private static string? SetServers(DnsAdapterInfo target, string[]? servers)
    {
        var wmi = SetByWmi(target.Index, servers);
        if (wmi is null) return null;
        var netsh = SetByNetsh(target.Name, servers);
        return netsh is null ? null : wmi + " " + netsh;
    }

    private static string? SetByWmi(int index, string[]? servers)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT * FROM Win32_NetworkAdapterConfiguration WHERE InterfaceIndex = {index} AND IPEnabled = TRUE");
            foreach (ManagementObject obj in searcher.Get())
            {
                using (obj)
                {
                    var result = (uint)obj.InvokeMethod("SetDNSServerSearchOrder", [servers]);
                    if (result is 0 or 1) return null;
                    return $"WMI code {result}";
                }
            }
            return "No IP-enabled adapter at that index.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string? SetByNetsh(string name, string[]? servers)
    {
        string[] first = servers is { Length: > 0 }
            ? ["interface", "ipv4", "set", "dnsservers", "name=" + name, "static", servers[0], "primary", "validate=no"]
            : ["interface", "ipv4", "set", "dnsservers", "name=" + name, "dhcp"];
        var error = RunNetsh(first);
        if (error is not null) return error;
        if (servers is { Length: > 1 })
            return RunNetsh(["interface", "ipv4", "add", "dnsservers", "name=" + name, servers[1], "index=2", "validate=no"]);
        return null;
    }

    private static string? RunNetsh(string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);
            using var proc = Process.Start(psi);
            if (proc is null) return "Could not start netsh.";
            var output = (proc.StandardOutput.ReadToEnd() + " " + proc.StandardError.ReadToEnd()).Trim();
            proc.WaitForExit(8000);
            if (proc.ExitCode != 0 && output.Length > 0) return output;
            if (output.Contains("not", StringComparison.OrdinalIgnoreCase) &&
                output.Contains("error", StringComparison.OrdinalIgnoreCase))
                return output;
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string CurrentDns(NetworkInterface nic)
    {
        var ips = nic.GetIPProperties().DnsAddresses
            .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork)
            .Select(ip => ip.ToString())
            .Where(ip => ip is not "0.0.0.0")
            .Distinct()
            .ToList();
        return ips.Count == 0 ? "Automatic" : string.Join(" / ", ips);
    }

    private static async Task<long?> RttAsync(string host, CancellationToken cancel)
    {
        if (!IPAddress.TryParse(host, out var ip)) return null;
        var ping = await PingAsync(ip);
        if (ping is not null) return ping;
        return await TcpAsync(ip, cancel);
    }

    private static async Task<long?> PingAsync(IPAddress ip)
    {
        try
        {
            using var ping = new Ping();
            var sw = Stopwatch.StartNew();
            var reply = await ping.SendPingAsync(ip, 800);
            if (reply.Status == IPStatus.Success)
                return Math.Max(1, reply.RoundtripTime == 0 ? sw.ElapsedMilliseconds : reply.RoundtripTime);
        }
        catch { }
        return null;
    }

    private static async Task<long?> TcpAsync(IPAddress ip, CancellationToken cancel)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(800);
            var sw = Stopwatch.StartNew();
            await client.ConnectAsync(ip, 53, timeout.Token);
            return Math.Max(1, sw.ElapsedMilliseconds);
        }
        catch { }
        return null;
    }
}
