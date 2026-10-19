using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Sallyport.Proxy;
using Sallyport.Runtime;
using Shenora.Core.Ipc;

namespace Sallyport.Client;

/// <summary>Where a tunnel comes out, as a public lookup service sees it through the tunnel's own SOCKS port.</summary>
internal sealed record Location(string? Ip, string? City, string? Region, string? Country, string? Org, DateTimeOffset Checked);

/// <summary>
/// The client's channels (docs/design/window.md): one per profile in use, one login each, in this process (D14). A
/// channel carries its profile's SOCKS5 proxy on the profile's loopback port and the apps routed through it
/// (<see cref="GameRunner"/>), each generation's packets parted between the two by its dispatcher. It is up while the
/// person connected it or a route holds it, so stopping one leaves the other working. A channel that ends on its own
/// (refused, or given up) keeps its last status until connected again, so the window can say why.
/// </summary>
internal sealed class Connections : IDisposable
{
    /// <summary>Long enough for the exit notices to reach every server; an exit never hangs on a dead one.</summary>
    private static readonly TimeSpan LeaveWithin = TimeSpan.FromSeconds(5);

    private readonly ProfileStore _store;
    private readonly ClientSettings _settings;
    private readonly object _gate = new();
    private readonly Dictionary<string, Channel> _channels = [];
    private readonly Dictionary<string, TunnelServiceStatus> _ended = [];
    private readonly System.Threading.Timer _poll;
    private Dictionary<string, TunnelServiceStatus> _shown = [];

    /// <summary>One channel, from the first who needed it to the last who let it go. What is learned about it lives
    /// here, so a new channel starts with none of it and a late answer for an old one has nowhere to land.</summary>
    private sealed class Channel(TunnelSession session, int port)
    {
        public TunnelSession Session { get; } = session;
        public CancellationTokenSource Stop { get; } = new();
        public int Port { get; } = port;
        public Task Run { get; set; } = Task.CompletedTask;
        public Socks5Server? Proxy { get; set; }
        /// <summary>Why the proxy is not listening, when it could not: a route through the channel works regardless.</summary>
        public string? ProxyProblem { get; set; }
        /// <summary>The person connected it (Connect); it stays up until they disconnect it.</summary>
        public bool Wanted { get; set; }
        /// <summary>The routes holding it, by their own key.</summary>
        public HashSet<string> Holders { get; } = [];
        public Location? Location { get; set; }
        /// <summary>The session's attempt count when it was last up: its count runs across the whole run.</summary>
        public int? AttemptsAtUp { get; set; }

        // The proxy's own counts beside the tunnel's: the bytes are the channel's whole, its proxy and its routes alike.
        public TunnelServiceStatus Status
        {
            get
            {
                var s = Session.Status;
                return new TunnelServiceStatus(Name(s.State), s.Since, s.Remote, s.Attempts, s.Reconnects, s.LastError, s.Address,
                    Proxy?.ActiveConnections ?? 0, s.BytesOut, s.BytesIn, Proxy?.Endpoint.Port);
            }
        }
    }

    /// <summary>Raised on a thread-pool thread whenever any profile's status differs from the last one raised.</summary>
    public event Action? Changed;

    /// <summary>Raised when a channel goes — disconnected by the person, or ended on its own — with the profile's id and
    /// why, in a sentence, for the routes that held it.</summary>
    public event Action<string, string>? ChannelEnded;

    public Connections(ProfileStore store, ClientSettings settings)
    {
        _store = store;
        _settings = settings;
        // Byte counts move without a state change; once a second is what a person reading them needs.
        _poll = new System.Threading.Timer(_ => Poll(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public TunnelServiceStatus? StatusOf(string id)
    {
        lock (_gate)
        {
            if (_channels.TryGetValue(id, out var channel)) return channel.Status;
            return _ended.GetValueOrDefault(id);
        }
    }

    public Location? LocationOf(string id)
    {
        lock (_gate) return _channels.GetValueOrDefault(id)?.Location;
    }

    /// <summary>Why the profile's proxy is not listening while its channel is up, or null.</summary>
    public string? ProxyProblemOf(string id)
    {
        lock (_gate) return _channels.GetValueOrDefault(id)?.ProxyProblem;
    }

    /// <summary>Attempts made since the tunnel was last up, or null when it has not been up in this run.</summary>
    public int? AttemptsSinceUp(string id)
    {
        lock (_gate)
        {
            if (!_channels.TryGetValue(id, out var channel) || channel.AttemptsAtUp is not { } atUp) return null;
            return channel.Session.Status.Attempts - atUp;
        }
    }

    /// <summary>Whether the profile's channel is up (or coming up), for whoever holds it.</summary>
    public bool IsRunning(string id)
    {
        lock (_gate) return _channels.ContainsKey(id);
    }

    /// <summary>Connects the profile for the person: its channel up, its proxy listening, until they disconnect it. A
    /// channel a route brought up already is kept, and its proxy opened if it is not.</summary>
    public void Connect(string id)
    {
        lock (_gate)
        {
            if (_channels.TryGetValue(id, out var existing))
            {
                existing.Wanted = true;
                if (existing.Proxy is null && existing.Session.Network is not null) Bind(existing);
                if (existing.ProxyProblem is { } problem) throw new ShenoraException("PORT_IN_USE", "port", existing.Port.ToString(), $"the proxy could not listen: {problem}");
            }
            else
            {
                var port = _settings.PortOf(id);
                if (_channels.Values.Any(c => c.Port == port))
                    throw new ShenoraException("PORT_TAKEN", "port", port.ToString(), $"another connected profile uses port {port}");
                // The proxy binds only once the tunnel is up; a port already taken is said now, not after the handshake.
                if (!ClientSettings.IsFree(port))
                    throw new ShenoraException("PORT_IN_USE", "port", port.ToString(), $"something else already listens on 127.0.0.1:{port}");
                Open(id, port).Wanted = true;
            }
        }
        Poll();
    }

    /// <summary>The profile's session, for a route to attach to: the channel brought up if it is not, held until
    /// <see cref="ReleaseAsync"/> with the same <paramref name="holder"/>.</summary>
    public TunnelSession Acquire(string id, string holder)
    {
        TunnelSession session;
        lock (_gate)
        {
            if (!_channels.TryGetValue(id, out var channel)) channel = Open(id, _settings.PortOf(id));
            channel.Holders.Add(holder);
            session = channel.Session;
        }
        Poll();
        return session;
    }

    /// <summary>Lets a route's hold go: the channel goes with it when nothing else holds it and the person did not
    /// connect it.</summary>
    public async Task ReleaseAsync(string id, string holder)
    {
        Channel? closing = null;
        lock (_gate)
        {
            if (_channels.TryGetValue(id, out var channel) && channel.Holders.Remove(holder) && channel.Holders.Count == 0 && !channel.Wanted)
            {
                _channels.Remove(id);
                closing = channel;
            }
        }
        if (closing is not null) await LeaveAsync(closing);
        Poll();
    }

    /// <summary>The routes holding the profile's channel, by their keys.</summary>
    public IReadOnlyCollection<string> HoldersOf(string id)
    {
        lock (_gate) return _channels.TryGetValue(id, out var channel) ? [.. channel.Holders] : [];
    }

    /// <summary>Takes the profile's channel down: its proxy, and the routes through it, which are told why.</summary>
    public async Task DisconnectAsync(string id)
    {
        Channel? channel;
        lock (_gate)
        {
            _ended.Remove(id);
            if (!_channels.Remove(id, out channel)) channel = null;
        }
        if (channel is not null)
        {
            if (channel.Holders.Count > 0) ChannelEnded?.Invoke(id, $"{_store.Find(id)?.Name ?? "Its profile"} was disconnected.");
            await LeaveAsync(channel);
        }
        Poll();
    }

    /// <summary>Waits for the profile to be up. Throws when it ends instead, or does not come up in time.</summary>
    public async Task<TunnelServiceStatus> WaitUpAsync(string id, TimeSpan within, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + within;
        while (true)
        {
            var status = StatusOf(id);
            if (status?.State == TunnelServiceStatus.Up) return status;
            if (!IsRunning(id))
                throw new ShenoraException("NOT_CONNECTED", "id", id, status?.LastError is { } why ? $"the profile did not connect: {why}" : "the profile is not connected");
            if (DateTime.UtcNow > deadline)
                throw new ShenoraException("CONNECT_TIMEOUT", "id", id, $"the profile was not up within {within.TotalSeconds:0} s");
            await Task.Delay(200, ct);
        }
    }

    /// <summary>Asks a public lookup service where this tunnel comes out, through the same SOCKS port an application
    /// would use: if this answers, the route the applications take works.</summary>
    public async Task<Location> LocateAsync(string id, CancellationToken ct)
    {
        Channel? channel;
        lock (_gate)
        {
            if (!_channels.TryGetValue(id, out channel) || channel.Session.Status.State != SessionState.Up)
                throw new ShenoraException("NOT_CONNECTED", "id", id, "the profile is not up");
        }
        // Could not be checked — not "failed": the proxy not listening says nothing of the tunnel (rule 6).
        if (channel.Proxy is not { } proxy)
            throw new ShenoraException("CHECK_UNAVAILABLE", "id", id, "its proxy is not listening, so the location could not be checked");

        using var handler = new SocketsHttpHandler
        {
            // .NET hands a SOCKS5 proxy the host name, so the lookup's name is resolved at the far end too.
            Proxy = new WebProxy($"socks5://127.0.0.1:{proxy.Endpoint.Port}"),
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        JsonElement answer;
        try { answer = JsonSerializer.Deserialize<JsonElement>(await http.GetStringAsync("https://ipinfo.io/json", ct)); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Could not be checked — not "failed": the lookup service is not the tunnel (rule 6).
            throw new ShenoraException("CHECK_UNAVAILABLE", "id", id, "the lookup did not answer through the tunnel; the tunnel itself may still work");
        }

        string? Read(string name) => answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var location = new Location(Read("ip"), Read("city"), Read("region"), Read("country"), Read("org"), DateTimeOffset.Now);
        lock (_gate) channel.Location = location;
        Poll(force: true);
        return location;
    }

    public void Dispose()
    {
        _poll.Dispose();
        List<Channel> all;
        lock (_gate)
        {
            all = [.. _channels.Values];
            _channels.Clear();
        }
        // Off the UI thread: the exit notices are async, and the message loop that would resume them is gone by now.
        Task.Run(() => Task.WhenAll(all.Select(LeaveAsync))).Wait(LeaveWithin + TimeSpan.FromSeconds(1));
    }

    // Called under _gate.
    private Channel Open(string id, int port)
    {
        Profile.OvpnProfile profile;
        try { profile = _store.Load(id); }
        catch (FileNotFoundException) { throw new ShenoraException("PROFILE_GONE", "id", id, "the profile is no longer stored"); }
        // The message of what failed to open is not passed on: it could quote the profile.
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or FormatException)
        { throw new ShenoraException("PROFILE_UNREADABLE", "id", id, "the stored profile could not be opened"); }

        // The profile's own connection settings, with the person's changes over them.
        var session = new TunnelSession(profile, new TunnelSessionOptions { Policy = _settings.PolicyOf(id).Over(profile.Policy) });
        var channel = new Channel(session, port);
        // Bound before the session says "up", and kept across reconnects: the connector follows the session's network.
        session.NetworkUp += _ => Bind(channel);
        session.StatusChanged += _ => Poll();
        _ended.Remove(id);
        _channels[id] = channel;
        channel.Run = Task.Run(() => session.RunAsync(channel.Stop.Token));
        _ = channel.Run.ContinueWith(run => OnEndedAsync(id, channel, run), TaskScheduler.Default).Unwrap();
        return channel;
    }

    // The proxy on the profile's port, once: a port something else took is the proxy's problem, said, and never the
    // routes' — they work without it (docs/design/window.md).
    private static void Bind(Channel channel)
    {
        lock (channel)
        {
            if (channel.Proxy is not null) return;
            try
            {
                var proxy = new Socks5Server(new SessionConnector(channel.Session), new IPEndPoint(IPAddress.Loopback, channel.Port));
                proxy.Start();
                channel.Proxy = proxy;
                channel.ProxyProblem = null;
            }
            catch (SocketException) { channel.ProxyProblem = $"port {channel.Port} is in use by something else"; }
        }
    }

    private async Task OnEndedAsync(string id, Channel channel, Task run)
    {
        if (channel.Stop.IsCancellationRequested) return;
        // Ended on its own: keep what it last said, and let go of it.
        var last = channel.Status;
        if (run.IsFaulted) last = last with { State = TunnelServiceStatus.Stopped, LastError = run.Exception!.InnerException?.GetType().Name ?? "failed" };
        lock (_gate)
        {
            if (!_channels.TryGetValue(id, out var current) || current != channel) return;
            _channels.Remove(id);
            _ended[id] = last;
        }
        if (channel.Holders.Count > 0)
            ChannelEnded?.Invoke(id, channel.Session.Status.State == SessionState.GaveUp
                ? $"The connection was given up after its retries. {Sentence(last.LastError)}".TrimEnd()
                : $"The connection was refused: {last.LastError ?? "no reason given"}.");
        await DisposeAsync(channel);
        Poll();
    }

    private static async Task LeaveAsync(Channel channel)
    {
        channel.Stop.Cancel();
        try { await channel.Run.WaitAsync(LeaveWithin); }
        catch (Exception) { /* Leaving is what was asked; how the run ended no longer matters. */ }
        await DisposeAsync(channel);
    }

    private static async Task DisposeAsync(Channel channel)
    {
        Socks5Server? proxy;
        lock (channel)
        {
            proxy = channel.Proxy;
            channel.Proxy = null;
        }
        if (proxy is not null) await proxy.DisposeAsync();
        await channel.Session.DisposeAsync();
        channel.Stop.Dispose();
    }

    private void Poll(bool force = false)
    {
        bool changed;
        lock (_gate)
        {
            var now = new Dictionary<string, TunnelServiceStatus>(_ended);
            foreach (var (id, channel) in _channels)
            {
                var status = channel.Status;
                // Every state change polls, so an up between two losses is never missed.
                if (status.State == TunnelServiceStatus.Up) channel.AttemptsAtUp = status.Attempts;
                now[id] = status;
            }
            changed = force || now.Count != _shown.Count || now.Any(p => !_shown.TryGetValue(p.Key, out var was) || was != p.Value);
            _shown = now;
        }
        if (changed) Changed?.Invoke();
    }

    private static string Name(SessionState state) => state switch
    {
        SessionState.Connecting => TunnelServiceStatus.Connecting,
        SessionState.Up => TunnelServiceStatus.Up,
        SessionState.Reconnecting => TunnelServiceStatus.Reconnecting,
        SessionState.Refused => TunnelServiceStatus.Refused,
        _ => TunnelServiceStatus.Stopped,
    };

    private static string Sentence(string? text) =>
        string.IsNullOrEmpty(text) ? "" : char.ToUpperInvariant(text[0]) + text[1..] + (text.EndsWith('.') ? "" : ".");
}
