using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;

namespace Sallyport.Observer;

internal sealed class EtwObserver : IDisposable
{
    private readonly ProcessTracker tracker;
    private readonly long deadlineFileTime;
    private readonly Channel<NetworkObservation> queue = Channel.CreateBounded<NetworkObservation>(new BoundedChannelOptions(8192)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly string sessionName = $"Sallyport-Observer-{Environment.ProcessId}-{Guid.NewGuid():N}";
    private readonly EtwNative.EventCallback eventCallback;
    private readonly EtwNative.BufferCallback bufferCallback;
    private readonly Guid sessionGuid = Guid.NewGuid();
    private IntPtr properties;
    private IntPtr loggerName;
    private ulong session;
    private ulong consumer = ulong.MaxValue;
    private Thread? thread;
    private bool threadStarted;
    private bool ownsSession;
    private bool disposed;
    private volatile bool stopping;
    private Exception? failure;
    private long queueDrops, schemaFailures, callbackEventsLost, callbackBuffersLost, bufferCallbacks;
    private uint eventsLost, logBuffersLost, realTimeBuffersLost;
    private bool statisticsAvailable;
    private string cleanup = "not-started";

    public EtwObserver(ProcessTracker tracker, long deadlineFileTime)
    {
        this.tracker = tracker;
        this.deadlineFileTime = deadlineFileTime;
        eventCallback = OnEvent;
        bufferCallback = OnBuffer;
    }

    public void Start()
    {
        bool ready = false;
        try
        {
            int headerSize = Marshal.SizeOf<EtwNative.Properties>();
            byte[] name = Encoding.Unicode.GetBytes(sessionName + '\0');
            int size = headerSize + 4096;
            properties = Marshal.AllocHGlobal(size);
            Marshal.Copy(new byte[size], 0, properties, size);
            var settings = new EtwNative.Properties
            {
                Wnode = new() { BufferSize = (uint)size, Guid = sessionGuid, ClientContext = 2, Flags = 0x20000 },
                BufferSize = 64, MinimumBuffers = 4, MaximumBuffers = 64,
                LogFileMode = 0x100, FlushTimer = 1, LoggerNameOffset = (uint)headerSize
            };
            Marshal.StructureToPtr(settings, properties, false);
            Marshal.Copy(name, 0, properties + headerSize, name.Length);
            Check(EtwNative.StartTraceW(out session, sessionName, properties), "StartTraceW");
            ownsSession = true;
            cleanup = "pending";
            Check(EtwNative.EnableTraceEx2(session, in EtwNative.Provider, 1, 5, 0x30, 0, 0, IntPtr.Zero), "EnableTraceEx2");
            loggerName = Marshal.StringToHGlobalUni(sessionName);
            var logfile = new EtwNative.Logfile
            {
                LoggerName = loggerName, ProcessTraceMode = 0x10000100,
                EventRecordCallback = Marshal.GetFunctionPointerForDelegate(eventCallback),
                BufferCallback = Marshal.GetFunctionPointerForDelegate(bufferCallback)
            };
            consumer = EtwNative.OpenTraceW(ref logfile);
            if (consumer == ulong.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenTraceW failed");
            ulong traceHandle = consumer;
            thread = new Thread(() => Consume(traceHandle)) { IsBackground = true, Name = "Sallyport observer ETW" };
            thread.Start();
            threadStarted = true;
            ready = true;
        }
        finally
        {
            if (!ready) Dispose();
        }
    }

    public bool TryRead(out NetworkObservation? observation) => queue.Reader.TryRead(out observation);
    public Exception? Failure => Volatile.Read(ref failure);

    private void Consume(ulong traceHandle)
    {
        try
        {
            uint result = EtwNative.ProcessTrace([traceHandle], 1, IntPtr.Zero, IntPtr.Zero);
            if (result is not (0 or 1223)) Check(result, "ProcessTrace");
            if (!stopping) throw new InvalidOperationException("ETW consumer ended before the learning deadline.");
        }
        catch (Exception error) { Interlocked.CompareExchange(ref failure, error, null); }
    }

    private void OnEvent(IntPtr pointer)
    {
        try
        {
            EtwNative.EventRecord record = Marshal.PtrToStructure<EtwNative.EventRecord>(pointer);
            if (record.Header.ProviderId != EtwNative.Provider || record.Header.Timestamp > deadlineFileTime) return;
            EtwNative.EventDescriptor descriptor = record.Header.Descriptor;
            EtwSchema? schema = EtwSchema.Find(descriptor.Id);
            if (schema is null)
            {
                if (descriptor.Opcode is 10 or 11 or 42 or 43) Interlocked.Increment(ref schemaFailures);
                return;
            }
            ulong familyKeyword = schema.AddressBytes == 4 ? 0x10UL : 0x20UL;
            // The id names the event in this provider's manifest; version 0 is the layout the properties were measured in.
            if (descriptor.Version != 0 || (descriptor.Keyword & 0x30) != familyKeyword)
            {
                Interlocked.Increment(ref schemaFailures);
                return;
            }
            byte[] pid = ReadProperty(pointer, "PID", 4);
            if (!tracker.KnowsPid(BinaryPrimitives.ReadUInt32LittleEndian(pid))) return;
            NetworkObservation observation = schema.Decode(pid, ReadProperty(pointer, schema.AddressProperty, schema.AddressBytes),
                ReadProperty(pointer, schema.PortProperty, 2), record.Header.Timestamp);
            if (observation.Address.Equals(System.Net.IPAddress.Any) || observation.Address.Equals(System.Net.IPAddress.IPv6Any)) return;
            if (!queue.Writer.TryWrite(observation)) Interlocked.Increment(ref queueDrops);
        }
        catch (Exception error) when (error is Win32Exception or InvalidDataException or ArgumentException)
        {
            Interlocked.Increment(ref schemaFailures);
        }
        catch (Exception error) { Interlocked.CompareExchange(ref failure, error, null); }
    }

    private uint OnBuffer(IntPtr pointer)
    {
        try
        {
            EtwNative.Logfile logfile = Marshal.PtrToStructure<EtwNative.Logfile>(pointer);
            Interlocked.Increment(ref bufferCallbacks);
            // EVENT_TRACE_LOGFILE.EventsLost is documented as unused; the header and controller own the loss statistics.
            Interlocked.Exchange(ref callbackEventsLost, Math.Max(Interlocked.Read(ref callbackEventsLost), logfile.Header.EventsLost));
            Interlocked.Exchange(ref callbackBuffersLost, Math.Max(Interlocked.Read(ref callbackBuffersLost), logfile.Header.BuffersLost));
        }
        catch (Exception error) { Interlocked.CompareExchange(ref failure, error, null); }
        return 1;
    }

    private static byte[] ReadProperty(IntPtr record, string name, int expectedSize)
    {
        IntPtr propertyName = Marshal.StringToHGlobalUni(name);
        try
        {
            var descriptor = new EtwNative.PropertyDescriptor { PropertyName = unchecked((ulong)propertyName.ToInt64()), ArrayIndex = uint.MaxValue };
            Check(EtwNative.TdhGetPropertySize(record, 0, IntPtr.Zero, 1, in descriptor, out uint size), "TdhGetPropertySize");
            if (size != expectedSize) throw new InvalidDataException("Unsupported network property size.");
            byte[] buffer = new byte[expectedSize];
            Check(EtwNative.TdhGetProperty(record, 0, IntPtr.Zero, 1, in descriptor, size, buffer), "TdhGetProperty");
            return buffer;
        }
        finally { Marshal.FreeHGlobal(propertyName); }
    }

    private static void Check(uint result, string operation)
    {
        if (result != 0) throw new Win32Exception((int)result, operation + " failed");
    }

    public object Statistics => new
    {
        sessionName, cleanup, statisticsAvailable,
        eventsLost = statisticsAvailable ? (long?)Math.Max(eventsLost, Interlocked.Read(ref callbackEventsLost)) : null,
        logBuffersLost = statisticsAvailable ? (long?)logBuffersLost : null,
        realTimeBuffersLost = statisticsAvailable ? (long?)realTimeBuffersLost : null,
        bufferCallbacks = Interlocked.Read(ref bufferCallbacks),
        callbackEventsLost = Interlocked.Read(ref bufferCallbacks) > 0 ? (long?)Interlocked.Read(ref callbackEventsLost) : null,
        callbackBuffersLost = Interlocked.Read(ref bufferCallbacks) > 0 ? (long?)Interlocked.Read(ref callbackBuffersLost) : null,
        queueDrops = Interlocked.Read(ref queueDrops), unsupportedSchemas = Interlocked.Read(ref schemaFailures)
    };

    public void Dispose()
    {
        if (disposed) return;
        stopping = true;
        try
        {
            if (ownsSession)
            {
                // Only the handle successfully returned by our StartTrace may be stopped; names are never used to take over a session.
                EtwNative.Properties settings = Marshal.PtrToStructure<EtwNative.Properties>(properties);
                settings.LogFileNameOffset = 0;
                settings.Wnode.Flags = 0x20000;
                Marshal.StructureToPtr(settings, properties, false);
                uint result = EtwNative.ControlTraceW(session, null, properties, 1);
                cleanup = result is 0 or 234 or 4201 ? "stopped" : $"stop-failed:{result}";
                if (result == 0)
                {
                    EtwNative.Properties final = Marshal.PtrToStructure<EtwNative.Properties>(properties);
                    eventsLost = final.EventsLost;
                    logBuffersLost = final.LogBuffersLost;
                    realTimeBuffersLost = final.RealTimeBuffersLost;
                    statisticsAvailable = true;
                }
                if (result is not (0 or 234 or 4201))
                    Interlocked.CompareExchange(ref failure, new Win32Exception((int)result, "ControlTraceW stop failed"), null);
                ownsSession = false;
            }
        }
        finally
        {
            if (consumer != ulong.MaxValue)
            {
                uint result = EtwNative.CloseTrace(consumer);
                if (result is not (0 or 7007))
                    Interlocked.CompareExchange(ref failure, new Win32Exception((int)result, "CloseTrace failed"), null);
                consumer = ulong.MaxValue;
            }
            bool finished = !threadStarted || thread is null || thread.Join(TimeSpan.FromSeconds(10));
            if (!finished)
            {
                Interlocked.CompareExchange(ref failure, new TimeoutException("ETW consumer cleanup unavailable: thread did not finish."), null);
                // A native consumer still using the logfile must retain its storage and callback delegates until process exit.
            }
            else
            {
                if (loggerName != IntPtr.Zero) Marshal.FreeHGlobal(loggerName);
                if (properties != IntPtr.Zero) Marshal.FreeHGlobal(properties);
                loggerName = properties = IntPtr.Zero;
            }
            disposed = true;
            GC.KeepAlive(eventCallback);
            GC.KeepAlive(bufferCallback);
        }
    }
}
