using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Sallyport.Proxy;
using Sallyport.Runtime;
using Shenora.Core.Ipc;

namespace Sallyport.Client;

/// <summary>Where a tunnel comes out, as a public lookup service sees it through the tunnel's own SOCKS port.</summary>
internal sealed record Location(string? Ip, string? City, string? Region, string? Country, string? Org, DateTimeOffset Checked);

/// <summary>A path's round trips: how many echoes were sent and answered, the median of the answered, and who answered —
/// the destination, or only a hop on the way.</summary>
internal sealed record PathSample(int Sent, int Answered, double? MedianMs, string? Responder, bool FromDestination);

/// <summary>A channel's latency, checked every half minute: to a public address through the tunnel, and to the tunnel's
/// gateway (the VPN server's side) when the server named one.</summary>
internal sealed record Latency(string Target, PathSample Vpn, string? Gateway, PathSample? ToGateway, DateTimeOffset Checked);

/// <summary>A channel's bytes so far and its rates over the last minute, a second apart, oldest first: bytes per second
/// received (<see cref="InPerSecond"/>) and sent.</summary>
internal sealed record Traffic(long BytesIn, long BytesOut, IReadOnlyList<double> InPerSecond, IReadOnlyList<double> OutPerSecond);

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
    private Dictionary<string, (double In, double Out)> _shownRates = [];

    /// <summary>One channel, from the first who needed it to the last who let it go. What is learned about it lives
    /// here, so a new channel starts with none of it and a late answer for an old one has nowhere to land.</summary>
    private sealed class Channel(TunnelSession session)
    {
        public TunnelSession Session { get; } = session;
        public CancellationTokenSource Stop { get; } = new();
        public Task Run { get; set; } = Task.CompletedTask;
        public Socks5Server? Proxy { get; set; }
        /// <summary>Why the proxy is not listening, when it could not: a route through the channel works regardless.</summary>
        public string? ProxyProblem { get; set; }
        /// <summary>The person connected it (Connect); it stays up until they disconnect it.</summary>
        public bool Wanted { get; set; }
        /// <summary>The routes holding it, by their own key.</summary>
        public HashSet<string> Holders { get; } = [];
        public Location? Location { get; set; }
        public Latency? Latency { get; set; }
        public bool CheckingLatency { get; set; }
        /// <summary>The session's attempt count when it was last up: its count runs across the whole run.</summary>
        public int? AttemptsAtUp { get; set; }
        /// <summary>The byte counts once a second, newest last, for the traffic graph: a minute's worth and one more.</summary>
        public List<(DateTimeOffset At, long In, long Out)> Samples { get; } = [];

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

    /// <summary>A profile's server did not accept its username and password (D29): the window asks for them again.</summary>
    public event Action<string>? SignInRefused;

    /// <summary>The username and password a profile that asks for them connects with, from the host: one remembered, or one
    /// typed for this connection alone. Asked each time a channel opens.</summary>
    public Func<string, Login?>? LoginFor { get; set; }

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

    /// <summary>When the channel's next round of attempts starts, while it waits for one; null otherwise.</summary>
    public DateTimeOffset? NextAttemptOf(string id)
    {
        lock (_gate) return _channels.TryGetValue(id, out var channel) ? channel.Session.Status.NextAttemptAt : null;
    }

    /// <summary>The channel's traffic: its byte counts and its rates a second apart over the last minute.</summary>
    public Traffic? TrafficOf(string id)
    {
        lock (_gate) return _channels.TryGetValue(id, out var channel) ? Rates(channel) : null;
    }

    private static Traffic Rates(Channel channel)
    {
        var samples = channel.Samples;
        var inRates = new List<double>(samples.Count);
        var outRates = new List<double>(samples.Count);
        for (var i = 1; i < samples.Count; i++)
        {
            var seconds = (samples[i].At - samples[i - 1].At).TotalSeconds;
            // A reconnect starts the counts again: a fall is a new session, not traffic running backwards.
            inRates.Add(seconds <= 0 ? 0 : Math.Max(0, samples[i].In - samples[i - 1].In) / seconds);
            outRates.Add(seconds <= 0 ? 0 : Math.Max(0, samples[i].Out - samples[i - 1].Out) / seconds);
        }
        var last = samples.Count > 0 ? samples[^1] : default;
        return new Traffic(last.In, last.Out, inRates, outRates);
    }

    /// <summary>The channel's last latency check, and whether one is under way.</summary>
    public (Latency? Last, bool Checking) LatencyOf(string id)
    {
        lock (_gate) return _channels.TryGetValue(id, out var channel) ? (channel.Latency, channel.CheckingLatency) : (null, false);
    }

    /// <summary>Round trips now, four of each: to <see cref="LatencyTarget"/> through the tunnel, and to the tunnel's
    /// gateway. An echo not answered is counted, never an error: no answer says nothing of the tunnel itself (rule 6).</summary>
    public async Task CheckLatencyAsync(string id, CancellationToken ct)
    {
        Channel? channel;
        lock (_gate)
        {
            if (!_channels.TryGetValue(id, out channel) || channel.CheckingLatency) return;
            channel.CheckingLatency = true;
        }
        Poll(force: true);
        try
        {
            if (channel.Session.Dispatcher is not { } dispatcher)
                throw new ShenoraException("NOT_CONNECTED", "id", id, L.T("the profile is not up"));
            var target = IPAddress.Parse(LatencyTarget);
            var vpn = await SampleAsync(() => dispatcher.Echo.PingAsync(target, TimeSpan.FromSeconds(1.5), ct: ct), ct);
            var gateway = dispatcher.Tunnel.Config.Gateway;
            var toGateway = gateway is null ? null : await SampleAsync(() => dispatcher.Echo.PingAsync(gateway, TimeSpan.FromSeconds(1.5), ct: ct), ct);
            lock (_gate) channel.Latency = new Latency(LatencyTarget, vpn, gateway?.ToString(), toGateway, DateTimeOffset.Now);
        }
        finally
        {
            lock (_gate) channel.CheckingLatency = false;
            Poll(force: true);
        }
    }

    /// <summary>The public address latency is measured to: one that answers echoes from anywhere.</summary>
    public const string LatencyTarget = "1.1.1.1";

    private static async Task<PathSample> SampleAsync(Func<Task<Net.EchoAnswer>> echo, CancellationToken ct)
    {
        var times = new List<double>();
        string? responder = null;
        var fromDestination = false;
        for (var i = 0; i < 4; i++)
        {
            var answer = await echo();
            if (answer.Milliseconds is { } ms)
            {
                times.Add(ms);
                responder = answer.From?.ToString();
                fromDestination |= answer.FromDestination;
            }
            await Task.Delay(150, ct);
        }
        times.Sort();
        return new PathSample(4, times.Count, times.Count == 0 ? null : times[times.Count / 2], responder, fromDestination);
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
                if (existing.ProxyProblem is { } problem) throw new ShenoraException("PORT_IN_USE", "id", id, L.T("the proxy could not listen: {0}", problem));
            }
            else Open(id).Wanted = true;
        }
        Poll();
    }

    /// <summary>Whether the person connected the profile and has not disconnected it: its channel is up or coming up, or
    /// reconnecting. What the apps turned on follow (D23).</summary>
    public bool IsWanted(string id)
    {
        lock (_gate) return _channels.TryGetValue(id, out var channel) && channel.Wanted;
    }

    /// <summary>The profile's session, for a route to attach to, held until <see cref="ReleaseAsync"/> with the same
    /// <paramref name="holder"/>: the channel brought up if it is not — unless <paramref name="open"/> is false, when a
    /// profile not connected is refused.</summary>
    public TunnelSession Acquire(string id, string holder, bool open = true)
    {
        TunnelSession session;
        lock (_gate)
        {
            if (!_channels.TryGetValue(id, out var channel))
                channel = open ? Open(id) : throw new ShenoraException("NOT_CONNECTED", "id", id, L.T("the profile is not connected"));
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
            if (channel.Holders.Count > 0) ChannelEnded?.Invoke(id, L.T("{0} was disconnected.", _store.Find(id)?.Name ?? L.T("Its profile")));
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
                throw new ShenoraException("NOT_CONNECTED", "id", id, status?.LastError is { } why ? L.T("the profile did not connect: {0}", why) : L.T("the profile is not connected"));
            if (DateTime.UtcNow > deadline)
                throw new ShenoraException("CONNECT_TIMEOUT", "id", id, L.T("the profile was not up within {0:0} s", within.TotalSeconds));
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
                throw new ShenoraException("NOT_CONNECTED", "id", id, L.T("the profile is not up"));
        }
        // Could not be checked — not "failed": the proxy not listening says nothing of the tunnel (rule 6).
        if (channel.Proxy is not { } proxy)
            throw new ShenoraException("CHECK_UNAVAILABLE", "id", id, L.T("its proxy is not listening, so the location could not be checked"));

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
            throw new ShenoraException("CHECK_UNAVAILABLE", "id", id, L.T("the lookup did not answer through the tunnel; the tunnel itself may still work"));
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
    private Channel Open(string id)
    {
        Profile.OvpnProfile profile;
        try { profile = _store.Load(id); }
        catch (FileNotFoundException) { throw new ShenoraException("PROFILE_GONE", "id", id, L.T("the profile is no longer stored")); }
        // The message of what failed to open is not passed on: it could quote the profile.
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or FormatException)
        { throw new ShenoraException("PROFILE_UNREADABLE", "id", id, L.T("the stored profile could not be opened")); }

        // The profile's own connection settings, Settings' choices over them, and its own changes over those.
        var policy = _settings.PolicyFor(id, profile.Policy, [.. profile.Remotes.Select(r => r.Proto).Distinct()]);
        // Held to a transport the profile no longer has servers of (imported again without them): said, not tried forever.
        if (policy.Protocol is { } only && !profile.Remotes.Any(r => r.Proto == only))
            throw new ShenoraException("PROTOCOL_GONE", "id", id, L.T("the profile has no {0} servers any more: change its connection type", only.ToUpperInvariant()));
        var session = new TunnelSession(profile, new TunnelSessionOptions { Policy = policy, Tunnel = new TunnelOptions { Login = LoginFor?.Invoke(id) } });
        var channel = new Channel(session);
        // Bound before the session says "up", and kept across reconnects: the connector follows the session's network.
        session.NetworkUp += _ => Bind(channel);
        session.StatusChanged += _ => Poll();
        _ended.Remove(id);
        _channels[id] = channel;
        channel.Run = Task.Run(() => session.RunAsync(channel.Stop.Token));
        _ = channel.Run.ContinueWith(run => OnEndedAsync(id, channel, run), TaskScheduler.Default).Unwrap();
        return channel;
    }

    // The proxy on any free loopback port, once, Windows' choice when the channel opens (the owner: "the connection port
    // should be auto setup (not fixed just use any free when connecting)"): one profile's port can never be another's or
    // something else's. A proxy that could not listen is the proxy's problem, said, and never the routes' — they work
    // without it (docs/design/window.md).
    private static void Bind(Channel channel)
    {
        lock (channel)
        {
            if (channel.Proxy is not null) return;
            try
            {
                var proxy = new Socks5Server(new SessionConnector(channel.Session), new IPEndPoint(IPAddress.Loopback, 0));
                proxy.Start();
                channel.Proxy = proxy;
                channel.ProxyProblem = null;
            }
            catch (SocketException e) { channel.ProxyProblem = e.Message; }
        }
    }

    /// <summary>The port the profile's proxy listens on now, Windows' choice when it opened; null while it does not.</summary>
    public int? ProxyPortOf(string id)
    {
        lock (_gate) return _channels.TryGetValue(id, out var channel) ? channel.Proxy?.Endpoint.Port : null;
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
        if (channel.Session.Status.AuthenticationRefused) SignInRefused?.Invoke(id);
        else if (channel.Holders.Count > 0)
            ChannelEnded?.Invoke(id, channel.Session.Status.State == SessionState.GaveUp
                ? L.T("The connection was given up after its retries. {0}", Sentence(last.LastError)).TrimEnd()
                : L.T("The connection was refused: {0}.", last.LastError ?? L.T("no reason given")));
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

    /// <summary>How often an up channel's latency is checked again on its own (the owner: "we dont really need this 检测延迟
    /// button which we can directly display the latency to vpn server in the monitoring"): twelve echoes a check, over a
    /// few seconds, against a minute's traffic of a game.</summary>
    public static readonly TimeSpan LatencyEvery = TimeSpan.FromSeconds(30);

    private void Poll(bool force = false)
    {
        bool changed;
        List<(string Id, CancellationToken Stop)> due = [];
        lock (_gate)
        {
            var now = new Dictionary<string, TunnelServiceStatus>(_ended);
            var rates = new Dictionary<string, (double, double)>();
            var at = DateTimeOffset.UtcNow;
            foreach (var (id, channel) in _channels)
            {
                var status = channel.Status;
                // Every state change polls, so an up between two losses is never missed.
                if (status.State == TunnelServiceStatus.Up) channel.AttemptsAtUp = status.Attempts;
                now[id] = status;
                // A sample a second, however often a state change polls in between.
                if (channel.Samples.Count == 0 || at - channel.Samples[^1].At >= TimeSpan.FromMilliseconds(900))
                {
                    channel.Samples.Add((at, status.BytesDown, status.BytesUp));
                    if (channel.Samples.Count > 61) channel.Samples.RemoveAt(0);
                }
                var traffic = Rates(channel);
                rates[id] = (traffic.InPerSecond.LastOrDefault(), traffic.OutPerSecond.LastOrDefault());
                if (status.State == TunnelServiceStatus.Up && !channel.CheckingLatency && channel.Session.Dispatcher is not null
                    && (channel.Latency is not { } last || at - last.Checked >= LatencyEvery))
                    due.Add((id, channel.Stop.Token));
            }
            // Traffic that stops changes no count, but its graph has to fall to nothing: a rate that moved is a change too.
            changed = force || now.Count != _shown.Count || now.Any(p => !_shown.TryGetValue(p.Key, out var was) || was != p.Value)
                || rates.Any(r => !_shownRates.TryGetValue(r.Key, out var was) || was != r.Value);
            _shown = now;
            _shownRates = rates;
        }
        if (changed) Changed?.Invoke();
        // Out of the lock: a check takes it, and polls when it starts and ends.
        foreach (var (id, stop) in due) _ = CheckQuietlyAsync(id, stop);
    }

    // A check of its own accord: one that cannot run (the channel went down meanwhile) is simply not shown.
    private async Task CheckQuietlyAsync(string id, CancellationToken stop)
    {
        try { await CheckLatencyAsync(id, stop); }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or ShenoraException or InvalidOperationException
            or SocketException or System.Net.NetworkInformation.PingException or TunnelEnded) { }
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
