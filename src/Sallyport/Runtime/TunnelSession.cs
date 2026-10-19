using System.Net.Sockets;
using Sallyport.Net;
using Sallyport.Profile;
using Sallyport.Proxy;

namespace Sallyport.Runtime;

/// <summary><see cref="Refused"/>: the profile cannot be served, or the server refused its credentials and the policy
/// does not retry that. <see cref="GaveUp"/>: the policy's rounds ran out (connect-retry-max).</summary>
public enum SessionState { Connecting, Up, Reconnecting, Stopped, Refused, GaveUp }

/// <summary>Where a <see cref="TunnelSession"/> is. No secret and no host name — what a status file or a window may
/// show. <see cref="Remote"/> is the transport and port only; <see cref="BytesIn"/> and <see cref="BytesOut"/> count the
/// IP packets carried, across every tunnel the session has had.</summary>
public sealed record SessionStatus(
    SessionState State, DateTimeOffset Since, string? Remote, int Attempts, int Reconnects, string? LastError,
    string? Address, long BytesIn, long BytesOut);

/// <summary>Options a session's host rarely sets.</summary>
public sealed class TunnelSessionOptions
{
    /// <summary>How the tunnel is kept up; null, the profile's own (<see cref="OvpnProfile.Policy"/>).</summary>
    public ConnectionPolicy? Policy { get; init; }
    public TunnelOptions Tunnel { get; init; } = new();
    /// <summary>What each tunnel's packets go to, given the tunnel and its generation (1 for the first): the one reader
    /// of <see cref="Tunnel.Packets"/>, disposed when that tunnel goes. Null: a <see cref="TunnelNetwork"/> — the
    /// userspace TCP a proxy dials through (<see cref="TunnelSession.Network"/>).</summary>
    public Func<Tunnel, int, IAsyncDisposable>? Consumer { get; init; }
    /// <summary>How a tunnel to a remote is opened; tests put an in-process server here.</summary>
    internal Func<Remote, TimeSpan, CancellationToken, Task<Tunnel>>? Connect { get; init; }
    /// <summary>The order of remotes under <see cref="ConnectionPolicy.RandomOrder"/>; tests fix it.</summary>
    internal Random Random { get; init; } = Random.Shared;
}

/// <summary>
/// One profile's tunnel, kept up as its <see cref="ConnectionPolicy"/> says: the remotes tried in rounds, and when the
/// tunnel ends — the server restarted or renegotiated, or the network dropped — connected again. Each tunnel is a
/// generation; what consumes its packets is built for it and disposed with it (<see cref="TunnelSessionOptions.Consumer"/>).
/// </summary>
public sealed class TunnelSession : IAsyncDisposable
{
    private readonly OvpnProfile _profile;
    private readonly TunnelSessionOptions _options;
    private readonly ConnectionPolicy _policy;
    private volatile IAsyncDisposable? _consumer;
    private SessionStatus _status = new(SessionState.Connecting, DateTimeOffset.UtcNow, null, 0, 0, null, null, 0, 0);
    private volatile Tunnel? _tunnel;
    private volatile TunnelNetwork? _network;
    // What tunnels already dropped carried, so the totals do not fall back to zero on a reconnect.
    private long _droppedIn, _droppedOut;

    public TunnelSession(OvpnProfile profile, TunnelSessionOptions? options = null)
    {
        _profile = profile;
        _options = options ?? new TunnelSessionOptions();
        _policy = _options.Policy ?? profile.Policy;
    }

    public ConnectionPolicy Policy => _policy;

    /// <summary>Raised on every change of state, on the session's own loop; byte counts move between raises, so read
    /// <see cref="Status"/> for them.</summary>
    public event Action<SessionStatus>? StatusChanged;

    /// <summary>Raised with each new tunnel's network BEFORE the session reports <see cref="SessionState.Up"/>, so what a
    /// host attaches here — a proxy, a packet router — is ready by the time anyone sees "up".</summary>
    public event Action<TunnelNetwork>? NetworkUp;

    /// <summary>Raised with each new tunnel and its generation once its consumer is built, BEFORE "up" is reported.</summary>
    public event Action<Tunnel, int>? TunnelUp;

    /// <summary>Raised when a generation's tunnel has gone and its consumer is disposed, with why it went.</summary>
    public event Action<int, string?>? TunnelDown;

    /// <summary>Raised when a consumer threw as it was disposed; the session goes on.</summary>
    public event Action<Exception>? ConsumerFailed;

    public SessionStatus Status
    {
        get
        {
            var s = _status;
            var counters = _tunnel?.Counters;
            return s with
            {
                // The proxy's network says its address; a consumer of another kind, the tunnel's pushed one.
                Address = _network?.LocalAddress.ToString() ?? _tunnel?.Config.Address?.ToString(),
                BytesIn = Interlocked.Read(ref _droppedIn) + (counters?.BytesIn ?? 0),
                BytesOut = Interlocked.Read(ref _droppedOut) + (counters?.BytesOut ?? 0),
            };
        }
    }

    /// <summary>The current tunnel's network, or null while there is none.</summary>
    public TunnelNetwork? Network => _network;

    /// <summary>The tunnel itself, for tests that put a packet straight into it.</summary>
    internal Tunnel? CurrentTunnel => _tunnel;

    /// <summary>Runs until <paramref name="ct"/> is cancelled, or until the session cannot go on: the profile cannot be
    /// served (it names no remote, or asks for what this version does not do), the server refused its credentials and the
    /// policy does not retry that, or the policy's rounds ran out.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (_profile.Remotes.Count == 0 || Tunnel.Unsupported(_profile) is not null)
        {
            Report(SessionState.Refused, null, 0, 0, Tunnel.Unsupported(_profile) ?? "the profile names no remote");
            return;
        }
        var connect = _options.Connect ?? ((remote, timeout, token) => Tunnel.ConnectAsync(_profile, remote, timeout,
            _options.Tunnel with { Ping = _policy.Ping, PingRestart = _policy.PingRestart, KeepaliveOverridesServer = _policy.KeepaliveOverridesServer }, ct: token));
        var attempts = 0;
        var reconnects = 0;
        var failedRounds = 0;
        string? lastError = null;
        var end = SessionState.Stopped;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Tunnel? tunnel = null;
                var refused = false;
                foreach (var remote in Order())
                {
                    attempts++;
                    // "connecting" until a tunnel has been up once, as Kobo's status file has always said.
                    Report(reconnects == 0 ? SessionState.Connecting : SessionState.Reconnecting, Label(remote), attempts, reconnects, lastError);
                    try
                    {
                        tunnel = await connect(remote, _policy.AttemptTimeout, ct);
                        break;
                    }
                    catch (HandshakeFailed e) when (e.Stage == HandshakeStage.Profile)
                    {
                        Report(SessionState.Refused, null, attempts, reconnects, e.Message);
                        return;
                    }
                    catch (HandshakeFailed e)
                    {
                        // This server, this time: it may answer the next attempt, or the next remote may — unless it
                        // refused the credentials, which every remote of one profile shares.
                        lastError = e.Message;
                        if (e.AuthenticationRefused && !_policy.RetryAfterAuthFailure) { refused = true; break; }
                    }
                }
                if (refused) { end = SessionState.Refused; break; }
                if (tunnel is null)
                {
                    failedRounds++;
                    if (_policy.RoundsMax is { } most && failedRounds >= most) { end = SessionState.GaveUp; break; }
                    Report(SessionState.Reconnecting, null, attempts, reconnects, lastError);
                    await Task.Delay(_policy.WaitAfter(failedRounds, _options.Random), ct);
                    continue;
                }

                failedRounds = 0;
                lastError = null;
                var generation = reconnects + 1;
                _tunnel = tunnel;
                if (_options.Consumer is { } consume) _consumer = consume(tunnel, generation);
                else
                {
                    var network = new TunnelNetwork(tunnel);
                    _network = network;
                    _consumer = network;
                    NetworkUp?.Invoke(network);
                }
                TunnelUp?.Invoke(tunnel, generation);
                Report(SessionState.Up, Label(tunnel.Remote), attempts, reconnects, null);

                var authRefused = false;
                try { await tunnel.Completion.WaitAsync(ct); }
                catch (TunnelEnded e)
                {
                    lastError = e.Message;
                    authRefused = e.AuthenticationRefused;
                }
                // Stopped while up: this generation still goes down, and is said to.
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                await DropTunnelAsync();
                TunnelDown?.Invoke(generation, lastError);
                if (ct.IsCancellationRequested) break;
                if (authRefused && !_policy.RetryAfterAuthFailure) { end = SessionState.Refused; break; }
                reconnects++;
                Report(SessionState.Reconnecting, null, attempts, reconnects, lastError);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped while connecting or waiting to retry: nothing was up to take down.
        }
        await DropTunnelAsync();
        Report(end, null, attempts, reconnects, lastError);
    }

    // The remotes for one round: as listed, or shuffled afresh (remote-random).
    private IEnumerable<Remote> Order()
    {
        if (!_policy.RandomOrder) return _profile.Remotes;
        var order = _profile.Remotes.ToArray();
        _options.Random.Shuffle(order);
        return order;
    }

    private async Task DropTunnelAsync()
    {
        var consumer = _consumer;
        var tunnel = _tunnel;
        _network = null;
        _consumer = null;
        // A consumer's failure to let go ends that consumer, not the session: the tunnel still goes, and the next comes.
        if (consumer is not null)
            try { await consumer.DisposeAsync(); }
            catch (Exception e) { ConsumerFailed?.Invoke(e); }
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
