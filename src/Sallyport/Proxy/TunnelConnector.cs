using System.Net;
using System.Net.Sockets;
using Sallyport.Net;

namespace Sallyport.Proxy;

/// <summary>Dials through a <see cref="TunnelNetwork"/>: the name is resolved by the tunnel's DNS and the connection is
/// the tunnel's own TCP, so neither the lookup nor the traffic touches the machine's network.</summary>
public sealed class TunnelConnector(TunnelNetwork network) : IConnector
{
    /// <summary>How many of a name's addresses are tried before giving up — enough to step past one dead server.</summary>
    private const int AddressesTried = 3;

    /// <inheritdoc/>
    public async ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken ct)
    {
        var addresses = await network.ResolveAsync(host, ct);
        SocketException? last = null;
        foreach (var address in addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Take(AddressesTried))
        {
            try { return await network.ConnectAsync(address, port, ct); }
            catch (SocketException e) { last = e; }
        }
        throw last ?? new SocketException((int)SocketError.NetworkUnreachable);
    }
}
