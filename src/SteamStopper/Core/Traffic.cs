using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace SteamStopper.Core;

public static class Traffic
{
    public static readonly (string Key, string Label, string[] Processes)[] Apps =
    [
        ("steam", "Steam", ["steam", "steamwebhelper"]),
        ("chrome", "Chrome", ["chrome"]),
        ("msedge", "Edge", ["msedge"]),
        ("firefox", "Firefox", ["firefox"]),
        ("pc", "This PC (all internet)", [])
    ];

    public static readonly (string Label, double Bps)[] Floors =
    [
        ("100 KB/s", 100 * 1024),
        ("200 KB/s", 200 * 1024),
        ("500 KB/s", 500 * 1024),
        ("1 MB/s", 1024 * 1024)
    ];

    private const uint ProcessQueryLimited = 0x1000;
    private const int IfTypeLoopback = 24;
    private const int ErrorInsufficientBuffer = 122;

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MibIfRow
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string wszName;
        public uint dwIndex;
        public uint dwType;
        public uint dwMtu;
        public uint dwSpeed;
        public uint dwPhysAddrLen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] bPhysAddr;
        public uint dwAdminStatus;
        public uint dwOperStatus;
        public uint dwLastChange;
        public uint dwInOctets;
        public uint dwInUcastPkts;
        public uint dwInNUcastPkts;
        public uint dwInDiscards;
        public uint dwInErrors;
        public uint dwInUnknownProtos;
        public uint dwOutOctets;
        public uint dwOutUcastPkts;
        public uint dwOutNUcastPkts;
        public uint dwOutDiscards;
        public uint dwOutErrors;
        public uint dwOutQLen;
        public uint dwDescrLen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public byte[] bDescr;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr handle, out IoCounters counters);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetIfTable(IntPtr table, ref int size, bool order);

    public static List<(string Id, long Bytes)> AdapterReceiveBytes()
    {
        var rows = new List<(string Id, long Bytes)>();
        rows.AddRange(IfTableRows());
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    long ipv4 = 0, all = 0;
                    try { ipv4 = nic.GetIPv4Statistics().BytesReceived; } catch { }
                    try { all = nic.GetIPStatistics().BytesReceived; } catch { }
                    var bytes = Math.Max(ipv4, all);
                    if (bytes > 0)
                        rows.Add(("nic:" + nic.Id, bytes));
                }
                catch { }
            }
        }
        catch { }
        return rows;
    }

    public static long ProcessIoBytes(params string[] names)
    {
        long reads = 0, writes = 0, others = 0;
        foreach (var name in names)
        {
            Process[] list;
            try { list = Process.GetProcessesByName(name); }
            catch { continue; }
            foreach (var process in list)
            {
                var handle = IntPtr.Zero;
                try
                {
                    handle = OpenProcess(ProcessQueryLimited, false, process.Id);
                    if (handle == IntPtr.Zero) continue;
                    if (!GetProcessIoCounters(handle, out var io)) continue;
                    reads += (long)io.ReadTransferCount;
                    writes += (long)io.WriteTransferCount;
                    others += (long)io.OtherTransferCount;
                }
                catch { }
                finally
                {
                    if (handle != IntPtr.Zero) CloseHandle(handle);
                    process.Dispose();
                }
            }
        }
        return Math.Max(reads, Math.Max(writes, others));
    }

    private static List<(string Id, long Bytes)> IfTableRows()
    {
        var rows = new List<(string Id, long Bytes)>();
        var size = 0;
        var status = GetIfTable(IntPtr.Zero, ref size, false);
        if (status != ErrorInsufficientBuffer || size <= 4)
            return rows;

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            status = GetIfTable(buffer, ref size, false);
            if (status != 0) return rows;

            var count = Math.Min(256, Marshal.ReadInt32(buffer));
            var rowSize = Marshal.SizeOf<MibIfRow>();
            var ptr = IntPtr.Add(buffer, 4);
            for (var i = 0; i < count; i++)
            {
                MibIfRow row;
                try { row = Marshal.PtrToStructure<MibIfRow>(ptr)!; }
                catch { break; }
                ptr = IntPtr.Add(ptr, rowSize);
                if (row.dwType == IfTypeLoopback) continue;
                if (row.dwOperStatus < 4) continue;
                rows.Add(("if:" + row.dwIndex, row.dwInOctets));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return rows;
    }
}

public sealed class NicMonitor
{
    private readonly Dictionary<string, (long Bytes, long Ticks)> _last = [];

    public double ReceiveBps()
    {
        double best = 0;
        var now = Stopwatch.GetTimestamp();
        foreach (var (id, bytes) in Traffic.AdapterReceiveBytes())
        {
            if (_last.TryGetValue(id, out var prev) && now > prev.Ticks && bytes >= prev.Bytes)
            {
                var seconds = Stopwatch.GetElapsedTime(prev.Ticks, now).TotalSeconds;
                if (seconds >= 0.3)
                {
                    var bps = (bytes - prev.Bytes) / seconds;
                    if (bps is > 0 and < 8_000_000_000 && bps > best)
                        best = bps;
                }
            }
            _last[id] = (bytes, now);
        }
        return best;
    }
}

public sealed class TrafficSampler
{
    private readonly Dictionary<string, (long Bytes, long Ticks)> _last = [];

    public double Bps(string key, long bytesNow)
    {
        var now = Stopwatch.GetTimestamp();
        double bps = 0;
        if (_last.TryGetValue(key, out var prev) && now > prev.Ticks && bytesNow >= prev.Bytes)
        {
            var seconds = Stopwatch.GetElapsedTime(prev.Ticks, now).TotalSeconds;
            if (seconds >= 0.4)
                bps = (bytesNow - prev.Bytes) / seconds;
        }
        _last[key] = (bytesNow, now);
        return bps;
    }
}

public sealed class SpeedWatch
{
    private long? _belowSince;

    public double ThresholdBps { get; set; } = 200 * 1024;
    public double HoldSeconds { get; set; } = 45;
    public bool RequireBurst { get; set; } = true;
    public bool SeenFast { get; private set; }
    public double LastBps { get; private set; }
    public double BelowSeconds { get; private set; }

    public void Reset()
    {
        _belowSince = null;
        SeenFast = false;
        BelowSeconds = 0;
    }

    public bool IsFinished(double bps)
    {
        LastBps = bps;
        if (bps >= ThresholdBps)
        {
            SeenFast = true;
            _belowSince = null;
            BelowSeconds = 0;
            return false;
        }
        if (RequireBurst && !SeenFast)
        {
            _belowSince = null;
            BelowSeconds = 0;
            return false;
        }
        _belowSince ??= Stopwatch.GetTimestamp();
        BelowSeconds = Stopwatch.GetElapsedTime(_belowSince.Value, Stopwatch.GetTimestamp()).TotalSeconds;
        return BelowSeconds >= HoldSeconds;
    }
}
