using System.IO;

namespace SteamStopper.Core;

/// <summary>Switch game files: split big ones for FAT32 SD cards, and where nsz converts .nsz/.xcz back.</summary>
public static class SwitchFiles
{
    // FAT32 can't hold a file of 4 GiB or more; this is the part size Switch homebrew installers expect.
    public const long PartSize = 0xFFFF0000;

    public static bool CanSplit(string path) =>
        Ext(path) is ".nsp" or ".xci" && File.Exists(path) && new FileInfo(path).Length > PartSize;

    /// <summary>
    /// NSP: a folder "&lt;name&gt;_split.nsp" holding 00, 01… with the archive bit set (how Atmosphère joins them).
    /// XCI: &lt;name&gt;.xc0, .xc1… next to it. Returns what it made; a cancelled or failed split leaves nothing behind.
    /// </summary>
    public static async Task<string> Split(string file, IProgress<long> progress, CancellationToken cancel = default, long partSize = PartSize)
    {
        var dir = Path.GetDirectoryName(file)!;
        var name = Path.GetFileNameWithoutExtension(file);
        var nsp = Ext(file) == ".nsp";
        var output = nsp ? Path.Combine(dir, name + "_split.nsp") : Path.Combine(dir, name + ".xc0");
        if (Path.Exists(output)) throw new IOException($"{Path.GetFileName(output)} is already there. Delete it first.");
        var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(file))!).AvailableFreeSpace;
        var size = new FileInfo(file).Length;
        if (free < size) throw new IOException($"Not enough free space: needs {size / (1 << 20)} MB, {free / (1 << 20)} MB free.");

        var parts = new List<string>();
        try
        {
            if (nsp) Directory.CreateDirectory(output);
            await using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
            var buffer = new byte[4 << 20];
            long done = 0;
            for (var index = 0; done < size; index++)
            {
                var part = nsp ? Path.Combine(output, index.ToString("00")) : Path.Combine(dir, $"{name}.xc{index}");
                parts.Add(part);
                await using var target = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
                for (long left = Math.Min(partSize, size - done); left > 0;)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), cancel);
                    if (read == 0) throw new EndOfStreamException("The file got shorter while splitting.");
                    await target.WriteAsync(buffer.AsMemory(0, read), cancel);
                    left -= read;
                    done += read;
                    progress.Report(done);
                }
            }
            if (nsp) new DirectoryInfo(output).Attributes |= FileAttributes.Archive;
            return output;
        }
        catch
        {
            foreach (var part in parts) try { File.Delete(part); } catch { }
            if (nsp) try { Directory.Delete(output, true); } catch { }
            throw;
        }
    }

    public static bool CanConvert(string path) => Ext(path) is ".nsz" or ".xcz";

    /// <summary>What nsz -D writes for a .nsz or .xcz into a folder.</summary>
    public static string ConvertedPath(string file, string outDir) =>
        Path.Combine(outDir, Path.GetFileNameWithoutExtension(file) + (Ext(file) == ".nsz" ? ".nsp" : ".xci"));

    /// <summary>The user's Switch keys: where nsz looks itself, else where Switch emulators keep them.</summary>
    public static string? FindKeys(string? appData = null, string? home = null)
    {
        appData ??= Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[]
        {
            Path.Combine(home, ".switch", "prod.keys"), Path.Combine(home, ".switch", "keys.txt"),
            Path.Combine(appData, "eden", "keys", "prod.keys"), Path.Combine(appData, "Ryujinx", "system", "prod.keys"),
            Path.Combine(appData, "citron", "keys", "prod.keys"), Path.Combine(appData, "yuzu", "keys", "prod.keys")
        }.FirstOrDefault(File.Exists);
    }

    private static string Ext(string path) => Path.GetExtension(path).ToLowerInvariant();
}
