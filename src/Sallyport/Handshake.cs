using Sallyport.Profile;
using Sallyport.Transport;

namespace Sallyport;

/// <summary>How far a handshake got, in order. <see cref="Profile"/>: refused before any packet, because the profile
/// asks for something this version does not do — so trying again cannot help.</summary>
public enum HandshakeStage { Profile, Connect, HardReset, Tls, KeyExchange, Push, DataChannel, Done }

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

/// <summary>Connect, report what the server said, leave — phase 1's measurement, kept as the cheapest check that a server
/// still accepts this client.</summary>
public static class Handshake
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
