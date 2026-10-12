using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Sallyport.Control;
using Sallyport.Crypto;
using Sallyport.Profile;
using Sallyport.Tls;
using Sallyport.Transport;

namespace Sallyport;

/// <summary>How far a handshake got, in order.</summary>
public enum HandshakeStage { Connect, HardReset, Tls, KeyExchange, Push, Done }

/// <summary>Three answers, never two (CLAUDE.md rule 6).</summary>
public enum Outcome { Holds, Failed, Unverifiable }

/// <summary>A handshake that stopped short. <see cref="Outcome.Failed"/>: the server answered and refused, or answered
/// with something this client cannot accept. <see cref="Outcome.Unverifiable"/>: no answer came, so nothing was learned
/// about the server — a wrong key, a wrong port and a blocked path all look the same from here.</summary>
public sealed class HandshakeFailed(HandshakeStage stage, Outcome outcome, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public HandshakeStage Stage { get; } = stage;
    public Outcome Outcome { get; } = outcome;
}

/// <summary>What a completed control-channel handshake established.</summary>
/// <remarks>A class rather than a record so that no generated <c>ToString</c> ever prints <see cref="PushOptions"/>,
/// which can carry a session credential.</remarks>
public sealed class HandshakeResult
{
    public required Remote Remote { get; init; }
    public required string TlsProtocol { get; init; }
    public required string CipherSuite { get; init; }
    public required string ServerSubject { get; init; }
    /// <summary>The options the server pushed, in order. May include <c>auth-token</c> — show them through
    /// <see cref="Sallyport.PushOptions.Redact"/>.</summary>
    public required IReadOnlyList<string> PushOptions { get; init; }
    /// <summary>Control messages that were neither a push reply nor a refusal (INFO, AUTH_PENDING, ...).</summary>
    public required IReadOnlyList<string> OtherMessages { get; init; }
    public required int DroppedPackets { get; init; }
    public required TimeSpan Elapsed { get; init; }
}

public static class PushOptions
{
    private static readonly string[] Secret = ["auth-token", "auth-token-user"];

    /// <summary>The option, with its value hidden when the value is a credential.</summary>
    public static string Redact(string option)
    {
        var name = option.Split(' ', 2)[0];
        return Secret.Contains(name, StringComparer.OrdinalIgnoreCase) ? $"{name} [redacted]" : option;
    }
}

/// <summary>
/// The control channel from first packet to <c>PUSH_REPLY</c>: hard reset, TLS, key method 2, push request. No data
/// channel yet — this is phase 1, and what it proves is that a real server accepts this client.
/// </summary>
public static class Handshake
{
    /// <summary>How long to wait for a push reply before asking again; a server may hold one back while it authenticates.</summary>
    private static readonly TimeSpan PushRetry = TimeSpan.FromSeconds(5);

    public static Task<HandshakeResult> RunAsync(OvpnProfile profile, Remote remote, TimeSpan timeout, Action<string>? log = null, CancellationToken ct = default) =>
        RunAsync(profile, remote, token => Transports.ConnectAsync(remote, token), timeout, log, ct);

    internal static async Task<HandshakeResult> RunAsync(OvpnProfile profile, Remote remote, Func<CancellationToken, Task<ITransport>> connect,
        TimeSpan timeout, Action<string>? log, CancellationToken ct)
    {
        Refuse(profile);
        var clock = Stopwatch.StartNew();
        var stage = HandshakeStage.Connect;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;
        try
        {
            await using var channel = new ControlChannel(await connect(token), TlsCrypt.ForClient(profile.TlsCryptKey!));
            channel.Start();

            stage = HandshakeStage.HardReset;
            await channel.SendAsync(Opcode.ControlHardResetClientV2, [], token);
            var reset = await channel.Inbound.ReadAsync(token);
            if (reset.Opcode != Opcode.ControlHardResetServerV2)
                throw new HandshakeFailed(stage, Outcome.Failed, $"the server answered the hard reset with {reset.Opcode}");
            log?.Invoke("hard reset answered");

            stage = HandshakeStage.Tls;
            using var certificate = Certificates.Client(profile.CertPem!, profile.KeyPem!);
            var authorities = Certificates.Authorities(profile.CaPem!);
            string? rejected = null;
            await using var tls = new SslStream(new ControlStream(channel), leaveInnerStreamOpen: false);
            var options = new SslClientAuthenticationOptions
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
            try { await tls.AuthenticateAsClientAsync(options, token); }
            catch (AuthenticationException e) { throw new HandshakeFailed(stage, Outcome.Failed, rejected ?? $"TLS was refused: {e.Message}", e); }
            var subject = tls.RemoteCertificate?.Subject ?? "";
            log?.Invoke($"TLS up: {tls.SslProtocol}, {tls.NegotiatedCipherSuite}");

            stage = HandshakeStage.KeyExchange;
            await tls.WriteAsync(KeyMethod2Message.Write(KeySource.ForClient(), OptionsString(profile, remote), "", "", PeerInfo()), token);
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
                else if (text.StartsWith("AUTH_FAILED", StringComparison.Ordinal))
                    throw new HandshakeFailed(stage, Outcome.Failed, $"the server refused authentication: {text}");
                else if (text.StartsWith("RESTART", StringComparison.Ordinal) || text.StartsWith("HALT", StringComparison.Ordinal))
                    throw new HandshakeFailed(stage, Outcome.Failed, $"the server told the client to stop: {text}");
                else other.Add(text);
                reading = inbox.ReadTextAsync(token);
            }
            stage = HandshakeStage.Done;
            log?.Invoke("push reply received");

            // Tell the server this client is leaving, so it frees the session now rather than at its keepalive timeout.
            // Best-effort and unmeasured: whether a server acts on it cannot be seen from the client.
            await tls.WriteAsync("EXIT\0"u8.ToArray(), token);
            for (var i = 0; i < 20 && !channel.AllAcknowledged; i++) await Task.Delay(100, token);

            return new HandshakeResult
            {
                Remote = remote,
                TlsProtocol = tls.SslProtocol.ToString(),
                CipherSuite = tls.NegotiatedCipherSuite.ToString(),
                ServerSubject = subject,
                PushOptions = pushed,
                OtherMessages = other,
                DroppedPackets = channel.Dropped,
                Elapsed = clock.Elapsed,
            };
        }
        catch (HandshakeFailed) { throw; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HandshakeFailed(stage, Outcome.Unverifiable, $"no answer within {timeout.TotalSeconds:0} s {Waiting(stage)}");
        }
        catch (SocketException e)
        {
            throw new HandshakeFailed(stage, Outcome.Unverifiable, $"the network failed {Waiting(stage)}: {e.Message}", e);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new HandshakeFailed(stage, Outcome.Failed, $"{Waiting(stage)}: {e.Message}", e);
        }
    }

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
        _ => "while closing",
    };

    // What a client states about its own configuration. A server compares it with its own and, at most, warns.
    private static string OptionsString(OvpnProfile profile, Remote remote) =>
        $"V4,dev-type {(profile.Dev.StartsWith("tap", StringComparison.Ordinal) ? "tap" : "tun")},link-mtu 1549,tun-mtu 1500,"
        + $"proto {(remote.Proto == "tcp" ? "TCPv4_CLIENT" : "UDPv4")},cipher {profile.Cipher},auth {profile.Auth},keysize 256,key-method 2,tls-client";

    /// <summary>What this client can do, as the server reads it.</summary>
    /// <remarks>IV_PROTO 130 = data-v2 packets (2) + told-on-exit (128). The ciphers are the ones phase 2 will
    /// implement; a server picks the first it shares. No IV_VER: this is not OpenVPN and does not claim a version of it.</remarks>
    private static string PeerInfo()
    {
        var platform = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "mac" : "unknown";
        return $"IV_PLAT={platform}\nIV_PROTO=130\nIV_NCP=2\nIV_CIPHERS=AES-256-GCM:AES-128-GCM\nIV_GUI_VER=Sallyport_0.1.0\n";
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
