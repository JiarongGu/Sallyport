using System.Runtime.InteropServices;

namespace Sallyport.Observer;

internal static class EtwNative
{
    internal static readonly Guid Provider = new("7DD42A49-5329-4832-8DFD-43D979153A88");

    [StructLayout(LayoutKind.Sequential)]
    internal struct WnodeHeader
    {
        public uint BufferSize, ProviderId;
        public ulong HistoricalContext;
        public long Timestamp;
        public Guid Guid;
        public uint ClientContext, Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Properties
    {
        public WnodeHeader Wnode;
        public uint BufferSize, MinimumBuffers, MaximumBuffers, MaximumFileSize, LogFileMode, FlushTimer, EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers, FreeBuffers, EventsLost, BuffersWritten, LogBuffersLost, RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset, LoggerNameOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EventDescriptor
    {
        public ushort Id;
        public byte Version, Channel, Level, Opcode;
        public ushort Task;
        public ulong Keyword;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EventHeader
    {
        public ushort Size, HeaderType, Flags, EventProperty;
        public uint ThreadId, ProcessId;
        public long Timestamp;
        public Guid ProviderId;
        public EventDescriptor Descriptor;
        public ulong ProcessorTime;
        public Guid ActivityId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EventRecord
    {
        public EventHeader Header;
        public uint BufferContext;
        public ushort ExtendedDataCount, UserDataLength;
        public IntPtr ExtendedData, UserData, UserContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyDescriptor
    {
        // PROPERTY_DATA_DESCRIPTOR.PropertyName is ULONGLONG (8 bytes), even in a 32-bit consumer.
        public ulong PropertyName;
        public uint ArrayIndex, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LegacyEventHeader
    {
        public ushort Size, FieldTypeFlags;
        public uint Version, ThreadId, ProcessId;
        public long Timestamp;
        public Guid Guid;
        public ulong ProcessorTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LegacyEvent
    {
        public LegacyEventHeader Header;
        public uint InstanceId, ParentInstanceId;
        public Guid ParentGuid;
        public IntPtr MofData;
        public uint MofLength, ClientContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemTime
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct TimeZoneInformation
    {
        public int Bias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string StandardName;
        public SystemTime StandardDate;
        public int StandardBias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DaylightName;
        public SystemTime DaylightDate;
        public int DaylightBias;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct LogfileHeader
    {
        public uint BufferSize, Version, ProviderVersion, NumberOfProcessors;
        public long EndTime;
        public uint TimerResolution, MaximumFileSize, LogFileMode, BuffersWritten;
        // This 16-byte union overlays LogInstanceGuid with four ULONGs, including EventsLost.
        public uint StartBuffers, PointerSize, EventsLost, CpuSpeedInMHz;
        public IntPtr LoggerName, LogFileName;
        public TimeZoneInformation TimeZone;
        public long BootTime, PerfFreq, StartTime;
        public uint ReservedFlags, BuffersLost;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct Logfile
    {
        public IntPtr LogFileName, LoggerName;
        public long CurrentTime;
        public uint BuffersRead, ProcessTraceMode;
        public LegacyEvent CurrentEvent;
        public LogfileHeader Header;
        public IntPtr BufferCallback;
        public uint BufferSize, Filled, EventsLost;
        public IntPtr EventRecordCallback;
        public uint IsKernelTrace;
        public IntPtr Context;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void EventCallback(IntPtr record);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate uint BufferCallback(IntPtr logfile);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint StartTraceW(out ulong handle, string name, IntPtr properties);
    [DllImport("advapi32.dll")]
    internal static extern uint EnableTraceEx2(ulong handle, in Guid provider, uint controlCode, byte level,
        ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, IntPtr parameters);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    internal static extern ulong OpenTraceW(ref Logfile logfile);
    [DllImport("advapi32.dll")]
    internal static extern uint ProcessTrace([In] ulong[] handles, uint count, IntPtr startTime, IntPtr endTime);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint ControlTraceW(ulong handle, string? name, IntPtr properties, uint controlCode);
    [DllImport("advapi32.dll")]
    internal static extern uint CloseTrace(ulong handle);
    [DllImport("tdh.dll")]
    internal static extern uint TdhGetPropertySize(IntPtr record, uint contextCount, IntPtr context,
        uint propertyCount, in PropertyDescriptor descriptor, out uint size);
    [DllImport("tdh.dll")]
    internal static extern uint TdhGetProperty(IntPtr record, uint contextCount, IntPtr context,
        uint propertyCount, in PropertyDescriptor descriptor, uint size, [Out] byte[] buffer);
}
