using System.Buffers.Binary;
using System.Text;
using Sallyport.Windows.PacketFilter;

namespace Sallyport.Windows.Tests;

public sealed class PacketDriverTests
{
    [Fact]
    public void The_control_codes_are_the_headers()
    {
        // CTL_CODE(0x8300, 0x830 + n, METHOD_BUFFERED, FILE_ANY_ACCESS): n as Common.h numbers them.
        Assert.Equal(0x830020C4u, Native.IoctlGetTcpipInterfaces);   // +1
        Assert.Equal(0x830020D4u, Native.IoctlSetAdapterMode);       // +5
        Assert.Equal(0x830020D8u, Native.IoctlFlushAdapterQueue);    // +6
        Assert.Equal(0x830020DCu, Native.IoctlSetEvent);             // +7
        Assert.Equal(0x830020F0u, Native.IoctlAdapterQueueSize);     // +12
        Assert.Equal(0x830020F4u, Native.IoctlGetAdapterMode);       // +13
        Assert.Equal(0x83002110u, Native.IoctlSendPacketsToAdapter); // +20
        Assert.Equal(0x83002114u, Native.IoctlSendPacketsToMstcp);   // +21
        Assert.Equal(0x83002118u, Native.IoctlReadPackets);          // +22
    }

    [Fact]
    public void The_adapter_list_is_laid_out_as_packed_x64()
    {
        Assert.Equal(8196, PacketDriver.HandlesOffset);
        Assert.Equal(8452, PacketDriver.MediumsOffset);
        Assert.Equal(8580, PacketDriver.AddressesOffset);
        Assert.Equal(8772, PacketDriver.MtusOffset);
        Assert.Equal(8836, PacketDriver.AdapterListSize);
    }

    [Fact]
    public void An_adapter_list_reads_back_each_column()
    {
        var list = new byte[PacketDriver.AdapterListSize];
        BinaryPrimitives.WriteUInt32LittleEndian(list, 2);
        Encoding.ASCII.GetBytes(@"\DEVICE\{5CEA7FAF-C10B-44B2-9375-FFC6977A368B}").CopyTo(list, PacketDriver.NamesOffset + 256);
        BinaryPrimitives.WriteUInt64LittleEndian(list.AsSpan(PacketDriver.HandlesOffset + 8), 0xFFFF_A000_1234_5678);
        BinaryPrimitives.WriteUInt32LittleEndian(list.AsSpan(PacketDriver.MediumsOffset + 4), 0);
        new byte[] { 0x02, 0x11, 0x22, 0x33, 0x44, 0x55 }.CopyTo(list, PacketDriver.AddressesOffset + 6);
        BinaryPrimitives.WriteUInt16LittleEndian(list.AsSpan(PacketDriver.MtusOffset + 2), 1500);

        var adapters = PacketDriver.ParseAdapterList(list);

        Assert.Equal(2, adapters.Count);
        var second = adapters[1];
        Assert.Equal(0xFFFF_A000_1234_5678ul, second.Handle);
        Assert.Equal(Guid.Parse("5CEA7FAF-C10B-44B2-9375-FFC6977A368B"), second.InterfaceId);
        Assert.Equal("021122334455", second.Address.ToString());
        Assert.Equal(1500, second.Mtu);
        Assert.Null(adapters[0].InterfaceId);
    }

    [Fact]
    public void A_count_beyond_the_list_reads_only_the_list()
    {
        var list = new byte[PacketDriver.AdapterListSize];
        BinaryPrimitives.WriteUInt32LittleEndian(list, 1000);
        Assert.Equal(32, PacketDriver.ParseAdapterList(list).Count);
    }

    [Fact]
    public unsafe void A_packet_reads_the_drivers_header()
    {
        using var batch = new PacketBatch(2);
        var raw = new Span<byte>(batch[1].Address, PacketBatch.Stride);
        BinaryPrimitives.WriteUInt32LittleEndian(raw[16..], 2);   // PACKET_FLAG_ON_RECEIVE
        BinaryPrimitives.WriteUInt32LittleEndian(raw[20..], 60);
        raw[52] = 0xAB;

        Assert.Equal(PacketDirection.Receive, batch[1].Direction);
        Assert.Equal(60, batch[1].Length);
        Assert.Equal(0xAB, batch[1].Frame[0]);
        Assert.Equal(PacketDirection.Unknown, batch[0].Direction);
    }

    [Fact]
    public unsafe void Buffers_hold_a_jumbo_frame_and_start_on_16_bytes()
    {
        Assert.True(PacketBatch.Stride >= 52 + 9014);
        Assert.Equal(0, PacketBatch.Stride % 16);
        using var batch = new PacketBatch(3);
        Assert.Equal(0, (long)batch[2].Address % 16);
    }

    [Fact]
    public unsafe void A_length_beyond_the_buffer_is_cut_to_it()
    {
        using var batch = new PacketBatch(1);
        BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(batch[0].Address + 20, 4), 70000);
        Assert.Equal(PacketBatch.FrameCapacity, batch[0].Length);
    }
}
