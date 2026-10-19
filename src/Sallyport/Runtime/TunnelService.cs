using System.Net;
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
    public const string Connecting = "connecting", Up = "up", Reconnecting = "reconnecting", Stopped = "stopped", Refused = "refused";
}

/// <summary>
/// A <see cref="TunnelSession"/> behind a loopback SOCKS5 proxy. The proxy binds only once the FIRST tunnel is up, so
/// "the port answers" means "the route works", and stays bound across reconnects.
/// </summary>
public sealed class TunnelService : IAsyncDisposable
{
    private readonly IPEndPoint _listen;
    private readonly Action<TunnelServiceStatus>? _onStatus;
    private readonly TunnelSession _session;
    private Socks5Server? _proxy;

    public TunnelService(OvpnProfile profile, IPEndPoint listen, Action<TunnelServiceStatus>? onStatus = null, TunnelSessionOptions? options = null)
    {
        _listen = listen;
        _onStatus = onStatus;
        _session = new TunnelSession(profile, options);
        _session.NetworkUp += OnNetworkUp;
        _session.StatusChanged += _ => _onStatus?.Invoke(Status);
    }

    public TunnelServiceStatus Status
    {
        get
        {
            var s = _session.Status;
            var proxy = _proxy;
            return new TunnelServiceStatus(Name(s.State), s.Since, s.Remote, s.Attempts, s.Reconnects, s.LastError, s.Address,
                proxy?.ActiveConnections ?? 0, proxy?.BytesUp ?? 0, proxy?.BytesDown ?? 0, proxy?.Endpoint.Port);
        }
    }

    /// <summary>Runs until <paramref name="ct"/> is cancelled, or returns early when the profile cannot be served at all.</summary>
    public Task RunAsync(CancellationToken ct) => _session.RunAsync(ct);

    private void OnNetworkUp(TunnelNetwork network)
    {
        // Bound before the session says "up", and kept across reconnects: the connector follows the session's network.
        if (_proxy is not null) return;
        _proxy = new Socks5Server(new SessionConnector(_session), _listen);
        _proxy.Start();
    }

    private static string Name(SessionState state) => state switch
    {
        SessionState.Connecting => TunnelServiceStatus.Connecting,
        SessionState.Up => TunnelServiceStatus.Up,
        SessionState.Reconnecting => TunnelServiceStatus.Reconnecting,
        SessionState.Refused => TunnelServiceStatus.Refused,
        _ => TunnelServiceStatus.Stopped,
    };

    public async ValueTask DisposeAsync()
    {
        if (_proxy is not null) await _proxy.DisposeAsync();
        await _session.DisposeAsync();
    }
}
