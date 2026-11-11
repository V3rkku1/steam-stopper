using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamStopper.Core;

public sealed record SteamServer(string Host, int CellId, int Load)
{
    public string City => DownloadDoctor.CityOf(Host);
    public double? PingMs { get; set; }
}

public sealed record DoctorReport(List<SteamServer> Servers, double LineBps, double SteamBps, string Verdict, string[] Fixes);

/// <summary>
/// Why are Steam downloads slow? Steam picks its download servers itself (the region setting is ignored
/// since late 2024), so this measures what can be measured: the servers Steam hands this PC, their
/// distance and load, and the line's own speed, then says which one is the limit.
/// </summary>
public static class DownloadDoctor
{
    // Airport codes Steam's cache hosts are named after (cache5-lax2.steamcontent.com).
    private static readonly Dictionary<string, string> Cities = new()
    {
        ["ams"] = "Amsterdam", ["atl"] = "Atlanta", ["bom"] = "Mumbai", ["bog"] = "Bogotá", ["bru"] = "Brussels", ["buh"] = "Bucharest",
        ["can"] = "Guangzhou", ["chi"] = "Chicago", ["cph"] = "Copenhagen", ["den"] = "Denver", ["dfw"] = "Dallas", ["dxb"] = "Dubai",
        ["eze"] = "Buenos Aires", ["fra"] = "Frankfurt", ["gru"] = "São Paulo", ["hel"] = "Helsinki", ["hkg"] = "Hong Kong",
        ["iad"] = "Washington", ["icn"] = "Seoul", ["jnb"] = "Johannesburg", ["kul"] = "Kuala Lumpur", ["lax"] = "Los Angeles",
        ["lhr"] = "London", ["lim"] = "Lima", ["lis"] = "Lisbon", ["mad"] = "Madrid", ["man"] = "Manchester", ["mel"] = "Melbourne",
        ["mia"] = "Miami", ["mil"] = "Milan", ["mex"] = "Mexico City", ["msp"] = "Minneapolis", ["nyc"] = "New York", ["ord"] = "Chicago",
        ["osl"] = "Oslo", ["par"] = "Paris", ["prg"] = "Prague", ["scl"] = "Santiago", ["sea"] = "Seattle", ["sgp"] = "Singapore",
        ["sha"] = "Shanghai", ["sjc"] = "San Jose", ["sof"] = "Sofia", ["sto"] = "Stockholm", ["syd"] = "Sydney", ["tyo"] = "Tokyo",
        ["vie"] = "Vienna", ["waw"] = "Warsaw", ["yyz"] = "Toronto", ["zrh"] = "Zurich"
    };

    public static string CityOf(string host)
    {
        var code = Regex.Match(host, @"-([a-z]{3})\d*[-.]").Groups[1].Value;
        return Cities.TryGetValue(code, out var city) ? city : code.Length > 0 ? code.ToUpperInvariant() : host;
    }

    /// <summary>The download servers Steam's directory hands this PC, one per site, least loaded first.</summary>
    public static List<SteamServer> ParseServers(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("response").GetProperty("servers").EnumerateArray()
            .Where(s => s.TryGetProperty("type", out var t) && t.GetString() is "SteamCache" or "CDN")
            .Select(s => new SteamServer(
                s.GetProperty("host").GetString() ?? "",
                s.TryGetProperty("cell_id", out var cell) ? cell.GetInt32() : 0,
                s.TryGetProperty("load", out var load) ? load.GetInt32() : 0))
            .Where(s => s.Host.Length > 0)
            .GroupBy(s => s.City)
            .Select(g => g.OrderBy(s => s.Load).First())
            .ToList();
    }

    public static async Task<List<SteamServer>> Servers(HttpClient http, CancellationToken cancel) =>
        ParseServers(await http.GetStringAsync(
            "https://api.steampowered.com/IContentServerDirectoryService/GetServersForSteamPipe/v1/?cell_id=0&max_servers=40", cancel));

    /// <summary>Best of three TCP connects to port 443, which is what a download's first round trip costs.</summary>
    public static async Task<double?> Ping(string host, CancellationToken cancel)
    {
        double? best = null;
        for (var i = 0; i < 3; i++)
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(2000);
            var watch = Stopwatch.StartNew();
            try { await client.ConnectAsync(host, 443, timeout.Token); }
            catch when (!cancel.IsCancellationRequested) { continue; }
            var ms = watch.Elapsed.TotalMilliseconds;
            best = best is null ? ms : Math.Min(best.Value, ms);
        }
        return best;
    }

    /// <summary>Line speed: download from Cloudflare's speed test for up to 8 seconds over 4 connections.</summary>
    public static async Task<double> LineSpeed(HttpClient http, CancellationToken cancel)
    {
        long total = 0;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        stop.CancelAfter(TimeSpan.FromSeconds(8));
        var watch = Stopwatch.StartNew();
        var workers = Enumerable.Range(0, 4).Select(async _ =>
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var response = await http.GetAsync("https://speed.cloudflare.com/__down?bytes=50000000", HttpCompletionOption.ResponseHeadersRead, stop.Token);
                    await using var body = await response.Content.ReadAsStreamAsync(stop.Token);
                    int read;
                    while ((read = await body.ReadAsync(buffer, stop.Token)) > 0) Interlocked.Add(ref total, read);
                }
            }
            catch (OperationCanceledException) { } // time's up
        });
        await Task.WhenAll(workers);
        cancel.ThrowIfCancellationRequested();
        return total / Math.Max(0.5, watch.Elapsed.TotalSeconds);
    }

    /// <summary>Turn the measurements into one plain answer and the fixes that still work.</summary>
    public static (string Verdict, string[] Fixes) Diagnose(List<SteamServer> servers, double lineBps, double steamBps)
    {
        var best = servers.Where(s => s.PingMs is not null).MinBy(s => s.PingMs);
        var mbps = lineBps * 8 / 1_000_000;
        if (best is null)
            return ("Steam's download servers didn't answer.",
                ["Check that Steam isn't blocked (Control page) and that a firewall or VPN isn't stopping it.", "Try again in a few minutes; Steam's servers may be busy."]);
        if (steamBps > 0 && steamBps < lineBps * 0.5)
            return ($"Steam is using only {steamBps / lineBps:P0} of your line ({Format.Speed(steamBps)} of {Format.Speed(lineBps)}).",
                ["Clear Steam's download cache: it fixes most stuck or slow downloads.", "In Steam, Settings > Downloads: make sure 'Limit bandwidth' is off.", "Pause other downloads (Fetch, browsers) while Steam downloads."]);
        if (mbps < 25)
            return ($"Your internet is the limit: {Format.Speed(lineBps)} ({mbps:0} Mbit/s). Steam can't download faster than this.",
                ["Use a cable instead of Wi-Fi if you can.", "Combine a second connection (phone tethering) on the Bond page.", "Pause other downloads and streams while Steam downloads."]);
        if (best.PingMs > 80)
            return ($"Steam's nearest servers are far away: {best.City} at {best.PingMs:0} ms.",
                ["Long distances slow single downloads; Steam's many connections usually hide it.", "Clear Steam's download cache so Steam picks its servers again."]);
        return ($"Your line ({Format.Speed(lineBps)}) and Steam's servers ({best.City}, {best.PingMs:0} ms) both look fine.",
            ["If Steam is still slow, clear its download cache.", "Busy times (evenings, big releases) slow Steam's servers down for everyone."]);
    }
}
