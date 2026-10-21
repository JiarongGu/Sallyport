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
/// IP packets carried, across every tunnel the session has had. <see cref="NextAttemptAt"/>: when the next round of
/// attempts starts, while the session waits for it after one that failed — and only then, so a countdown is never
/// shown that is not one; <see cref="FailedRounds"/>: rounds failed since the tunnel was last up.</summary>
public sealed record SessionStatus(
    SessionState State, DateTimeOffset Since, string? Remote, int Attempts, int Reconnects, string? LastError,
    string? Address, long BytesIn, long BytesOut, DateTimeOffset? NextAttemptAt = null, int FailedRounds = 0,
    bool AuthenticationRefused = false);

/// <summary>Options a session's host rarely sets.</summary>
public sealed class TunnelSessionOptions
{
    /// <summary>How the tunnel is kept up; null, the profile's own (<see cref="OvpnProfile.Policy"/>).</summary>
    public ConnectionPolicy? Policy { get; init; }
    public TunnelOptions Tunnel { get; init; } = new();
    /// <summary>What each tunnel's packets go to, given the tunnel and its generation (1 for the first): the one reader
    /// of <see cref="Tunnel.Packets"/>, disposed when that tunnel goes. Null: a <see cref="TunnelDispatcher"/> — the
    /// userspace TCP a proxy dials through (<see cref="TunnelSession.Network"/>), with a packet router attachable beside it
    /// (<see cref="TunnelSession.DispatcherUp"/>).</summary>
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
    private volatile TunnelDispatcher? _dispatcher;
    // What tunnels already dropped carried, so the totals do not fall back to zero on a reconnect.
    private long _droppedIn, _droppedOut;
    // The session's own end: disposing it stops its run, whatever the caller's token says, so a disposed session never
    // connects again.
    private readonly CancellationTokenSource _lifetime = new();
    private Task _running = Task.CompletedTask;
    private int _disposed;

    public TunnelSession(OvpnProfile profile, TunnelSessionOptions? options = null)
    {
        _profile = profile;
        _options = options ?? new TunnelSessionOptions();
        _policy = _options.Policy ?? profile.Policy;
        // A round with no remote in it would fail at once and wait, forever, with nothing tried and nothing to say.
        if (_policy.Protocol is { } only && !profile.Remotes.Any(r => r.Proto == only))
            throw new ArgumentException($"The profile has no {only} remote.", nameof(options));
    }

    public ConnectionPolicy Policy => _policy;

    /// <summary>Raised on every change of state, on the session's own loop; byte counts move between raises, so read
    /// <see cref="Status"/> for them.</summary>
    public event Action<SessionStatus>? StatusChanged;

    /// <summary>Raised with each new tunnel's network BEFORE the session reports <see cref="SessionState.Up"/>, so what a
    /// host attaches here — a proxy, a packet router — is ready by the time anyone sees "up".</summary>
    public event Action<TunnelNetwork>? NetworkUp;

    /// <summary>Raised with each new tunnel's dispatcher BEFORE "up" is reported, so a packet router attached here
    /// (<see cref="TunnelDispatcher.Attach"/>) has the generation from its first packet.</summary>
    public event Action<TunnelDispatcher>? DispatcherUp;

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

    /// <summary>The current tunnel's dispatcher, or null while there is none (or the session has a consumer of its own).</summary>
    public TunnelDispatcher? Dispatcher => _dispatcher;

    /// <summary>The tunnel itself, for tests that put a packet straight into it.</summary>
    internal Tunnel? CurrentTunnel => _tunnel;

    /// <summary>Runs until <paramref name="ct"/> is cancelled or the session disposed, or until the session cannot go on:
    /// the profile cannot be served (it names no remote, or asks for what this version does not do), the server refused
    /// its credentials and the policy does not retry that, or the policy's rounds ran out.</summary>
    public Task RunAsync(CancellationToken ct)
    {
        var run = RunUntilAsync(ct);
        _running = run;
        return run;
    }

    private async Task RunUntilAsync(CancellationToken caller)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(caller, _lifetime.Token);
        var ct = lifetime.Token;
        var cannot = _profile.Remotes.Count == 0 ? "the profile names no remote"
            : Tunnel.Unsupported(_profile)
            ?? (_profile.AuthUserPass && _options.Tunnel.Login is null ? "the profile asks for a username and password, and none was given" : null);
        if (cannot is not null)
        {
            Report(SessionState.Refused, null, 0, 0, cannot, 0);
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
                    Report(reconnects == 0 ? SessionState.Connecting : SessionState.Reconnecting, Label(remote), attempts, reconnects, lastError, failedRounds);
                    try
                    {
                        tunnel = await connect(remote, _policy.AttemptTimeout, ct);
                        break;
                    }
                    catch (HandshakeFailed e) when (e.Stage == HandshakeStage.Profile)
                    {
                        Report(SessionState.Refused, null, attempts, reconnects, e.Message, failedRounds);
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
                    var wait = _policy.WaitAfter(failedRounds, _options.Random);
                    // Still "connecting" while the tunnel has never been up, as the attempts themselves are said.
                    Report(reconnects == 0 ? SessionState.Connecting : SessionState.Reconnecting, null, attempts, reconnects, lastError, failedRounds,
                        DateTimeOffset.UtcNow + wait);
                    await Task.Delay(wait, ct);
                    continue;
                }

                failedRounds = 0;
                lastError = null;
                var generation = reconnects + 1;
                _tunnel = tunnel;
                if (_options.Consumer is { } consume) _consumer = consume(tunnel, generation);
                else
                {
                    var dispatcher = new TunnelDispatcher(tunnel, generation);
                    _dispatcher = dispatcher;
                    _network = dispatcher.Network;
                    _consumer = dispatcher;
                    NetworkUp?.Invoke(dispatcher.Network);
                    DispatcherUp?.Invoke(dispatcher);
                }
                TunnelUp?.Invoke(tunnel, generation);
                Report(SessionState.Up, Label(tunnel.Remote), attempts, reconnects, null, 0);

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
                Report(SessionState.Reconnecting, null, attempts, reconnects, lastError, 0);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped while connecting or waiting to retry: nothing was up to take down.
        }
        await DropTunnelAsync();
        // Refused here is only ever the server's answer to the credentials: a profile it cannot serve returned above.
        Report(end, null, attempts, reconnects, lastError, failedRounds, authenticationRefused: end == SessionState.Refused);
    }

    // The remotes for one round, of the policy's transport when it names one: as listed, or shuffled afresh (remote-random).
    private IEnumerable<Remote> Order()
    {
        var remotes = _policy.Protocol is { } only ? _profile.Remotes.Where(r => r.Proto == only).ToList() : _profile.Remotes;
        if (!_policy.RandomOrder) return remotes;
        var order = remotes.ToArray();
        _options.Random.Shuffle(order);
        return order;
    }

    private async Task DropTunnelAsync()
    {
        var consumer = _consumer;
        var tunnel = _tunnel;
        _network = null;
        _dispatcher = null;
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

    private void Report(SessionState state, string? remote, int attempts, int reconnects, string? error, int failedRounds, DateTimeOffset? next = null,
        bool authenticationRefused = false)
    {
        _status = new SessionStatus(state, DateTimeOffset.UtcNow, remote, attempts, reconnects, error, null, 0, 0, next, failedRounds, authenticationRefused);
        StatusChanged?.Invoke(Status);
    }

    private static string Label(Remote remote) => $"{remote.Proto}/{remote.Port}";

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        await _lifetime.CancelAsync();
        // Whatever ended the run was its caller's to hear; here it only has to have ended — or, stuck past ten seconds in
        // a step that does not hear its token, to have its tunnel taken from under it as before.
        try { await _running.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception) { }
        await DropTunnelAsync();
    }
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
