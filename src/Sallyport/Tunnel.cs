using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Channels;
using Sallyport.Control;
using Sallyport.Crypto;
using Sallyport.Data;
using Sallyport.Profile;
using Sallyport.Tls;
using Sallyport.Transport;

namespace Sallyport;

/// <summary>The tunnel stopped after it was up: the server went silent, said to stop, or the transport failed.</summary>
public sealed class TunnelEnded(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A snapshot of what has crossed the tunnel. <see cref="OppositeDirection"/> counts packets that authenticate
/// only under this side's own sending key — both sides chose the same key half, which no other fault produces.</summary>
public readonly record struct TunnelCounters(
    long PacketsIn, long PacketsOut, long BytesIn, long BytesOut, long PingsIn, long PingsOut,
    long Rejected, long OppositeDirection, long Replayed, int ControlDropped, long MissingIn);

/// <summary>Options a caller rarely sets.</summary>
public sealed class TunnelOptions
{
    /// <summary>Which half of the derived key block this client sends data with. 0 unless measured otherwise —
    /// <see cref="TunnelCounters.OppositeDirection"/> says when it is wrong.</summary>
    public int DataSendHalf { get; init; }
}

/// <summary>
/// One connection to an OpenVPN-compatible server: the control channel up to the push reply, then the data channel —
/// IPv4 packets in and out, keepalive, and the server's later control messages. It ends in packets, not an adapter (D2).
/// </summary>
public sealed class Tunnel : IAsyncDisposable
{
    /// <summary>How long to wait for a push reply before asking again; a server may hold one back while it authenticates.</summary>
    private static readonly TimeSpan PushRetry = TimeSpan.FromSeconds(5);

    private readonly ControlChannel _channel;
    private readonly SslStream _tls;
    private readonly TlsInbox _inbox;
    private readonly DataChannel _data;
    private readonly X509Certificate2 _certificate;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<byte[]> _packets = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4096)
        { SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<string> _other;
    private Task _loops = Task.CompletedTask;
    private long _lastSent, _lastReceived;
    private long _packetsIn, _packetsOut, _bytesIn, _bytesOut, _pingsIn, _pingsOut, _rejected, _opposite, _replayed;
    private int _disposed;

    private Tunnel(ControlChannel channel, SslStream tls, TlsInbox inbox, DataChannel data, X509Certificate2 certificate, List<string> other)
    {
        _channel = channel;
        _tls = tls;
        _inbox = inbox;
        _data = data;
        _certificate = certificate;
        _other = other;
    }

    public required Remote Remote { get; init; }
    public required string TlsProtocol { get; init; }
    public required string CipherSuite { get; init; }
    public required string ServerSubject { get; init; }
    /// <summary>The options the server pushed, in order. May include <c>auth-token</c> — show them through
    /// <see cref="Sallyport.PushOptions.Redact"/>.</summary>
    public required IReadOnlyList<string> PushOptions { get; init; }
    public required PushedConfig Config { get; init; }
    public required TimeSpan ConnectTime { get; init; }

    /// <summary>Control messages that were neither a push reply nor a refusal (INFO, AUTH_PENDING, ...).</summary>
    public IReadOnlyList<string> OtherMessages { get { lock (_other) return [.. _other]; } }

    /// <summary>IPv4 packets from the tunnel. Completes, with <see cref="TunnelEnded"/>, when the tunnel stops.</summary>
    public ChannelReader<byte[]> Packets => _packets.Reader;

    /// <summary>Completes when the tunnel stops: normally on dispose, with <see cref="TunnelEnded"/> otherwise.</summary>
    public Task Completion => _ended.Task;

    public TunnelCounters Counters => new(
        Interlocked.Read(ref _packetsIn), Interlocked.Read(ref _packetsOut), Interlocked.Read(ref _bytesIn), Interlocked.Read(ref _bytesOut),
        Interlocked.Read(ref _pingsIn), Interlocked.Read(ref _pingsOut), Interlocked.Read(ref _rejected), Interlocked.Read(ref _opposite),
        Interlocked.Read(ref _replayed), _channel.Dropped, _data.Missing);

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

    public static Task<Tunnel> ConnectAsync(OvpnProfile profile, Remote remote, TimeSpan timeout, TunnelOptions? options = null,
        Action<string>? log = null, CancellationToken ct = default) =>
        ConnectAsync(profile, remote, token => Transports.ConnectAsync(remote, token), timeout, options ?? new TunnelOptions(), log, ct);

    internal static async Task<Tunnel> ConnectAsync(OvpnProfile profile, Remote remote, Func<CancellationToken, Task<ITransport>> connect,
        TimeSpan timeout, TunnelOptions options, Action<string>? log, CancellationToken ct)
    {
        Refuse(profile);
        var clock = Stopwatch.StartNew();
        var stage = HandshakeStage.Connect;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;
        ControlChannel? channel = null;
        SslStream? tls = null;
        X509Certificate2? certificate = null;
        DataChannel? data = null;
        try
        {
            channel = new ControlChannel(await connect(token), TlsCrypt.ForClient(profile.TlsCryptKey!));
            channel.Start();

            stage = HandshakeStage.HardReset;
            await channel.SendAsync(Opcode.ControlHardResetClientV2, [], token);
            var reset = await channel.Inbound.ReadAsync(token);
            if (reset.Opcode != Opcode.ControlHardResetServerV2)
                throw new HandshakeFailed(stage, Outcome.Failed, $"the server answered the hard reset with {reset.Opcode}");
            log?.Invoke("hard reset answered");

            stage = HandshakeStage.Tls;
            certificate = Certificates.Client(profile.CertPem!, profile.KeyPem!);
            var authorities = Certificates.Authorities(profile.CaPem!);
            string? rejected = null;
            tls = new SslStream(new ControlStream(channel), leaveInnerStreamOpen: false);
            var tlsOptions = new SslClientAuthenticationOptions
            {
                TargetHost = remote.Host,
                ClientCertificateContext = SslStreamCertificateContext.Create(certificate, null, offline: true),
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (_, presented, chain, _) =>
                {
                    if (presented is null) { rejected = "the server presented no certificate"; return false; }
                    using var server = X509CertificateLoader.LoadCertificate(presented.GetRawCertData());
                    var intermediates = chain?.ChainElements.Select(e => e.Certificate) ?? [];
                    rejected = Certificates.CheckServer(server, authorities, intermediates, profile.RemoteCertTlsServer);
                    return rejected is null;
                },
            };
            try { await tls.AuthenticateAsClientAsync(tlsOptions, token); }
            catch (AuthenticationException e) { throw new HandshakeFailed(stage, Outcome.Failed, rejected ?? $"TLS was refused: {e.Message}", e); }
            var subject = tls.RemoteCertificate?.Subject ?? "";
            log?.Invoke($"TLS up: {tls.SslProtocol}, {tls.NegotiatedCipherSuite}");

            stage = HandshakeStage.KeyExchange;
            var mine = KeySource.ForClient();
            await tls.WriteAsync(KeyMethod2Message.Write(mine, OptionsString(profile, remote), "", "", PeerInfo()), token);
            var inbox = new TlsInbox(tls);
            KeyMethod2Message? theirs;
            int used;
            try
            {
                while (!KeyMethod2Message.TryRead(inbox.Data, fromClient: false, out theirs, out used)) await inbox.FillAsync(token);
            }
            catch (FormatException)
            {
                throw new HandshakeFailed(stage, Outcome.Failed, $"the server answered the key exchange with something else: \"{Printable(inbox.Data)}\"");
            }
            inbox.Consume(used);
            log?.Invoke("key exchange answered");

            stage = HandshakeStage.Push;
            var request = "PUSH_REQUEST\0"u8.ToArray();
            await tls.WriteAsync(request, token);
            var pushed = new List<string>();
            var other = new List<string>();
            var reading = inbox.ReadTextAsync(token);
            while (true)
            {
                // Never cancel a pending TLS read to wait less — a cancelled read can leave the stream unusable — so
                // the read stays outstanding and the retry races it.
                if (await Task.WhenAny(reading, Task.Delay(PushRetry, token)) != reading)
                {
                    await tls.WriteAsync(request, token);
                    continue;
                }
                var text = await reading;
                if (text.StartsWith("PUSH_REPLY", StringComparison.Ordinal))
                {
                    var more = false;
                    foreach (var option in text.Split(',').Skip(1))
                    {
                        // "push-continuation 2": more PUSH_REPLY messages follow; "1": this was the last.
                        if (option.StartsWith("push-continuation ", StringComparison.Ordinal)) { more = option.EndsWith('2'); continue; }
                        pushed.Add(option);
                    }
                    if (!more) break;
                }
                else if (Stop(text) is { } why) throw new HandshakeFailed(stage, Outcome.Failed, why);
                else other.Add(text);
                reading = inbox.ReadTextAsync(token);
            }
            log?.Invoke("push reply received");

            stage = HandshakeStage.DataChannel;
            var config = PushedConfig.Parse(pushed);
            if (config.PeerId is not { } peerId)
                throw new HandshakeFailed(stage, Outcome.Failed, "the server pushed no peer-id, and the older data packet format is not supported yet");
            var cipher = config.Cipher ?? profile.Cipher;
            if (!DataChannel.Ciphers.TryGetValue(cipher, out var keyLength))
                throw new HandshakeFailed(stage, Outcome.Failed, $"the server chose the cipher {cipher}, which this version does not implement");
            var block = DataKeys.Expand(mine, theirs!.Source, channel.LocalSession, channel.PeerSession!);
            data = new DataChannel(DataKeys.FromBlock(block, options.DataSendHalf, keyLength), peerId, keyId: 0);

            var tunnel = new Tunnel(channel, tls, inbox, data, certificate, other)
            {
                Remote = remote,
                TlsProtocol = tls.SslProtocol.ToString(),
                CipherSuite = tls.NegotiatedCipherSuite.ToString(),
                ServerSubject = subject,
                PushOptions = pushed,
                Config = config,
                ConnectTime = clock.Elapsed,
            };
            tunnel.Start();
            log?.Invoke($"data channel ready: {cipher}, peer-id {peerId}");
            return tunnel;
        }
        catch (Exception e)
        {
            data?.Dispose();
            if (tls is not null) await tls.DisposeAsync();
            if (channel is not null) await channel.DisposeAsync();
            certificate?.Dispose();
            throw e switch
            {
                HandshakeFailed => e,
                OperationCanceledException when !ct.IsCancellationRequested =>
                    new HandshakeFailed(stage, Outcome.Unverifiable, $"no answer within {timeout.TotalSeconds:0} s {Waiting(stage)}"),
                OperationCanceledException => e,
                SocketException => new HandshakeFailed(stage, Outcome.Unverifiable, $"the network failed {Waiting(stage)}: {e.Message}", e),
                _ => new HandshakeFailed(stage, Outcome.Failed, $"{Waiting(stage)}: {e.Message}", e),
            };
        }
    }

    private void Start()
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
                if (Config.PingRestart > 0 && now - Volatile.Read(ref _lastReceived) >= Config.PingRestart * 1000L)
                {
                    End(new TunnelEnded($"nothing from the server for {Config.PingRestart} s (its ping-restart)"));
                    return;
                }
                if (Config.Ping > 0 && now - Volatile.Read(ref _lastSent) >= Config.Ping * 1000L)
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
                if (Stop(text) is { } why) { End(new TunnelEnded(why)); return; }
                lock (_other) _other.Add(text);
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
    /// negotiated that), then closes. Best-effort: whether the server acts on it cannot be seen from here.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
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
            catch (Exception) when (grace.IsCancellationRequested || _ended.Task.IsCompleted) { }
        }
        _ended.TrySetResult();
        _packets.Writer.TryComplete();
        await _stop.CancelAsync();
        try { await _loops; } catch (OperationCanceledException) { }
        await _tls.DisposeAsync();
        await _channel.DisposeAsync();
        _data.Dispose();
        _certificate.Dispose();
        _stop.Dispose();
    }

    private static string? Stop(string text) =>
        text.StartsWith("AUTH_FAILED", StringComparison.Ordinal) ? $"the server refused authentication: {text}"
        : text.StartsWith("RESTART", StringComparison.Ordinal) || text.StartsWith("HALT", StringComparison.Ordinal) ? $"the server told the client to stop: {text}"
        : text == "EXIT" ? "the server is leaving"
        : null;

    private static void Refuse(OvpnProfile profile)
    {
        string? why =
            profile.TlsCryptV2 ? "the profile uses tls-crypt-v2, which is not supported yet"
            : profile.TlsCryptKey is null && profile.TlsAuthKey is not null ? "the profile uses tls-auth, which is not supported yet"
            : profile.TlsCryptKey is null ? "the profile has no tls-crypt key; only tls-crypt profiles are supported yet"
            : profile.AuthUserPass ? "the profile asks for a username and password, which is not supported yet"
            : profile.CaPem is null ? "the profile has no <ca>"
            : profile.CertPem is null || profile.KeyPem is null ? "the profile has no client certificate and key"
            : null;
        if (why is not null) throw new HandshakeFailed(HandshakeStage.Connect, Outcome.Unverifiable, why);
    }

    private static string Waiting(HandshakeStage stage) => stage switch
    {
        HandshakeStage.Connect => "while connecting",
        HandshakeStage.HardReset => "to the hard reset (a wrong tls-crypt key, a wrong port and a blocked path all look like this)",
        HandshakeStage.Tls => "during the TLS handshake",
        HandshakeStage.KeyExchange => "to the key exchange",
        HandshakeStage.Push => "to the push request",
        HandshakeStage.DataChannel => "setting up the data channel",
        _ => "while closing",
    };

    // What a client states about its own configuration. A server compares it with its own and, at most, warns.
    private static string OptionsString(OvpnProfile profile, Remote remote) =>
        $"V4,dev-type {(profile.Dev.StartsWith("tap", StringComparison.Ordinal) ? "tap" : "tun")},link-mtu 1549,tun-mtu 1500,"
        + $"proto {(remote.Proto == "tcp" ? "TCPv4_CLIENT" : "UDPv4")},cipher {profile.Cipher},auth {profile.Auth},keysize 256,key-method 2,tls-client";

    /// <summary>What this client can do, as the server reads it.</summary>
    /// <remarks>IV_PROTO 130 = data-v2 packets (2) + told-on-exit (128). The ciphers are the ones <see cref="DataChannel"/>
    /// implements; a server picks the first it shares. No IV_VER: this is not OpenVPN and does not claim a version of it (D4).</remarks>
    private static string PeerInfo()
    {
        var platform = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "mac" : "unknown";
        return $"IV_PLAT={platform}\nIV_PROTO=130\nIV_NCP=2\nIV_CIPHERS={string.Join(':', DataChannel.Ciphers.Keys)}\nIV_GUI_VER=Sallyport_0.1.0\n";
    }

    private static string Printable(ReadOnlySpan<byte> data)
    {
        var take = data[..Math.Min(data.Length, 80)];
        var text = new StringBuilder(take.Length);
        foreach (var b in take) text.Append(b is >= 0x20 and < 0x7f ? (char)b : '.');
        return text.ToString();
    }
}

/// <summary>What has arrived over TLS and not yet been read: the key-method-2 message, then NUL-terminated text messages.</summary>
internal sealed class TlsInbox(Stream tls)
{
    private byte[] _buffer = new byte[32768];
    private int _length;

    public ReadOnlySpan<byte> Data => _buffer.AsSpan(0, _length);

    public async Task FillAsync(CancellationToken ct)
    {
        // Room for a whole TLS record (16 KB) on every read, so a message written in one piece is read in one piece.
        if (_buffer.Length - _length < 16384) Array.Resize(ref _buffer, _buffer.Length * 2);
        var n = await tls.ReadAsync(_buffer.AsMemory(_length), ct);
        if (n == 0) throw new EndOfStreamException("the server closed the control channel");
        _length += n;
    }

    public void Consume(int count)
    {
        Buffer.BlockCopy(_buffer, count, _buffer, 0, _length - count);
        _length -= count;
    }

    public async Task<string> ReadTextAsync(CancellationToken ct)
    {
        while (true)
        {
            var end = Data.IndexOf((byte)0);
            if (end >= 0)
            {
                var text = Encoding.UTF8.GetString(Data[..end]);
                Consume(end + 1);
                return text;
            }
            await FillAsync(ct);
        }
    }
}
