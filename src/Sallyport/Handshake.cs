using Sallyport.Profile;
using Sallyport.Protocol;

namespace Sallyport;

/// <summary>How far a handshake got, in order.</summary>
public enum HandshakeStage
{
    /// <summary>Refused before any packet: the profile asks for something this version does not do, so trying again
    /// cannot help.</summary>
    Profile,
    /// <summary>Opening the transport (UDP, or the TCP connection) to the remote.</summary>
    Connect,
    /// <summary>The control channel's hard reset, which a server answers only when the static key matches.</summary>
    HardReset,
    /// <summary>The TLS handshake inside the control channel: the server's certificate, and this client's.</summary>
    Tls,
    /// <summary>The key-method-2 exchange that carries the key material, the options and any username and password.</summary>
    KeyExchange,
    /// <summary>The push request and the server's reply: the address, routes and cipher.</summary>
    Push,
    /// <summary>The data channel's keys, derived from what the exchange carried.</summary>
    DataChannel,
    /// <summary>Up.</summary>
    Done,
}

/// <summary>What a check found: never collapsed to two answers, since "it did not answer" is not "it refused".</summary>
public enum Outcome
{
    /// <summary>Checked, and it holds.</summary>
    Holds,
    /// <summary>Checked, and it failed: the server answered and refused, or answered with what this client cannot accept.</summary>
    Failed,
    /// <summary>Could not be checked: no answer came, so nothing was learned about the server.</summary>
    Unverifiable,
}

/// <summary>A handshake that stopped short. <see cref="Outcome.Failed"/>: the server answered and refused, or answered
/// with something this client cannot accept. <see cref="Outcome.Unverifiable"/>: no answer came, so nothing was learned
/// about the server — a wrong key, a wrong port and a blocked path all look the same from here.</summary>
public sealed class HandshakeFailed(HandshakeStage stage, Outcome outcome, string message, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>The step that failed.</summary>
    public HandshakeStage Stage { get; } = stage;
    /// <summary>Whether the server refused (<see cref="Outcome.Failed"/>) or never answered (<see cref="Outcome.Unverifiable"/>).</summary>
    public Outcome Outcome { get; } = outcome;
    /// <summary>The server said AUTH_FAILED: it knows this client and will not have it.</summary>
    public bool AuthenticationRefused { get; init; }
}

/// <summary>What a completed control-channel handshake established.</summary>
/// <remarks>A class rather than a record so that no generated <c>ToString</c> ever prints <see cref="PushOptions"/>,
/// which can carry a session credential.</remarks>
internal sealed class HandshakeResult
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

/// <summary>The server's pushed options as they may be shown or logged.</summary>
public static class PushOptions
{
    private static readonly string[] Secret = ["auth-token", "auth-token-user"];

    /// <summary>The option, with its value hidden when the value is a credential — however it is spaced.</summary>
    public static string Redact(string option)
    {
        var name = option.TrimStart().Split((char[])[' ', '\t'], 2)[0];
        return Secret.Contains(name, StringComparer.OrdinalIgnoreCase) ? $"{name} [redacted]" : option;
    }
}

/// <summary>Connect, report what the server said, leave — phase 1's measurement, kept as the cheapest check that a server
/// still accepts this client.</summary>
internal static class Handshake
{
    public static Task<HandshakeResult> RunAsync(OvpnProfile profile, Remote remote, TimeSpan timeout, Action<string>? log = null, CancellationToken ct = default) =>
        RunAsync(profile, remote, token => Transports.ConnectAsync(remote, token), timeout, log, ct);

    internal static async Task<HandshakeResult> RunAsync(OvpnProfile profile, Remote remote, Func<CancellationToken, Task<ITransport>> connect,
        TimeSpan timeout, Action<string>? log, CancellationToken ct)
    {
        await using var tunnel = await Tunnel.ConnectAsync(profile, remote, connect, timeout, new TunnelOptions(), log, ct);
        return new HandshakeResult
        {
            Remote = remote,
            TlsProtocol = tunnel.TlsProtocol,
            CipherSuite = tunnel.CipherSuite,
            ServerSubject = tunnel.ServerSubject,
            PushOptions = tunnel.PushOptions,
            OtherMessages = tunnel.OtherMessages,
            DroppedPackets = tunnel.Counters.ControlDropped,
            Elapsed = tunnel.ConnectTime,
        };
    }
}
