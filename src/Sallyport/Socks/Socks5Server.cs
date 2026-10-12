using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Sallyport.Socks;

/// <summary>Opens the outbound side of a proxied connection. Names are resolved by the CONNECTOR, never by the proxy: in
/// phase 3 the connector resolves through the VPN's own DNS, so a name lookup cannot leak outside the tunnel.</summary>
/// <remarks>A failure is a <see cref="SocketException"/> (its <see cref="SocketException.SocketErrorCode"/> picks the
/// SOCKS reply code) or any other exception, which is a general failure.</remarks>
public interface IConnector
{
    /// <summary>`host` is a domain name or an IP literal.</summary>
    ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken ct);
}

/// <summary>Dials with the OS. A baseline and a test double: it does NOT go through any tunnel.</summary>
public sealed class DirectConnector : IConnector
{
    public async ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(host, port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>A stream whose write side can end while its read side stays open, so the relay can pass one side's
/// end-of-stream on without cutting the reply still on its way back.</summary>
public interface IHalfClosable
{
    void ShutdownWrite();
}

/// <summary>A SOCKS5 proxy (RFC 1928, CONNECT only) that hands each request to an <see cref="IConnector"/>. It has no
/// authentication, so it binds loopback only — anything else would be an open proxy.</summary>
public sealed class Socks5Server : IAsyncDisposable
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private const int BufferSize = 81920;

    private readonly IConnector _connector;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Task, byte> _connections = new();
    private Task? _accepting;
    private int _active;
    private long _up, _down;

    public Socks5Server(IConnector connector, IPEndPoint? listen = null)
    {
        listen ??= new IPEndPoint(IPAddress.Loopback, 0);
        if (!IPAddress.IsLoopback(listen.Address))
            throw new ArgumentException("The proxy has no authentication, so it may only listen on loopback.", nameof(listen));
        _connector = connector;
        _listener = new TcpListener(listen);
        _listener.Start();
        Endpoint = (IPEndPoint)_listener.LocalEndpoint;
    }

    /// <summary>The bound endpoint, so a requested port 0 shows the real port.</summary>
    public IPEndPoint Endpoint { get; }

    public int ActiveConnections => Volatile.Read(ref _active);
    /// <summary>Bytes relayed client to remote.</summary>
    public long BytesUp => Interlocked.Read(ref _up);
    /// <summary>Bytes relayed remote to client.</summary>
    public long BytesDown => Interlocked.Read(ref _down);

    public void Start() => _accepting ??= Task.Run(AcceptLoopAsync);

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket socket;
            try { socket = await _listener.AcceptSocketAsync(_cts.Token); }
            catch (Exception) when (_cts.IsCancellationRequested) { return; }
            catch (ObjectDisposedException) { return; }
            // One client resetting while still queued (ConnectionReset on Windows) is that client's failure: ending the
            // loop on it would leave a proxy that is up and accepts nothing, which nothing reports.
            catch (SocketException) { continue; }
            Interlocked.Increment(ref _active);
            var task = Task.Run(() => HandleAsync(socket));
            _connections[task] = 0;
            _ = task.ContinueWith(t => _connections.TryRemove(t, out _), TaskScheduler.Default);
        }
    }

    private async Task HandleAsync(Socket socket)
    {
        Stream? client = null, remote = null;
        try
        {
            socket.NoDelay = true;
            client = new NetworkStream(socket, ownsSocket: true);
            // A client that connects and says nothing must not hold a task forever.
            using var hello = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            hello.CancelAfter(HandshakeTimeout);
            var target = await NegotiateAsync(client, hello.Token);
            if (target is not var (host, port)) return;
            try
            {
                remote = await _connector.ConnectAsync(host, port, _cts.Token);
            }
            catch (Exception e) when (!_cts.IsCancellationRequested)
            {
                await ReplyAsync(client, ReplyFor(e), _cts.Token);
                return;
            }
            await ReplyAsync(client, 0x00, _cts.Token);
            await RelayAsync(client, remote, _cts.Token);
        }
        catch (Exception)
        {
            // A connection's failure ends that connection only.
        }
        finally
        {
            if (remote is not null) await remote.DisposeAsync();
            if (client is not null) await client.DisposeAsync(); else socket.Dispose();
            Interlocked.Decrement(ref _active);
        }
    }

    /// <summary>Reads the greeting and the request. Null means the client was refused (and answered, where RFC 1928 says
    /// to) and the connection should end.</summary>
    private static async Task<(string Host, int Port)?> NegotiateAsync(Stream s, CancellationToken ct)
    {
        var head = new byte[2];
        await s.ReadExactlyAsync(head, ct);
        if (head[0] != 5) return null;
        var methods = new byte[head[1]];
        await s.ReadExactlyAsync(methods, ct);
        if (!methods.Contains((byte)0x00))
        {
            await s.WriteAsync(new byte[] { 5, 0xFF }, ct);
            return null;
        }
        await s.WriteAsync(new byte[] { 5, 0x00 }, ct);

        var req = new byte[4];
        await s.ReadExactlyAsync(req, ct);
        if (req[0] != 5) return null;
        if (req[1] != 1) { await ReplyAsync(s, 0x07, ct); return null; }
        string host;
        switch (req[3])
        {
            case 1:
                var v4 = new byte[4];
                await s.ReadExactlyAsync(v4, ct);
                host = new IPAddress(v4).ToString();
                break;
            case 3:
                // One length byte caps a name at 255, so this buffer is bounded by the wire format.
                var len = new byte[1];
                await s.ReadExactlyAsync(len, ct);
                var name = new byte[len[0]];
                await s.ReadExactlyAsync(name, ct);
                host = Encoding.ASCII.GetString(name);
                break;
            case 4:
                var v6 = new byte[16];
                await s.ReadExactlyAsync(v6, ct);
                host = new IPAddress(v6).ToString();
                break;
            default:
                await ReplyAsync(s, 0x08, ct);
                return null;
        }
        var port = new byte[2];
        await s.ReadExactlyAsync(port, ct);
        if (host.Length == 0) { await ReplyAsync(s, 0x01, ct); return null; }
        return (host, (port[0] << 8) | port[1]);
    }

    /// <summary>BND.ADDR 0.0.0.0 and port 0: a CONNECT client does not use them.</summary>
    private static ValueTask ReplyAsync(Stream s, byte rep, CancellationToken ct) =>
        s.WriteAsync(new byte[] { 5, rep, 0, 1, 0, 0, 0, 0, 0, 0 }, ct);

    private static byte ReplyFor(Exception e) => e is SocketException se ? (byte)(se.SocketErrorCode switch
    {
        SocketError.NetworkUnreachable => 0x03,
        SocketError.HostUnreachable or SocketError.HostNotFound or SocketError.NoData => 0x04,
        SocketError.ConnectionRefused => 0x05,
        SocketError.TimedOut => 0x06,
        _ => 0x01,
    }) : (byte)0x01;

    private async Task RelayAsync(Stream client, Stream remote, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Cancelling a read on a socket stream is not guaranteed to be prompt, so closing the streams is what
        // actually unblocks both loops when one fails or the server shuts down.
        using var closer = cts.Token.Register(() =>
        {
            try { client.Dispose(); remote.Dispose(); } catch (Exception) { }
        });
        await Task.WhenAll(
            PumpAsync(client, remote, up: true, cts),
            PumpAsync(remote, client, up: false, cts));
    }

    private async Task PumpAsync(Stream from, Stream to, bool up, CancellationTokenSource cts)
    {
        var buffer = new byte[BufferSize];
        try
        {
            int n;
            while ((n = await from.ReadAsync(buffer, cts.Token)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, n), cts.Token);
                if (up) Interlocked.Add(ref _up, n); else Interlocked.Add(ref _down, n);
            }
            HalfClose(to);
        }
        catch (Exception)
        {
            await cts.CancelAsync();
        }
    }

    private static void HalfClose(Stream to)
    {
        try
        {
            if (to is IHalfClosable h) h.ShutdownWrite();
            else if (to is NetworkStream ns) ns.Socket.Shutdown(SocketShutdown.Send);
        }
        catch (Exception)
        {
            // Already gone: the other loop sees it on its own next read.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        if (_accepting is not null) await _accepting;
        await Task.WhenAll(_connections.Keys);
        _cts.Dispose();
    }
}
