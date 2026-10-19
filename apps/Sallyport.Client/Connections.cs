using System.Net;
using System.Text.Json;
using Sallyport.Runtime;
using Shenora.Core.Ipc;

namespace Sallyport.Client;

/// <summary>Where a tunnel comes out, as a public lookup service sees it through the tunnel's own SOCKS port.</summary>
internal sealed record Location(string? Ip, string? City, string? Region, string? Country, string? Org, DateTimeOffset Checked);

/// <summary>
/// The connected profiles: a <see cref="TunnelService"/> each, on that profile's loopback port, in this process (D14).
/// A run that ends on its own (refused, or failed for good) keeps its last status until connected again, so the
/// window can say why.
/// </summary>
internal sealed class Connections : IDisposable
{
    /// <summary>Long enough for the exit notices to reach every server; an exit never hangs on a dead one.</summary>
    private static readonly TimeSpan LeaveWithin = TimeSpan.FromSeconds(5);

    private readonly ProfileStore _store;
    private readonly ClientSettings _settings;
    private readonly object _gate = new();
    private readonly Dictionary<string, Running> _running = [];
    private readonly Dictionary<string, TunnelServiceStatus> _ended = [];
    private readonly System.Threading.Timer _poll;
    private Dictionary<string, TunnelServiceStatus> _shown = [];

    /// <summary>One connection, from Connect to its end. What is learned about it lives here, so a new connection
    /// starts with none of it and a late answer for an old one has nowhere to land.</summary>
    private sealed class Running(TunnelService service, CancellationTokenSource stop, int port)
    {
        public TunnelService Service { get; } = service;
        public CancellationTokenSource Stop { get; } = stop;
        public int Port { get; } = port;
        public Task Run { get; set; } = Task.CompletedTask;
        public Location? Location { get; set; }
        /// <summary>The session's attempt count when it was last up: its count runs across the whole run.</summary>
        public int? AttemptsAtUp { get; set; }
    }

    /// <summary>Raised on a thread-pool thread whenever any profile's status differs from the last one raised.</summary>
    public event Action? Changed;

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
            if (_running.TryGetValue(id, out var running)) return running.Service.Status;
            return _ended.GetValueOrDefault(id);
        }
    }

    public Location? LocationOf(string id)
    {
        lock (_gate) return _running.GetValueOrDefault(id)?.Location;
    }

    /// <summary>Attempts made since the tunnel was last up, or null when it has not been up in this run.</summary>
    public int? AttemptsSinceUp(string id)
    {
        lock (_gate)
        {
            if (!_running.TryGetValue(id, out var running) || running.AttemptsAtUp is not { } atUp) return null;
            return running.Service.Status.Attempts - atUp;
        }
    }

    public bool IsRunning(string id)
    {
        lock (_gate) return _running.ContainsKey(id);
    }

    public void Connect(string id)
    {
        lock (_gate)
        {
            if (_running.ContainsKey(id)) return;
            var port = _settings.PortOf(id);
            if (_running.Values.Any(r => r.Port == port))
                throw new ShenoraException("PORT_TAKEN", "port", port.ToString(), $"another connected profile uses port {port}");
            // TunnelService binds only once the tunnel is up; a port already taken is said now, not after the handshake.
            if (!ClientSettings.IsFree(port))
                throw new ShenoraException("PORT_IN_USE", "port", port.ToString(), $"something else already listens on 127.0.0.1:{port}");

            Profile.OvpnProfile profile;
            try { profile = _store.Load(id); }
            catch (FileNotFoundException) { throw new ShenoraException("PROFILE_GONE", "id", id, "the profile is no longer stored"); }
            // The message of what failed to open is not passed on: it could quote the profile.
            catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or FormatException)
            { throw new ShenoraException("PROFILE_UNREADABLE", "id", id, "the stored profile could not be opened"); }

            var running = new Running(new TunnelService(profile, new IPEndPoint(IPAddress.Loopback, port), _ => Poll()), new CancellationTokenSource(), port);
            _ended.Remove(id);
            _running[id] = running;
            running.Run = Task.Run(() => running.Service.RunAsync(running.Stop.Token));
            _ = running.Run.ContinueWith(run => OnEndedAsync(id, running, run), TaskScheduler.Default).Unwrap();
        }
        Poll();
    }

    public async Task DisconnectAsync(string id)
    {
        Running? running;
        lock (_gate)
        {
            _ended.Remove(id);
            if (!_running.Remove(id, out running)) { running = null; }
        }
        if (running is not null) await LeaveAsync(running);
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
        Running? running;
        lock (_gate)
        {
            if (!_running.TryGetValue(id, out running) || running.Service.Status.State != TunnelServiceStatus.Up)
                throw new ShenoraException("NOT_CONNECTED", "id", id, "the profile is not up");
        }
        var port = running.Port;

        using var handler = new SocketsHttpHandler
        {
            // .NET hands a SOCKS5 proxy the host name, so the lookup's name is resolved at the far end too.
            Proxy = new WebProxy($"socks5://127.0.0.1:{port}"),
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
        lock (_gate) running.Location = location;
        Poll(force: true);
        return location;
    }

    public void Dispose()
    {
        _poll.Dispose();
        List<Running> all;
        lock (_gate)
        {
            all = [.. _running.Values];
            _running.Clear();
        }
        // Off the UI thread: the exit notices are async, and the message loop that would resume them is gone by now.
        Task.Run(() => Task.WhenAll(all.Select(LeaveAsync))).Wait(LeaveWithin + TimeSpan.FromSeconds(1));
    }

    private async Task OnEndedAsync(string id, Running running, Task run)
    {
        if (running.Stop.IsCancellationRequested) return;
        // Ended on its own: keep what it last said, and let go of it.
        var last = running.Service.Status;
        if (run.IsFaulted) last = last with { State = TunnelServiceStatus.Stopped, LastError = run.Exception!.InnerException?.GetType().Name ?? "failed" };
        lock (_gate)
        {
            if (!_running.TryGetValue(id, out var current) || current != running) return;
            _running.Remove(id);
            _ended[id] = last;
        }
        await running.Service.DisposeAsync();
        running.Stop.Dispose();
        Poll();
    }

    private static async Task LeaveAsync(Running running)
    {
        running.Stop.Cancel();
        try { await running.Run.WaitAsync(LeaveWithin); }
        catch (Exception) { /* Leaving is what was asked; how the run ended no longer matters. */ }
        await running.Service.DisposeAsync();
        running.Stop.Dispose();
    }

    private void Poll(bool force = false)
    {
        bool changed;
        lock (_gate)
        {
            var now = new Dictionary<string, TunnelServiceStatus>(_ended);
            foreach (var (id, running) in _running)
            {
                var status = running.Service.Status;
                // Every state change polls, so an up between two losses is never missed.
                if (status.State == TunnelServiceStatus.Up) running.AttemptsAtUp = status.Attempts;
                now[id] = status;
            }
            changed = force || now.Count != _shown.Count || now.Any(p => !_shown.TryGetValue(p.Key, out var was) || was != p.Value);
            _shown = now;
        }
        if (changed) Changed?.Invoke();
    }
}
