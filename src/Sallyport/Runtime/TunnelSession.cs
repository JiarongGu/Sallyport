using System.Net.Sockets;
using Sallyport.Net;
using Sallyport.Profile;
using Sallyport.Proxy;

namespace Sallyport.Runtime;

public enum SessionState { Connecting, Up, Reconnecting, Stopped, Refused }

/// <summary>Where a <see cref="TunnelSession"/> is. No secret and no host name — what a status file or a window may
/// show. <see cref="Remote"/> is the transport and port only; <see cref="BytesIn"/> and <see cref="BytesOut"/> count the
/// IP packets carried, across every tunnel the session has had.</summary>
public sealed record SessionStatus(
    SessionState State, DateTimeOffset Since, string? Remote, int Attempts, int Reconnects, string? LastError,
    string? Address, long BytesIn, long BytesOut);

/// <summary>Options a session's host rarely sets.</summary>
public sealed class TunnelSessionOptions
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>The wait after a round in which no remote answered; doubled each time up to <see cref="LongestBackoff"/>.</summary>
    public TimeSpan FirstBackoff { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan LongestBackoff { get; init; } = TimeSpan.FromSeconds(30);
    public TunnelOptions Tunnel { get; init; } = new();
    /// <summary>How a tunnel to a remote is opened; tests put an in-process server here.</summary>
    internal Func<Remote, CancellationToken, Task<Tunnel>>? Connect { get; init; }
}

/// <summary>
/// One profile's tunnel, kept up: the remotes tried in turn, and when the tunnel ends — the server restarted or
/// renegotiated, or the network dropped — connected again with backoff. A host attaches what it serves (a proxy, a packet
/// router) to <see cref="Network"/>, which is null between two tunnels.
/// </summary>
public sealed class TunnelSession : IAsyncDisposable
{
    private readonly OvpnProfile _profile;
    private readonly TunnelSessionOptions _options;
    private SessionStatus _status = new(SessionState.Connecting, DateTimeOffset.UtcNow, null, 0, 0, null, null, 0, 0);
    private volatile Tunnel? _tunnel;
    private volatile TunnelNetwork? _network;
    // What tunnels already dropped carried, so the totals do not fall back to zero on a reconnect.
    private long _droppedIn, _droppedOut;

    public TunnelSession(OvpnProfile profile, TunnelSessionOptions? options = null)
    {
        _profile = profile;
        _options = options ?? new TunnelSessionOptions();
    }

    /// <summary>Raised on every change of state, on the session's own loop; byte counts move between raises, so read
    /// <see cref="Status"/> for them.</summary>
    public event Action<SessionStatus>? StatusChanged;

    /// <summary>Raised with each new tunnel's network BEFORE the session reports <see cref="SessionState.Up"/>, so what a
    /// host attaches here — a proxy, a packet router — is ready by the time anyone sees "up".</summary>
    public event Action<TunnelNetwork>? NetworkUp;

    public SessionStatus Status
    {
        get
        {
            var s = _status;
            var counters = _tunnel?.Counters;
            return s with
            {
                Address = _network?.LocalAddress.ToString(),
                BytesIn = Interlocked.Read(ref _droppedIn) + (counters?.BytesIn ?? 0),
                BytesOut = Interlocked.Read(ref _droppedOut) + (counters?.BytesOut ?? 0),
            };
        }
    }

    /// <summary>The current tunnel's network, or null while there is none.</summary>
    public TunnelNetwork? Network => _network;

    /// <summary>The tunnel itself, for tests that put a packet straight into it.</summary>
    internal Tunnel? CurrentTunnel => _tunnel;

    /// <summary>Runs until <paramref name="ct"/> is cancelled. Returns early only when the profile cannot be served at
    /// all (it names no remote, or asks for what this version does not do) — retrying that would only repeat it.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (_profile.Remotes.Count == 0 || Tunnel.Unsupported(_profile) is not null)
        {
            Report(SessionState.Refused, null, 0, 0, Tunnel.Unsupported(_profile) ?? "the profile names no remote");
            return;
        }
        var connect = _options.Connect ?? ((remote, token) => Tunnel.ConnectAsync(_profile, remote, _options.ConnectTimeout, _options.Tunnel, ct: token));
        var backoff = _options.FirstBackoff;
        var attempts = 0;
        var reconnects = 0;
        string? lastError = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Tunnel? tunnel = null;
                foreach (var remote in _profile.Remotes)
                {
                    attempts++;
                    // "connecting" until a tunnel has been up once, as Kobo's status file has always said.
                    Report(reconnects == 0 ? SessionState.Connecting : SessionState.Reconnecting, Label(remote), attempts, reconnects, lastError);
                    try
                    {
                        tunnel = await connect(remote, ct);
                        break;
                    }
                    catch (HandshakeFailed e) when (e.Stage == HandshakeStage.Profile)
                    {
                        Report(SessionState.Refused, null, attempts, reconnects, e.Message);
                        return;
                    }
                    catch (HandshakeFailed e)
                    {
                        // This server, this time: it may answer the next attempt, or the next remote may.
                        lastError = e.Message;
                    }
                }
                if (tunnel is null)
                {
                    Report(SessionState.Reconnecting, null, attempts, reconnects, lastError);
                    await Task.Delay(backoff, ct);
                    backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, _options.LongestBackoff.Ticks));
                    continue;
                }

                backoff = _options.FirstBackoff;
                lastError = null;
                _tunnel = tunnel;
                var network = new TunnelNetwork(tunnel);
                _network = network;
                NetworkUp?.Invoke(network);
                Report(SessionState.Up, Label(tunnel.Remote), attempts, reconnects, null);

                try { await tunnel.Completion.WaitAsync(ct); }
                catch (TunnelEnded e) { lastError = e.Message; }
                await DropTunnelAsync();
                if (ct.IsCancellationRequested) break;
                reconnects++;
                Report(SessionState.Reconnecting, null, attempts, reconnects, lastError);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped while connecting or waiting to retry: nothing was up to take down.
        }
        await DropTunnelAsync();
        Report(SessionState.Stopped, null, attempts, reconnects, lastError);
    }

    private async Task DropTunnelAsync()
    {
        var network = _network;
        var tunnel = _tunnel;
        _network = null;
        if (network is not null) await network.DisposeAsync();
        if (tunnel is null) return;
        await tunnel.DisposeAsync();
        // Detached before its bytes join the totals, so a status read in between counts them at most once.
        _tunnel = null;
        var counters = tunnel.Counters;
        Interlocked.Add(ref _droppedIn, counters.BytesIn);
        Interlocked.Add(ref _droppedOut, counters.BytesOut);
    }

    private void Report(SessionState state, string? remote, int attempts, int reconnects, string? error)
    {
        _status = new SessionStatus(state, DateTimeOffset.UtcNow, remote, attempts, reconnects, error, null, 0, 0);
        StatusChanged?.Invoke(Status);
    }

    private static string Label(Remote remote) => $"{remote.Proto}/{remote.Port}";

    public ValueTask DisposeAsync() => new(DropTunnelAsync());
}

/// <summary>Dials through whichever tunnel a session has now, and answers "network down" in the gap between two — which
/// a SOCKS client hears as "network unreachable".</summary>
public sealed class SessionConnector(TunnelSession session) : IConnector
{
    public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken ct) =>
        session.Network is { } network
            ? new TunnelConnector(network).ConnectAsync(host, port, ct)
            : ValueTask.FromException<Stream>(new SocketException((int)SocketError.NetworkDown));
}
