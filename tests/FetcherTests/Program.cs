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
                if (ctx.Request.Url!.AbsolutePath == "/signed")
                {
                    // Like GitHub release downloads: a redirect to a link that only works for a short while.
                    res.Redirect($"http://127.0.0.1:{port}/blob?t={DateTime.UtcNow.Ticks}"); res.Close(); return;
                }
                if (ctx.Request.Url!.AbsolutePath == "/blob" && DateTime.UtcNow.Ticks - long.Parse(ctx.Request.QueryString["t"]!) > TimeSpan.FromSeconds(1).Ticks)
                {
                    res.StatusCode = 403; res.Close(); return;
                }
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
var pick = EmuStore.PickGitHub(gh, pcsx2);
Check("github: skips draft, symbols, installer, arm64", pick?.Url == "https://x/good.7z" && pick.Version == "v2.4.1");
Check("github: second pattern used when first misses",
    EmuStore.PickGitHub(gh, pcsx2 with { Assets = [@"nomatch", @"x64-Qt\.7z$"] })?.Url == "https://x/good.7z");
Check("github: nothing matches gives null", EmuStore.PickGitHub(gh, pcsx2 with { Assets = [@"\.dmg$"] }) is null);
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
Check("titles drop region and dump tags", RomLibrary.TitleOf("Super Mario 64 (USA) [!].z64") == "Super Mario 64" && RomLibrary.TitleOf("Zelda_TOTK.nsp") == "Zelda TOTK");
var store = new RomLibrary.Store { Folders = [roms] };
var found = RomLibrary.Scan(store);
Check("PS5 folder dump: titled from param.json; PS4 folder and its eboot not added",
    store.Games.Any(g => g.Path == astro && g.Title == "ASTRO BOT" && g.Console == "PS5")
    && !store.Games.Any(g => g.Path.StartsWith(ps4)) && !store.Games.Any(g => g.Path.EndsWith("eboot.bin")));
Check(".zar is PS5", store.Games.Any(g => g.Title == "Demon's Souls" && g.Console == "PS5"));
Check($"scan finds 13 games, second scan finds none (got {found})", found == 13 && RomLibrary.Scan(store) == 0);
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

Directory.Delete(dir, true);
Console.WriteLine(fails == 0 ? "ALL PASS" : $"{fails} FAILED");
return fails;
