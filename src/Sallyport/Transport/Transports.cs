using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Sallyport.Profile;

namespace Sallyport.Transport;

/// <summary>Moves whole packets to and from one server. UDP carries one packet per datagram; TCP frames each with a
/// 2-byte length.</summary>
internal interface ITransport : IAsyncDisposable
{
    /// <summary>The transport itself never loses a packet, so the control channel must not resend.</summary>
    bool IsReliable { get; }
    ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken ct);
    ValueTask<byte[]> ReceiveAsync(CancellationToken ct);
}

internal static class Transports
{
    public static async Task<ITransport> ConnectAsync(Remote remote, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(remote.Host, ct);
        if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);
        // IPv4 first: a profile's servers are reachable over it far more often than over IPv6 from a home connection.
        var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0];
        var endpoint = new IPEndPoint(address, remote.Port);
        return remote.Proto == "tcp" ? await TcpTransport.ConnectAsync(endpoint, ct) : await UdpTransport.ConnectAsync(endpoint, ct);
    }
}

internal sealed class UdpTransport : ITransport
{
    private readonly Socket _socket;
    private readonly byte[] _buffer = new byte[65536];

    private UdpTransport(Socket socket) => _socket = socket;

    public static async Task<UdpTransport> ConnectAsync(IPEndPoint endpoint, CancellationToken ct)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        if (OperatingSystem.IsWindows())
        {
            // SIO_UDP_CONNRESET off: otherwise one ICMP "port unreachable" makes every later receive throw, and a
            // server that is merely restarting looks like a dead socket.
            socket.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
        }
        await socket.ConnectAsync(endpoint, ct);
        return new UdpTransport(socket);
    }

    public bool IsReliable => false;

    public async ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken ct) =>
        await _socket.SendAsync(packet, SocketFlags.None, ct);

    public async ValueTask<byte[]> ReceiveAsync(CancellationToken ct)
    {
        var n = await _socket.ReceiveAsync(_buffer, SocketFlags.None, ct);
        return _buffer.AsSpan(0, n).ToArray();
    }

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class TcpTransport : ITransport
{
    private readonly Socket _socket;
    private readonly NetworkStream _stream;

    private TcpTransport(Socket socket)
    {
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: false);
    }

    public static async Task<TcpTransport> ConnectAsync(IPEndPoint endpoint, CancellationToken ct)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await socket.ConnectAsync(endpoint, ct);
        return new TcpTransport(socket);
    }

    public bool IsReliable => true;

    public async ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken ct)
    {
        var framed = new byte[2 + packet.Length];
        BinaryPrimitives.WriteUInt16BigEndian(framed, checked((ushort)packet.Length));
        packet.CopyTo(framed.AsMemory(2));
        await _stream.WriteAsync(framed, ct);
    }

    public async ValueTask<byte[]> ReceiveAsync(CancellationToken ct)
    {
        var length = new byte[2];
        await _stream.ReadExactlyAsync(length, ct);
        var packet = new byte[BinaryPrimitives.ReadUInt16BigEndian(length)];
        await _stream.ReadExactlyAsync(packet, ct);
        return packet;
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        _socket.Dispose();
    }
}
