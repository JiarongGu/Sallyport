using Sallyport.Protocol;

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

    // A peer that writes its packets by hand: what the channel under test acknowledges is read back off the link.
    private static async Task<(ControlChannel Channel, List<ControlPacket> Answers)> Feed(params ControlPacket[] packets)
    {
        var key = TestKeys.StaticKey();
        var (clientEnd, serverEnd) = MemoryTransport.Pair();
        var channel = new ControlChannel(clientEnd, TlsCrypt.ForClient(key)) { Tick = TimeSpan.FromMilliseconds(10) };
        channel.Start();
        var peer = new TlsCrypt(key, sendHalf: 0);
        foreach (var packet in packets) await serverEnd.SendAsync(packet.Encode(peer), CancellationToken.None);
        var answers = new List<ControlPacket>();
        using var listen = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        try
        {
            while (true) if (ControlPacket.Decode(await serverEnd.ReceiveAsync(listen.Token), peer) is { } answer) answers.Add(answer);
        }
        catch (OperationCanceledException) { }
        return (channel, answers);
    }

    private static readonly byte[] PeerSessionId = [9, 9, 9, 9, 9, 9, 9, 9];

    [Fact]
    public async Task AMessagePastTheReceiveWindowIsNeitherKeptNorAcknowledged()
    {
        var (channel, answers) = await Feed(
            new ControlPacket(Opcode.ControlV1, 0, PeerSessionId, 1, 1, [], null, 0, [1]),
            new ControlPacket(Opcode.ControlV1, 0, PeerSessionId, 2, 1, [], null, 200, [2]));
        await using (channel)
        {
            var acked = answers.SelectMany(a => a.Acks).ToList();
            Assert.Contains(0u, acked);
            Assert.DoesNotContain(200u, acked);
            Assert.True(channel.Inbound.TryRead(out var delivered));
            Assert.Equal(0u, delivered!.MessageId);
            Assert.False(channel.Inbound.TryRead(out _));
        }
    }

    [Fact]
    public async Task AFirstPacketThatAcknowledgesAnotherSessionPinsNoPeer()
    {
        var (channel, answers) = await Feed(
            new ControlPacket(Opcode.ControlHardResetServerV2, 0, PeerSessionId, 1, 1, [0], [7, 7, 7, 7, 7, 7, 7, 7], 0, []));
        await using (channel)
        {
            Assert.Null(channel.PeerSession);
            Assert.Equal(1, channel.Dropped);
            Assert.Empty(answers);
        }
    }
}
