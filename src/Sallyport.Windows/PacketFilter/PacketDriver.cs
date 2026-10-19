using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Sallyport.Windows.PacketFilter;

/// <summary>What the driver does with an adapter's packets (ADAPTER_MODE.dwFlags, the MSTCP_FLAG_* values).</summary>
[Flags]
public enum AdapterMode : uint
{
    None = 0,
    /// <summary>Packets Windows sends are held for this program instead of reaching the network.</summary>
    SentTunnel = 0x01,
    /// <summary>Packets arriving are held for this program instead of reaching Windows.</summary>
    ReceiveTunnel = 0x02,
    SentListen = 0x04,
    ReceiveListen = 0x08,
    FilterDirect = 0x10,
    LoopbackFilter = 0x20,
    LoopbackBlock = 0x40,
}

/// <summary>An adapter as the driver lists it (TCP_AdapterList). <see cref="Handle"/> is the driver's key for every
/// request about it; <see cref="InterfaceId"/> ties it to Windows' own view of its interfaces.</summary>
public sealed record DriverAdapter(ulong Handle, string Name, uint Medium, PhysicalAddress Address, ushort Mtu)
{
    /// <summary>The GUID in the driver's name for it, "\DEVICE\{GUID}"; null when the name carries none.</summary>
    public Guid? InterfaceId
    {
        get
        {
            var open = Name.IndexOf('{');
            return open >= 0 && Guid.TryParse(Name.AsSpan(open), out var id) ? id : null;
        }
    }
}

/// <summary>
/// Windows Packet Filter's device (<c>\\.\NDISRD</c>), through NT Kernel's documented control codes (ndisapi, MIT,
/// Common.h) — never their DLL (rule 4). Opening it needs administrator (measured, D18). The modes and events set
/// through one handle are what that handle's owner answers for.
/// </summary>
public sealed unsafe class PacketDriver : IDisposable
{
    // TCP_AdapterList under pack(1) on x64: m_nAdapterCount DWORD, then arrays of ADAPTER_LIST_SIZE (32) entries —
    // names ADAPTER_NAME_SIZE (256) bytes each, HANDLEs (8), medium DWORDs, 6-byte MACs, USHORT MTUs.
    internal const int ListSize = 32;
    internal const int NameSize = 256;
    internal const int NamesOffset = 4;
    internal const int HandlesOffset = NamesOffset + ListSize * NameSize;   // 8196
    internal const int MediumsOffset = HandlesOffset + ListSize * 8;        // 8452
    internal const int AddressesOffset = MediumsOffset + ListSize * 4;      // 8580
    internal const int MtusOffset = AddressesOffset + ListSize * 6;         // 8772
    internal const int AdapterListSize = MtusOffset + ListSize * 2;         // 8836

    // ETH_M_REQUEST: hAdapterHandle (8), dwPacketsNumber (4), dwPacketsSuccess (4), then one 8-byte pointer per packet.
    internal const int RequestHeaderSize = 16;

    private readonly SafeFileHandle _device;

    private PacketDriver(SafeFileHandle device) => _device = device;

    /// <summary>Opens the device; throws <see cref="Win32Exception"/> (5: not administrator, 2: driver not installed).</summary>
    public static PacketDriver Open()
    {
        var device = Native.CreateFile(PacketFilterRelease.DevicePath, Native.GENERIC_READ | Native.GENERIC_WRITE, 0, IntPtr.Zero,
            Native.OPEN_EXISTING, 0, IntPtr.Zero);
        if (device.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            device.Dispose();
            throw new Win32Exception(error);
        }
        return new(device);
    }

    public IReadOnlyList<DriverAdapter> Adapters()
    {
        var list = new byte[AdapterListSize];
        fixed (byte* p = list) Control(Native.IoctlGetTcpipInterfaces, p, AdapterListSize, p, AdapterListSize);
        return ParseAdapterList(list);
    }

    internal static IReadOnlyList<DriverAdapter> ParseAdapterList(ReadOnlySpan<byte> list)
    {
        var count = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(list), ListSize);
        var adapters = new List<DriverAdapter>(count);
        for (var i = 0; i < count; i++)
        {
            var name = list.Slice(NamesOffset + i * NameSize, NameSize);
            var end = name.IndexOf((byte)0);
            adapters.Add(new(
                BinaryPrimitives.ReadUInt64LittleEndian(list[(HandlesOffset + i * 8)..]),
                Encoding.ASCII.GetString(end < 0 ? name : name[..end]),
                BinaryPrimitives.ReadUInt32LittleEndian(list[(MediumsOffset + i * 4)..]),
                new PhysicalAddress(list.Slice(AddressesOffset + i * 6, 6).ToArray()),
                BinaryPrimitives.ReadUInt16LittleEndian(list[(MtusOffset + i * 2)..])));
        }
        return adapters;
    }

    public AdapterMode Mode(ulong adapter)
    {
        var mode = stackalloc byte[12];   // ADAPTER_MODE: handle (8), dwFlags (4)
        *(ulong*)mode = adapter;
        *(uint*)(mode + 8) = 0;
        Control(Native.IoctlGetAdapterMode, mode, 12, mode, 12);
        return (AdapterMode)(*(uint*)(mode + 8));
    }

    public void SetMode(ulong adapter, AdapterMode flags)
    {
        var mode = stackalloc byte[12];
        *(ulong*)mode = adapter;
        *(uint*)(mode + 8) = (uint)flags;
        Control(Native.IoctlSetAdapterMode, mode, 12, null, 0);
    }

    /// <summary>The event the driver signals when the adapter's queue holds packets; null takes it away. The handle
    /// must stay open while the driver has it.</summary>
    public void SetPacketEvent(ulong adapter, WaitHandle? signal)
    {
        var request = stackalloc byte[16];   // ADAPTER_EVENT: handle (8), hEvent (8)
        *(ulong*)request = adapter;
        *(ulong*)(request + 8) = signal is null ? 0 : (ulong)signal.SafeWaitHandle.DangerousGetHandle();
        Control(Native.IoctlSetEvent, request, 16, null, 0);
    }

    public uint QueueSize(ulong adapter)
    {
        uint size = 0;
        Control(Native.IoctlAdapterQueueSize, &adapter, 8, &size, 4);
        return size;
    }

    /// <summary>Drops whatever the adapter's queue holds.</summary>
    public void FlushQueue(ulong adapter) => Control(Native.IoctlFlushAdapterQueue, &adapter, 8, null, 0);

    /// <summary>The driver's answer to a read of an empty queue: the read refused with ERROR_INVALID_PARAMETER (measured
    /// 2026-10-20 — every drain of a live adapter ended so, and no packet was lost to it).</summary>
    public const int EmptyQueue = 87;

    /// <summary>Fills <paramref name="batch"/> from the adapter's queue, from its first buffer; returns how many. A
    /// refused read returns 0 with the driver's reason in <see cref="LastReadError"/> — <see cref="EmptyQueue"/> when
    /// there was nothing to read, anything else a fault.</summary>
    public int Read(ulong adapter, PacketBatch batch)
    {
        var size = RequestHeaderSize + 8 * batch.Capacity;
        var request = stackalloc byte[size];
        *(ulong*)request = adapter;
        *(uint*)(request + 8) = (uint)batch.Capacity;
        *(uint*)(request + 12) = 0;
        for (var i = 0; i < batch.Capacity; i++) *(ulong*)(request + RequestHeaderSize + 8 * i) = (ulong)batch[i].Address;
        if (!Native.DeviceIoControl(_device, Native.IoctlReadPackets, request, (uint)size, request, (uint)size, out _, IntPtr.Zero))
        {
            LastReadError = Marshal.GetLastPInvokeError();
            batch.Count = 0;
            return 0;
        }
        LastReadError = 0;
        batch.Count = (int)Math.Min(*(uint*)(request + 12), (uint)batch.Capacity);
        return batch.Count;
    }

    public int LastReadError { get; private set; }

    /// <summary>Sends the chosen buffers on: towards the network (<paramref name="toNetwork"/>) or up into Windows'
    /// stack. Returns how many the driver says it took, or null when it wrote no answer back (its call succeeded, which
    /// is then all it said). Throws when the driver refuses the call.</summary>
    public int? Send(ulong adapter, PacketBatch batch, ReadOnlySpan<int> which, bool toNetwork)
    {
        if (which.IsEmpty) return 0;
        var size = RequestHeaderSize + 8 * which.Length;
        var request = stackalloc byte[size];
        *(ulong*)request = adapter;
        *(uint*)(request + 8) = (uint)which.Length;
        *(uint*)(request + 12) = 0;
        for (var i = 0; i < which.Length; i++) *(ulong*)(request + RequestHeaderSize + 8 * i) = (ulong)batch[which[i]].Address;
        var code = toNetwork ? Native.IoctlSendPacketsToAdapter : Native.IoctlSendPacketsToMstcp;
        if (!Native.DeviceIoControl(_device, code, request, (uint)size, request, (uint)size, out var returned, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        // METHOD_BUFFERED copies back only the bytes the driver says it wrote: fewer than the header, and
        // dwPacketsSuccess is still the 0 this program put there, not the driver's count.
        return returned >= RequestHeaderSize ? (int)*(uint*)(request + 12) : null;
    }

    /// <summary>The interface Windows sends <paramref name="destination"/> through, as its GUID — the adapter whose
    /// packets carry the person's internet traffic. Null when Windows has no route.</summary>
    public static Guid? InterfaceFor(IPAddress destination)
    {
        if (Native.GetBestInterface(BitConverter.ToUInt32(destination.GetAddressBytes()), out var index) != 0) return null;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            int? nicIndex = null;
            try { nicIndex = nic.GetIPProperties().GetIPv4Properties()?.Index; }
            catch (NetworkInformationException) { }
            if (nicIndex == index && Guid.TryParse(nic.Id, out var id)) return id;
        }
        return null;
    }

    public void Dispose() => _device.Dispose();

    private void Control(uint code, void* input, int inputSize, void* output, int outputSize)
    {
        if (!Native.DeviceIoControl(_device, code, input, (uint)inputSize, output, (uint)outputSize, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }
}
