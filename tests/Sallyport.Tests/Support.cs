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
