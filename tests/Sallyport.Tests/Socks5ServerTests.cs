using System.Net;
using System.Net.Sockets;
using Sallyport.Socks;

namespace Sallyport.Tests;

public class Socks5ServerTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    /// <summary>Echoes until EOF, then closes. With <c>replyAtEof</c> it holds everything back until the client's EOF, so
    /// only a half-close that propagates lets the reply arrive.</summary>
    private sealed class EchoServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _loop;

        public EchoServer(bool replyAtEof = false)
        {
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var client = await _listener.AcceptTcpClientAsync();
                        _ = Task.Run(() => Serve(client, replyAtEof));
                    }
                }
                catch (Exception) { }
            });
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        private static async Task Serve(TcpClient client, bool replyAtEof)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var buffer = new byte[8192];
                    var held = new MemoryStream();
                    int n;
                    while ((n = await stream.ReadAsync(buffer)) > 0)
                    {
                        if (replyAtEof) held.Write(buffer, 0, n);
                        else await stream.WriteAsync(buffer.AsMemory(0, n));
                    }
                    if (replyAtEof) await stream.WriteAsync(held.ToArray());
                }
                catch (Exception) { }
            }
        }

        public ValueTask DisposeAsync()
        {
            _listener.Stop();
            return new ValueTask(_loop);
        }
    }

    /// <summary>Reads until the client's EOF, then sends one byte every <c>interval</c> for <c>ticks</c> times — and never
    /// closes its own side, as some real servers do after a client's FIN.</summary>
    private sealed class NeverClosingServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public NeverClosingServer(int ticks = 0, TimeSpan interval = default)
        {
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                try
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var stream = client.GetStream();
                    var buffer = new byte[8192];
                    while (await stream.ReadAsync(buffer, _stop.Token) > 0) { }
                    for (var i = 0; i < ticks; i++)
                    {
                        await Task.Delay(interval, _stop.Token);
                        await stream.WriteAsync(new[] { (byte)i }, _stop.Token);
                    }
                    await Task.Delay(Timeout.Infinite, _stop.Token);
                }
                catch (Exception) { }
            });
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _loop;
            _stop.Dispose();
        }
    }

    private sealed class StubConnector(Func<string, int, Exception?> fail) : IConnector
    {
        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken ct) =>
            throw fail(host, port)!;
    }

    /// <summary>Maps "localhost" to 127.0.0.1 before dialling: the OS may resolve it to ::1 first, and the echo listener
    /// is IPv4-only. Cheaper and more deterministic than a dual-mode listener, which needs IPv6 on the machine.</summary>
    private sealed class LocalhostIsV4Connector : IConnector
    {
        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken ct) =>
            new DirectConnector().ConnectAsync(host == "localhost" ? "127.0.0.1" : host, port, ct);
    }

    private static byte[] Request(byte cmd, byte atyp, byte[] address, int port) =>
        [5, cmd, 0, atyp, .. address, (byte)(port >> 8), (byte)port];

    private static byte[] V4(int port, byte cmd = 1) => Request(cmd, 1, [127, 0, 0, 1], port);

    private static byte[] Domain(string name, int port) =>
        Request(1, 3, [(byte)name.Length, .. System.Text.Encoding.ASCII.GetBytes(name)], port);

    private static async Task<(TcpClient Client, NetworkStream Stream)> OpenAsync(Socks5Server server)
    {
        var client = new TcpClient();
        await client.ConnectAsync(server.Endpoint);
        return (client, client.GetStream());
    }

    /// <summary>The handshake by hand; the reply byte is returned, and the stream is ready for payload on 0x00.</summary>
    private static async Task<(TcpClient Client, NetworkStream Stream, byte Rep)> HandshakeAsync(
        Socks5Server server, byte[] request, CancellationToken ct)
    {
        var (client, stream) = await OpenAsync(server);
        await stream.WriteAsync(new byte[] { 5, 1, 0 }, ct);
        var method = new byte[2];
        await stream.ReadExactlyAsync(method, ct);
        Assert.Equal(new byte[] { 5, 0 }, method);
        await stream.WriteAsync(request, ct);
        var reply = new byte[10];
        await stream.ReadExactlyAsync(reply, ct);
        Assert.Equal(5, reply[0]);
        return (client, stream, reply[1]);
    }

    private static Socks5Server Started(IConnector connector)
    {
        var server = new Socks5Server(connector);
        server.Start();
        return server;
    }

    [Fact]
    public async Task Connect_by_ipv4_relays_100_KB_intact()
    {
        using var cts = new CancellationTokenSource(Limit);
        await using var echo = new EchoServer();
        await using var server = Started(new DirectConnector());
        var (client, stream, rep) = await HandshakeAsync(server, V4(echo.Port), cts.Token);
        using var _ = client;
        Assert.Equal(0x00, rep);

        var payload = new byte[100 * 1024];
        Random.Shared.NextBytes(payload);
        var write = Task.Run(async () =>
        {
            await stream.WriteAsync(payload, cts.Token);
            client.Client.Shutdown(SocketShutdown.Send);
        }, cts.Token);
        var back = new MemoryStream();
        await stream.CopyToAsync(back, cts.Token);
        await write;
        Assert.Equal(payload, back.ToArray());
        Assert.Equal(payload.Length, server.BytesUp);
        Assert.Equal(payload.Length, server.BytesDown);
    }

    [Fact]
    public async Task Connect_by_domain_name_succeeds()
    {
        using var cts = new CancellationTokenSource(Limit);
        await using var echo = new EchoServer();
        await using var server = Started(new LocalhostIsV4Connector());
        var (client, stream, rep) = await HandshakeAsync(server, Domain("localhost", echo.Port), cts.Token);
        using var _ = client;
        Assert.Equal(0x00, rep);
        await stream.WriteAsync("hi"u8.ToArray(), cts.Token);
        var back = new byte[2];
        await stream.ReadExactlyAsync(back, cts.Token);
        Assert.Equal("hi"u8.ToArray(), back);
    }

    [Fact]
    public async Task Greeting_without_no_auth_gets_no_acceptable_method()
    {
        using var cts = new CancellationTokenSource(Limit);
        await using var server = Started(new DirectConnector());
        var (client, stream) = await OpenAsync(server);
        using var _ = client;
        await stream.WriteAsync(new byte[] { 5, 1, 2 }, cts.Token);
        var reply = new byte[2];
        await stream.ReadExactlyAsync(reply, cts.Token);
        Assert.Equal(new byte[] { 5, 0xFF }, reply);
        Assert.Equal(0, await stream.ReadAsync(new byte[1], cts.Token));
    }

    [Fact]
    public async Task Bind_is_not_supported()
    {
        using var cts = new CancellationTokenSource(Limit);
        await using var server = Started(new DirectConnector());
        var (client, _, rep) = await HandshakeAsync(server, V4(1, cmd: 2), cts.Token);
        client.Dispose();
        Assert.Equal(0x07, rep);
    }

    [Fact]
    public async Task Unknown_address_type_gets_0x08()
    {
        using var cts = new CancellationTokenSource(Limit);
        await using var server = Started(new DirectConnector());
        var (client, _, rep) = await HandshakeAsync(server, Request(1, 9, [1, 2, 3, 4], 80), cts.Token);
        client.Dispose();
        Assert.Equal(0x08, rep);
    }

    [Fact]
    public async Task Closed_port_gets_connection_refused()
    {
        using var cts = new CancellationTokenSource(Limit);
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        await using var server = Started(new DirectConnector());
        var (client, _, rep) = await HandshakeAsync(server, V4(port), cts.Token);
        client.Dispose();
        Assert.Equal(0x05, rep);
    }

    [Theory]
    [InlineData(SocketError.HostNotFound, 0x04)]
    [InlineData(SocketError.NetworkUnreachable, 0x03)]
    [InlineData(SocketError.TimedOut, 0x06)]
    public async Task Connector_socket_error_picks_the_reply(SocketError error, int expected)
    {
        using var cts = new CancellationTokenSource(Limit);
        await using var server = Started(new StubConnector((_, _) => new SocketException((int)error)));
        var (client, _, rep) = await HandshakeAsync(server, Domain("nowhere.invalid", 80), cts.Token);
        client.Dispose();
        Assert.Equal(expected, rep);
    }

    [Fact]
    public async Task Connector_other_exception_is_a_general_failure()
    {
        using var cts = new CancellationTokenSource(Limit);
        await using var server = Started(new StubConnector((_, _) => new InvalidOperationException()));
        var (client, _, rep) = await HandshakeAsync(server, V4(80), cts.Token);
        client.Dispose();
        Assert.Equal(0x01, rep);
    }

    [Fact]
    public async Task Half_close_passes_end_of_stream_while_the_reply_still_flows()
    {
        using var cts = new CancellationTokenSource(Limit);
        await using var echo = new EchoServer(replyAtEof: true);
        await using var server = Started(new DirectConnector());
        var (client, stream, rep) = await HandshakeAsync(server, V4(echo.Port), cts.Token);
        using var _ = client;
        Assert.Equal(0x00, rep);

        await stream.WriteAsync("ping"u8.ToArray(), cts.Token);
        client.Client.Shutdown(SocketShutdown.Send);
        var back = new MemoryStream();
        await stream.CopyToAsync(back, cts.Token);
        Assert.Equal("ping"u8.ToArray(), back.ToArray());
    }

    [Fact]
    public async Task A_half_closed_relay_whose_server_stays_silent_is_let_go_after_the_silence_limit()
    {
        using var cts = new CancellationTokenSource(Limit);
        await using var silent = new NeverClosingServer();
        await using var server = new Socks5Server(new DirectConnector()) { HalfClosedSilence = TimeSpan.FromMilliseconds(300) };
        server.Start();
        var (client, stream, rep) = await HandshakeAsync(server, V4(silent.Port), cts.Token);
        using var _ = client;
        Assert.Equal(0x00, rep);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        client.Client.Shutdown(SocketShutdown.Send);

        // The proxy gives up on the silent side and closes ours: the read ends rather than waiting for the test's limit.
        Assert.Equal(0, await stream.ReadAsync(new byte[1], cts.Token));
        Assert.InRange(clock.Elapsed, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(3));
        for (var i = 0; i < 100 && server.ActiveConnections > 0; i++) await Task.Delay(10, cts.Token);
        Assert.Equal(0, server.ActiveConnections);
    }

    [Fact]
    public async Task A_half_closed_relay_stays_up_while_the_open_side_keeps_talking()
    {
        using var cts = new CancellationTokenSource(Limit);
        // Six bytes 150 ms apart: 900 ms in all, three times the silence limit, but never 300 ms without a word.
        await using var talking = new NeverClosingServer(ticks: 6, interval: TimeSpan.FromMilliseconds(150));
        await using var server = new Socks5Server(new DirectConnector()) { HalfClosedSilence = TimeSpan.FromMilliseconds(300) };
        server.Start();
        var (client, stream, rep) = await HandshakeAsync(server, V4(talking.Port), cts.Token);
        using var _ = client;
        Assert.Equal(0x00, rep);

        client.Client.Shutdown(SocketShutdown.Send);
        var got = new byte[6];
        await stream.ReadExactlyAsync(got, cts.Token);

        Assert.Equal(new byte[] { 0, 1, 2, 3, 4, 5 }, got);
        // And once it falls silent, the limit applies again.
        Assert.Equal(0, await stream.ReadAsync(new byte[1], cts.Token));
    }

    [Fact]
    public void Non_loopback_listen_address_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new Socks5Server(new DirectConnector(), new IPEndPoint(IPAddress.Any, 0)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Greeting_and_request_work_in_one_write_or_byte_by_byte(bool byteByByte)
    {
        using var cts = new CancellationTokenSource(Limit);
        await using var echo = new EchoServer();
        await using var server = Started(new DirectConnector());
        var (client, stream) = await OpenAsync(server);
        using var _ = client;
        byte[] all = [5, 1, 0, .. V4(echo.Port)];
        if (byteByByte)
            foreach (var b in all)
            {
                await stream.WriteAsync(new[] { b }, cts.Token);
                await stream.FlushAsync(cts.Token);
            }
        else await stream.WriteAsync(all, cts.Token);

        var reply = new byte[12];
        await stream.ReadExactlyAsync(reply, cts.Token);
        Assert.Equal(new byte[] { 5, 0 }, reply[..2]);
        Assert.Equal(0x00, reply[3]);
        await stream.WriteAsync("x"u8.ToArray(), cts.Token);
        var back = new byte[1];
        await stream.ReadExactlyAsync(back, cts.Token);
        Assert.Equal((byte)'x', back[0]);
    }

    [Fact]
    public async Task Dispose_closes_connections_and_stops_listening()
    {
        using var cts = new CancellationTokenSource(Limit);
        await using var echo = new EchoServer();
        var server = Started(new DirectConnector());
        var (client, _, rep) = await HandshakeAsync(server, V4(echo.Port), cts.Token);
        using var _ = client;
        Assert.Equal(0x00, rep);
        Assert.Equal(1, server.ActiveConnections);

        await server.DisposeAsync();
        Assert.Equal(0, server.ActiveConnections);
        await Assert.ThrowsAsync<SocketException>(async () =>
        {
            using var late = new TcpClient();
            await late.ConnectAsync(server.Endpoint, cts.Token);
        });
    }
}
