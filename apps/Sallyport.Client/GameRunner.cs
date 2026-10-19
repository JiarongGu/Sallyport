using Sallyport.Profile;
using Sallyport.Runtime;
using Sallyport.Windows.Routing;
using Sallyport.Windows.Service;

namespace Sallyport.Client;

/// <summary>Where a game's run is, for the view: from the client's side (preparing, its tunnel) and the service's (the
/// route's phase and counts). <see cref="Ended"/> is set once it is over, and why.</summary>
internal sealed record RunFacts(
    string GameId, string ProfileId, LossPolicy Loss, DateTimeOffset Started, bool Preparing, SessionStatus? Tunnel, RouteState? Route,
    DateTimeOffset? PhaseSince, string? Ended, DateTimeOffset? EndedAt);

/// <summary>
/// The client's one game run (docs/design/product.md §1, §3): it asks the router service to route a game's programs,
/// holds the tunnel itself — a <see cref="TunnelSession"/> with the profile's connection policy — and pumps each
/// generation's packets to and from the run. A run lives no longer than the client: exiting, or this object going, ends
/// it and the game goes direct.
/// </summary>
internal sealed class GameRunner : IDisposable
{
    /// <summary>Why a run the person stopped ended: said plainly, not as trouble.</summary>
    public const string StoppedByPerson = "Stopped.";

    private readonly object _gate = new();
    private Running? _running;
    private RunFacts? _last;

    private sealed class Running(string gameId, string profileId, LossPolicy loss)
    {
        public string GameId { get; } = gameId;
        public string ProfileId { get; } = profileId;
        public LossPolicy Loss { get; } = loss;
        public DateTimeOffset Started { get; } = DateTimeOffset.Now;
        public CancellationTokenSource Stop { get; } = new();
        public Task Work { get; set; } = Task.CompletedTask;
        public TunnelSession? Session;
        public RouterRun? Run;
        public RunPump? Current;
        public RoutePhase? Phase;
        public DateTimeOffset? PhaseSince;
        public string? Ended;
    }

    /// <summary>Raised whenever the run's state may have changed, on whichever thread changed it.</summary>
    public event Action? Changed;

    public bool IsRunning
    {
        get { lock (_gate) return _running is not null; }
    }

    /// <summary>The profile the run uses, which a proxy may not use meanwhile (product design §4: one reader of a
    /// tunnel's packets).</summary>
    public string? ProfileInUse
    {
        get { lock (_gate) return _running?.ProfileId; }
    }

    /// <summary>The run now, or the last one that ended (until another starts).</summary>
    public RunFacts? Facts()
    {
        lock (_gate)
        {
            if (_running is not { } r) return _last;
            return new RunFacts(r.GameId, r.ProfileId, r.Loss, r.Started, r.Run is null && r.Ended is null, r.Session?.Status, r.Run?.State,
                r.PhaseSince, r.Ended, null);
        }
    }

    /// <summary>Starts routing <paramref name="game"/>'s programs through <paramref name="profile"/>. Returns at once;
    /// what follows — the service's answer, the tunnel — is in <see cref="Facts"/>.</summary>
    public void Start(GameProfile game, string profileId, OvpnProfile profile, ConnectionPolicy policy, LossPolicy loss)
    {
        Running running;
        lock (_gate)
        {
            if (_running is not null) throw new InvalidOperationException("a game is being routed already");
            running = _running = new Running(game.Id, profileId, loss);
            _last = null;
        }
        running.Work = Task.Run(() => RunAsync(running, game.IncludedPaths, profile, policy));
        Changed?.Invoke();
    }

    public async Task StopAsync()
    {
        Running? running;
        lock (_gate) running = _running;
        if (running is null) return;
        End(running, StoppedByPerson);
        await running.Work;
    }

    private async Task RunAsync(Running r, IReadOnlyList<string> executables, OvpnProfile profile, ConnectionPolicy policy)
    {
        var folder = AppContext.BaseDirectory;
        RouterClient? client = null;
        try
        {
            try { client = await RouterClient.ConnectAsync(folder, "client", TimeSpan.FromSeconds(5), r.Stop.Token); }
            catch (TimeoutException) { End(r, "The Sallyport service is not running. Reinstall Sallyport to put it back."); return; }
            catch (RouterRefused e) { End(r, e.Message); return; }
            if (!client.Welcome.DriverReady) { End(r, client.Welcome.DriverProblem ?? "The packet driver is not ready."); return; }
            client.RunEnded += ended => End(r, Sentence(ended.Reason));

            RouterRun run;
            try { run = await client.PrepareAsync(executables, r.Loss, folder, r.Stop.Token); }
            catch (RouterRefused e) { End(r, e.Message); return; }
            await using (run)
            {
                run.StateChanged += state => OnState(r, state);
                lock (_gate) r.Run = run;
                Changed?.Invoke();
                await using var session = new TunnelSession(profile, new TunnelSessionOptions
                {
                    Policy = policy,
                    Consumer = (tunnel, generation) =>
                    {
                        var pump = new RunPump(tunnel, generation, run);
                        Volatile.Write(ref r.Current, pump);
                        return pump;
                    },
                });
                session.StatusChanged += _ => Changed?.Invoke();
                lock (_gate) r.Session = session;
                var forwarding = RunPump.ForwardAsync(run, () => Volatile.Read(ref r.Current));
                // The worker going (its pipe closed) is the run's end too, whatever the supervisor says of it.
                _ = run.Completion.ContinueWith(_ => End(r, "The router stopped."), TaskScheduler.Default);
                await session.RunAsync(r.Stop.Token);
                if (session.Status.State is SessionState.Refused or SessionState.GaveUp)
                    End(r, session.Status.State == SessionState.GaveUp
                        ? $"The connection was given up after its retries. {Sentence(session.Status.LastError)}"
                        : $"The connection was refused: {session.Status.LastError}");
                try { await client.StopAsync(run.RunId); }
                catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
                await run.DisposeAsync();
                await forwarding;
            }
        }
        catch (OperationCanceledException) when (r.Stop.IsCancellationRequested) { }
        catch (Exception e) { End(r, $"Routing stopped ({e.GetType().Name})."); }
        finally
        {
            if (client is not null) await client.DisposeAsync();
            Finish(r);
        }
    }

    private void OnState(Running r, RouteState state)
    {
        lock (_gate)
        {
            if (r.Phase != state.Phase)
            {
                r.Phase = state.Phase;
                r.PhaseSince = DateTimeOffset.Now;
            }
        }
        // Under the Stop policy the route is over once its tunnel is lost: so are the tunnel's retries.
        if (state.Phase == RoutePhase.Stopped) End(r, "The connection was lost, and this game stops routing when it is.");
        Changed?.Invoke();
    }

    // The first reason given is the one kept: what ended the run, not what its ending caused.
    private void End(Running r, string why)
    {
        lock (_gate) r.Ended ??= why;
        try { r.Stop.Cancel(); }
        catch (ObjectDisposedException) { }
        Changed?.Invoke();
    }

    private void Finish(Running r)
    {
        lock (_gate)
        {
            if (_running != r) return;
            _running = null;
            _last = new RunFacts(r.GameId, r.ProfileId, r.Loss, r.Started, false, r.Session?.Status, r.Run?.State, r.PhaseSince,
                r.Ended ?? StoppedByPerson, DateTimeOffset.Now);
        }
        r.Stop.Dispose();
        Changed?.Invoke();
    }

    private static string Sentence(string? text) =>
        string.IsNullOrEmpty(text) ? "" : char.ToUpperInvariant(text[0]) + text[1..] + (text.EndsWith('.') ? "" : ".");

    /// <summary>Exiting ends the run first, so the game goes direct before anything else closes.</summary>
    public void Dispose() => Task.Run(StopAsync).Wait(TimeSpan.FromSeconds(10));
}
