using Sallyport.Control;
using Sallyport.Crypto;

namespace Sallyport.Tests;

public class ControlChannelTests
{
    private static async Task<List<ControlPacket>> Exchange(int messages, Func<int, bool>? dropToServer, Func<int, bool>? dropToClient)
    {
        var key = TestKeys.StaticKey();
        var (clientEnd, serverEnd) = MemoryTransport.Pair(dropToServer, dropToClient);
        // Fast timers: the same layer, measured in a fraction of the time a real link's 2 s retransmit would take.
        await using var client = new ControlChannel(clientEnd, TlsCrypt.ForClient(key)) { FirstRetransmitMs = 40, Tick = TimeSpan.FromMilliseconds(10) };
        await using var server = new ControlChannel(serverEnd, new TlsCrypt(key, sendHalf: 0)) { FirstRetransmitMs = 40, Tick = TimeSpan.FromMilliseconds(10) };
        client.Start();
        server.Start();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        for (var i = 0; i < messages; i++) await client.SendAsync(Opcode.ControlV1, [(byte)i], timeout.Token);
        var received = new List<ControlPacket>();
        while (received.Count < messages) received.Add(await server.Inbound.ReadAsync(timeout.Token));
        while (!client.AllAcknowledged) await Task.Delay(10, timeout.Token);
        Assert.False(server.Inbound.TryRead(out _));
        return received;
    }

    [Fact]
    public async Task DeliversInOrderOverAPerfectLink()
    {
        var got = await Exchange(30, null, null);
        Assert.Equal(Enumerable.Range(0, 30).Select(i => (byte)i), got.Select(p => p.Payload[0]));
    }

    [Fact]
    public async Task DeliversEachMessageOnceAndInOrderWhenAThirdOfEveryDirectionIsLost()
    {
        var got = await Exchange(30, n => n % 3 == 0, n => n % 3 == 1);
        Assert.Equal(Enumerable.Range(0, 30).Select(i => (byte)i), got.Select(p => p.Payload[0]));
        Assert.Equal(Enumerable.Range(0, 30).Select(i => (uint)i), got.Select(p => p.MessageId!.Value));
    }

    [Fact]
    public async Task APacketUnderAnotherKeyIsDroppedAndCounted()
    {
        var (clientEnd, serverEnd) = MemoryTransport.Pair();
        await using var stranger = new ControlChannel(clientEnd, TlsCrypt.ForClient(TestKeys.StaticKey()));
        await using var server = new ControlChannel(serverEnd, new TlsCrypt(TestKeys.StaticKey(), sendHalf: 0));
        stranger.Start();
        server.Start();

        await stranger.SendAsync(Opcode.ControlHardResetClientV2, [], CancellationToken.None);
        for (var i = 0; i < 100 && server.Dropped == 0; i++) await Task.Delay(10);

        Assert.Equal(1, server.Dropped);
        Assert.Null(server.PeerSession);
        Assert.False(server.Inbound.TryRead(out _));
    }
}
