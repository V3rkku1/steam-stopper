// Runs Fetcher against a local HttpListener with ranges, throttling and random drops; two links on 127.0.0.1 and 127.0.0.2 (Linux, or Windows with 127.0.0.2 reachable).
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using SteamStopper.Core;

// dotnet run -- --live: ask every emulator's real release page which file Install would download.
if (args is ["--live"])
{
    var missing = 0;
    foreach (var emu in AppHub.Emulators.Where(e => e.Source is not null))
    {
        foreach (var pgo in new[] { true, false })
        {
            try
            {
                var hit = await EmuStore.FindLatest(emu.Source!, pgo, CancellationToken.None);
                Console.WriteLine($"{(hit is null ? "MISS" : "OK  ")} {emu.Name,-14} pgo={pgo,-5} {hit?.Version} {hit?.File}");
                if (hit is null) missing++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL {emu.Name,-14} pgo={pgo,-5} {ex.Message}");
                missing++;
            }
        }
    }
    Console.WriteLine(missing == 0 ? "ALL FOUND" : $"{missing} NOT FOUND");
    return missing;
}


// dotnet run -- --covers "God of War" PS2: what the cover picker finds, live.
if (args is ["--covers", var q, var console])
{
    using var h = new System.Net.Http.HttpClient();
    foreach (var c in await RomLibrary.SearchCovers(console, q, h, 10)) Console.WriteLine($"{c.Rank} {c.Source,-28} {c.Name}  {c.Urls[0]}");
    return 0;
}
// dotnet run -- --roms: game library sniffing of .pkg files, PARAM.SFO titles and PS3/PS4 folder dumps.
if (args is ["--roms"])
{
    var root = Path.Combine(Path.GetTempPath(), "roms-" + Guid.NewGuid().ToString("N"));
    // A PARAM.SFO with text values: header, one 16-byte entry per value, then the key and data tables.
    static byte[] Sfo(string title, params (string Key, string Value)[] more)
    {
        var all = more.Prepend((Key: "TITLE", Value: title)).ToArray();
        var keys = all.Select(a => System.Text.Encoding.ASCII.GetBytes(a.Key + "\0")).ToArray();
        var values = all.Select(a => System.Text.Encoding.UTF8.GetBytes(a.Value + "\0")).ToArray();
        int keyStart = 20 + 16 * all.Length, dataStart = keyStart + keys.Sum(k => k.Length);
        var b = new byte[dataStart + values.Sum(v => v.Length)];
        "\0PSF"u8.CopyTo(b);
        BitConverter.GetBytes(keyStart).CopyTo(b, 8);
        BitConverter.GetBytes(dataStart).CopyTo(b, 12);
        BitConverter.GetBytes(all.Length).CopyTo(b, 16);
        int k = 0, d = 0;
        for (var i = 0; i < all.Length; i++)
        {
            var e = 20 + i * 16;
            BitConverter.GetBytes((ushort)k).CopyTo(b, e);
            BitConverter.GetBytes(values[i].Length).CopyTo(b, e + 4);
            BitConverter.GetBytes(d).CopyTo(b, e + 12);
            keys[i].CopyTo(b, keyStart + k);
            values[i].CopyTo(b, dataStart + d);
            k += keys[i].Length;
            d += values[i].Length;
        }
        return b;
    }
    void Put(string path, byte[] bytes) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes); }
    byte[] Pkg(string magic, byte platform, string contentId)
    {
        var b = new byte[2048];
        System.Text.Encoding.Latin1.GetBytes(magic).CopyTo(b, 0);
        b[7] = platform;
        System.Text.Encoding.ASCII.GetBytes(contentId).CopyTo(b, 0x30);
        return b;
    }
    Put(Path.Combine(root, "ps4", "eboot.bin"), [1]);
    Put(Path.Combine(root, "ps4", "sce_sys", "param.sfo"), Sfo("Bloodborne"));
    Put(Path.Combine(root, "ps3disc", "PS3_GAME", "USRDIR", "EBOOT.BIN"), [1]);
    Put(Path.Combine(root, "ps3disc", "PS3_GAME", "PARAM.SFO"), Sfo("Demon's Souls"));
    Put(Path.Combine(root, "NPUB30001", "USRDIR", "EBOOT.BIN"), [1]);
    Put(Path.Combine(root, "NPUB30001", "PARAM.SFO"), Sfo("Flower"));
    Put(Path.Combine(root, "a.pkg"), Pkg("\u007FCNT", 0, ""));
    Put(Path.Combine(root, "b.pkg"), Pkg("\u007FPKG", 1, "UP9000-BCUS98174_00-0000000000000000"));
    Put(Path.Combine(root, "c.pkg"), Pkg("\u007FPKG", 2, "UP9000-PCSA00001_00-0000000000000000"));
    Put(Path.Combine(root, "d.pkg"), Pkg("\u007FPKG", 2, "UP9000-ULUS10041_00-0000000000000000"));
    Put(Path.Combine(root, "e.pkg"), [1, 2, 3]);
    Put(Path.Combine(root, "f.vpk"), [1]);
    Put(Path.Combine(root, "PCSE00001", "eboot.bin"), [1]);
    Put(Path.Combine(root, "PCSE00001", "sce_sys", "param.sfo"), Sfo("Gravity Rush", ("TITLE_ID", "PCSE00001")));
    Put(Path.Combine(root, "BLUS30443", "USRDIR", "EBOOT.BIN"), [1]);
    Put(Path.Combine(root, "BLUS30443", "PARAM.SFO"), Sfo("Demon's Souls update", ("CATEGORY", "GD")));
    Put(Path.Combine(root, "BCUS98174", "USRDIR", "EBOOT.BIN"), [1]);
    Put(Path.Combine(root, "BCUS98174", "PARAM.SFO"), Sfo("Uncharted", ("CATEGORY", "HG")));
    Put(Path.Combine(root, "ps2", "Okami.chd"), [1]);
    Put(Path.Combine(root, "Sega Dreamcast", "Shenmue.m3u"), [1]);
    Put(Path.Combine(root, "Crash.chd"), [1]);
    var pkgStore = new RomLibrary.Store { Folders = [root] };
    RomLibrary.Scan(pkgStore);
    var got = pkgStore.Games.ToDictionary(g => Path.GetRelativePath(root, g.Path), g => $"{g.Console}|{g.Title}");
    var want = new Dictionary<string, string>
    {
        ["ps4"] = "PS4|Bloodborne", ["ps3disc"] = "PS3|Demon's Souls", ["NPUB30001"] = "PS3|Flower",
        ["a.pkg"] = "PS4|a", ["b.pkg"] = "PS3|b", ["c.pkg"] = "VITA|c", ["d.pkg"] = "|d", ["e.pkg"] = "|e", ["f.vpk"] = "VITA|f",
        ["PCSE00001"] = "VITA|Gravity Rush", ["BCUS98174"] = "PS3|Uncharted",
        [Path.Combine("ps2", "Okami.chd")] = "PS2|Okami", [Path.Combine("Sega Dreamcast", "Shenmue.m3u")] = "DREAMCAST|Shenmue", ["Crash.chd"] = "PS1|Crash"
    };
    var bad = want.Where(w => !got.TryGetValue(w.Key, out var g) || g != w.Value).Select(w => $"{w.Key}: want {w.Value}, got {got.GetValueOrDefault(w.Key)}")
        .Concat(got.Keys.Except(want.Keys).Select(k => $"unexpected {k}")).ToList();
    if (RomLibrary.Arguments("RPCS3", Path.Combine(root, "b.pkg")) != "") bad.Add("pkg should launch without arguments");
    if (RomLibrary.Arguments("shadPS4", "C:\\g") != "-g \"C:\\g\\eboot.bin\"") bad.Add("shadPS4 args");
    if (RomLibrary.InstalledPackages(pkgStore.Games).Select(g => Path.GetFileName(g.Path)).SingleOrDefault() != "b.pkg") bad.Add("installed b.pkg should be the one hidden");
    if (RomLibrary.Arguments("Vita3K", "/g/ux0/app/PCSE00001") != "-r PCSE00001") bad.Add("Vita3K args");
    if (RomLibrary.ConsoleOf("x/Game.ffpfs") != "PS5" || !RomLibrary.IsPs5Image("x/Game.FFPKG") || RomLibrary.IsPs5Image("x/Game.zar")) bad.Add("ffpfs is a PS5 image");
    if (RomLibrary.UnpackedDir(Path.Combine("g", "Game.ffpfs")) != Path.Combine("g", "Game")) bad.Add("ffpfs unpacks next to itself");
    if (RomLibrary.UnpackProgress("""{"kind":"progress","job":1,"stage":"write","done":50,"total":200}""") != (50UL, 200UL)
        || RomLibrary.UnpackProgress("""{"kind":"progress","job":1,"stage":"scan","done":0,"total":0}""") is not null
        || RomLibrary.UnpackProgress("""{"kind":"log","job":1,"line":"x"}""") is not null || RomLibrary.UnpackProgress("not json") is not null) bad.Add("Dump Forge progress");
    var imageDir = Directory.CreateDirectory(Path.Combine(root, "img")).FullName;
    File.WriteAllText(Path.Combine(imageDir, "Packed.ffpfs"), "");
    File.WriteAllText(Path.Combine(imageDir, "Done.ffpfs"), "");
    Directory.CreateDirectory(Path.Combine(imageDir, "Done"));
    var imageStore = new RomLibrary.Store { Folders = [imageDir] };
    RomLibrary.Scan(imageStore);
    if (imageStore.Games.Select(g => Path.GetFileName(g.Path)).SingleOrDefault() != "Packed.ffpfs") bad.Add("unpacked image not listed twice");
    // A part from a Forge that's gone is deleted; one still being written is kept and reported.
    Directory.CreateDirectory(Path.Combine(imageDir, "Packed.1-111.part"));
    Directory.CreateDirectory(Path.Combine(imageDir, "Packed.1-222.part"));
    Directory.CreateDirectory(Path.Combine(imageDir, "Packed.ffpfs.1-333.part")); // another output's part: not ours
    var live = RomLibrary.UnpackLeftovers(Path.Combine(imageDir, "Packed"), pid => pid == 222);
    if (live != 222 || Directory.Exists(Path.Combine(imageDir, "Packed.1-111.part")) || !Directory.Exists(Path.Combine(imageDir, "Packed.1-222.part"))
        || !Directory.Exists(Path.Combine(imageDir, "Packed.ffpfs.1-333.part"))) bad.Add("unpack leftovers");
    // A chosen unpack folder: the image's game goes there by name.
    var unpackTo = Directory.CreateDirectory(Path.Combine(root, "unpacked")).FullName;
    RomLibrary.UnpackRoot = unpackTo;
    var into = RomLibrary.UnpackedDir(Path.Combine(imageDir, "Packed.ffpfs"));
    RomLibrary.UnpackRoot = "";
    if (into != Path.Combine(unpackTo, "Packed")) bad.Add("unpack folder setting");

    // Steam: fully installed games only, the redistributables left out, Steam's cached art first.
    var steamRoot = Directory.CreateDirectory(Path.Combine(root, "Steam")).FullName;
    var steamApps = Directory.CreateDirectory(Path.Combine(steamRoot, "steamapps")).FullName;
    string Acf(string id, string name, int flags) => $"\"AppState\"\n{{\n\"appid\" \"{id}\"\n\"name\" \"{name}\"\n\"StateFlags\" \"{flags}\"\n}}\n";
    File.WriteAllText(Path.Combine(steamApps, "appmanifest_10.acf"), Acf("10", "Counter-Strike", 4));
    File.WriteAllText(Path.Combine(steamApps, "appmanifest_20.acf"), Acf("20", "Half Done", 1026));
    File.WriteAllText(Path.Combine(steamApps, "appmanifest_228980.acf"), Acf("228980", "Steamworks Common Redistributables", 4));
    Directory.CreateDirectory(Path.Combine(steamRoot, "appcache", "librarycache", "10"));
    File.WriteAllText(Path.Combine(steamRoot, "appcache", "librarycache", "10", "library_600x900.jpg"), "");
    var steamGames = StoreGames.Steam([steamRoot], steamRoot);
    if (steamGames is not [{ Title: "Counter-Strike", Path: "steam://rungameid/10", Console: "STEAM", IsLink: true, Exists: true } cs]
        || cs.ArtUrl != Path.Combine(steamRoot, "appcache", "librarycache", "10", "library_600x900.jpg")
        || !StoreGames.Steam([steamRoot], null)[0].ArtUrl.StartsWith("https://")) bad.Add("Steam games");

    // Epic: the game, not its DLC or a broken install.
    var epic = Directory.CreateDirectory(Path.Combine(root, "Manifests")).FullName;
    File.WriteAllText(Path.Combine(epic, "a.item"), """{"DisplayName":"Fortnite","AppName":"Fortnite","MainGameAppName":"Fortnite","CatalogNamespace":"fn","CatalogItemId":"4fe7","InstallLocation":"C:\\FN"}""");
    File.WriteAllText(Path.Combine(epic, "b.item"), """{"DisplayName":"Some DLC","AppName":"Dlc1","MainGameAppName":"Fortnite","CatalogNamespace":"fn","CatalogItemId":"x"}""");
    File.WriteAllText(Path.Combine(epic, "c.item"), """{"DisplayName":"Broken","AppName":"Br","MainGameAppName":"Br","bIsIncompleteInstall":true}""");
    File.WriteAllText(Path.Combine(epic, "d.item"), "not json");
    if (StoreGames.Epic(epic) is not [{ Title: "Fortnite", Console: "EPIC" } fn]
        || fn.Path != "com.epicgames.launcher://apps/fn%3A4fe7%3AFortnite?action=launch&silent=true") bad.Add("Epic games");

    // Heroic: Epic through legendary (no DLC), GOG named from its library cache, and added apps that are installed.
    var heroic = Path.Combine(root, "heroic");
    Directory.CreateDirectory(Path.Combine(heroic, "legendaryConfig", "legendary"));
    Directory.CreateDirectory(Path.Combine(heroic, "gog_store"));
    Directory.CreateDirectory(Path.Combine(heroic, "store_cache"));
    Directory.CreateDirectory(Path.Combine(heroic, "sideload_apps"));
    File.WriteAllText(Path.Combine(heroic, "legendaryConfig", "legendary", "installed.json"),
        """{"Quail":{"app_name":"Quail","title":"Quail Game","is_dlc":false},"QDlc":{"title":"Quail DLC","is_dlc":true}}""");
    File.WriteAllText(Path.Combine(heroic, "store_cache", "legendary_library.json"), """{"library":[{"app_name":"Quail","title":"Quail Game","art_square":"https://img/q.jpg"}]}""");
    File.WriteAllText(Path.Combine(heroic, "gog_store", "installed.json"), """{"installed":[{"appName":"1207658924","install_path":"C:\\G"}]}""");
    File.WriteAllText(Path.Combine(heroic, "store_cache", "gog_library.json"), """{"games":[{"app_name":"1207658924","title":"Witcher","art_square":"https://img/w.jpg"}]}""");
    File.WriteAllText(Path.Combine(heroic, "sideload_apps", "library.json"),
        """{"games":[{"app_name":"abc","title":"My App","is_installed":true,"art_square":"https://img/a.jpg"},{"app_name":"old","title":"Gone","is_installed":false}]}""");
    var heroicGames = StoreGames.Heroic(heroic).Select(g => $"{g.Title}|{g.Path}|{g.ArtUrl}").ToList();
    if (!heroicGames.SequenceEqual([
            "Quail Game|heroic://launch?appName=Quail&runner=legendary|https://img/q.jpg",
            "Witcher|heroic://launch?appName=1207658924&runner=gog|https://img/w.jpg",
            "My App|heroic://launch?appName=abc&runner=sideload|https://img/a.jpg"]))
        bad.Add("Heroic games: " + string.Join("; ", heroicGames));
    if (StoreGames.Heroic(Path.Combine(root, "nope")).Count != 0 || StoreGames.Epic(Path.Combine(root, "nope")).Count != 0) bad.Add("missing launchers");
    if (RomLibrary.Arguments("RetroArch", "x.cue", "PS1") != "-L \"cores\\swanstation_libretro.dll\" \"x.cue\""
        || !RomLibrary.Arguments("RetroArch", "x.nes", "NES · SNES").Contains("mesen_libretro") || RomLibrary.Arguments("RetroArch", "x", "PS2") != "\"x\"") bad.Add("RetroArch args");
    var emu = Path.Combine(root, "emu");
    Put(Path.Combine(emu, "user", "config.toml"), System.Text.Encoding.UTF8.GetBytes($"[GUI]\ninstallDirs = [\"{root.Replace("\\", "\\\\")}\", \"/nope\"]\n"));
    Directory.CreateDirectory(Path.Combine(emu, "dev_hdd0", "game"));
    if (!RomLibrary.EmulatorGameFolders("shadPS4", Path.Combine(emu, "shadPS4.exe")).SequenceEqual([root])) bad.Add("shadPS4 install dirs from config.toml");
    if (!RomLibrary.EmulatorGameFolders("RPCS3", Path.Combine(emu, "rpcs3.exe")).SequenceEqual([Path.Combine(emu, "dev_hdd0", "game")])) bad.Add("RPCS3 game folder");
    Directory.Delete(root, true);
    bad.ForEach(Console.WriteLine);
    Console.WriteLine(bad.Count == 0 ? "ROMS OK" : $"{bad.Count} ROM CHECKS FAILED");
    return bad.Count;
}


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
                if (ctx.Request.Url!.AbsolutePath == "/signed")
                {
                    // Like GitHub release downloads: a redirect to a link that only works for a short while.
                    res.Redirect($"http://127.0.0.1:{port}/blob?t={DateTime.UtcNow.Ticks}"); res.Close(); return;
                }
                if (ctx.Request.Url!.AbsolutePath == "/blob" && DateTime.UtcNow.Ticks - long.Parse(ctx.Request.QueryString["t"]!) > TimeSpan.FromSeconds(1).Ticks)
                {
                    res.StatusCode = 403; res.Close(); return;
                }
                if (ctx.Request.Url!.AbsolutePath == "/small") { res.ContentLength64 = 10; await res.OutputStream.WriteAsync(new byte[10]); res.Close(); return; }
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

Console.WriteLine("1b) speed limit 4 MB/s for 2 s");
fetcher.LimitBps = 4 << 20;
var jl = await RunJob(new FetchJob { Url = $"http://127.0.0.1:{port}/files/limited.bin", Folder = dir }, 8, 2000);
fetcher.LimitBps = 0;
Check($"limit holds the speed down (got {Format.Bytes(jl.Received)} in 2 s)", jl.Received > 4L << 20 && jl.Received < 11L << 20);
jl.DeleteLeftovers();

slow = 40; Console.WriteLine("2) pause after 1s, reload from state, resume");
var j2 = await RunJob(new FetchJob { Url = $"http://127.0.0.1:{port}/files/test%20file.bin", Folder = dir }, 8, 1000);
Check("paused", j2.State == FetchState.Paused && File.Exists(j2.StatePath));
var r2 = FetchJob.Load(j2.StatePath)!;
Console.WriteLine($"  saved {r2.Received} of {data.Length}, segs {r2.Segments.Count}"); Check("reloaded progress partial", r2.Received > 0 && r2.Received < data.Length);
Check("unique name", r2.FileName == "test file (1).bin");
await RunJob(r2, 8);
Check("resumed hash", Same(r2));

Console.WriteLine("1c) auto connections: starts small, adds while the speed rises");
var peak = 0;
var jAuto = new FetchJob { Url = $"http://127.0.0.1:{port}/files/auto.bin", Folder = dir };
var autoRun = fetcher.Run(jAuto, 0, CancellationToken.None);
while (!autoRun.IsCompleted) { peak = Math.Max(peak, jAuto.Connections); await Task.WhenAny(autoRun, Task.Delay(200)); }
await autoRun;
Check($"auto connections: finishes, grew past the start ({peak} at most)", Same(jAuto) && peak > 6);

Console.WriteLine("2a) pause and resume the same download without reloading it");
var jp = await RunJob(new FetchJob { Url = $"http://127.0.0.1:{port}/files/pause.bin", Folder = dir }, 8, 1000);
await RunJob(jp, 8);
Check("pause then resume in one session finishes", Same(jp));

Console.WriteLine("2b) expired link: pause, then continue from a new link to the same file");
var jr = await RunJob(new FetchJob { Url = $"http://127.0.0.1:{port}/files/relink.bin", Folder = dir }, 8, 1000);
var kept = jr.Received;
Check("relink: a different file is refused", (await fetcher.Relink(jr, $"http://127.0.0.1:{port}/small", CancellationToken.None))?.Contains("different file") == true && jr.Url.EndsWith("relink.bin"));
Check("relink: the same file is taken, progress kept", await fetcher.Relink(jr, $"http://127.0.0.1:{port}/mirror/relink.bin", CancellationToken.None) is null
    && jr.Url.Contains("/mirror/") && jr.Received == kept && kept > 0);
await RunJob(jr, 8);
Check("relink: finishes with the right file", Same(jr));

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
Console.WriteLine("10) signed redirect link expires mid-download");
slow = 40;
var j10 = await RunJob(new FetchJob { Url = $"http://127.0.0.1:{port}/signed", Folder = dir }, 4);
Check("re-follows redirect after 403", Same(j10));
slow = 5;

Console.WriteLine("9) emulator release picking, exe finding, unzip");
var gh = """
[{"tag_name":"v2.5.0-draft","draft":true,"assets":[{"name":"pcsx2-v2.5.0-windows-x64-Qt.7z","browser_download_url":"https://x/draft.7z"}]},
 {"tag_name":"v2.4.1","draft":false,"assets":[
   {"name":"pcsx2-v2.4.1-windows-x64-Qt-symbols.7z","browser_download_url":"https://x/sym.7z"},
   {"name":"pcsx2-v2.4.1-windows-x64-installer.exe","browser_download_url":"https://x/inst.exe"},
   {"name":"pcsx2-v2.4.1-windows-arm64-Qt.7z","browser_download_url":"https://x/arm.7z"},
   {"name":"pcsx2-v2.4.1-windows-x64-Qt.7z","browser_download_url":"https://x/good.7z"}]}]
""";
var pcsx2 = new EmuSource("PCSX2/pcsx2", [@"windows-x64-Qt\.7z$"], "pcsx2");
var aza = """[{"tag_name":"2127","assets":[{"name":"azahar-libretro-windows-x86_64-2127.zip","browser_download_url":"https://a/core"},{"name":"azahar-windows-msvc-2127.zip","browser_download_url":"https://a/app"}]}]""";
Check("libretro cores are never picked", EmuStore.PickGitHub(aza, new EmuSource("azahar-emu/azahar", [@"windows-msvc.*\.zip$", @"windows.*\.zip$"], "azahar"))?.Url == "https://a/app");
var pick = EmuStore.PickGitHub(gh, pcsx2);
Check("github: skips draft, symbols, installer, arm64", pick?.Url == "https://x/good.7z" && pick.Version == "v2.4.1");
Check("github: second pattern used when first misses",
    EmuStore.PickGitHub(gh, pcsx2 with { Assets = [@"nomatch", @"x64-Qt\.7z$"] })?.Url == "https://x/good.7z");
Check("github: nothing matches gives null", EmuStore.PickGitHub(gh, pcsx2 with { Assets = [@"\.dmg$"] }) is null);
var egl = """
[{"tag_name":"daily-20261107","assets":[
   {"name":"eglantine-windows-x86_64-pgo.zip","browser_download_url":"https://e/pgo"},
   {"name":"eglantine-linux-x86_64.AppImage","browser_download_url":"https://e/linux"},
   {"name":"eglantine-windows-x86_64.zip","browser_download_url":"https://e/std"}]}]
""";
var eglSrc = new EmuSource("Djihads80/Eglantine", [@"windows-x86_64(-pgo)?\.zip$", @"windows.*\.zip$"], @"^eglantine\.exe$");
Check("pgo on: takes the PGO build", EmuStore.PickGitHub(egl, eglSrc, pgo: true)?.Url == "https://e/pgo");
Check("pgo off: takes the standard build", EmuStore.PickGitHub(egl, eglSrc, pgo: false)?.Url == "https://e/std");
Check("pgo on, none published: standard build", EmuStore.PickGitHub(gh, pcsx2, pgo: true)?.Url == "https://x/good.7z");
// Asset lists as the real Hookshot v2.0.14 and Dusklight v2.0.3 release pages show them.
var hook = AppHub.Emulators.First(e => e.Name == "Hookshot").Source!;
var hookJson = """
[{"tag_name":"v2.0.14","assets":[{"name":"hookshot-linux.txt","browser_download_url":"https://h/t"},{"name":"hookshot-setup.txt","browser_download_url":"https://h/s"},
  {"name":"Hookshot-v2.0.14-x86_64.AppImage","browser_download_url":"https://h/a"},{"name":"HookshotBuilder.exe","browser_download_url":"https://h/exe"}]}]
""";
Check("Hookshot: takes the bare builder exe", EmuStore.PickGitHub(hookJson, hook)?.Url == "https://h/exe");
var dusk = AppHub.Emulators.First(e => e.Name == "Dusklight").Source!;
var duskJson = """
[{"tag_name":"v2.0.3","assets":[{"name":"Dusklight-v2.0.3-win32-arm64.zip","browser_download_url":"https://d/arm"},
  {"name":"Dusklight-v2.0.3-macos-x86_64.zip","browser_download_url":"https://d/mac"},{"name":"Dusklight-v2.0.3-win32-x86_64.zip","browser_download_url":"https://d/win"}]}]
""";
Check("Dusklight: takes the x64 Windows zip", EmuStore.PickGitHub(duskJson, dusk)?.Url == "https://d/win");
var bare = Path.Combine(Path.GetTempPath(), "ss-bare-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(bare);
File.WriteAllText(Path.Combine(bare, "HookshotBuilder.exe"), "MZ");
EmuStore.Extract(Path.Combine(bare, "HookshotBuilder.exe"), Path.Combine(bare, "out"));
Check("a bare exe release is copied in and found", EmuStore.FindExe(Path.Combine(bare, "out"), hook.Exe)?.EndsWith("HookshotBuilder.exe") == true);
Directory.Delete(bare, true);
Check("'pgo' only as its own word", EmuStore.IsPgo("x-windows-pgo.zip") && EmuStore.IsPgo("x_PGO.zip") && !EmuStore.IsPgo("pgoat.zip"));
var gl = """
[{"tag_name":"1.3.2","assets":{"links":[
   {"name":"ryujinx-1.3.2-linux_x64.tar.gz","url":"https://g/l"},
   {"name":"ryujinx-1.3.2-win_x64.zip","url":"https://g/w","direct_asset_url":"https://g/direct-w"}]}}]
""";
var ryu = new EmuSource("ryubing/ryujinx", [@"win_x64\.zip$"], @"^ryujinx\.exe$", "https://git.example");
Check("gitlab: prefers direct asset url", EmuStore.PickGitLab(gl, ryu)?.Url == "https://g/direct-w");

var emuDir = Path.Combine(dir, "emu");
Directory.CreateDirectory(Path.Combine(emuDir, "Cemu_2.6"));
File.WriteAllBytes(Path.Combine(emuDir, "Cemu_2.6", "Cemu.exe"), new byte[2000]);
File.WriteAllBytes(Path.Combine(emuDir, "Cemu_2.6", "updater.exe"), new byte[9000]);
File.WriteAllBytes(Path.Combine(emuDir, "Cemu_2.6", "unins000.exe"), new byte[9000]);
Check("exe: hint wins over bigger helper exes", Path.GetFileName(EmuStore.FindExe(emuDir, "cemu")!) == "Cemu.exe");
Check("exe: no hint match falls back to non-helper", Path.GetFileName(EmuStore.FindExe(emuDir, "zzz")!) == "Cemu.exe");

var zipSrc = Path.Combine(dir, "zipsrc", "melonDS");
Directory.CreateDirectory(zipSrc);
File.WriteAllText(Path.Combine(zipSrc, "melonDS.exe"), "v2");
var zip = Path.Combine(dir, "melonDS-windows.zip");
System.IO.Compression.ZipFile.CreateFromDirectory(Path.GetDirectoryName(zipSrc)!, zip);
var dest = Path.Combine(dir, "melon");
Directory.CreateDirectory(Path.Combine(dest, "melonDS"));
File.WriteAllText(Path.Combine(dest, "melonDS", "melonDS.exe"), "v1");
File.WriteAllText(Path.Combine(dest, "melonDS", "keys.txt"), "mine");
EmuStore.Extract(zip, dest);
Check("unzip: overwrites program, keeps user files",
    File.ReadAllText(Path.Combine(dest, "melonDS", "melonDS.exe")) == "v2" && File.Exists(Path.Combine(dest, "melonDS", "keys.txt")));

Console.WriteLine("11) game library: detection, titles, scan, launch args");
var roms = Path.Combine(dir, "roms");
Directory.CreateDirectory(Path.Combine(roms, "ps1", "FF7"));
void Disc(string name, params (long At, byte[] Bytes)[] marks)
{
    using var f = File.Create(Path.Combine(roms, name));
    f.SetLength(0x10000 + 64);
    foreach (var (at, bytes) in marks) { f.Position = at; f.Write(bytes); }
}
Disc("Wii Sports (USA).iso", (0x18, new byte[] { 0x5D, 0x1C, 0x9E, 0xA3 }));
Disc("Melee (USA) (Rev 2).iso", (0x1C, new byte[] { 0xC2, 0x33, 0x9F, 0x3D }));
Disc("GTA SA.iso", (0x8008, System.Text.Encoding.ASCII.GetBytes("PLAYSTATION")));
Disc("Crisis Core.iso", (0x8008, System.Text.Encoding.ASCII.GetBytes("PSP GAME")));
Disc("Halo.iso", (0x10000, System.Text.Encoding.ASCII.GetBytes("MICROSOFT*XBOX*MEDIA")));
Disc("mystery.iso");
File.WriteAllText(Path.Combine(roms, "Super Mario 64 (USA) [!].z64"), "");
File.WriteAllText(Path.Combine(roms, "Zelda_TOTK.nsp"), "");
File.WriteAllText(Path.Combine(roms, "readme.txt"), "");
File.WriteAllText(Path.Combine(roms, "ps1", "Crash.cue"), "");
File.WriteAllText(Path.Combine(roms, "ps1", "Crash.bin"), "");
File.WriteAllText(Path.Combine(roms, "ps1", "FF7", "FF7 (Disc 1).cue"), "");
File.WriteAllText(Path.Combine(roms, "ps1", "FF7", "FF7 (Disc 2).cue"), "");
File.WriteAllText(Path.Combine(roms, "ps1", "FF7", "FF7.m3u"), "FF7 (Disc 1).cue\nFF7 (Disc 2).cue\n");
File.WriteAllBytes(Path.Combine(roms, "Mario Kart World.nup"), [(byte)'N', (byte)'U', (byte)'P', 1, 0, 0]);
File.WriteAllBytes(Path.Combine(roms, "router-firmware.nup"), [(byte)'P', (byte)'K', 3, 4]);
var astro = Path.Combine(roms, "ps5", "PPSA01325-app");
Directory.CreateDirectory(Path.Combine(astro, "sce_sys"));
File.WriteAllText(Path.Combine(astro, "eboot.bin"), "");
File.WriteAllText(Path.Combine(astro, "sce_sys", "param.json"),
    """{"localizedParameters":{"defaultLanguage":"en-US","en-US":{"titleName":"ASTRO BOT"}}}""");
var ps4 = Path.Combine(roms, "ps4", "CUSA00001");
Directory.CreateDirectory(Path.Combine(ps4, "sce_sys"));
File.WriteAllText(Path.Combine(ps4, "eboot.bin"), "");
File.WriteAllText(Path.Combine(ps4, "sce_sys", "param.sfo"), "");
File.WriteAllText(Path.Combine(roms, "Demon's Souls.zar"), "");
string? Con(string n) => RomLibrary.ConsoleOf(Path.Combine(roms, n));
Check("Switch 2 .nup: Lichii archive yes, other .nup no", Con("Mario Kart World.nup") == "SWITCH 2" && Con("router-firmware.nup") is null);
Check("iso headers: Wii, GameCube, PS2, PSP, Xbox",
    Con("Wii Sports (USA).iso") == "WII" && Con("Melee (USA) (Rev 2).iso") == "GAMECUBE" && Con("GTA SA.iso") == "PS2"
    && Con("Crisis Core.iso") == "PSP" && Con("Halo.iso") == "XBOX");
Check("unknown iso is a game with no console", Con("mystery.iso") == "");
Check("extensions: N64, Switch; .txt and .bin ignored",
    Con("Super Mario 64 (USA) [!].z64") == "N64" && Con("Zelda_TOTK.nsp") == "SWITCH" && Con("readme.txt") is null && Con("ps1/Crash.bin") is null);
Check("multi-disc: discs listed in .m3u are hidden", Con("ps1/FF7/FF7 (Disc 1).cue") is null && Con("ps1/FF7/FF7.m3u") == "PS1");
Check("titles put a moved article back in front", RomLibrary.TitleOf("Legend of Zelda, The - Ocarina of Time (USA).z64") == "The Legend of Zelda - Ocarina of Time");
Check("titles drop region and dump tags", RomLibrary.TitleOf("Super Mario 64 (USA) [!].z64") == "Super Mario 64" && RomLibrary.TitleOf("Zelda_TOTK.nsp") == "Zelda TOTK");
var store = new RomLibrary.Store { Folders = [roms] };
var found = RomLibrary.Scan(store);
Check("PS5 folder dump: titled from param.json; PS4 folder named after itself without a title; no eboot added",
    store.Games.Any(g => g.Path == astro && g.Title == "ASTRO BOT" && g.Console == "PS5")
    && store.Games.Any(g => g.Path == ps4 && g.Title == "CUSA00001" && g.Console == "PS4") && !store.Games.Any(g => g.Path.EndsWith("eboot.bin")));
Check(".zar is PS5", store.Games.Any(g => g.Title == "Demon's Souls" && g.Console == "PS5"));
Check($"scan finds 14 games, second scan finds none (got {found})", found == 14 && RomLibrary.Scan(store) == 0);
var art = RomLibrary.ByKey(["Legend of Zelda, The - Ocarina of Time (Europe)", "Legend of Zelda, The - Ocarina of Time (USA) (Beta)",
    "Legend of Zelda, The - Ocarina of Time (USA)", "Mario Kart 64 (USA)"]);
Check("cover art matches loose file names, USA release first",
    RomLibrary.BestMatch("Mario Kart 64 (USA)", "Mario Kart 64", art) == "Mario Kart 64 (USA)"
    && RomLibrary.BestMatch("the_legend_of_zelda_ocarina_of_time", "the legend of zelda ocarina of time", art) == "Legend of Zelda, The - Ocarina of Time (USA)"
    && RomLibrary.BestMatch("Pokemon Snap", "Pokemon Snap", art) is null);
var tdb = RomLibrary.TdbLines("""
<game name="Super Mario Odyssey (JA)">
		<id>AAACB</id>
		<type>Switch</type>
		<region>JPN</region></game>
<game name="Super Mario Odyssey [Starter Pack] (EN,FR)">
		<id>AAACA</id>
		<type>Switch</type>
		<region>USA</region></game>
<game name="Mario &amp; Sonic (EN)">
		<id>AT4AB</id>
		<type>Switch</type>
		<region>EUR</region></game>
""").ToList();
var tdbGames = RomLibrary.TdbByKey(tdb);
Check("gametdb lines: decoded name, id, art region", tdb.Count == 3 && tdb[2] == "Mario & Sonic (EN)|AT4AB|EN");
Check("switch covers: scene file name finds the US release",
    RomLibrary.TdbPick("Super Mario Odyssey [0100000000010000][v0]", "Super Mario Odyssey", tdbGames) == ("AAACA", "US")
    && RomLibrary.TdbPick("Mario and Sonic", "Mario and Sonic", tdbGames) == ("AT4AB", "EN")
    && RomLibrary.TdbPick("Unknown Game", "Unknown Game", tdbGames) is null);
var ps3Tdb = RomLibrary.TdbByKey(RomLibrary.TdbLines("""
<game name="God of War HD (EN)">
		<id>NPUA80490</id>
		<type>SEN</type>
		<region/>
<game name="God of War Collection (USA) (EN)">
		<id>BCUS98229</id>
		<type>PS3</type>
		<region>NTSC-U</region>
"""));
Check("ps3 covers: PSN games without a region, NTSC-U is US, found by title or by id in a pkg name",
    RomLibrary.TdbPick("God of War HD", "God of War® HD", ps3Tdb) == ("NPUA80490", "EN")
    && RomLibrary.TdbPick("God of War Collection", "God of War Collection", ps3Tdb) == ("BCUS98229", "US")
    && RomLibrary.TitleId("UP9000-NPUA80490_00-GODOFWARHD000001.pkg") is "NPUA80490" && RomLibrary.TdbPick("x", "x", ps3Tdb, "NPUA80490") == ("NPUA80490", "EN"));
Check("cover search words: articles moved and dropped, symbols split",
    RomLibrary.Words("Legend of Zelda, The - Ocarina of Time (USA)").SequenceEqual(["legend", "of", "zelda", "ocarina", "of", "time"])
    && RomLibrary.Words("God of War® HD").SequenceEqual(["god", "of", "war", "hd"]));
var saveApp = Path.Combine(Path.GetTempPath(), "ss-saves-" + Guid.NewGuid().ToString("N"));
SaveBackup.Places["TestEmu"] = [@"{app}\saves", @"{app}\missing"];
Directory.CreateDirectory(Path.Combine(saveApp, "saves", "slot1"));
File.WriteAllText(Path.Combine(saveApp, "saves", "slot1", "game.sav"), "v1");
Check("save folders: only existing ones count", SaveBackup.Found("TestEmu", saveApp).Count == 1);
var backup = SaveBackup.Create("TestEmu", saveApp, Path.Combine(saveApp, "backups"))!;
File.WriteAllText(Path.Combine(saveApp, "saves", "slot1", "game.sav"), "v2-broken");
var restored = SaveBackup.Restore("TestEmu", saveApp, backup);
Check("save backup restores the old file", restored == 1 && File.ReadAllText(Path.Combine(saveApp, "saves", "slot1", "game.sav")) == "v1");
var evil = Path.Combine(saveApp, "evil.zip");
using (var z = System.IO.Compression.ZipFile.Open(evil, System.IO.Compression.ZipArchiveMode.Create))
{
    using (var w = new StreamWriter(z.CreateEntry("steamstopper-backup.json").Open())) w.Write("[\"{app}\\\\saves\"]");
    using (var w = new StreamWriter(z.CreateEntry("0/../../escaped.txt").Open())) w.Write("x");
}
SaveBackup.Restore("TestEmu", saveApp, evil);
Check("restore never writes outside the save folder", !File.Exists(Path.Combine(saveApp, "..", "escaped.txt")) && !File.Exists(Path.Combine(saveApp, "escaped.txt")));
Directory.Delete(saveApp, true);
// Restore also left a "Before restore" zip in the real backups folder.
if (Directory.Exists(SaveBackup.FolderFor("TestEmu"))) Directory.Delete(SaveBackup.FolderFor("TestEmu"), true);
if (Directory.Exists(SaveBackup.Root) && !Directory.EnumerateFileSystemEntries(SaveBackup.Root).Any()) Directory.Delete(SaveBackup.Root);
SaveBackup.Places.Remove("TestEmu");
var raList = """
[{"Title":"Legend of Zelda, The: Ocarina of Time ~Hack~ Master Quest","ID":900,"ConsoleID":2},
 {"Title":"Legend of Zelda, The: Ocarina of Time","ID":3,"ConsoleID":2},
 {"Title":"Mario Kart 64","ID":10,"ConsoleID":2}]
""";
Check("retroachievements: title match prefers the main set",
    Cheevos.Match(raList, "Legend of Zelda, The - Ocarina of Time (USA)", "The Legend of Zelda - Ocarina of Time") == 3
    && Cheevos.Match(raList, "Pilotwings 64 (USA)", "Pilotwings 64") is null);
var raProgress = Cheevos.ParseProgress("""
{"ID":3,"Title":"Legend of Zelda, The: Ocarina of Time","Achievements":{
 "1":{"Title":"Kokiri","Description":"Get the sword","Points":5,"BadgeName":"111","DateEarned":"2025-01-01 10:00:00"},
 "2":{"Title":"Master","Description":"Beat Ganon","Points":50,"BadgeName":"222"}}}
""");
Check("retroachievements: progress counts earned and lists earned first",
    raProgress.Total == 2 && raProgress.Earned == 1 && raProgress.List[0].Title == "Kokiri"
    && raProgress.List[1].BadgeUrl.EndsWith("222_lock.png") && raProgress.List[0].BadgeUrl.EndsWith("111.png"));
var steamServers = DownloadDoctor.ParseServers("""
{"response":{"servers":[
 {"type":"SteamCache","cell_id":64,"load":55,"host":"cache5-lax2.steamcontent.com"},
 {"type":"SteamCache","cell_id":64,"load":40,"host":"cache9-lax2.steamcontent.com"},
 {"type":"SteamCache","cell_id":76,"load":47,"host":"cache2-msp-mice.steamcontent.com"},
 {"type":"OpenCache","cell_id":1,"load":1,"host":"lancache.local"}]}}
""");
Check("download doctor: one server per city, least loaded, caches only",
    steamServers.Count == 2 && steamServers[0].Host == "cache9-lax2.steamcontent.com" && steamServers[0].City == "Los Angeles" && steamServers[1].City == "Minneapolis");
steamServers[0].PingMs = 20;
Check("download doctor: slow line is blamed on the line", DownloadDoctor.Diagnose(steamServers, 1_000_000, 0).Verdict.StartsWith("Your internet is the limit"));
Check("download doctor: Steam using half the line is blamed on Steam", DownloadDoctor.Diagnose(steamServers, 50_000_000, 5_000_000).Verdict.StartsWith("Steam is using only"));
var css = CssTheme.Parse("""
/* my theme */
:root { --bg: #112233; --Accent: rgba(10, 132, 255, .5); --radius: 22; }
.ignored { color: red; }
:root { --bg: #abc; }
""");
Check("css theme: variables, later wins, comments and other rules ignored",
    css.Count == 3 && css["bg"] == "#abc" && css["accent"] == "rgba(10, 132, 255, .5)" && css["radius"] == "22");
Check("css theme: colour forms",
    CssTheme.Color("#abc") == (255, 0xAA, 0xBB, 0xCC) && CssTheme.Color("#11223380") == (0x80, 0x11, 0x22, 0x33)
    && CssTheme.Color("rgba(10, 132, 255, .5)") == (128, 10, 132, 255) && CssTheme.Color("rgb(1 2 3 / 50%)") == (128, 1, 2, 3)
    && CssTheme.Color("transparent") == (0, 0, 0, 0) && CssTheme.Color("blue") is null && CssTheme.Color("#12345") is null);
var blink = new FetchJob { Url = "http://x/y" };
blink.SetState(FetchState.Downloading);
blink.Add(512 * 1024, "LinkA");
blink.Add(512 * 1024, "LinkB");
blink.Sample(1);
blink.Add(512 * 1024, "LinkA"); // LinkB gets no chunk this second
blink.Sample(1);
Check($"fetch: a link with a quiet second keeps its speed shown ({blink.Detail})", blink.Detail.Contains("LinkA") && blink.Detail.Contains("LinkB"));
var recent = Cheevos.ParseRecent("""
[{"Date":"2026-11-11 10:00:00","AchievementID":42,"Title":"First Steps","Description":"Leave the house","Points":5,"BadgeName":"9001","GameTitle":"Ocarina of Time"}]
""");
Check("retroachievements: recent unlocks parsed", recent is [(42, { Title: "First Steps", Points: 5, Earned: true }, "Ocarina of Time")] && Cheevos.ParseRecent("{}").Count == 0);
// A real reply for example.com: question, then a CNAME (skipped) and two A records with name pointers.
var dnsQuery = LinkDns.Query("example.com", 0x1234);
Check("link dns: query encodes the name", dnsQuery.Length == 29 && dnsQuery[12] == 7 && dnsQuery[20] == 3 && dnsQuery[24] == 0);
byte[] dnsReply = [.. dnsQuery[..2], 0x81, 0x80, 0, 1, 0, 3, 0, 0, 0, 0, .. dnsQuery[12..],
    0xC0, 0x0C, 0, 5, 0, 1, 0, 0, 0, 30, 0, 2, 0xC0, 0x0C,
    0xC0, 0x0C, 0, 1, 0, 1, 0, 0, 1, 0, 0, 4, 93, 184, 216, 34,
    0xC0, 0x0C, 0, 1, 0, 1, 0, 0, 0, 90, 0, 4, 1, 2, 3, 4];
var (dnsAddresses, dnsTtl) = LinkDns.Parse(dnsReply, 0x1234);
Check("link dns: A records and shortest TTL, CNAME skipped", dnsAddresses.Select(a => a.ToString()).SequenceEqual(["93.184.216.34", "1.2.3.4"]) && dnsTtl == 90);
Check("link dns: another id is ignored", LinkDns.Parse(dnsReply, 0x9999).Addresses.Length == 0);
Check("power plan: plugged-in value read", NetTuning.ParsePowerIndex("Current AC Power Setting Index: 0x00000003\nCurrent DC Power Setting Index: 0x00000002") == 3 && NetTuning.ParsePowerIndex("") is null);
Check("tcp tuning: only disabled or restricted counts as limited", NetTuning.AutoTuningLimited("Disabled") && NetTuning.AutoTuningLimited("Restricted") && !NetTuning.AutoTuningLimited("Normal") && !NetTuning.AutoTuningLimited(null));
var fastLink = new BondLink { Name = "fast" };
var slowLink = new BondLink { Name = "slow" };
Interlocked.Add(ref fastLink.ByteCount, 10_000_000); fastLink.Sample(1);
Interlocked.Add(ref slowLink.ByteCount, 1_000_000); slowLink.Sample(1);
BondLink.Rebalance([fastLink, slowLink]);
Check($"auto-balance: shares follow measured speed ({fastLink.Share:0.##} vs {slowLink.Share:0.##})", fastLink.Share == 1 && slowLink.Share == 0.2);
BondLink.AutoBalance = false; BondLink.Rebalance([fastLink, slowLink]); BondLink.AutoBalance = true;
Check("auto-balance off: weight only", fastLink.Share == 1 && slowLink.Share == 1);
var tail = new FetchJob { Url = "http://x/tail", Ranged = true, Total = 4 << 20, Segments = [new FetchSegment { Pos = (4 << 20) - (1536 << 10), End = 4 << 20, Busy = true }] };
var normalSplit = tail.Take(1 << 20);
var endgame = tail.Take(128 << 10);
Check("endgame: a 1.5 MB tail isn't split normally, but is in endgame", normalSplit is null && endgame is not null && endgame.End == 4 << 20 && tail.Segments[0].End == endgame.Pos);
Check("KytyPS5 starts games with --game", RomLibrary.Arguments("KytyPS5", "D:\\g\\x.zar") == "--game \"D:\\g\\x.zar\"");
Check("launch args", RomLibrary.Arguments("Dolphin", @"C:\g\a.iso") == "-b -e \"C:\\g\\a.iso\"" && RomLibrary.Arguments("Unknown", "x") == "\"x\"");
Check("every console tile has emulators, all in the catalog",
    AppHub.Consoles.All(c => c.Emulators.Length > 0) && AppHub.Consoles.SelectMany(c => c.Emulators).All(n => AppHub.Emulators.Any(e => e.Name == n)));
Check("every launchable emulator exists in the catalog", RomLibrary.LaunchArgs.Keys.All(n => AppHub.Emulators.Any(e => e.Name == n)));

Console.WriteLine("12) updater: release picking, checksum, file swap and rollback");
var rel = """
{"tag_name":"v2.5.0","html_url":"https://gh/rel","body":"## New\n- **Games** tab","assets":[
 {"name":"SteamStopper.msi","browser_download_url":"https://gh/msi"},
 {"name":"SteamStopper-portable.zip","browser_download_url":"https://gh/portable"},
 {"name":"SteamStopper.zip","browser_download_url":"https://gh/app"},
 {"name":"SteamStopper.zip.sha256","browser_download_url":"https://gh/app.sha"}]}
""";
var up = Updater.Pick(rel, new Version(2, 4, 0), installed: true);
Check("installed copy takes app zip and its checksum", up?.ZipUrl == "https://gh/app" && up.ShaUrl == "https://gh/app.sha" && up.Version == new Version(2, 5, 0));
var upPortable = Updater.Pick(rel, new Version(2, 4, 0), installed: false);
Check("portable copy takes portable zip, no checksum listed", upPortable?.ZipUrl == "https://gh/portable" && upPortable.ShaUrl is null);
Check("same or older version is no update", Updater.Pick(rel, new Version(2, 5, 0), true) is null && Updater.Pick(rel, new Version(3, 0, 0), true) is null);

var sumFile = Path.Combine(dir, "sum.bin");
File.WriteAllText(sumFile, "hello");
Check("checksum: matches 'hash  name', rejects other",
    Updater.Verify(sumFile, "2CF24DBA5FB0A30E26E83B2AC5B9E29E1B161E5C1FA7425E73043362938B9824  sum.bin")
    && !Updater.Verify(sumFile, "00f24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824"));

var src = Path.Combine(dir, "new"); var dst = Path.Combine(dir, "app");
Directory.CreateDirectory(Path.Combine(src, "runtimes")); Directory.CreateDirectory(dst);
File.WriteAllText(Path.Combine(src, "SteamStopper.exe"), "new exe");
File.WriteAllText(Path.Combine(src, "README.txt"), "new readme");
File.WriteAllText(Path.Combine(src, "runtimes", "x.dll"), "new dll");
File.WriteAllText(Path.Combine(dst, "SteamStopper.exe"), "old exe");
File.WriteAllText(Path.Combine(dst, "README.txt"), "the user's own readme");
File.WriteAllText(Path.Combine(dst, "settings-ish.txt"), "keep me");
var err = Updater.Install(999999, src, dst, start: false);
Check($"swap: new files in, user files kept, backup cleared (err={err})",
    err is null && File.ReadAllText(Path.Combine(dst, "SteamStopper.exe")) == "new exe"
    && File.ReadAllText(Path.Combine(dst, "runtimes", "x.dll")) == "new dll"
    && File.ReadAllText(Path.Combine(dst, "README.txt")) == "the user's own readme"
    && File.Exists(Path.Combine(dst, "settings-ish.txt")) && !Directory.Exists(Path.Combine(dst, ".update-backup")));

// A target that can't be written (a folder where a file should go) must roll everything back.
File.WriteAllText(Path.Combine(src, "SteamStopper.exe"), "newer exe");
Directory.CreateDirectory(Path.Combine(src, "zz")); File.WriteAllText(Path.Combine(src, "zz", "blocked.dll"), "x");
Directory.CreateDirectory(Path.Combine(dst, "zz", "blocked.dll"));
File.WriteAllText(Path.Combine(src, "runtimes", "added.dll"), "added");
err = Updater.Install(999999, src, dst, start: false);
Check($"rollback: failure restores old files and removes added ones (err={err})",
    err is not null && File.ReadAllText(Path.Combine(dst, "SteamStopper.exe")) == "new exe"
    && !File.Exists(Path.Combine(dst, "runtimes", "added.dll")));

Console.WriteLine("13) Steam shortcuts: binary VDF, keep user's own, replace ours, box art");
Check("crc32 check value", SteamShortcuts.Crc32(System.Text.Encoding.ASCII.GetBytes("123456789")) == 0xCBF43926);
var steam = Path.Combine(dir, "steam");
Directory.CreateDirectory(Path.Combine(steam, "config"));
Directory.CreateDirectory(Path.Combine(steam, "userdata", "12345", "config"));
File.WriteAllText(Path.Combine(steam, "config", "loginusers.vdf"),
    "\"users\"\n{\n\t\"76561197960278073\"\n\t{\n\t\t\"AccountName\"\t\t\"me\"\n\t\t\"MostRecent\"\t\t\"1\"\n\t}\n}\n");
var cfg = SteamShortcuts.UserConfig(steam);
Check("user folder from loginusers MostRecent (account id = SteamID64 low bits)", cfg == Path.Combine(steam, "userdata", "12345", "config"));
// A file as Steam writes it, with one shortcut of the user's own and an unusual uint64 field.
var mine = new Dictionary<string, object> { ["shortcuts"] = new Dictionary<string, object> { ["0"] = new Dictionary<string, object>
    { ["appid"] = -123, ["AppName"] = "My Tool", ["Exe"] = "\"C:\\tool.exe\"", ["LastPlayTime"] = 5, ["big"] = 9UL,
      ["tags"] = new Dictionary<string, object> { ["0"] = "Utilities" } } } };
var bytes = SteamShortcuts.Write(mine);
Check("binary layout ends with two end markers", bytes[^1] == 8 && bytes[^2] == 8 && bytes[0] == 0);
File.WriteAllBytes(Path.Combine(cfg!, "shortcuts.vdf"), bytes);
var cover = Path.Combine(dir, "cover.png"); File.WriteAllText(cover, "png");
SteamShortcuts.Sync(cfg!, [new SteamGame("Wii Sports", @"C:\emu\Dolphin.exe", "-b -e \"C:\\g\\ws.iso\"", cover, "Nintendo Wii"),
                           new SteamGame("Mario Kart 64", @"C:\emu\ares.exe", "\"C:\\g\\mk.z64\"", null, "Nintendo 64")]);
SteamShortcuts.Sync(cfg!, [new SteamGame("Wii Sports", @"C:\emu\Dolphin.exe", "-b -e \"C:\\g\\ws.iso\"", cover, "Nintendo Wii")]);
var back = (Dictionary<string, object>)SteamShortcuts.Read(File.ReadAllBytes(Path.Combine(cfg!, "shortcuts.vdf")))["shortcuts"];
var entries = back.Values.Cast<Dictionary<string, object>>().ToList();
Check($"user's shortcut kept intact, ours replaced not duplicated (got {entries.Count})",
    entries.Count == 2 && entries[0]["AppName"] as string == "My Tool" && (ulong)entries[0]["big"] == 9UL && (int)entries[0]["appid"] == -123
    && entries[1]["AppName"] as string == "Wii Sports" && entries[1]["Exe"] as string == "\"C:\\emu\\Dolphin.exe\""
    && entries[1]["StartDir"] as string == $"\"{Path.GetDirectoryName(@"C:\emu\Dolphin.exe")}\"" && back.Keys.SequenceEqual(["0", "1"]));
var id = SteamShortcuts.AppId("\"C:\\emu\\Dolphin.exe\"", "Wii Sports");
Check("appid has the top bit set and matches the stored int; box art named <id>p.png",
    (id & 0x80000000) != 0 && unchecked((uint)(int)entries[1]["appid"]) == id && File.Exists(Path.Combine(cfg!, "grid", $"{id}p.png")));
Check("backup of the previous file kept", File.Exists(Path.Combine(cfg!, "shortcuts.vdf.bak")));

Directory.Delete(dir, true);
// The kernel network trace only exists on Windows (CI runs there, as administrator).
if (OperatingSystem.IsWindows())
{
    using var net = new NetMonitor();
    var netError = net.Start();
    Check($"network monitor starts ({netError ?? "ok"})", netError is null);
    if (netError is null)
    {
        using var web = new HttpClient();
        var got = (await web.GetByteArrayAsync("https://github.com/")).Length;
        await Task.Delay(3000);
        var netMine = net.Snapshot().GetValueOrDefault(Environment.ProcessId);
        Check($"network monitor counts this process ({netMine.Received} of {got} bytes received, {netMine.Sent} sent)", netMine.Received > got / 2 && netMine.Sent > 0);
        net.Stop();
        Check("network monitor stops", !net.Running);
    }
    var live = await LinkDns.Resolve("github.com", IPAddress.Parse("8.8.8.8"), IPAddress.Any, CancellationToken.None);
    Check($"link dns: live lookup through a chosen server ({string.Join(", ", live.Select(a => a.ToString()))})", live.Length > 0);
}

Console.WriteLine(fails == 0 ? "ALL PASS" : $"{fails} FAILED");
return fails;
