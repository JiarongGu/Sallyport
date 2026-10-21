using System.Net;
using System.Net.Sockets;
using Sallyport.Net;
using Sallyport.Profile;
using Sallyport.Proxy;

namespace Sallyport.Runtime;

/// <summary>Where a <see cref="TunnelService"/> is, for a host to show. No secret and no host name: what a status file
/// or a window may print. Kobo reads it as JSON, so its shape is kept as it is.</summary>
public sealed record TunnelServiceStatus(
    string State, DateTimeOffset Since, string? Remote, int Attempts, int Reconnects, string? LastError,
    string? Address, int OpenConnections, long BytesUp, long BytesDown, int? ProxyPort)
{
    /// <summary>Trying to bring the first tunnel up.</summary>
    public const string Connecting = "connecting";
    /// <summary>A tunnel is up and the proxy listening.</summary>
    public const string Up = "up";
    /// <summary>Was up; connecting again. The proxy stays bound, its connections answered "network down" meanwhile.</summary>
    public const string Reconnecting = "reconnecting";
    /// <summary>The run ended: stopped by its host, or its connection policy gave up.</summary>
    public const string Stopped = "stopped";
    /// <summary>Cannot go on as it is: the profile, the credentials or the proxy's port. <see cref="LastError"/> says which.</summary>
    public const string Refused = "refused";
}

/// <summary>
/// A <see cref="TunnelSession"/> behind a loopback SOCKS5 proxy, for a host that routes a browser or another program
/// through one profile. The proxy binds only once the FIRST tunnel is up, so "the port answers" means "the route works",
/// and stays bound across reconnects. The host's own requests need no proxy: <see cref="CreateHttpHandler"/>.
/// </summary>
public sealed class TunnelService : IAsyncDisposable
{
    private readonly IPEndPoint _listen;
    private readonly TunnelSession _session;
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile Socks5Server? _proxy;
    // Why the proxy could not listen, which ends the run as a refusal: the port is the host's to change.
    private volatile string? _cannotListen;
    private CancellationTokenSource? _run;

    /// <param name="profile">The profile, parsed by the host from wherever it keeps it.</param>
    /// <param name="listen">Where the proxy listens; null or port 0, a free port on loopback, reported in
    /// <see cref="ProxyEndpoint"/> and <see cref="TunnelServiceStatus.ProxyPort"/> once bound.</param>
    /// <param name="options">The session's options: its credentials, its log, its policy.</param>
    public TunnelService(OvpnProfile profile, IPEndPoint? listen = null, TunnelSessionOptions? options = null)
    {
        _listen = listen ?? new IPEndPoint(IPAddress.Loopback, 0);
        _session = new TunnelSession(profile, options);
        _session.NetworkUp += OnNetworkUp;
        _session.StatusChanged += OnSessionStatus;
    }

    /// <summary>Raised on every change of state, on the session's own loop — a reconnect is
    /// <see cref="TunnelServiceStatus.Reconnecting"/>, never a "stopped" between two "up"s.</summary>
    public event Action<TunnelServiceStatus>? StatusChanged;

    /// <summary>True once the first tunnel is up and the proxy listening — what a host awaits in place of probing the
    /// port; false if the run ended without that (the profile refused, the policy gave up, stopped first).</summary>
    public Task<bool> Ready => _ready.Task;

    /// <summary>Where the proxy listens, once it does; null before the first tunnel is up.</summary>
    public IPEndPoint? ProxyEndpoint => _proxy?.Endpoint;

    /// <summary>Where the service is now, its byte counts current.</summary>
    public TunnelServiceStatus Status
    {
        get
        {
            var s = _session.Status;
            var proxy = _proxy;
            var state = _cannotListen is null ? Name(s.State) : TunnelServiceStatus.Refused;
            return new TunnelServiceStatus(state, s.Since, s.Remote, s.Attempts, s.Reconnects, _cannotListen ?? s.LastError, s.Address,
                proxy?.ActiveConnections ?? 0, proxy?.BytesUp ?? 0, proxy?.BytesDown ?? 0, proxy?.Endpoint.Port);
        }
    }

    /// <inheritdoc cref="TunnelSession.ConnectAsync"/>
    public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken ct = default) => _session.ConnectAsync(host, port, ct);

    /// <inheritdoc cref="TunnelSession.ResolveAsync"/>
    public Task<IReadOnlyList<IPAddress>> ResolveAsync(string name, CancellationToken ct = default) => _session.ResolveAsync(name, ct);

    /// <inheritdoc cref="TunnelSession.CreateHttpHandler"/>
    public SocketsHttpHandler CreateHttpHandler() => _session.CreateHttpHandler();

    /// <summary>Runs until <paramref name="ct"/> is cancelled, or returns early when the profile cannot be served at all
    /// or the proxy cannot listen where it was asked to (<see cref="TunnelServiceStatus.Refused"/>, and why).</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _run = run;
        try { await _session.RunAsync(run.Token); }
        finally { _ready.TrySetResult(false); }
    }

    private void OnNetworkUp(TunnelNetwork network)
    {
        // Bound before the session says "up", and kept across reconnects: the connector follows the session's network.
        if (_proxy is not null || _cannotListen is not null) return;
        try
        {
            var proxy = new Socks5Server(new SessionConnector(_session), _listen);
            proxy.Start();
            _proxy = proxy;
        }
        catch (SocketException e)
        {
            _cannotListen = $"the proxy could not listen at {_listen}: {e.SocketErrorCode}";
            _run?.Cancel();
        }
    }

    private void OnSessionStatus(SessionStatus status)
    {
        var now = Status;
        if (now.State == TunnelServiceStatus.Up && _proxy is not null) _ready.TrySetResult(true);
        StatusChanged?.Invoke(now);
    }

    private static string Name(SessionState state) => state switch
    {
        SessionState.Connecting => TunnelServiceStatus.Connecting,
        SessionState.Up => TunnelServiceStatus.Up,
        SessionState.Reconnecting => TunnelServiceStatus.Reconnecting,
        SessionState.Refused => TunnelServiceStatus.Refused,
        _ => TunnelServiceStatus.Stopped,
    };

    /// <summary>Stops the proxy and the session; what <see cref="RunAsync"/> was doing ends.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_proxy is { } proxy) await proxy.DisposeAsync();
        await _session.DisposeAsync();
    }
}
