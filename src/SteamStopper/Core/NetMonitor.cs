using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace SteamStopper.Core;

/// <summary>
/// Bytes each process sends and receives over the network, from the Windows kernel's TCP/IP and UDP
/// events (ETW). It is what Resource Monitor shows; Windows has no simpler per-app counter. Needs admin.
/// </summary>
public sealed class NetMonitor : IDisposable
{
    // shortcut: a private "system logger" session (Windows 8+), so it never fights another tool over the one
    // NT Kernel Logger, and one left behind by a crash is ours to stop by name.
    private const string SessionName = "SteamStopperNet";
    private const uint EventTraceRealTimeMode = 0x100;
    private const uint EventTraceSystemLoggerMode = 0x02000000;
    private const uint EnableFlagNetworkTcpIp = 0x00010000;
    private const uint WnodeFlagTracedGuid = 0x00020000;
    private const uint ProcessTraceModeRealTime = 0x100;
    private const uint ProcessTraceModeEventRecord = 0x10000000;
    private const uint ControlStop = 1;
    private const int ErrorAlreadyExists = 183;
    private const int PropertiesSize = 120;
    private const int LogFileSize = 448;
    private static readonly long InvalidHandle = unchecked((long)ulong.MaxValue);
    private static readonly Guid TcpIp = new("9a280ac0-c8e0-11d1-84e2-00c04fb998a2");
    private static readonly Guid UdpIp = new("bf3a50c5-a9c9-4988-a005-2df0b7c80f80");

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void RecordCallback(IntPtr record);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int StartTraceW(out long handle, string name, IntPtr properties);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int ControlTraceW(long handle, string name, IntPtr properties, uint code);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern long OpenTraceW(IntPtr logfile);
    [DllImport("advapi32.dll")]
    private static extern int ProcessTrace(long[] handles, uint count, IntPtr start, IntPtr end);
    [DllImport("advapi32.dll")]
    private static extern int CloseTrace(long handle);

    private readonly ConcurrentDictionary<int, long[]> _bytes = new();
    private readonly RecordCallback _callback;
    private long _trace = InvalidHandle;
    private Thread? _thread;
    private IntPtr _nameBuffer;
    private IntPtr _logFile;

    public NetMonitor() => _callback = OnRecord;

    public bool Running => _thread is { IsAlive: true };

    /// <summary>Received and sent bytes per process id since Start.</summary>
    public Dictionary<int, (long Received, long Sent)> Snapshot() =>
        _bytes.ToDictionary(p => p.Key, p => (Interlocked.Read(ref p.Value[0]), Interlocked.Read(ref p.Value[1])));

    /// <summary>Start listening. Returns null when it runs, else why it couldn't.</summary>
    public string? Start()
    {
        if (Running) return null;
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 2)) return "Needs Windows 8 or newer.";
        StopSession();
        var properties = Properties();
        try
        {
            var error = StartTraceW(out _, SessionName, properties);
            if (error == ErrorAlreadyExists)
            {
                StopSession();
                error = StartTraceW(out _, SessionName, properties);
            }
            if (error != 0) return error == 5 ? "Needs Steam Stopper to run as administrator." : $"Windows refused the network trace (error {error}).";
        }
        finally { Marshal.FreeHGlobal(properties); }

        _nameBuffer = Marshal.StringToHGlobalUni(SessionName);
        _logFile = Marshal.AllocHGlobal(LogFileSize);
        for (var i = 0; i < LogFileSize; i++) Marshal.WriteByte(_logFile, i, 0);
        Marshal.WriteIntPtr(_logFile, 8, _nameBuffer);                                        // LoggerName
        Marshal.WriteInt32(_logFile, 28, unchecked((int)(ProcessTraceModeRealTime | ProcessTraceModeEventRecord)));
        Marshal.WriteIntPtr(_logFile, 424, Marshal.GetFunctionPointerForDelegate(_callback)); // EventRecordCallback
        _trace = OpenTraceW(_logFile);
        if (_trace == InvalidHandle)
        {
            StopSession();
            return $"Windows refused the network trace (error {Marshal.GetLastWin32Error()}).";
        }
        // ProcessTrace blocks until the session stops, delivering events on this thread.
        _thread = new Thread(() => ProcessTrace([_trace], 1, IntPtr.Zero, IntPtr.Zero)) { IsBackground = true, Name = "NetMonitor" };
        _thread.Start();
        return null;
    }

    public void Stop()
    {
        StopSession();
        if (_trace != InvalidHandle) CloseTrace(_trace);
        _trace = InvalidHandle;
        _thread?.Join(2000);
        _thread = null;
        if (_logFile != IntPtr.Zero) Marshal.FreeHGlobal(_logFile);
        if (_nameBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_nameBuffer);
        _logFile = _nameBuffer = IntPtr.Zero;
    }

    public void Dispose() => Stop();

    private static void StopSession()
    {
        var properties = Properties();
        try { ControlTraceW(0, SessionName, properties, ControlStop); }
        finally { Marshal.FreeHGlobal(properties); }
    }

    /// <summary>EVENT_TRACE_PROPERTIES for a real-time session with only the TCP/IP kernel events on.</summary>
    private static IntPtr Properties()
    {
        var size = PropertiesSize + (SessionName.Length + 1) * 2;
        var p = Marshal.AllocHGlobal(size);
        for (var i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
        Marshal.WriteInt32(p, 0, size);                          // Wnode.BufferSize
        Marshal.Copy(Guid.NewGuid().ToByteArray(), 0, p + 24, 16); // Wnode.Guid
        Marshal.WriteInt32(p, 40, 1);                            // Wnode.ClientContext: QPC clock
        Marshal.WriteInt32(p, 44, unchecked((int)WnodeFlagTracedGuid));
        Marshal.WriteInt32(p, 64, unchecked((int)(EventTraceRealTimeMode | EventTraceSystemLoggerMode))); // LogFileMode
        Marshal.WriteInt32(p, 68, 1);                            // FlushTimer: deliver every second
        Marshal.WriteInt32(p, 72, unchecked((int)EnableFlagNetworkTcpIp));
        Marshal.WriteInt32(p, 116, PropertiesSize);              // LoggerNameOffset
        return p;
    }

    /// <summary>One TCP or UDP send/receive event: the payload starts with the process id and the byte count.</summary>
    private void OnRecord(IntPtr record)
    {
        var provider = Marshal.PtrToStructure<Guid>(record + 24);
        if (provider != TcpIp && provider != UdpIp) return;
        var opcode = Marshal.ReadByte(record + 45);
        var received = opcode is 11 or 27;
        if (!received && opcode is not (10 or 26)) return;
        var data = Marshal.ReadIntPtr(record + 96);
        if (Marshal.ReadInt16(record + 86) < 16 || data == IntPtr.Zero) return;
        // IPv4 loopback (127.x on either end) is programs talking to each other, not the internet.
        if (opcode is 10 or 11 && (Marshal.ReadByte(data + 8) == 127 || Marshal.ReadByte(data + 12) == 127)) return;
        var pid = Marshal.ReadInt32(data);
        var size = Marshal.ReadInt32(data + 4);
        var counts = _bytes.GetOrAdd(pid, _ => new long[2]);
        Interlocked.Add(ref counts[received ? 0 : 1], size);
    }
}
