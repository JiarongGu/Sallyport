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
        // key container, which .NET removes again when the certificate is disposed. The export holds the key unsealed.
        var exported = inMemory.Export(X509ContentType.Pkcs12);
        try { return X509CertificateLoader.LoadPkcs12(exported, null); }
        finally { CryptographicOperations.ZeroMemory(exported); }
    }

    public static X509Certificate2Collection Authorities(string caPem)
    {
        var authorities = new X509Certificate2Collection();
        authorities.ImportFromPem(caPem);
        if (authorities.Count == 0) throw new FormatException("the profile's <ca> holds no certificate");
        return authorities;
    }

    /// <summary>Whether <paramref name="server"/> chains to the profile's own CA — and, when the profile says
    /// <c>remote-cert-tls server</c>, whether it is marked for server use; when it says <c>verify-x509-name</c>, whether
    /// it carries that name.</summary>
    /// <returns>Null when it holds; otherwise why it does not.</returns>
    /// <remarks>The host name is not checked: an OpenVPN profile trusts its own CA, not the public web's, and server
    /// certificates are commonly issued to a name that is not the host's. One CA may sign several servers, which is
    /// what <c>verify-x509-name</c> tells apart.</remarks>
    public static string? CheckServer(X509Certificate2 server, X509Certificate2Collection authorities, IEnumerable<X509Certificate2> presented, bool requireServerUse,
        Sallyport.Profile.ServerName? name = null)
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
        // The names themselves are not said: they are the server's, and the profile's.
        if (name is not null && !Carries(server.SubjectName, name))
            return "the server's certificate is not the one the profile names (verify-x509-name)";
        return null;
    }

    // The attributes OpenVPN writes a subject with, by their object identifiers; anything else is compared by its dotted
    // identifier, which a profile then has to name the same way.
    private static readonly Dictionary<string, string> Short = new()
    {
        ["2.5.4.3"] = "CN", ["2.5.4.6"] = "C", ["2.5.4.7"] = "L", ["2.5.4.8"] = "ST", ["2.5.4.10"] = "O", ["2.5.4.11"] = "OU",
        ["2.5.4.5"] = "SERIALNUMBER", ["2.5.4.9"] = "STREET", ["2.5.4.12"] = "TITLE", ["2.5.4.4"] = "SN", ["2.5.4.42"] = "GN",
        ["2.5.4.17"] = "POSTALCODE", ["2.5.4.46"] = "DNQUALIFIER", ["0.9.2342.19200300.100.1.25"] = "DC",
        ["0.9.2342.19200300.100.1.1"] = "UID", ["1.2.840.113549.1.9.1"] = "EMAILADDRESS",
    };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["S"] = "ST", ["E"] = "EMAILADDRESS", ["EMAIL"] = "EMAILADDRESS",
    };

    internal static bool Carries(X500DistinguishedName subject, Sallyport.Profile.ServerName name)
    {
        var attributes = new List<(string Key, string Value)>();
        foreach (var rdn in subject.EnumerateRelativeDistinguishedNames())
        {
            if (rdn.HasMultipleElements) return false;
            var oid = rdn.GetSingleElementType().Value ?? "";
            attributes.Add((Short.GetValueOrDefault(oid, oid), rdn.GetSingleElementValue() ?? ""));
        }
        var common = attributes.Where(a => a.Key == "CN").Select(a => a.Value).ToList();
        return name.Type switch
        {
            "name" => common.Count == 1 && common[0] == name.Name,
            "name-prefix" => common.Count == 1 && common[0].StartsWith(name.Name, StringComparison.Ordinal),
            // "C=KG, ST=NA, CN=Server-1", or the older "/C=KG/ST=NA/CN=Server-1": the same attributes, each its value.
            _ => Parse(name.Name) is { } wanted
                 && wanted.Order().SequenceEqual(attributes.Select(a => (a.Key, a.Value)).Order()),
        };
    }

    private static List<(string Key, string Value)>? Parse(string subject)
    {
        var parts = subject.StartsWith('/') ? subject[1..].Split('/') : subject.Split(',');
        var wanted = new List<(string, string)>();
        foreach (var part in parts)
        {
            var at = part.IndexOf('=');
            if (at <= 0) return null;
            var key = part[..at].Trim().ToUpperInvariant();
            wanted.Add((Aliases.GetValueOrDefault(key, key), part[(at + 1)..].Trim()));
        }
        return wanted.Count > 0 ? wanted : null;
    }
}
