using System.Runtime.InteropServices;

namespace NotchPC.Platform.Games;

/// <summary>
/// Counts frames per process from Windows' own graphics events (ETW), like PresentMon does: nothing is injected
/// into the game, so anti-cheats have nothing to see. A real-time ETW session needs no administrator rights for
/// members of "Performance Log Users" (checked on this PC). Only the per-frame events are enabled (event-id filter),
/// so the session costs a few ms of CPU per second at most. Runs only while the game overlay is visible.
/// </summary>
/// <remarks>
/// Frame = DXGI Present_Start (id 42; DirectX 10–12, also Vulkan/OpenGL games presenting through DXGI), D3D9
/// Present_Start (id 1) for DirectX 9, otherwise DxgKrnl PresentHistoryDetailed_Start (id 215), which every
/// present that reaches the kernel produces (fallback for other Vulkan/OpenGL paths: <b>do weryfikacji</b>).
/// </remarks>
public sealed unsafe partial class FpsMeter : IDisposable
{
    private const string SessionName = "NotchPC-FPS";
    private const uint ErrorAlreadyExists = 183;
    private const uint ControlStop = 1;
    private const ulong InvalidHandle = ulong.MaxValue;

    private static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private static readonly Guid DxgKrnl = new("802EC45A-1E99-4B83-9920-87C98277BA9D");
    private static FpsMeter? _active;

    private readonly Lock _gate = new();
    private Dictionary<uint, Counts> _counts = [];
    private long _windowStart;
    private ulong _session;
    private ulong _trace = InvalidHandle;
    private Thread? _thread;
    private Task _stopping = Task.CompletedTask;

    /// <summary>Why the meter could not start (Win32 error), or null when it runs / never tried.</summary>
    public uint? LastError { get; private set; }

    public bool IsRunning => _thread is not null;

    /// <summary>Starts the session. False when Windows refused it (e.g. not in "Performance Log Users").</summary>
    public bool Start()
    {
        if (IsRunning)
        {
            return true;
        }

        if (_active is not null)
        {
            throw new InvalidOperationException("Only one FpsMeter can run.");
        }

        // A stop that is still closing the previous session (rare: the owner waits a while before stopping).
        _stopping.Wait(2000);

        var rc = StartSession(out _session);
        if (rc == ErrorAlreadyExists)
        {
            StopSession(0); // left over from a crash
            rc = StartSession(out _session);
        }

        if (rc != 0)
        {
            LastError = rc;
            return false;
        }

        Enable(Dxgi, 42);
        Enable(D3D9, 1);
        Enable(DxgKrnl, 215);

        var log = default(EventTraceLogfile);
        var name = Marshal.StringToHGlobalUni(SessionName);
        try
        {
            log.LoggerName = name;
            log.ProcessTraceMode = 0x100 | 0x10000000; // real time | EVENT_RECORD callback
            log.EventRecordCallback = (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnEvent;
            _trace = OpenTraceW(&log);
        }
        finally
        {
            Marshal.FreeHGlobal(name);
        }

        if (_trace == InvalidHandle)
        {
            LastError = (uint)Marshal.GetLastPInvokeError();
            StopSession(_session);
            return false;
        }

        lock (_gate)
        {
            _counts = [];
            _windowStart = Environment.TickCount64;
        }

        _active = this;
        LastError = null;
        var trace = _trace;
        _thread = new Thread(() => Pump(trace))
        {
            IsBackground = true,
            Name = "NotchPC FPS (ETW)",
        };
        _thread.Start();
        return true;
    }

    /// <summary>Blocks on the ETW thread until the trace is closed.</summary>
    private static void Pump(ulong trace) => _ = ProcessTrace(&trace, 1, IntPtr.Zero, IntPtr.Zero);

    /// <summary>Stops counting at once; the session is closed on a worker thread (it can take up to a second).</summary>
    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        var (session, trace, thread) = (_session, _trace, _thread);
        _thread = null;
        _trace = InvalidHandle;
        if (ReferenceEquals(_active, this))
        {
            _active = null;
        }

        _stopping = Task.Run(() =>
        {
            StopSession(session);
            _ = CloseTrace(trace);
            thread?.Join(1000);
        });
    }

    /// <summary>
    /// Frames per second of every process seen since the last call (call about once a second).
    /// </summary>
    public IReadOnlyDictionary<uint, double> TakeRates()
    {
        Dictionary<uint, Counts> counts;
        long start;
        lock (_gate)
        {
            counts = _counts;
            start = _windowStart;
            _counts = [];
            _windowStart = Environment.TickCount64;
        }

        var seconds = Math.Max(0.25, (Environment.TickCount64 - start) / 1000.0);
        return counts.ToDictionary(c => c.Key, c => c.Value.Frames / seconds);
    }

    public void Dispose()
    {
        Stop();
        _stopping.Wait(2000); // on exit: do not leave the ETW session behind
    }

    private static uint StartSession(out ulong session)
    {
        var buffer = NewProperties(out var props);
        try
        {
            fixed (ulong* handle = &session)
            {
                return StartTraceW(handle, SessionName, props);
            }
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    private static void StopSession(ulong session)
    {
        var buffer = NewProperties(out var props);
        try
        {
            _ = ControlTraceW(session, session == 0 ? SessionName : null, props, ControlStop);
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    private static void* NewProperties(out EventTraceProperties* props)
    {
        var size = sizeof(EventTraceProperties) + ((SessionName.Length + 1) * 2);
        var buffer = NativeMemory.AllocZeroed((nuint)size);
        props = (EventTraceProperties*)buffer;
        props->BufferSize = (uint)size;
        props->Flags = 0x20000;        // WNODE_FLAG_TRACED_GUID
        props->ClientContext = 1;      // QPC timestamps
        props->LogFileMode = 0x100;    // EVENT_TRACE_REAL_TIME_MODE
        props->BufferSizeKb = 64;
        props->FlushTimer = 1;         // deliver at least once a second
        props->LoggerNameOffset = (uint)sizeof(EventTraceProperties);
        return buffer;
    }

    /// <summary>Enables one provider for the given event ids only (EVENT_FILTER_TYPE_EVENT_ID).</summary>
    private void Enable(Guid provider, ushort eventId)
    {
        var filter = stackalloc byte[6];
        filter[0] = 1;                     // FilterIn
        filter[1] = 0;
        *(ushort*)(filter + 2) = 1;        // count
        *(ushort*)(filter + 4) = eventId;

        var descriptor = default(EventFilterDescriptor);
        descriptor.Ptr = (ulong)filter;
        descriptor.Size = 6;
        descriptor.Type = 0x80000200;      // EVENT_FILTER_TYPE_EVENT_ID

        var parameters = default(EnableTraceParameters);
        parameters.Version = 2;
        parameters.EnableFilterDesc = &descriptor;
        parameters.FilterDescCount = 1;

        _ = EnableTraceEx2(_session, &provider, 1, 4, 0, 0, 0, &parameters);
    }

    private void Count(uint pid, Guid provider)
    {
        lock (_gate)
        {
            _counts.TryGetValue(pid, out var c);
            if (provider == Dxgi)
            {
                c.Dxgi++;
            }
            else if (provider == D3D9)
            {
                c.D3D9++;
            }
            else
            {
                c.Kernel++;
            }

            _counts[pid] = c;
        }
    }

    [UnmanagedCallersOnly]
    private static void OnEvent(IntPtr record)
    {
        // EVENT_RECORD → EVENT_HEADER: ProcessId at 12, ProviderId at 24.
        var p = (byte*)record;
        _active?.Count(*(uint*)(p + 12), *(Guid*)(p + 24));
    }

    private struct Counts
    {
        public int Dxgi;
        public int D3D9;
        public int Kernel;

        /// <summary>The API-level count when there is one (exact), otherwise the kernel's.</summary>
        public readonly int Frames => Dxgi > 0 ? Dxgi : D3D9 > 0 ? D3D9 : Kernel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTraceProperties
    {
        public uint BufferSize;
        public uint ProviderId;
        public ulong HistoricalContext;
        public long TimeStamp;
        public Guid Guid;
        public uint ClientContext;
        public uint Flags;
        public uint BufferSizeKb;
        public uint MinimumBuffers;
        public uint MaximumBuffers;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint FlushTimer;
        public uint EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers;
        public uint FreeBuffers;
        public uint EventsLost;
        public uint BuffersWritten;
        public uint LogBuffersLost;
        public uint RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset;
        public uint LoggerNameOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTraceLogfile
    {
        public IntPtr LogFileName;
        public IntPtr LoggerName;
        public long CurrentTime;
        public uint BuffersRead;
        public uint ProcessTraceMode;
        public fixed byte CurrentEvent[88];   // EVENT_TRACE
        public fixed byte LogfileHeader[280]; // TRACE_LOGFILE_HEADER
        public IntPtr BufferCallback;
        public uint BufferSize;
        public uint Filled;
        public uint EventsLost;
        public IntPtr EventRecordCallback;
        public uint IsKernelTrace;
        public IntPtr Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventFilterDescriptor
    {
        public ulong Ptr;
        public uint Size;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EnableTraceParameters
    {
        public uint Version;
        public uint EnableProperty;
        public uint ControlFlags;
        public Guid SourceId;
        public EventFilterDescriptor* EnableFilterDesc;
        public uint FilterDescCount;
    }

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint StartTraceW(ulong* handle, string name, EventTraceProperties* properties);

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint ControlTraceW(ulong handle, string? name, EventTraceProperties* properties, uint code);

    [LibraryImport("advapi32.dll")]
    private static partial uint EnableTraceEx2(ulong handle, Guid* provider, uint control, byte level, ulong matchAny, ulong matchAll, uint timeout, EnableTraceParameters* parameters);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial ulong OpenTraceW(EventTraceLogfile* logfile);

    [LibraryImport("advapi32.dll")]
    private static partial uint ProcessTrace(ulong* handles, uint count, IntPtr start, IntPtr end);

    [LibraryImport("advapi32.dll")]
    private static partial uint CloseTrace(ulong handle);
}
