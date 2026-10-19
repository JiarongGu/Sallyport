using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Sallyport.Windows.PacketFilter;

/// <summary>Which way a packet the driver handed up was going: out to the network, or in to Windows' stack.</summary>
public enum PacketDirection { Unknown, Send, Receive }

/// <summary>
/// One packet as the driver holds it: an INTERMEDIATE_BUFFER (Common.h, pack 1) in native memory. Handing it back to
/// the driver unchanged passes it on; its frame is an Ethernet frame, link header included.
/// </summary>
public readonly unsafe struct DriverPacket
{
    // INTERMEDIATE_BUFFER under pack(1) on x64: a 16-byte union (adapter handle / LIST_ENTRY), then five DWORDs and
    // m_Reserved[4] before the frame.
    internal const int DeviceFlagsOffset = 16;   // m_dwDeviceFlags: PACKET_FLAG_ON_SEND 1, PACKET_FLAG_ON_RECEIVE 2
    internal const int LengthOffset = 20;        // m_Length: the frame's bytes
    internal const int NdisFlagsOffset = 24;     // m_Flags
    internal const int Vlan8021qOffset = 28;     // m_8021q
    internal const int FilterIdOffset = 32;      // m_FilterID
    internal const int FrameOffset = 52;         // m_IBuffer

    private readonly byte* _buffer;

    internal DriverPacket(byte* buffer) => _buffer = buffer;

    internal byte* Address => _buffer;

    public PacketDirection Direction => (Read(DeviceFlagsOffset) & 3) switch
    {
        1 => PacketDirection.Send,
        2 => PacketDirection.Receive,
        _ => PacketDirection.Unknown,
    };

    public int Length => (int)Math.Min(Read(LengthOffset), (uint)PacketBatch.FrameCapacity);

    public uint NdisFlags => Read(NdisFlagsOffset);

    public uint Vlan8021q => Read(Vlan8021qOffset);

    public Span<byte> Frame => new(_buffer + FrameOffset, Length);

    private uint Read(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(_buffer + offset, 4));
}

/// <summary>
/// A fixed set of packet buffers in native memory the driver reads into and sends from. Native because the driver is
/// handed their addresses inside a METHOD_BUFFERED request: memory the garbage collector may move cannot be named.
/// </summary>
public sealed unsafe class PacketBatch : IDisposable
{
    /// <summary>MAX_ETHER_FRAME in the driver's jumbo-frame build. The header leaves the build's choice (1514 or 9014)
    /// to the driver, so every buffer is sized for the larger; nothing here strides over buffers by the driver's size.</summary>
    public const int FrameCapacity = 9014;

    // Each buffer starts on a 16-byte boundary: the driver's view of the union is a LIST_ENTRY.
    internal const int Stride = (DriverPacket.FrameOffset + FrameCapacity + 15) & ~15;

    private byte* _buffers;

    public PacketBatch(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        _buffers = (byte*)NativeMemory.AlignedAlloc((nuint)(Stride * capacity), 16);
        NativeMemory.Clear(_buffers, (nuint)(Stride * capacity));
    }

    public int Capacity { get; }

    /// <summary>How many buffers the last read filled, from the first.</summary>
    public int Count { get; internal set; }

    public DriverPacket this[int index]
    {
        get
        {
            ObjectDisposedException.ThrowIf(_buffers is null, this);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)Capacity, nameof(index));
            return new(_buffers + (nint)Stride * index);
        }
    }

    public void Dispose()
    {
        if (_buffers is null) return;
        NativeMemory.AlignedFree(_buffers);
        _buffers = null;
    }
}
