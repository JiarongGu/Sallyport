using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Sallyport.Protocol;

internal static class Certificates
{
    private const string ServerAuth = "1.3.6.1.5.5.7.3.1";

    /// <summary>The client certificate with its private key, in a form the platform's TLS can sign with.</summary>
    public static X509Certificate2 Client(string certPem, string keyPem)
    {
        using var inMemory = X509Certificate2.CreateFromPem(certPem, keyPem);
        // Windows' TLS (SChannel) cannot use a key that exists only in process memory; a PKCS#12 round trip gives it a
        // key container, which .NET removes again when the certificate is disposed.
        return X509CertificateLoader.LoadPkcs12(inMemory.Export(X509ContentType.Pkcs12), null);
    }

    public static X509Certificate2Collection Authorities(string caPem)
    {
        var authorities = new X509Certificate2Collection();
        authorities.ImportFromPem(caPem);
        if (authorities.Count == 0) throw new FormatException("the profile's <ca> holds no certificate");
        return authorities;
    }

    /// <summary>Whether <paramref name="server"/> chains to the profile's own CA — and, when the profile says
    /// <c>remote-cert-tls server</c>, whether it is marked for server use.</summary>
    /// <returns>Null when it holds; otherwise why it does not.</returns>
    /// <remarks>The host name is not checked: an OpenVPN profile trusts its own CA, not the public web's, and server
    /// certificates are commonly issued to a name that is not the host's.</remarks>
    public static string? CheckServer(X509Certificate2 server, X509Certificate2Collection authorities, IEnumerable<X509Certificate2> presented, bool requireServerUse)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(authorities);
        chain.ChainPolicy.ExtraStore.AddRange(authorities);
        foreach (var c in presented) chain.ChainPolicy.ExtraStore.Add(c);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        if (!chain.Build(server))
            return "the server's certificate does not chain to the profile's CA: "
                + string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation.Trim()).Where(s => s.Length > 0));

        if (requireServerUse)
        {
            // Present AND naming server use: a certificate with no usage extension at all is "any use" to a chain
            // builder, which is exactly the client certificate an attacker holding one would present.
            var usage = server.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
            if (usage is null || !usage.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == ServerAuth))
                return "the server's certificate is not marked for server use, and the profile requires it (remote-cert-tls server)";
        }
        return null;
    }
}
