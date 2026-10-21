using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Sallyport.Profile;

namespace Sallyport.Protocol;

/// <summary>
/// Everything between a transport and a running <see cref="Tunnel"/>: the hard reset, TLS over the control channel, the
/// key-method-2 exchange, the push request and its reply, and the data channel's keys. What it builds is handed to the
/// tunnel whole, or released here if any step fails.
/// </summary>
internal static class TunnelHandshake
{
    /// <summary>How long to wait for a push reply before asking again; a server may hold one back while it authenticates.</summary>
    private static readonly TimeSpan PushRetry = TimeSpan.FromSeconds(5);

    /// <summary>What this build calls itself to a server (D4): its own version, never OpenVPN's.</summary>
    private static readonly string GuiVersion =
        typeof(TunnelHandshake).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0";

    public static async Task<Tunnel> ConnectAsync(OvpnProfile profile, Remote remote, Func<CancellationToken, Task<ITransport>> connect,
        TimeSpan timeout, TunnelOptions options, Action<string>? log, CancellationToken ct)
    {
        if (Tunnel.Unsupported(profile) is { } why) throw new HandshakeFailed(HandshakeStage.Profile, Outcome.Unverifiable, why);
        // Asked before a packet is sent: the server would only refuse it after the whole handshake.
        if (profile.AuthUserPass && options.Login is null)
            throw new HandshakeFailed(HandshakeStage.Profile, Outcome.Unverifiable, "the profile asks for a username and password, and none was given");
        var clock = Stopwatch.StartNew();
        var stage = HandshakeStage.Connect;
        ControlChannel? channel = null;
        SslStream? tls = null;
        X509Certificate2? certificate = null;
        DataChannel? data = null;
        try
        {
            // Inside the try: a timeout no timer can hold is the profile's to answer for, not an exception of its own.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout);
            var token = deadline.Token;

            channel = new ControlChannel(await connect(token), TlsCrypt.ForClient(profile.TlsCryptKey!));
            channel.Start();

            stage = HandshakeStage.HardReset;
            await channel.SendAsync(Opcode.ControlHardResetClientV2, [], token);
            var reset = await channel.Inbound.ReadAsync(token);
            if (reset.Opcode != Opcode.ControlHardResetServerV2)
                throw new HandshakeFailed(stage, Outcome.Failed, $"the server answered the hard reset with {reset.Opcode}");
            log?.Invoke("hard reset answered");

            stage = HandshakeStage.Tls;
            // A profile that signs in by its username and password alone has no certificate of its own to present.
            certificate = profile.CertPem is not null ? Certificates.Client(profile.CertPem, profile.KeyPem!) : null;
            var authorities = Certificates.Authorities(profile.CaPem!);
            string? rejected = null;
            tls = new SslStream(new ControlStream(channel), leaveInnerStreamOpen: false);
            var tlsOptions = new SslClientAuthenticationOptions
            {
                TargetHost = remote.Host,
                ClientCertificateContext = certificate is null ? null : SslStreamCertificateContext.Create(certificate, null, offline: true),
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (_, presented, chain, _) =>
                {
                    if (presented is null) { rejected = "the server presented no certificate"; return false; }
                    using var server = X509CertificateLoader.LoadCertificate(presented.GetRawCertData());
                    var intermediates = chain?.ChainElements.Select(e => e.Certificate) ?? [];
                    rejected = Certificates.CheckServer(server, authorities, intermediates, profile.RemoteCertTlsServer, profile.VerifyName);
                    return rejected is null;
                },
            };
            try { await tls.AuthenticateAsClientAsync(tlsOptions, token); }
            catch (AuthenticationException e) { throw new HandshakeFailed(stage, Outcome.Failed, rejected ?? $"TLS was refused: {e.Message}", e); }
            var subject = tls.RemoteCertificate?.Subject ?? "";
            log?.Invoke($"TLS up: {tls.SslProtocol}, {tls.NegotiatedCipherSuite}");

            stage = HandshakeStage.KeyExchange;
            var mine = KeySource.ForClient();
            await tls.WriteAsync(KeyMethod2Message.Write(mine, OptionsString(profile, remote), options.Login?.Username ?? "", options.Login?.Password ?? "", PeerInfo()), token);
            var inbox = new TlsInbox(tls);
            KeyMethod2Message? theirs;
            int used;
            try
            {
                while (!KeyMethod2Message.TryRead(inbox.Data, fromClient: false, out theirs, out used))
                {
                    // A key-method-2 message opens with four zero bytes; text in its place is the server refusing.
                    if (inbox.Data.Length > 0 && inbox.Data[0] != 0 && inbox.Data.IndexOf((byte)0) >= 0) break;
                    await inbox.FillAsync(token);
                }
            }
            catch (FormatException)
            {
                theirs = null;
                used = 0;
            }
            if (theirs is null)
            {
                var text = inbox.Data.IndexOf((byte)0) is var end and >= 0 ? Encoding.UTF8.GetString(inbox.Data[..end]) : null;
                if (text is not null && Tunnel.Stop(text) is { } stop)
                    throw new HandshakeFailed(stage, Outcome.Failed, stop) { AuthenticationRefused = Tunnel.RefusesAuthentication(text) };
                throw new HandshakeFailed(stage, Outcome.Failed, $"the server answered the key exchange with something else: \"{ServerWords.Quote(Printable(inbox.Data))}\"");
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
                else if (Tunnel.Stop(text) is { } stop) throw new HandshakeFailed(stage, Outcome.Failed, stop) { AuthenticationRefused = Tunnel.RefusesAuthentication(text) };
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
                throw new HandshakeFailed(stage, Outcome.Failed, $"the server chose the cipher {ServerWords.Quote(cipher)}, which this version does not implement");
            var block = DataKeys.Expand(mine, theirs.Source, channel.LocalSession, channel.PeerSession!);
            // The keys are the data channel's from here; what they were made from is not kept.
            try { data = new DataChannel(DataKeys.FromBlock(block, options.DataSendHalf, keyLength), peerId, keyId: 0); }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(block);
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(mine.PreMaster);
            }

            var ownKeepalive = options.KeepaliveOverridesServer;
            var tunnel = new Tunnel(channel, tls, inbox, data, certificate, other,
                ping: ownKeepalive && options.Ping is { } ping ? ping : config.Ping > 0 ? config.Ping : options.Ping ?? 0,
                pingRestart: ownKeepalive && options.PingRestart is { } restart ? restart : config.PingRestart > 0 ? config.PingRestart : options.PingRestart ?? 0)
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
                ArgumentOutOfRangeException when stage == HandshakeStage.Connect =>
                    new HandshakeFailed(HandshakeStage.Profile, Outcome.Unverifiable, $"the profile's timeout of {timeout.TotalSeconds:0} s is longer than a timer can wait"),
                SocketException => new HandshakeFailed(stage, Outcome.Unverifiable, $"the network failed {Waiting(stage)}: {e.Message}", e),
                _ => new HandshakeFailed(stage, Outcome.Failed, $"{Waiting(stage)}: {e.Message}", e),
            };
        }
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
        return $"IV_PLAT={platform}\nIV_PROTO=130\nIV_NCP=2\nIV_CIPHERS={string.Join(':', DataChannel.Ciphers.Keys)}\nIV_GUI_VER=Sallyport_{GuiVersion}\n";
    }

    private static string Printable(ReadOnlySpan<byte> data)
    {
        var take = data[..Math.Min(data.Length, 80)];
        var text = new StringBuilder(take.Length);
        foreach (var b in take) text.Append(b is >= 0x20 and < 0x7f ? (char)b : '.');
        return text.ToString();
    }
}

/// <summary>What a server said, as far as it may be repeated in a status, an exception or a log.</summary>
internal static class ServerWords
{
    private const int Longest = 120;

    /// <summary>Printable, at most 120 characters, and never a challenge's state or a session's token — Access Server's
    /// dynamic challenge is <c>AUTH_FAILED,CRV1:flags:state:username:text</c>, the username in base64.</summary>
    public static string Quote(string text)
    {
        foreach (var marker in (string[])["CRV1:", "SESSION:", "auth-token"])
            if (text.IndexOf(marker, StringComparison.OrdinalIgnoreCase) is >= 0 and var at) text = text[..at] + "[a challenge or token, not shown]";
        var printable = new StringBuilder(Math.Min(text.Length, Longest + 1));
        foreach (var c in text)
        {
            if (printable.Length == Longest) return printable.Append('…').ToString();
            printable.Append(char.IsControl(c) ? ' ' : c);
        }
        return printable.ToString();
    }
}
