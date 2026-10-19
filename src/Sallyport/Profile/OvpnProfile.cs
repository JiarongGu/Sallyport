namespace Sallyport.Profile;

/// <summary>One server a profile may connect to.</summary>
public sealed record Remote(string Host, int Port, string Proto);

/// <summary>
/// The parts of an OpenVPN client profile (<c>.ovpn</c>) a connection needs. Inline blocks (<c>&lt;ca&gt;</c>,
/// <c>&lt;cert&gt;</c>, <c>&lt;key&gt;</c>, <c>&lt;tls-crypt&gt;</c>, <c>&lt;tls-auth&gt;</c>) are kept as text; nothing here
/// is ever written out or logged — a profile carries a private key and a static key.
/// </summary>
public sealed class OvpnProfile
{
    public IReadOnlyList<Remote> Remotes { get; init; } = [];
    public string? CaPem { get; init; }
    public string? CertPem { get; init; }
    public string? KeyPem { get; init; }
    /// <summary>The 256-byte static key of <c>tls-crypt</c> (v1), or null.</summary>
    public byte[]? TlsCryptKey { get; init; }
    /// <summary>The 256-byte static key of <c>tls-auth</c>, or null — not yet supported, only recognised.</summary>
    public byte[]? TlsAuthKey { get; init; }
    /// <summary>The profile carries a <c>tls-crypt-v2</c> client key — recognised, not yet supported.</summary>
    public bool TlsCryptV2 { get; init; }
    public string Cipher { get; init; } = "AES-256-CBC";
    public IReadOnlyList<string> DataCiphers { get; init; } = [];
    public string Auth { get; init; } = "SHA1";
    /// <summary>The profile asks for a username and password as well as (or instead of) a certificate.</summary>
    public bool AuthUserPass { get; init; }
    public bool RemoteCertTlsServer { get; init; }
    public string Dev { get; init; } = "tun";

    public static OvpnProfile Parse(string text)
    {
        var remotes = new List<Remote>();
        var blocks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string proto = "udp", cipher = "AES-256-CBC", auth = "SHA1", dev = "tun";
        var dataCiphers = new List<string>();
        bool userPass = false, certTls = false;

        string? block = null;
        var body = new System.Text.StringBuilder();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (block is not null)
            {
                if (line.Equals($"</{block}>", StringComparison.OrdinalIgnoreCase)) { blocks[block] = body.ToString(); block = null; body.Clear(); }
                else body.Append(line).Append('\n');
                continue;
            }
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line[0] == '<' && line[^1] == '>' && line[1] != '/') { block = line[1..^1]; continue; }

            var parts = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            switch (parts[0].ToLowerInvariant())
            {
                case "remote" when parts.Length >= 2:
                    // A remote's own protocol, when it names one, outranks the profile's `proto` — read at the end.
                    var port = parts.Length >= 3 && int.TryParse(parts[2], out var p) ? p : 1194;
                    remotes.Add(new Remote(parts[1], port, parts.Length >= 4 ? parts[3].ToLowerInvariant() : ""));
                    break;
                case "proto" when parts.Length >= 2: proto = parts[1].ToLowerInvariant(); break;
                case "cipher" when parts.Length >= 2: cipher = parts[1]; break;
                case "data-ciphers" when parts.Length >= 2: dataCiphers.AddRange(parts[1].Split(':', StringSplitOptions.RemoveEmptyEntries)); break;
                case "auth" when parts.Length >= 2: auth = parts[1]; break;
                case "auth-user-pass": userPass = true; break;
                case "remote-cert-tls" when parts.Length >= 2 && parts[1] == "server": certTls = true; break;
                case "dev" when parts.Length >= 2: dev = parts[1]; break;
            }
        }

        // A profile commonly lists one server several times (Access Server writes it once per transport); one is enough.
        var distinct = remotes.Select(r => r with { Proto = Normalise(r.Proto.Length > 0 ? r.Proto : proto) })
            .Distinct().ToList();

        return new OvpnProfile
        {
            Remotes = distinct,
            CaPem = blocks.GetValueOrDefault("ca"),
            CertPem = blocks.GetValueOrDefault("cert"),
            KeyPem = blocks.GetValueOrDefault("key"),
            TlsCryptKey = blocks.TryGetValue("tls-crypt", out var tc) ? StaticKey.Parse(tc) : null,
            TlsAuthKey = blocks.TryGetValue("tls-auth", out var ta) ? StaticKey.Parse(ta) : null,
            TlsCryptV2 = blocks.ContainsKey("tls-crypt-v2"),
            Cipher = cipher,
            DataCiphers = dataCiphers,
            Auth = auth,
            AuthUserPass = userPass,
            RemoteCertTlsServer = certTls,
            Dev = dev,
        };
    }

    // udp, udp4, udp6 → udp; tcp, tcp-client, tcp4 → tcp.
    private static string Normalise(string proto) => proto.StartsWith("tcp", StringComparison.Ordinal) ? "tcp" : "udp";
}

/// <summary>An OpenVPN static key (the <c>tls-crypt</c> / <c>tls-auth</c> blocks): 256 bytes written as hex lines.</summary>
internal static class StaticKey
{
    public const int Length = 256;

    public static byte[] Parse(string text)
    {
        var hex = new System.Text.StringBuilder();
        var inside = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("-----BEGIN", StringComparison.Ordinal)) { inside = true; continue; }
            if (line.StartsWith("-----END", StringComparison.Ordinal)) break;
            if (inside && line.Length > 0 && line[0] != '#') hex.Append(line);
        }
        var bytes = Convert.FromHexString(hex.ToString());
        if (bytes.Length != Length) throw new FormatException($"a static key is {Length} bytes; this one is {bytes.Length}");
        return bytes;
    }
}
