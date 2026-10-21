using System.Globalization;
using System.Net;

namespace Sallyport;

/// <summary>What the server's push reply configures, read from its options. Only what this client acts on is kept;
/// the full list stays on <see cref="Tunnel.PushOptions"/>.</summary>
public sealed class PushedConfig
{
    /// <summary>This client's address inside the tunnel.</summary>
    public IPAddress? Address { get; init; }
    /// <summary>The tunnel subnet's mask under <c>topology subnet</c>; the peer's address under <c>net30</c>/<c>p2p</c>.</summary>
    public IPAddress? IfconfigSecond { get; init; }
    /// <summary>The pushed <c>topology</c>: <c>net30</c> (the default), <c>subnet</c> or <c>p2p</c>.</summary>
    public string Topology { get; init; } = "net30";
    /// <summary>The pushed <c>route-gateway</c>, or null.</summary>
    public IPAddress? Gateway { get; init; }
    /// <summary>The DNS servers pushed (<c>dhcp-option DNS</c>), in order; a session resolves through these.</summary>
    public IReadOnlyList<IPAddress> DnsServers { get; init; } = [];
    /// <summary>Seconds of silence after which to send a keepalive; 0 when none was pushed.</summary>
    public int Ping { get; init; }
    /// <summary>Seconds of silence from the server after which the tunnel is dead; 0 when none was pushed.</summary>
    public int PingRestart { get; init; }
    /// <summary>The pushed <c>peer-id</c>, which each data packet then carries; null when none was pushed.</summary>
    public uint? PeerId { get; init; }
    /// <summary>The data channel's cipher, as the server chose it.</summary>
    public string? Cipher { get; init; }
    /// <summary>The server asked to be told when this client leaves (<c>explicit-exit-notify</c>).</summary>
    public bool ExplicitExitNotify { get; init; }
    /// <summary>The pushed <c>protocol-flags</c>.</summary>
    public IReadOnlyList<string> ProtocolFlags { get; init; } = [];

    internal static PushedConfig Parse(IEnumerable<string> options)
    {
        IPAddress? address = null, second = null, gateway = null;
        var dns = new List<IPAddress>();
        var flags = new List<string>();
        string topology = "net30";
        string? cipher = null;
        int ping = 0, pingRestart = 0;
        uint? peerId = null;
        var exitNotify = false;
        foreach (var option in options)
        {
            var parts = option.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            switch (parts[0])
            {
                case "ifconfig" when parts.Length >= 3:
                    IPAddress.TryParse(parts[1], out address);
                    IPAddress.TryParse(parts[2], out second);
                    break;
                case "topology" when parts.Length >= 2: topology = parts[1]; break;
                case "route-gateway" when parts.Length >= 2: IPAddress.TryParse(parts[1], out gateway); break;
                case "dhcp-option" when parts.Length >= 3 && parts[1] == "DNS" && IPAddress.TryParse(parts[2], out var server): dns.Add(server); break;
                case "ping" when parts.Length >= 2: int.TryParse(parts[1], CultureInfo.InvariantCulture, out ping); break;
                case "ping-restart" when parts.Length >= 2: int.TryParse(parts[1], CultureInfo.InvariantCulture, out pingRestart); break;
                case "peer-id" when parts.Length >= 2 && uint.TryParse(parts[1], CultureInfo.InvariantCulture, out var id): peerId = id; break;
                case "cipher" when parts.Length >= 2: cipher = parts[1]; break;
                case "explicit-exit-notify": exitNotify = true; break;
                case "protocol-flags": flags.AddRange(parts.Skip(1)); break;
            }
        }
        return new PushedConfig
        {
            Address = address,
            IfconfigSecond = second,
            Topology = topology,
            Gateway = gateway,
            DnsServers = dns,
            Ping = ping,
            PingRestart = pingRestart,
            PeerId = peerId,
            Cipher = cipher,
            ExplicitExitNotify = exitNotify,
            ProtocolFlags = flags,
        };
    }
}
