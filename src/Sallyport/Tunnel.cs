using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Sallyport.Protocol;
using Sallyport.Profile;

namespace Sallyport;

/// <summary>The tunnel stopped after it was up: the server went silent, said to stop, or the transport failed.</summary>
public sealed class TunnelEnded(string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>The server ended it with AUTH_FAILED (as when it renegotiates and no longer accepts the client).</summary>
    public bool AuthenticationRefused { get; init; }
}

/// <summary>A snapshot of what has crossed the tunnel. <see cref="OppositeDirection"/> counts packets that authenticate
/// only under this side's own sending key — both sides chose the same key half, which no other fault produces.</summary>
/// <remarks><see cref="MissingIn"/> counts packet ids the server sent that never opened here, wherever they were lost.
/// <see cref="Overflowed"/> counts what this process threw away itself, before opening or after, because a queue
/// between two of its own loops was full. Without it, a reader falling behind reads as a lossy path.</remarks>
public readonly record struct TunnelCounters(
    long PacketsIn, long PacketsOut, long BytesIn, long BytesOut, long PingsIn, long PingsOut,
    long Rejected, long OppositeDirection, long Replayed, int ControlDropped, long MissingIn, long Overflowed);

/// <summary>Options a caller rarely sets.</summary>
public sealed record TunnelOptions
{
    /// <summary>Which half of the derived key block this client sends data with. 0 unless measured otherwise —
    /// <see cref="TunnelCounters.OppositeDirection"/> says when it is wrong.</summary>
    public int DataSendHalf { get; init; }
    /// <summary>The keepalive when the server pushes none (<see cref="ConnectionPolicy.Ping"/>,
    /// <see cref="ConnectionPolicy.PingRestart"/>): a pushed value outranks the client's, as in OpenVPN, unless
    /// <see cref="KeepaliveOverridesServer"/> (its <c>pull-filter ignore</c>).</summary>
    public int? Ping { get; init; }
    /// <summary>Seconds of silence from the server after which the tunnel is dead, when the server pushes none.</summary>
    public int? PingRestart { get; init; }
    /// <summary>This client's keepalive outranks the server's pushed one.</summary>
    public bool KeepaliveOverridesServer { get; init; }
    /// <summary>The username and password a profile that asks for them (<c>auth-user-pass</c>) signs in with; every
    /// reconnect of a session signs in with them again.</summary>
    public Login? Login { get; init; }
}

/// <summary>A username and password, sent in the key-method-2 message after the options string. A class, not a record:
/// nothing that prints the options it rides in (a record's own ToString would) may print either.</summary>
public sealed class Login(string username, string password)
{
    /// <summary>The username.</summary>
    public string Username { get; } = username;
    /// <summary>The password. Never show or log it.</summary>
    public string Password { get; } = password;

    /// <summary>"Login (hidden)", whatever it holds.</summary>
    public override string ToString() => "Login (hidden)";
}

/// <summary>
/// One connection to an OpenVPN-compatible server: the control channel up to the push reply, then the data channel —
/// IPv4 packets in and out, keepalive, and the server's later control messages. It ends in packets, not an adapter (D2).
/// The handshake that makes one is <see cref="TunnelHandshake"/>'s.
/// </summary>
public sealed class Tunnel : IAsyncDisposable
{
    /// <summary>The later control messages kept for <see cref="OtherMessages"/>: the latest, not a whole day's.</summary>
    private const int OtherKept = 64;

    private readonly ControlChannel _channel;
    private readonly SslStream _tls;
    private readonly TlsInbox _inbox;
    private readonly DataChannel _data;
    private readonly X509Certificate2? _certificate;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<byte[]> _packets;
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<string> _other;
    private Task _loops = Task.CompletedTask;
    private long _lastSent, _lastReceived;
    // The keepalive in force: the server's, or the client's own where it pushed none.
    private readonly int _ping, _pingRestart;
    private long _packetsIn, _packetsOut, _bytesIn, _bytesOut, _pingsIn, _pingsOut, _rejected, _opposite, _replayed, _overflowed;
    private int _disposed;

    internal Tunnel(ControlChannel channel, SslStream tls, TlsInbox inbox, DataChannel data, X509Certificate2? certificate, List<string> other,
        int ping, int pingRestart)
    {
        _channel = channel;
        _tls = tls;
        _inbox = inbox;
        _data = data;
        _certificate = certificate;
        _other = other;
        _ping = ping;
        _pingRestart = pingRestart;
        // Lossy for the same reason as the control channel's data queue: an IP packet nobody reads in time is dropped.
        _packets = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4096) { SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest },
            _ => Interlocked.Increment(ref _overflowed));
    }

    /// <summary>The remote this tunnel went to.</summary>
    public required Remote Remote { get; init; }
    /// <summary>The control channel's TLS version, as negotiated.</summary>
    public required string TlsProtocol { get; init; }
    /// <summary>The control channel's TLS cipher suite, as negotiated.</summary>
    public required string CipherSuite { get; init; }
    /// <summary>The subject of the server's certificate.</summary>
    public required string ServerSubject { get; init; }
    /// <summary>The options the server pushed, in order. May include <c>auth-token</c> — show them through
    /// <see cref="Sallyport.PushOptions.Redact"/>.</summary>
    public required IReadOnlyList<string> PushOptions { get; init; }
    /// <summary>What the push reply configured: the address, the DNS servers, the cipher.</summary>
    public required PushedConfig Config { get; init; }
    /// <summary>How long the handshake took, from the first packet to the data channel.</summary>
    public required TimeSpan ConnectTime { get; init; }

    /// <summary>What the tunnel adds to each packet on the path to the server: the outer IPv4 header, the transport's
    /// (UDP 8; TCP 20 and the 2-byte length each packet is framed with) and the data channel's own. A path's MTU less
    /// this is the largest packet the tunnel carries in one outer packet.</summary>
    public int Overhead => 20 + (Remote.Proto == "tcp" ? 20 + 2 : 8) + DataChannel.Overhead;

    /// <summary>The server's control messages that were neither the push reply it connected with nor a refusal — INFO,
    /// AUTH_PENDING, a later PUSH_REPLY (its options redacted, as <see cref="Sallyport.PushOptions.Redact"/> does) — the
    /// latest 64.</summary>
    public IReadOnlyList<string> OtherMessages { get { lock (_other) return [.. _other]; } }

    /// <summary>IPv4 packets from the tunnel. Completes, with <see cref="TunnelEnded"/>, when the tunnel stops.</summary>
    public ChannelReader<byte[]> Packets => _packets.Reader;

    /// <summary>Completes when the tunnel stops: normally on dispose, with <see cref="TunnelEnded"/> otherwise.</summary>
    public Task Completion => _ended.Task;

    /// <summary>Packets, bytes and keepalives each way, and what was refused or lost, since the tunnel came up.</summary>
    public TunnelCounters Counters => new(
        Interlocked.Read(ref _packetsIn), Interlocked.Read(ref _packetsOut), Interlocked.Read(ref _bytesIn), Interlocked.Read(ref _bytesOut),
        Interlocked.Read(ref _pingsIn), Interlocked.Read(ref _pingsOut), Interlocked.Read(ref _rejected), Interlocked.Read(ref _opposite),
        Interlocked.Read(ref _replayed), _channel.Dropped, _data.Missing, _channel.DataOverflowed + Interlocked.Read(ref _overflowed));

    /// <summary>Sends one IPv4 packet through the tunnel.</summary>
    /// <exception cref="TunnelEnded">The tunnel has stopped.</exception>
    public async ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken ct = default)
    {
        if (_ended.Task.IsCompleted) throw new TunnelEnded("the tunnel has stopped", _ended.Task.Exception?.InnerException);
        await SendPayloadAsync(packet, ct);
        Interlocked.Increment(ref _packetsOut);
        Interlocked.Add(ref _bytesOut, packet.Length);
    }

    private async ValueTask SendPayloadAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var sealedPacket = _data.Seal(payload.Span);
        await _channel.SendDataAsync(sealedPacket, ct);
        Volatile.Write(ref _lastSent, Environment.TickCount64);
    }

    /// <summary>Opens one tunnel to <paramref name="remote"/>, once: no retry, no reconnect — a
    /// <see cref="Runtime.TunnelSession"/> does those. <paramref name="log"/> hears each step of the handshake, never a
    /// key or a password.</summary>
    /// <exception cref="HandshakeFailed">It did not come up within <paramref name="timeout"/>, or the server refused;
    /// <see cref="HandshakeFailed.Stage"/> and <see cref="HandshakeFailed.Outcome"/> say where and how.</exception>
    public static Task<Tunnel> ConnectAsync(OvpnProfile profile, Remote remote, TimeSpan timeout, TunnelOptions? options = null,
        Action<string>? log = null, CancellationToken ct = default) =>
        ConnectAsync(profile, remote, token => Transports.ConnectAsync(remote, token), timeout, options ?? new TunnelOptions(), log, ct);

    internal static Task<Tunnel> ConnectAsync(OvpnProfile profile, Remote remote, Func<CancellationToken, Task<ITransport>> connect,
        TimeSpan timeout, TunnelOptions options, Action<string>? log, CancellationToken ct) =>
        TunnelHandshake.ConnectAsync(profile, remote, connect, timeout, options, log, ct);

    internal void Start()
    {
        var now = Environment.TickCount64;
        _lastSent = now;
        _lastReceived = now;
        _loops = Task.WhenAll(
            Task.Run(() => ReceiveLoopAsync(_stop.Token)),
            Task.Run(() => KeepaliveLoopAsync(_stop.Token)),
            Task.Run(() => ControlLoopAsync(_stop.Token)));
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var packet in _channel.DataInbound.ReadAllAsync(ct))
            {
                switch (_data.Open(packet, out var payload))
                {
                    case DataChannel.Verdict.Opened:
                        Volatile.Write(ref _lastReceived, Environment.TickCount64);
                        if (Signals.IsPing(payload)) { Interlocked.Increment(ref _pingsIn); break; }
                        if (Signals.IsOcc(payload, out var kind))
                        {
                            if (kind == Signals.OccExit) End(new TunnelEnded("the server announced it is leaving"));
                            break;
                        }
                        Interlocked.Increment(ref _packetsIn);
                        Interlocked.Add(ref _bytesIn, payload.Length);
                        _packets.Writer.TryWrite(payload);
                        break;
                    case DataChannel.Verdict.OppositeDirection: Interlocked.Increment(ref _opposite); break;
                    case DataChannel.Verdict.Replayed: Interlocked.Increment(ref _replayed); break;
                    default: Interlocked.Increment(ref _rejected); break;
                }
            }
            End(new TunnelEnded("the connection to the server closed"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e) { End(new TunnelEnded($"the connection to the server failed: {e.Message}", e)); }
    }

    private async Task KeepaliveLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var now = Environment.TickCount64;
                if (_pingRestart > 0 && now - Volatile.Read(ref _lastReceived) >= _pingRestart * 1000L)
                {
                    End(new TunnelEnded($"nothing from the server for {_pingRestart} s (ping-restart)"));
                    return;
                }
                if (_ping > 0 && now - Volatile.Read(ref _lastSent) >= _ping * 1000L)
                {
                    await SendPayloadAsync(Signals.Ping, ct);
                    Interlocked.Increment(ref _pingsOut);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e) { End(new TunnelEnded($"the keepalive could not be sent: {e.Message}", e)); }
    }

    private async Task ControlLoopAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var text = await _inbox.ReadTextAsync(ct);
                if (Stop(text) is { } why) { End(new TunnelEnded(why) { AuthenticationRefused = RefusesAuthentication(text) }); return; }
                // A later push reply can carry a new session token: kept as the first one's options are shown.
                if (text.StartsWith("PUSH_REPLY", StringComparison.Ordinal)) text = string.Join(',', text.Split(',').Select(Sallyport.PushOptions.Redact));
                lock (_other)
                {
                    _other.Add(text);
                    if (_other.Count > OtherKept) _other.RemoveAt(0);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e) { End(new TunnelEnded($"the control channel ended: {e.Message}", e)); }
    }

    private void End(TunnelEnded why)
    {
        if (_ended.TrySetException(why))
        {
            _packets.Writer.TryComplete(why);
            _stop.Cancel();
        }
    }

    /// <summary>Leaves: tells the server (on the data channel if it asked for that, and on the control channel if it
    /// negotiated that), then closes. Best-effort: whether the server acts on it cannot be seen from here, and a notice
    /// that cannot be sent keeps nothing from being let go.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        try
        {
            if (!_ended.Task.IsCompleted)
            {
                using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    if (Config.ExplicitExitNotify) await SendPayloadAsync(Signals.OccExitMessage(), grace.Token);
                    if (Config.ProtocolFlags.Contains("cc-exit"))
                    {
                        await _tls.WriteAsync("EXIT\0"u8.ToArray(), grace.Token);
                        while (!_channel.AllAcknowledged) await Task.Delay(50, grace.Token);
                    }
                }
                // Whatever stopped the notice — the grace over, the transport failed, the tunnel ended meanwhile — the
                // server is left as it would be by a lost datagram.
                catch (Exception) { }
            }
        }
        finally
        {
            _ended.TrySetResult();
            _packets.Writer.TryComplete();
            await _stop.CancelAsync();
            try { await _loops; } catch (OperationCanceledException) { }
            await _tls.DisposeAsync();
            await _channel.DisposeAsync();
            _data.Dispose();
            _certificate?.Dispose();
            _stop.Dispose();
        }
    }

    /// <summary>Why the server's message <paramref name="text"/> ends the tunnel, or null when it does not; the server's
    /// own words as far as they may be repeated (<see cref="ServerWords.Quote"/>).</summary>
    internal static string? Stop(string text) =>
        RefusesAuthentication(text) ? $"the server refused authentication: {ServerWords.Quote(text)}"
        : text.StartsWith("RESTART", StringComparison.Ordinal) || text.StartsWith("HALT", StringComparison.Ordinal) ? $"the server told the client to stop: {ServerWords.Quote(text)}"
        : text == "EXIT" ? "the server is leaving"
        : null;

    internal static bool RefusesAuthentication(string text) => text.StartsWith("AUTH_FAILED", StringComparison.Ordinal);

    /// <summary>Why this version cannot connect with <paramref name="profile"/>, or null when it can — so a host can say so
    /// when a profile is added, before any packet is sent. What only the server can refuse (a username and password it
    /// does not accept, the older data packet format) is learned by connecting.</summary>
    public static string? Unsupported(OvpnProfile profile) =>
        profile.TlsCryptV2 ? "the profile uses tls-crypt-v2, which is not supported yet"
        : profile.TlsCryptKey is null && profile.TlsAuthKey is not null ? "the profile uses tls-auth, which is not supported yet"
        : profile.TlsCryptKey is null ? "the profile has no tls-crypt key; only tls-crypt profiles are supported yet"
        : profile.CaPem is null ? "the profile has no <ca>"
        : (profile.CertPem is null) != (profile.KeyPem is null) ? "the profile has a client certificate without its key, or a key without its certificate"
        // A username and password may take the certificate's place; without them, it is the only way to sign in.
        : profile.CertPem is null && !profile.AuthUserPass ? "the profile has no client certificate and key"
        // A check of the server this version cannot make is never skipped: the profile is refused instead.
        : profile.UnsupportedCheck is { } check ? $"the profile checks its server with {check}, which is not supported yet"
        : null;
}
