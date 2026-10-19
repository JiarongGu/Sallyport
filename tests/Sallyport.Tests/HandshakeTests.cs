using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Sallyport.Protocol;
using Sallyport.Profile;

namespace Sallyport.Tests;

/// <summary>The whole client handshake against an in-process server built from the same layers. It proves the layers
/// fit together; it cannot prove a real server agrees with them — that is the probe's job (CLAUDE.md rule 3).</summary>
public class HandshakeTests
{
    private static readonly Remote Remote = new("vpn.example.test", 1194, "udp");

    private static Task<Tunnel> Connect(string profileText, MemoryTransport end) =>
        Tunnel.ConnectAsync(OvpnProfile.Parse(profileText), Remote, _ => Task.FromResult<ITransport>(end), TimeSpan.FromSeconds(30), new TunnelOptions(), null, CancellationToken.None);

    [Fact]
    public async Task APacketCrossesTheDataChannelBothWays()
    {
        using var pki = new TestPki();
        var key = TestKeys.StaticKey();
        var (clientEnd, serverEnd) = MemoryTransport.Pair();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var server = new FakeServer();
        var serving = server.RunAsync(serverEnd, key, pki.Server, stop.Token);

        var tunnel = await Connect(pki.Profile(key), clientEnd);
        byte[] packet = [0x45, 0, 0, 20, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
        await tunnel.SendAsync(packet);
        var echoed = await tunnel.Packets.ReadAsync(stop.Token);
        for (var i = 0; i < 200 && tunnel.Counters.PingsIn == 0; i++) await Task.Delay(10);
        var counters = tunnel.Counters;
        await tunnel.DisposeAsync();
        await serving;

        Assert.Equal(packet, echoed);
        Assert.Equal(new IPAddress([10, 8, 0, 2]), tunnel.Config.Address);
        Assert.Equal(1, counters.PacketsOut);
        Assert.Equal(1, counters.PacketsIn);
        Assert.Equal(1, counters.PingsIn);   // the server's ping is a keepalive, not a packet for the reader
        Assert.Equal(0, counters.Rejected + counters.OppositeDirection);
        Assert.True(server.SawOccExit);
        Assert.True(server.SawExit);
        await Assert.ThrowsAsync<TunnelEnded>(() => tunnel.SendAsync(packet).AsTask());
    }

    [Fact]
    public async Task BothSidesOnTheSameKeyHalfIsNamedAsSuch()
    {
        using var pki = new TestPki();
        var key = TestKeys.StaticKey();
        var (clientEnd, serverEnd) = MemoryTransport.Pair();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serving = new FakeServer { DataSendHalf = 0 }.RunAsync(serverEnd, key, pki.Server, stop.Token);

        await using var tunnel = await Connect(pki.Profile(key), clientEnd);
        for (var i = 0; i < 200 && tunnel.Counters.OppositeDirection == 0; i++) await Task.Delay(10);

        Assert.Equal(1, tunnel.Counters.OppositeDirection);
        Assert.Equal(0, tunnel.Counters.PingsIn);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => serving);
    }

    private static Task<HandshakeResult> Client(string profileText, MemoryTransport end, TimeSpan timeout) =>
        Handshake.RunAsync(OvpnProfile.Parse(profileText), Remote, _ => Task.FromResult<ITransport>(end), timeout, null, CancellationToken.None);

    [Fact]
    public async Task ReachesThePushReply()
    {
        using var pki = new TestPki();
        var key = TestKeys.StaticKey();
        var (clientEnd, serverEnd) = MemoryTransport.Pair();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var server = new FakeServer();
        var serving = server.RunAsync(serverEnd, key, pki.Server, stop.Token);

        var result = await Client(pki.Profile(key), clientEnd, TimeSpan.FromSeconds(30));
        await serving;

        Assert.Equal(
            ["route 10.0.0.0 255.0.0.0", "ifconfig 10.8.0.2 255.255.255.0", "auth-token SESSION-SECRET", "peer-id 3", "cipher AES-256-GCM",
             "explicit-exit-notify", "protocol-flags cc-exit"],
            result.PushOptions);
        Assert.Equal("auth-token [redacted]", PushOptions.Redact(result.PushOptions[2]));
        Assert.Equal("CN=server", result.ServerSubject);
        Assert.Equal(0, result.DroppedPackets);
        Assert.Contains("IV_PROTO=130\n", server.ClientPeerInfo);
        Assert.Contains("IV_CIPHERS=AES-256-GCM:AES-128-GCM\n", server.ClientPeerInfo);
        Assert.StartsWith("V4,dev-type tun,", server.ClientOptions);
        Assert.EndsWith(",tls-client", server.ClientOptions);
        Assert.True(server.SawExit);
    }

    [Fact]
    public async Task AServerOutsideTheProfilesCaIsRefused()
    {
        using var pki = new TestPki();
        using var stranger = new TestPki();
        var key = TestKeys.StaticKey();
        var (clientEnd, serverEnd) = MemoryTransport.Pair();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serving = new FakeServer().RunAsync(serverEnd, key, pki.Server, stop.Token);

        var failed = await Assert.ThrowsAsync<HandshakeFailed>(() => Client(stranger.Profile(key), clientEnd, TimeSpan.FromSeconds(30)));
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => serving);

        Assert.Equal(HandshakeStage.Tls, failed.Stage);
        Assert.Equal(Outcome.Failed, failed.Outcome);
        Assert.Contains("does not chain to the profile's CA", failed.Message);
    }

    [Fact]
    public async Task AServerCertificateNotMarkedForServerUseIsRefusedWhenTheProfileAsks()
    {
        using var pki = new TestPki(serverMarkedForServerUse: false);
        var key = TestKeys.StaticKey();
        var (clientEnd, serverEnd) = MemoryTransport.Pair();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serving = new FakeServer().RunAsync(serverEnd, key, pki.Server, stop.Token);

        var failed = await Assert.ThrowsAsync<HandshakeFailed>(() => Client(pki.Profile(key), clientEnd, TimeSpan.FromSeconds(30)));
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => serving);

        Assert.Equal(Outcome.Failed, failed.Outcome);
        Assert.Contains("not marked for server use", failed.Message);
    }

    [Fact]
    public async Task AWrongStaticKeyGetsNoAnswerAndSaysSo()
    {
        using var pki = new TestPki();
        var (clientEnd, serverEnd) = MemoryTransport.Pair();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serving = new FakeServer().RunAsync(serverEnd, TestKeys.StaticKey(), pki.Server, stop.Token);

        var failed = await Assert.ThrowsAsync<HandshakeFailed>(() => Client(pki.Profile(TestKeys.StaticKey()), clientEnd, TimeSpan.FromSeconds(2)));
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => serving);

        // Unverifiable, not failed: silence does not say whether the key, the port or the path was wrong.
        Assert.Equal(HandshakeStage.HardReset, failed.Stage);
        Assert.Equal(Outcome.Unverifiable, failed.Outcome);
    }

    [Fact]
    public async Task AProfileItCannotServeIsRefusedBeforeAnyPacketIsSent()
    {
        var (clientEnd, _) = MemoryTransport.Pair();
        var failed = await Assert.ThrowsAsync<HandshakeFailed>(() => Client("remote x 1194\nauth-user-pass\n", clientEnd, TimeSpan.FromSeconds(2)));
        Assert.Equal(Outcome.Unverifiable, failed.Outcome);
        Assert.Equal(0, clientEnd.Sent);
    }
}
