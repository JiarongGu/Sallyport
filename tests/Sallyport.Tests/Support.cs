using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Channels;
using Sallyport.Protocol;

namespace Sallyport.Tests;

/// <summary>Keys and certificates made fresh for each test run — no real profile or key ever enters this repository.</summary>
internal static class TestKeys
{
    public static byte[] StaticKey() => RandomNumberGenerator.GetBytes(256);

    public static string StaticKeyText(byte[] key)
    {
        var text = new StringBuilder("#\n# 2048 bit OpenVPN static key\n#\n-----BEGIN OpenVPN Static key V1-----\n");
        for (var i = 0; i < key.Length; i += 16) text.Append(Convert.ToHexStringLower(key, i, 16)).Append('\n');
        return text.Append("-----END OpenVPN Static key V1-----\n").ToString();
    }
}

internal sealed class TestPki : IDisposable
{
    private const string ServerAuth = "1.3.6.1.5.5.7.3.1", ClientAuth = "1.3.6.1.5.5.7.3.2";

    public X509Certificate2 Authority { get; }
    public X509Certificate2 Server { get; }
    public string AuthorityPem { get; }
    public string ClientCertPem { get; }
    public string ClientKeyPem { get; }

    private readonly ECDsa _authorityKey;

    public TestPki(bool serverMarkedForServerUse = true)
    {
        _authorityKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Sallyport Test CA", _authorityKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        Authority = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        AuthorityPem = Authority.ExportCertificatePem();

        Server = Persisted(Issue("CN=server", serverMarkedForServerUse ? ServerAuth : null, out _));
        using var client = Issue("CN=client", ClientAuth, out var clientKey);
        ClientCertPem = client.ExportCertificatePem();
        ClientKeyPem = clientKey.ExportPkcs8PrivateKeyPem();
        clientKey.Dispose();
    }

    private X509Certificate2 Issue(string subject, string? usage, out ECDsa key)
    {
        key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        if (usage is not null) request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(usage)], false));
        using var issued = request.Create(Authority, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(8));
        return issued.CopyWithPrivateKey(key);
    }

    // Windows' TLS cannot sign with a key that lives only in process memory; see Certificates.Client.
    private static X509Certificate2 Persisted(X509Certificate2 certificate)
    {
        using (certificate) return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null);
    }

    public string Profile(byte[] staticKey, string? authorityPem = null, bool remoteCertTls = true) =>
        $"""
        client
        dev tun
        proto udp
        remote vpn.example.test 1194
        {(remoteCertTls ? "remote-cert-tls server" : "")}
        cipher AES-256-CBC
        auth SHA256
        <ca>
        {authorityPem ?? AuthorityPem}
        </ca>
        <cert>
        {ClientCertPem}
        </cert>
        <key>
        {ClientKeyPem}
        </key>
        <tls-crypt>
        {TestKeys.StaticKeyText(staticKey)}
        </tls-crypt>
        """;

    public void Dispose()
    {
        Server.Dispose();
        Authority.Dispose();
        _authorityKey.Dispose();
    }
}

/// <summary>Two ends of an in-memory datagram link, each direction able to drop the packets a rule picks.</summary>
internal sealed class MemoryTransport : ITransport
{
    private readonly Channel<byte[]> _inbox, _outbox;
    private readonly Func<int, bool>? _drop;
    private int _sent;

    private MemoryTransport(Channel<byte[]> inbox, Channel<byte[]> outbox, Func<int, bool>? drop)
    {
        _inbox = inbox;
        _outbox = outbox;
        _drop = drop;
    }

    public int Sent => Volatile.Read(ref _sent);

    public static (MemoryTransport Client, MemoryTransport Server) Pair(Func<int, bool>? dropToServer = null, Func<int, bool>? dropToClient = null)
    {
        var toServer = Channel.CreateUnbounded<byte[]>();
        var toClient = Channel.CreateUnbounded<byte[]>();
        return (new MemoryTransport(toClient, toServer, dropToServer), new MemoryTransport(toServer, toClient, dropToClient));
    }

    public bool IsReliable => false;

    public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken ct)
    {
        var n = Interlocked.Increment(ref _sent);
        if (_drop?.Invoke(n) != true) _outbox.Writer.TryWrite(packet.ToArray());
        return ValueTask.CompletedTask;
    }

    public ValueTask<byte[]> ReceiveAsync(CancellationToken ct) => _inbox.Reader.ReadAsync(ct);

    public ValueTask DisposeAsync()
    {
        _outbox.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

/// <summary>An OpenVPN-compatible server built from the client's own layers, for tests: it answers one handshake, echoes
/// data packets, and waits for the client's EXIT — or, when <see cref="RestartWhen"/> completes, tells it RESTART.</summary>
internal sealed class FakeServer
{
    public string? ClientPeerInfo;
    public string? ClientOptions;
    public bool SawExit, SawOccExit;
    /// <summary>The half the server sends data with: the client's opposite (1), unless a test wants a mix-up.</summary>
    public int DataSendHalf = 1;
    /// <summary>When completed, the server tells the client RESTART, as a real server does before it goes down.</summary>
    public TaskCompletionSource? RestartWhen;
    /// <summary>The server answers the push request with AUTH_FAILED, as one that no longer accepts the client does.</summary>
    public bool RefuseAuthentication;

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
        var mine = KeySource.ForServer();
        await tls.WriteAsync(KeyMethod2Message.Write(mine, "V4,dev-type tun,tls-server"), ct);

        Assert.Equal("PUSH_REQUEST", await inbox.ReadTextAsync(ct));
        if (RefuseAuthentication)
        {
            await tls.WriteAsync("AUTH_FAILED\0"u8.ToArray(), ct);
            await Task.Delay(Timeout.Infinite, ct);
        }
        await tls.WriteAsync("PUSH_REPLY,route 10.0.0.0 255.0.0.0,push-continuation 2\0"u8.ToArray(), ct);
        await tls.WriteAsync("PUSH_REPLY,ifconfig 10.8.0.2 255.255.255.0,auth-token SESSION-SECRET,peer-id 3,cipher AES-256-GCM,explicit-exit-notify,protocol-flags cc-exit,push-continuation 1\0"u8.ToArray(), ct);

        // The data channel, keyed as the server derives it: the client's key source and session id first.
        var block = DataKeys.Expand(hello.Source, mine, channel.PeerSession!, channel.LocalSession);
        using var data = new DataChannel(DataKeys.FromBlock(block, DataSendHalf, 32), 3, 0);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var echoing = Task.Run(async () =>
        {
            await channel.SendDataAsync(data.Seal(Signals.Ping), stop.Token);
            await foreach (var packet in channel.DataInbound.ReadAllAsync(stop.Token))
            {
                if (data.Open(packet, out var payload) != DataChannel.Verdict.Opened || Signals.IsPing(payload)) continue;
                if (Signals.IsOcc(payload, out var kind)) { SawOccExit = kind == Signals.OccExit; continue; }
                await channel.SendDataAsync(data.Seal(payload), stop.Token);
            }
        }, stop.Token);
        var exit = inbox.ReadTextAsync(ct);
    if (RestartWhen is not null && await Task.WhenAny(exit, RestartWhen.Task) != exit)
    {
        await tls.WriteAsync("RESTART\0"u8.ToArray(), ct);
        // The client leaves without an EXIT; its transport closing ends the read.
        try { await exit; } catch (Exception) { }
    }
    else SawExit = await exit == "EXIT";
        await stop.CancelAsync();
        try { await echoing; } catch (OperationCanceledException) { }
    }
}
