using System.Buffers.Binary;
using System.Threading.Channels;

namespace Sallyport.Net;

/// <summary>
/// How one tunnel's port space is shared (docs/design/window.md, adoption change 1): the proxy's own TCP and DNS use
/// 40960–49151, below where Windows gives programs their ports; the packet router's NAT has the rest, so a routed
/// program's own port — almost always 49152 or above — is kept.
/// </summary>
public static class TunnelPorts
{
    public const int ProxyFirst = 40960;
    public const int ProxyLast = 49151;
    public const int ProxyCount = ProxyLast - ProxyFirst + 1;

    public static bool IsProxy(int port) => port is >= ProxyFirst and <= ProxyLast;
}

/// <summary>What the router side of a channel takes from its tunnel: every packet that is not the proxy's.</summary>
public interface ITunnelRoute : IAsyncDisposable
{
    void FromTunnel(byte[] packet);
}

/// <summary>Where a dispatcher sent what came out of its tunnel. <see cref="Overflowed"/>: the proxy's packets this
/// process threw away itself, its network's reader having fallen 4,096 behind (D7: it looks like loss on the path).</summary>
public readonly record struct DispatchCounters(long ToProxy, long ToRouter, long Unclaimed, long NotForUs, long ToEcho, long Overflowed = 0);

/// <summary>
/// The one reader of a tunnel generation's packets (docs/design/window.md): each goes to the proxy's network or to the
/// packet router, by the port it is addressed to — TCP and UDP by destination port, an ICMP error by the source port of
/// the packet it quotes (the one this side sent). A piece of a datagram after its first carries no port: it goes to the
/// router (adoption change 2), the proxy's own never being cut up. Nothing is guessed: a router-bound packet with no
/// router attached, or one not addressed to the tunnel, is counted and dropped.
/// </summary>
public sealed class TunnelDispatcher : IAsyncDisposable
{
    private const byte Icmp = 1, Tcp = 6, Udp = 17;

    private readonly Channel<byte[]> _proxy;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _reading;
    private readonly uint _address;
    private ITunnelRoute? _route;
    private long _toProxy, _toRouter, _unclaimed, _notForUs, _toEcho, _overflowed;

    public TunnelDispatcher(Tunnel tunnel, int generation)
    {
        // As the tunnel's own queue is, and for its reason: unbounded here, that one would bound nothing.
        _proxy = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4096) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest },
            _ => Interlocked.Increment(ref _overflowed));
        Tunnel = tunnel;
        Generation = generation;
        var address = tunnel.Config.Address is { } a && Ipv4.IsIpv4(a)
            ? a
            : throw new InvalidOperationException("the server pushed no IPv4 address for this client");
        _address = BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
        Network = new TunnelNetwork(tunnel, _proxy.Reader);
        Echo = new TunnelEcho(address, packet => tunnel.SendAsync(packet));
        _reading = Task.Run(() => ReadAsync(_stop.Token));
    }

    public Tunnel Tunnel { get; }
    public int Generation { get; }

    /// <summary>The proxy's userspace TCP and DNS, fed from here.</summary>
    public TunnelNetwork Network { get; }

    /// <summary>The channel's own echoes, for its latency: their answers are claimed here before any other side sees them.</summary>
    public TunnelEcho Echo { get; }

    public DispatchCounters Counters => new(Interlocked.Read(ref _toProxy), Interlocked.Read(ref _toRouter), Interlocked.Read(ref _unclaimed),
        Interlocked.Read(ref _notForUs), Interlocked.Read(ref _toEcho), Interlocked.Read(ref _overflowed));

    /// <summary>Gives the router side this generation's packets from now on. One route at a time; it is disposed with
    /// the generation unless detached first.</summary>
    public void Attach(ITunnelRoute route)
    {
        if (Interlocked.CompareExchange(ref _route, route, null) is not null) throw new InvalidOperationException("a route is attached already");
    }

    /// <summary>Stops giving packets to <paramref name="route"/>; what the router side would have had is dropped since.</summary>
    public void Detach(ITunnelRoute route) => Interlocked.CompareExchange(ref _route, null, route);

    private async Task ReadAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var packet in Tunnel.Packets.ReadAllAsync(ct)) Dispatch(packet);
        }
        // The tunnel's packets end with what ended it; the session says why.
        catch (Exception e) when (e is OperationCanceledException or TunnelEnded or IOException or ObjectDisposedException) { }
        _proxy.Writer.TryComplete();
    }

    private void Dispatch(byte[] packet)
    {
        if (Echo.Claim(packet))
        {
            Interlocked.Increment(ref _toEcho);
            return;
        }
        switch (Classify(packet, _address))
        {
            case Destination.Proxy:
                Interlocked.Increment(ref _toProxy);
                _proxy.Writer.TryWrite(packet);
                break;
            case Destination.Router when Volatile.Read(ref _route) is { } route:
                Interlocked.Increment(ref _toRouter);
                route.FromTunnel(packet);
                break;
            case Destination.Router:
                Interlocked.Increment(ref _unclaimed);
                break;
            default:
                Interlocked.Increment(ref _notForUs);
                break;
        }
    }

    public enum Destination { NotForUs, Proxy, Router }

    /// <summary>Which side a packet from the tunnel is for, by the IPv4 header's destination, its fragment fields, and
    /// the ports: TCP and UDP their destination port, an ICMP error the source port of the packet it quotes (its IPv4
    /// header, then at least 8 bytes of it: RFC 792).</summary>
    public static Destination Classify(ReadOnlySpan<byte> packet, uint address)
    {
        if (packet.Length < 20 || packet[0] >> 4 != 4) return Destination.NotForUs;
        var headerLength = (packet[0] & 0x0F) * 4;
        if (headerLength < 20 || packet.Length < headerLength) return Destination.NotForUs;
        if (BinaryPrimitives.ReadUInt32BigEndian(packet[16..]) != address) return Destination.NotForUs;
        var fragmentOffset = BinaryPrimitives.ReadUInt16BigEndian(packet[6..]) & 0x1FFF;
        if (fragmentOffset != 0) return Destination.Router;
        var protocol = packet[9];
        var transport = packet[headerLength..];
        switch (protocol)
        {
            case Tcp or Udp:
                if (transport.Length < 4) return Destination.NotForUs;
                return TunnelPorts.IsProxy(BinaryPrimitives.ReadUInt16BigEndian(transport[2..])) ? Destination.Proxy : Destination.Router;
            case Icmp when transport.Length >= 8 + 20 && transport[0] is 3 or 11 or 12:
                var quote = transport[8..];
                var quotedLength = (quote[0] & 0x0F) * 4;
                if (quote[0] >> 4 != 4 || quotedLength < 20 || quote.Length < quotedLength + 8) return Destination.Router;
                return quote[9] is Tcp or Udp && TunnelPorts.IsProxy(BinaryPrimitives.ReadUInt16BigEndian(quote[quotedLength..]))
                    ? Destination.Proxy : Destination.Router;
            default:
                return Destination.Router;
        }
    }

    /// <summary>Stops reading, lets the router side go (retiring its generation) and resets the proxy's connections.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _route, null) is { } route)
            await route.DisposeAsync();
        await _stop.CancelAsync();
        await _reading;
        await Network.DisposeAsync();
        _stop.Dispose();
    }
}
