using System.Net;
using System.Net.Sockets;
using Sallyport.Net;
using Sallyport.Profile;
using Sallyport.Socks;

namespace Sallyport.Hosting;

/// <summary>Where a <see cref="TunnelService"/> is, for a host to show. No secret and no host name: what a status file
/// or a window may print.</summary>
public sealed record TunnelServiceStatus(
    string State, DateTimeOffset Since, string? Remote, int Attempts, int Reconnects, string? LastError,
    string? Address, int OpenConnections, long BytesUp, long BytesDown, int? ProxyPort)
{
    public const string Connecting = "connecting", Up = "up", Reconnecting = "reconnecting", Stopped = "stopped", Refused = "refused";
}

/// <summary>
/// A tunnel kept up behind a loopback SOCKS5 proxy: connects, serves, and when the tunnel ends — the server restarted,
/// renegotiated, or the network dropped — connects again with backoff while the proxy stays bound. The proxy binds only
/// once the FIRST tunnel is up, so "the port answers" means "the route works".
/// </summary>
public sealed class TunnelService : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30), LongestBackoff = TimeSpan.FromSeconds(30);

    private readonly OvpnProfile _profile;
    private readonly IPEndPoint _listen;
    private readonly Action<TunnelServiceStatus>? _onStatus;
    private readonly SwitchingConnector _connector = new();
    private Socks5Server? _proxy;
    private Tunnel? _tunnel;
    private TunnelNetwork? _network;
    private TunnelServiceStatus _status;

    public TunnelService(OvpnProfile profile, IPEndPoint listen, Action<TunnelServiceStatus>? onStatus = null)
    {
        _profile = profile;
        _listen = listen;
        _onStatus = onStatus;
        _status = new TunnelServiceStatus(TunnelServiceStatus.Connecting, DateTimeOffset.UtcNow, null, 0, 0, null, null, 0, 0, 0, null);
    }

    public TunnelServiceStatus Status
    {
        get
        {
            var s = _status;
            var proxy = _proxy;
            var network = _network;
            return s with
            {
                OpenConnections = proxy?.ActiveConnections ?? 0,
                BytesUp = proxy?.BytesUp ?? 0,
                BytesDown = proxy?.BytesDown ?? 0,
                Address = network?.LocalAddress.ToString(),
            };
        }
    }

    /// <summary>Runs until <paramref name="ct"/> is cancelled. Returns early only when the profile cannot be served at
    /// all (the server refused it, or it asks for what this version does not do) — retrying that would only repeat it.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(2);
        var attempts = 0;
        var reconnects = 0;
        while (!ct.IsCancellationRequested)
        {
            Exception? failure = null;
            foreach (var remote in _profile.Remotes)
            {
                attempts++;
                Report(_tunnel is null && reconnects == 0 ? TunnelServiceStatus.Connecting : TunnelServiceStatus.Reconnecting,
                    $"{remote.Proto}/{remote.Port}", attempts, reconnects, failure?.Message);
                try
                {
                    _tunnel = await Tunnel.ConnectAsync(_profile, remote, ConnectTimeout, ct: ct);
                    failure = null;
                    break;
                }
                catch (HandshakeFailed e) when (e.Stage == HandshakeStage.Profile)
                {
                    // The profile asks for what this version does not do: every retry would refuse it the same way.
                    Report(TunnelServiceStatus.Refused, null, attempts, reconnects, e.Message);
                    return;
                }
                catch (HandshakeFailed e)
                {
                    // Anything later is this server, this time — it may answer the next attempt, or the next remote may.
                    failure = e;
                }
            }
            if (_tunnel is null)
            {
                Report(TunnelServiceStatus.Reconnecting, null, attempts, reconnects, failure?.Message);
                try { await Task.Delay(backoff, ct); } catch (OperationCanceledException) { break; }
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, LongestBackoff.Ticks));
                continue;
            }

            backoff = TimeSpan.FromSeconds(2);
            _network = new TunnelNetwork(_tunnel);
            _connector.Current = _network;
            if (_proxy is null)
            {
                _proxy = new Socks5Server(_connector, _listen);
                _proxy.Start();
            }
            Report(TunnelServiceStatus.Up, $"{_tunnel.Remote.Proto}/{_tunnel.Remote.Port}", attempts, reconnects, null);

            string? why = null;
            try { await _tunnel.Completion.WaitAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (TunnelEnded e) { why = e.Message; }
            await DropTunnelAsync();
            if (ct.IsCancellationRequested) break;
            reconnects++;
            Report(TunnelServiceStatus.Reconnecting, null, attempts, reconnects, why);
        }
        Report(TunnelServiceStatus.Stopped, null, attempts, reconnects, _status.LastError);
    }

    private async Task DropTunnelAsync()
    {
        _connector.Current = null;
        if (_network is not null) await _network.DisposeAsync();
        if (_tunnel is not null) await _tunnel.DisposeAsync();
        _network = null;
        _tunnel = null;
    }

    private void Report(string state, string? remote, int attempts, int reconnects, string? error)
    {
        _status = new TunnelServiceStatus(state, DateTimeOffset.UtcNow, remote, attempts, reconnects, error, null, 0, 0, 0,
            _proxy?.Endpoint.Port);
        _onStatus?.Invoke(Status);
    }

    public async ValueTask DisposeAsync()
    {
        if (_proxy is not null) await _proxy.DisposeAsync();
        await DropTunnelAsync();
    }

    /// <summary>The proxy's connector across reconnects: whichever network is current, and a clean "unreachable" in the
    /// gap between two, which the proxy answers as SOCKS' "network unreachable".</summary>
    private sealed class SwitchingConnector : IConnector
    {
        private volatile TunnelNetwork? _current;
        public TunnelNetwork? Current { get => _current; set => _current = value; }

        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken ct) =>
            _current is { } network
                ? new TunnelConnector(network).ConnectAsync(host, port, ct)
                : ValueTask.FromException<Stream>(new SocketException((int)SocketError.NetworkDown));
    }
}
