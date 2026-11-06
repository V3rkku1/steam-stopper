// Runs Fetcher against a local HttpListener with ranges, throttling and random drops; two links on 127.0.0.1 and 127.0.0.2 (Linux, or Windows with 127.0.0.2 reachable).
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using SteamStopper.Core;

var data = RandomNumberGenerator.GetBytes(40 * 1024 * 1024 + 12345);
var sha = Convert.ToHexString(SHA256.HashData(data));
var rnd = new Random(1);
const int port = 18765;
var listener = new HttpListener();
listener.Prefixes.Add($"http://127.0.0.1:{port}/");
listener.Start();
var hits = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
bool noRange = false, unknownLen = false; int slow = 5; double dropRate = 0.15;
_ = Task.Run(async () =>
{
    while (true)
    {
        var ctx = await listener.GetContextAsync();
        _ = Task.Run(async () =>
        {
            var res = ctx.Response;
            try
            {
                hits.AddOrUpdate(ctx.Request.RemoteEndPoint.Address.ToString(), 1, (_, n) => n + 1);
                if (ctx.Request.Url!.AbsolutePath == "/gone" && ctx.Request.Headers["Range"] != "bytes=0-") { res.StatusCode = 403; res.Close(); return; }
                if (ctx.Request.Url!.AbsolutePath == "/redir") { res.Redirect($"http://127.0.0.1:{port}/files/test%20file.bin"); res.Close(); return; }
                long start = 0, end = data.Length - 1;
                var range = ctx.Request.Headers["Range"];
                res.Headers["ETag"] = "\"abc\"";
                res.SendChunked = unknownLen;
                if (range is not null && !noRange)
                {
                    var r = range["bytes=".Length..].Split('-');
                    start = long.Parse(r[0]);
                    if (r[1].Length > 0) end = Math.Min(end, long.Parse(r[1]));
                    res.StatusCode = 206;
                    res.Headers["Content-Range"] = $"bytes {start}-{end}/{data.Length}";
                }
                if (!unknownLen) res.ContentLength64 = end - start + 1;
                var dropAt = rnd.NextDouble() < dropRate ? start + (end - start) / 3 : long.MaxValue;
                for (var p = start; p <= end;)
                {
                    var n = (int)Math.Min(64 * 1024, end - p + 1);
                    if (p >= dropAt) { res.Abort(); return; }
                    await res.OutputStream.WriteAsync(data.AsMemory((int)p, n));
                    p += n;
                    await Task.Delay(slow);
                }
                res.Close();
            }
            catch { try { res.Abort(); } catch { } }
        });
    }
});

var links = new List<BondLink>
{
    new() { Name = "LinkA", Address = IPAddress.Parse("127.0.0.1") },
    new() { Name = "LinkB", Address = IPAddress.Parse("127.0.0.2"), Weight = 2 },
};
var fetcher = new Fetcher(() => links);
var dir = Path.Combine(Path.GetTempPath(), "fetchtest-" + Guid.NewGuid().ToString("N"));
var fails = 0;

async Task<FetchJob> RunJob(FetchJob job, int conns, int pauseAfterMs = 0)
{
    using var cts = new CancellationTokenSource();
    if (pauseAfterMs > 0) cts.CancelAfter(pauseAfterMs);
    var sw = Stopwatch.StartNew();
    var t = fetcher.Run(job, conns, cts.Token);
    while (!t.IsCompleted) { await Task.WhenAny(t, Task.Delay(500)); job.Sample(0.5); }
    await t;
    Console.WriteLine($"  {job.State} {job.FileName} {Format.Bytes(job.Received)} in {sw.Elapsed.TotalSeconds:0.0}s err='{job.Error}'");
    return job;
}

void Check(string what, bool ok) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}"); if (!ok) fails++; }
bool Same(FetchJob j) => j.State == FetchState.Done && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(j.FinalPath))) == sha;

Console.WriteLine("1) ranged, 8 conns, redirect + filename, random drops");
var j1 = await RunJob(new FetchJob { Url = $"http://127.0.0.1:{port}/redir", Folder = dir }, 8);
Check("hash matches", Same(j1));
Check("name from redirect url", j1.FileName == "test file.bin");
Check("both links used", hits.ContainsKey("127.0.0.1") && hits.ContainsKey("127.0.0.2"));
Check("no leftovers", !File.Exists(j1.PartPath) && !File.Exists(j1.StatePath));
Console.WriteLine($"  hits: {string.Join(", ", hits.Select(h => h.Key + "=" + h.Value))}");

slow = 40; Console.WriteLine("2) pause after 1s, reload from state, resume");
var j2 = await RunJob(new FetchJob { Url = $"http://127.0.0.1:{port}/files/test%20file.bin", Folder = dir }, 8, 1000);
Check("paused", j2.State == FetchState.Paused && File.Exists(j2.StatePath));
var r2 = FetchJob.Load(j2.StatePath)!;
Console.WriteLine($"  saved {r2.Received} of {data.Length}, segs {r2.Segments.Count}"); Check("reloaded progress partial", r2.Received > 0 && r2.Received < data.Length);
Check("unique name", r2.FileName == "test file (1).bin");
await RunJob(r2, 8);
Check("resumed hash", Same(r2));

slow = 5; Console.WriteLine("3) no range support");
noRange = true;
var j3 = await RunJob(new FetchJob { Url = $"http://127.0.0.1:{port}/a.bin", Folder = dir }, 8);
Check("single conn hash", Same(j3));

Console.WriteLine("4) no range + unknown length (chunked)");
unknownLen = true; dropRate = 0;
var j4 = await RunJob(new FetchJob { Url = $"http://127.0.0.1:{port}/b.bin", Folder = dir }, 8);
Check("chunked hash", Same(j4) && j4.Total == data.Length);
noRange = false; unknownLen = false; dropRate = 0.15;

Console.WriteLine("5) bad url / 404-ish");
var j5 = await RunJob(new FetchJob { Url = "ftp://x/y", Folder = dir }, 4);
Check("bad scheme fails", j5.State == FetchState.Failed);
var j6 = await RunJob(new FetchJob { Url = "http://127.0.0.1:1/x", Folder = dir }, 4);
Check("refused fails", j6.State == FetchState.Failed);

Console.WriteLine("6) one dead link (bind fails)");
links.Add(new BondLink { Name = "Dead", Address = IPAddress.Parse("10.255.255.1"), Weight = 4 });
var j7 = await RunJob(new FetchJob { Url = $"http://127.0.0.1:{port}/c.bin", Folder = dir }, 8);
Check("dead link skipped", Same(j7)); Console.WriteLine(string.Join(",", links.Select(l => l.Name + ":" + l.Failing)));
Console.WriteLine("7) same link twice at once");
var ja = new FetchJob { Url = $"http://127.0.0.1:{port}/same.bin", Folder = dir };
var jb = new FetchJob { Url = $"http://127.0.0.1:{port}/same.bin", Folder = dir };
await Task.WhenAll(RunJob(ja, 4), RunJob(jb, 4));
Check("distinct names, both ok", ja.FileName != jb.FileName && Same(ja) && Same(jb));
Console.WriteLine("8) 403 mid-download keeps real error");
var j8 = await RunJob(new FetchJob { Url = $"http://127.0.0.1:{port}/gone", Folder = dir }, 8);
Check("403 reported", j8.State == FetchState.Failed && j8.Error.Contains("403"));
Directory.Delete(dir, true);
Console.WriteLine(fails == 0 ? "ALL PASS" : $"{fails} FAILED");
return fails;
