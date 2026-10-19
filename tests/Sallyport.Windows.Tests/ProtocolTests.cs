using System.Buffers.Binary;
using Sallyport.Windows.Routing;
using Sallyport.Windows.Service;

namespace Sallyport.Windows.Tests;

/// <summary>The router service's frames (RouterProtocol): what one side writes, the other reads back the same.</summary>
public sealed class ProtocolTests
{
    private static async Task<FrameStream> WrittenAsync(Func<FrameStream, Task> write)
    {
        var buffer = new MemoryStream();
        await write(new FrameStream(buffer));
        return new FrameStream(new MemoryStream(buffer.ToArray()));
    }

    [Fact]
    public async Task A_control_message_reads_back_as_written()
    {
        var reader = await WrittenAsync(async f => await f.SendAsync(new PrepareRun([@"E:\Games\Zenless\ZenlessZoneZero.exe"], LossMode.Direct, 15)));

        var frame = await reader.ReadAsync(CancellationToken.None);

        var prepare = Assert.IsType<PrepareRun>(frame!.Value.Message);
        Assert.Equal(@"E:\Games\Zenless\ZenlessZoneZero.exe", Assert.Single(prepare.Executables));
        Assert.Equal((LossMode.Direct, (int?)15), (prepare.Loss, prepare.DirectAfterSeconds));
    }

    [Fact]
    public async Task A_packet_keeps_its_generation_and_bytes()
    {
        var packet = Enumerable.Range(0, 1500).Select(i => (byte)i).ToArray();
        var reader = await WrittenAsync(async f =>
        {
            await f.SendPacketAsync(7, packet);
            await f.SendAsync(new RetireGeneration(7));
        });

        var first = (await reader.ReadAsync(CancellationToken.None))!.Value;
        var second = (await reader.ReadAsync(CancellationToken.None))!.Value;

        Assert.Equal(RouterProtocol.PacketFrame, first.Kind);
        Assert.Equal(7, first.Generation);
        Assert.Equal(packet, first.Packet);
        Assert.Null(first.Message);
        Assert.Equal(7, Assert.IsType<RetireGeneration>(second.Message).Generation);
    }

    [Fact]
    public async Task The_other_end_closing_between_frames_is_the_end_and_inside_one_is_an_error()
    {
        var empty = new FrameStream(new MemoryStream());
        Assert.Null(await empty.ReadAsync(CancellationToken.None));

        var buffer = new MemoryStream();
        await new FrameStream(buffer).SendAsync(new Heartbeat());
        var cut = new FrameStream(new MemoryStream(buffer.ToArray()[..^2]));
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await cut.ReadAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(RouterProtocol.MostFrame + 1)]
    [InlineData(-5)]
    public async Task A_frame_length_outside_its_bounds_is_refused_before_it_is_read(int length)
    {
        var head = new byte[5];
        BinaryPrimitives.WriteInt32LittleEndian(head, length);
        head[4] = RouterProtocol.ControlFrame;
        var reader = new FrameStream(new MemoryStream(head));

        await Assert.ThrowsAsync<InvalidDataException>(async () => await reader.ReadAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(@"C:\Program Files\Sallyport\Sallyport.Client.exe", @"C:\Program Files\Sallyport\", true)]
    [InlineData(@"C:\PROGRAM FILES\SALLYPORT\Sallyport.Client.exe", @"C:\Program Files\Sallyport", true)]
    [InlineData(@"C:\Program Files\Sallyport\sub\Sallyport.Client.exe", @"C:\Program Files\Sallyport\", false)]
    [InlineData(@"C:\Users\x\Downloads\Sallyport.Client.exe", @"C:\Program Files\Sallyport\", false)]
    public void A_peer_runs_from_a_folder_only_directly_in_it(string image, string folder, bool expected)
    {
        Assert.Equal(expected, new PipePeer(1, 1, null, image).RunsFrom(folder));
    }
}
