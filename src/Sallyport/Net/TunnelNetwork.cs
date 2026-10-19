using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace Sallyport.Net;

/// <summary>What the userspace network has done, for a host to show.</summary>
/// <remarks><see cref="SegmentsOutOfOrder"/> and <see cref="SegmentsRepeated"/> are the only view of loss on the way IN:
/// a server's retransmissions cannot be counted from this side, only their traces.</remarks>
public readonly record struct NetworkCounters(int OpenConnections, long ConnectionsOpened, long ConnectsFailed, long Retransmits,
    long Lookups, long LookupsFromCache, long PacketsDropped, long SegmentsIn, long SegmentsOutOfOrder, long SegmentsRepeated, long AcksSent);

/// <summary>
/// A userspace network over a <see cref="Tunnel"/>: outbound TCP connections and DNS lookups, as the tunnel's own
/// address, with nothing touching the machine's network stack or routing table (D2). It reads what a
/// <see cref="TunnelDispatcher"/> gives it, or, alone on a tunnel, <see cref="Tunnel.Packets"/> itself. Its ports are the
/// proxy's share of the tunnel's (<see cref="TunnelPorts"/>), so a packet router can share the tunnel.
/// </summary>
public sealed class TunnelNetwork : IAsyncDisposable, IPacketSink
{
    private const int FirstEphemeralPort = TunnelPorts.ProxyFirst, EphemeralPorts = TunnelPorts.ProxyCount;

    private readonly Tunnel _tunnel;
    private readonly ChannelReader<byte[]> _inbound;
    private readonly IPAddress _local;
    private readonly ConcurrentDictionary<(IPAddress Remote, ushort RemotePort, ushort LocalPort), TcpConnection> _tcp = new();
    private readonly ConcurrentDictionary<ushort, Action<Ipv4Header, ushort, ArraySegment<byte>>> _udp = new();
    private readonly Channel<byte[]> _outbound = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly TunnelResolver _resolver;
    private readonly Task _loops;
    private int _nextPort = RandomNumberGenerator.GetInt32(EphemeralPorts), _ipId;
    private long _opened, _failed, _dropped;
    // What connections already forgotten contributed, so the totals do not shrink when one expires.
    private long _closedRetransmits, _closedIn, _closedOutOfOrder, _closedRepeated, _closedAcks;

    public TunnelNetwork(Tunnel tunnel, ChannelReader<byte[]>? inbound = null)
    {
        _tunnel = tunnel;
        _inbound = inbound ?? tunnel.Packets;
        _local = tunnel.Config.Address is { } address && Ipv4.IsIpv4(address)
            ? address
            : throw new InvalidOperationException("the server pushed no IPv4 address for this client");
        _resolver = new TunnelResolver(this, tunnel.Config.DnsServers);
        _loops = Task.WhenAll(
            Task.Run(() => ReadLoopAsync(_stop.Token)),
            Task.Run(() => SendLoopAsync(_stop.Token)),
            Task.Run(() => TickLoopAsync(_stop.Token)));
    }

    public IPAddress LocalAddress => _local;

    public NetworkCounters Counters
    {
        get
        {
            var live = _tcp.Values.ToList();
            return new(live.Count, Interlocked.Read(ref _opened), Interlocked.Read(ref _failed),
                Interlocked.Read(ref _closedRetransmits) + live.Sum(c => (long)c.Retransmits),
                _resolver.Lookups, _resolver.FromCache, Interlocked.Read(ref _dropped),
                Interlocked.Read(ref _closedIn) + live.Sum(c => (long)c.SegmentsIn),
                Interlocked.Read(ref _closedOutOfOrder) + live.Sum(c => (long)c.SegmentsOutOfOrder),
                Interlocked.Read(ref _closedRepeated) + live.Sum(c => (long)c.SegmentsRepeated),
                Interlocked.Read(ref _closedAcks) + live.Sum(c => (long)c.AcksSent));
        }
    }

    /// <summary>Resolves through the tunnel's DNS servers, never the machine's. Throws <see cref="SocketException"/>:
    /// HostNotFound when a server answered that the name does not exist, TryAgain when no server answered at all.</summary>
    public Task<IReadOnlyList<IPAddress>> ResolveAsync(string name, CancellationToken ct = default) => _resolver.ResolveAsync(name, ct);

    /// <summary>A TCP connection to <paramref name="address"/> through the tunnel, once its handshake completes.</summary>
    public async Task<Stream> ConnectAsync(IPAddress address, int port, CancellationToken ct = default)
    {
        // The tunnel carries IPv4 (the server pushes block-ipv6); a v6 destination is unreachable from inside it.
        if (!Ipv4.IsIpv4(address)) throw new SocketException((int)SocketError.NetworkUnreachable);
        var remotePort = checked((ushort)port);
        TcpConnection connection;
        while (true)
        {
            var localPort = (ushort)(FirstEphemeralPort + (uint)Interlocked.Increment(ref _nextPort) % EphemeralPorts);
            connection = new TcpConnection(this, Clock, _local, localPort, address, remotePort);
            if (_tcp.TryAdd((address, remotePort, localPort), connection)) break;
        }
        Interlocked.Increment(ref _opened);
        connection.Open();
        try
        {
            await connection.Connected.WaitAsync(ct);
            return connection;
        }
        catch
        {
            Interlocked.Increment(ref _failed);
            connection.Abort();
            throw;
        }
    }

    internal void ListenUdp(ushort port, Action<Ipv4Header, ushort, ArraySegment<byte>> handler) => _udp[port] = handler;

    internal void SendUdp(ushort localPort, IPAddress destination, ushort port, ReadOnlySpan<byte> payload) =>
        Send(Ipv4.UdpPacket(_local, localPort, destination, port, payload, NextIpId()));

    void IPacketSink.Send(byte[] packet) => Send(packet);
    private void Send(byte[] packet) => _outbound.Writer.TryWrite(packet);
    public ushort NextIpId() => (ushort)Interlocked.Increment(ref _ipId);
    private static long Clock() => Environment.TickCount64;

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var touched = new HashSet<TcpConnection>();
        try
        {
            while (await _inbound.WaitToReadAsync(ct))
            {
                while (_inbound.TryRead(out var packet)) Dispatch(packet, touched);
                // The queue is empty: acknowledge everything that arrived in this burst with one ACK per connection.
                foreach (var connection in touched) connection.FlushAck();
                touched.Clear();
            }
            EndAll(SocketError.NetworkDown);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception) { EndAll(SocketError.NetworkDown); }
    }

    private void Dispatch(byte[] packet, HashSet<TcpConnection> touched)
    {
        if (!Ipv4.TryRead(packet, out var header) || !header.Destination.Equals(_local))
        {
            Interlocked.Increment(ref _dropped);
            return;
        }
        if (header.Protocol == Ipv4.Tcp && Tcp.TryRead(packet, header, out var segment))
        {
            if (_tcp.TryGetValue((header.Source, segment.SourcePort, segment.DestinationPort), out var connection))
            {
                connection.OnSegment(segment);
                touched.Add(connection);
            }
            else
            {
                Interlocked.Increment(ref _dropped);
                RefuseStray(header, segment);
            }
            return;
        }
        if (header.Protocol == Ipv4.Udp && Ipv4.TryReadUdp(packet, out header, out var from, out var to, out var payload)
            && _udp.TryGetValue(to, out var handler))
        {
            handler(header, from, payload);
            return;
        }
        Interlocked.Increment(ref _dropped);
    }

    /// <summary>A segment for no connection — usually a retransmission after this side forgot one — gets a reset, so
    /// the peer stops instead of retrying for minutes.</summary>
    private void RefuseStray(in Ipv4Header header, in TcpSegment segment)
    {
        if (segment.Has(TcpFlags.Rst)) return;
        var length = (uint)segment.Payload.Count + (segment.Has(TcpFlags.Syn) ? 1u : 0) + (segment.Has(TcpFlags.Fin) ? 1u : 0);
        Send(segment.Has(TcpFlags.Ack)
            ? Tcp.Build(_local, header.Source, NextIpId(), segment.DestinationPort, segment.SourcePort, segment.Ack, 0, TcpFlags.Rst, 0, [])
            : Tcp.Build(_local, header.Source, NextIpId(), segment.DestinationPort, segment.SourcePort, 0, segment.Seq + length, TcpFlags.Rst | TcpFlags.Ack, 0, []));
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var packet in _outbound.Reader.ReadAllAsync(ct)) await _tunnel.SendAsync(packet, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception) { EndAll(SocketError.NetworkDown); }
    }

    private async Task TickLoopAsync(CancellationToken ct)
    {
        // 20 ms: well under the smallest retransmission timeout (300 ms), so a timer never fires late by much.
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var now = Clock();
                foreach (var (key, connection) in _tcp)
                {
                    connection.OnTick(now);
                    if (connection.Expired(now) && _tcp.TryRemove(key, out _))
                    {
                        Interlocked.Add(ref _closedRetransmits, connection.Retransmits);
                        Interlocked.Add(ref _closedIn, connection.SegmentsIn);
                        Interlocked.Add(ref _closedOutOfOrder, connection.SegmentsOutOfOrder);
                        Interlocked.Add(ref _closedRepeated, connection.SegmentsRepeated);
                        Interlocked.Add(ref _closedAcks, connection.AcksSent);
                    }
                }
                _resolver.OnTick(now);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private void EndAll(SocketError reason)
    {
        foreach (var connection in _tcp.Values) connection.Abort(reason);
        _resolver.EndAll();
    }

    /// <summary>Resets every open connection and stops. The tunnel itself stays open; its owner closes it.</summary>
    public async ValueTask DisposeAsync()
    {
        EndAll(SocketError.ConnectionAborted);
        // Give the resets a moment to leave before the send loop stops.
        await Task.Delay(50);
        await _stop.CancelAsync();
        _outbound.Writer.TryComplete();
        try { await _loops; } catch (OperationCanceledException) { }
        _stop.Dispose();
    }
}

/// <summary>DNS through the tunnel: A records from the pushed servers, cached for their TTL.</summary>
internal sealed class TunnelResolver
{
    private static readonly TimeSpan AskAgain = TimeSpan.FromMilliseconds(1500);
    private const int Attempts = 4;
    private const uint MinTtl = 30, MaxTtl = 3600, NegativeTtl = 15;

    private readonly TunnelNetwork _network;
    private readonly IReadOnlyList<IPAddress> _servers;
    private readonly ushort _port;
    private readonly ConcurrentDictionary<ushort, TaskCompletionSource<(int Code, List<IPAddress> Addresses, uint Ttl)>> _pending = new();
    private readonly ConcurrentDictionary<string, (IReadOnlyList<IPAddress>? Addresses, long Until)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private long _lookups, _fromCache;

    public TunnelResolver(TunnelNetwork network, IReadOnlyList<IPAddress> pushed)
    {
        _network = network;
        // No pushed resolver: a public one, still reached THROUGH the tunnel — never the machine's own.
        _servers = pushed.Where(Ipv4.IsIpv4).ToList() is { Count: > 0 } servers ? servers : [IPAddress.Parse("1.1.1.1")];
        _port = (ushort)RandomNumberGenerator.GetInt32(TunnelPorts.ProxyFirst, TunnelPorts.ProxyLast + 1);
        network.ListenUdp(_port, OnDatagram);
    }

    public long Lookups => Interlocked.Read(ref _lookups);
    public long FromCache => Interlocked.Read(ref _fromCache);

    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string name, CancellationToken ct)
    {
        if (IPAddress.TryParse(name, out var literal)) return [literal];
        name = name.TrimEnd('.');
        Interlocked.Increment(ref _lookups);
        var now = Environment.TickCount64;
        if (_cache.TryGetValue(name, out var cached) && cached.Until > now)
        {
            Interlocked.Increment(ref _fromCache);
            return cached.Addresses ?? throw new SocketException((int)SocketError.HostNotFound);
        }
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            ushort id;
            TaskCompletionSource<(int, List<IPAddress>, uint)> waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
            do id = (ushort)RandomNumberGenerator.GetInt32(1, 65536); while (!_pending.TryAdd(id, waiter));
            try
            {
                _network.SendUdp(_port, _servers[attempt % _servers.Count], 53, DnsMessage.Query(id, name, DnsMessage.TypeA));
                var (code, addresses, ttl) = await waiter.Task.WaitAsync(AskAgain, ct);
                if (code == 0 && addresses.Count > 0)
                {
                    _cache[name] = (addresses, Environment.TickCount64 + Math.Clamp(ttl, MinTtl, MaxTtl) * 1000);
                    return addresses;
                }
                // NXDOMAIN, or a name with no IPv4 address: an answer, and a negative one.
                if (code == 3 || code == 0)
                {
                    _cache[name] = (null, Environment.TickCount64 + NegativeTtl * 1000);
                    throw new SocketException((int)SocketError.HostNotFound);
                }
                // SERVFAIL and the like say nothing about the name: ask again, the next server if there is one.
            }
            catch (TimeoutException) { }
            finally { _pending.TryRemove(id, out _); }
        }
        // No server answered: that is not "the name does not exist", and must not be reported as it.
        throw new SocketException((int)SocketError.TryAgain);
    }

    private void OnDatagram(Ipv4Header header, ushort from, ArraySegment<byte> payload)
    {
        if (from != 53 || payload.Count < 2) return;
        var id = (ushort)(payload[0] << 8 | payload[1]);
        if (_pending.TryGetValue(id, out var waiter)
            && DnsMessage.TryReadAnswers(payload, id, DnsMessage.TypeA, out var code, out var addresses, out var ttl))
            waiter.TrySetResult((code, addresses, ttl));
    }

    public void OnTick(long now)
    {
        // Expired entries are dropped now and then rather than on read, so a long session's cache does not only grow.
        if (now % 10_000 >= 20) return;
        foreach (var (name, entry) in _cache) if (entry.Until <= now) _cache.TryRemove(name, out _);
    }

    public void EndAll()
    {
        foreach (var waiter in _pending.Values) waiter.TrySetException(new SocketException((int)SocketError.NetworkDown));
    }
}
