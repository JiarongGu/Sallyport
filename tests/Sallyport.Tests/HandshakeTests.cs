using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Sallyport.Control;
using Sallyport.Crypto;
using Sallyport.Profile;
using Sallyport.Transport;

namespace Sallyport.Tests;

/// <summary>The whole client handshake against an in-process server built from the same layers. It proves the layers
/// fit together; it cannot prove a real server agrees with them — that is the probe's job (CLAUDE.md rule 3).</summary>
public class HandshakeTests
{
    private static readonly Remote Remote = new("vpn.example.test", 1194, "udp");

    private sealed class FakeServer
    {
        public string? ClientPeerInfo;
        public string? ClientOptions;
        public bool SawExit;

        public async Task RunAsync(ITransport transport, byte[] staticKey, X509Certificate2 certificate, CancellationToken ct)
        {
            await using var channel = new ControlChannel(transport, new TlsCrypt(staticKey, sendHalf: 0));
            channel.Start();
            var reset = await channel.Inbound.ReadAsync(ct);
            Assert.Equal(Opcode.ControlHardResetClientV2, reset.Opcode);
            await channel.SendAsync(Opcode.ControlHardResetServerV2, [], ct);

            await using var tls = new SslStream(new ControlStream(channel));
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificateContext = SslStreamCertificateContext.Create(certificate, null, offline: true),
                ClientCertificateRequired = true,
                RemoteCertificateValidationCallback = (_, presented, _, _) => presented is not null,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, ct);

            var inbox = new TlsInbox(tls);
            KeyMethod2Message? hello;
            int used;
            while (!KeyMethod2Message.TryRead(inbox.Data, fromClient: true, out hello, out used)) await inbox.FillAsync(ct);
            inbox.Consume(used);
            ClientOptions = hello!.Options;
            ClientPeerInfo = hello.Trailing.Count == 3 ? hello.Trailing[2] : null;
            await tls.WriteAsync(KeyMethod2Message.Write(KeySource.ForServer(), "V4,dev-type tun,tls-server"), ct);

            Assert.Equal("PUSH_REQUEST", await inbox.ReadTextAsync(ct));
            await tls.WriteAsync("PUSH_REPLY,route 10.0.0.0 255.0.0.0,push-continuation 2\0"u8.ToArray(), ct);
            await tls.WriteAsync("PUSH_REPLY,ifconfig 10.8.0.2 255.255.255.0,auth-token SESSION-SECRET,peer-id 3,cipher AES-256-GCM,push-continuation 1\0"u8.ToArray(), ct);
            SawExit = await inbox.ReadTextAsync(ct) == "EXIT";
        }
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
            ["route 10.0.0.0 255.0.0.0", "ifconfig 10.8.0.2 255.255.255.0", "auth-token SESSION-SECRET", "peer-id 3", "cipher AES-256-GCM"],
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
