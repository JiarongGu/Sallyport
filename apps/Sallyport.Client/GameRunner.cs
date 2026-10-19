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
/// holds its profile's channel (<see cref="Connections"/>) — the same login the profile's proxy uses, brought up if it is
/// not — and pumps each generation's packets to and from the run through that channel's dispatcher. Stopping the run
/// lets the channel go, which stays up if the person connected it. A run lives no longer than the client: exiting, or
/// this object going, ends it and the game goes direct.
/// </summary>
internal sealed class GameRunner(Connections connections) : IDisposable
{
    /// <summary>Why a run the person stopped ended: said plainly, not as trouble.</summary>
    public const string StoppedByPerson = "Stopped.";

    private readonly object _gate = new();
    private Running? _running;
    private RunFacts? _last;

    private sealed class Running(string gameId, string profileId, LossPolicy loss)
    {
        /// <summary>This run's hold on its profile's channel.</summary>
        public string Holder { get; } = $"route:{Guid.NewGuid():N}";
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

    /// <summary>The profile the run uses: its channel is held while the run lasts.</summary>
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
    public void Start(GameProfile game, string profileId, LossPolicy loss)
    {
        Running running;
        lock (_gate)
        {
            if (_running is not null) throw new InvalidOperationException("a game is being routed already");
            running = _running = new Running(game.Id, profileId, loss);
            _last = null;
        }
        running.Work = Task.Run(() => RunAsync(running, game.IncludedPaths));
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

    private async Task RunAsync(Running r, IReadOnlyList<string> executables)
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
                TunnelSession session;
                try { session = connections.Acquire(r.ProfileId, r.Holder); }
                catch (Shenora.Core.Ipc.ShenoraException e) { End(r, Sentence(e.Message)); return; }
                // Each generation's router side, attached to its dispatcher and let go with it — the one up already, when
                // the channel was (a proxy's), and each one after.
                void Attach(Sallyport.Net.TunnelDispatcher dispatcher)
                {
                    lock (_gate)
                    {
                        if (r.Ended is not null || Volatile.Read(ref r.Current)?.Generation >= dispatcher.Generation) return;
                        try { Volatile.Write(ref r.Current, new RunPump(dispatcher, run)); }
                        catch (InvalidOperationException) { /* this generation went, or has its route already */ }
                    }
                }
                void Gone(string id, string why) { if (id == r.ProfileId) End(r, why); }
                void Moved(SessionStatus _) => Changed?.Invoke();
                session.DispatcherUp += Attach;
                session.StatusChanged += Moved;
                connections.ChannelEnded += Gone;
                lock (_gate) r.Session = session;
                if (session.Dispatcher is { } up) Attach(up);
                var forwarding = RunPump.ForwardAsync(run, () => Volatile.Read(ref r.Current));
                // The worker going (its pipe closed) is the run's end too, whatever the supervisor says of it.
                _ = run.Completion.ContinueWith(_ => End(r, "The router stopped."), TaskScheduler.Default);
                try { await Task.Delay(Timeout.Infinite, r.Stop.Token); }
                catch (OperationCanceledException) { }
                finally
                {
                    session.DispatcherUp -= Attach;
                    session.StatusChanged -= Moved;
                    connections.ChannelEnded -= Gone;
                    // The router side goes first, so the channel's proxy keeps its tunnel and the run retires its generation.
                    if (Volatile.Read(ref r.Current) is { } pump) await pump.DisposeAsync();
                    try { await client.StopAsync(run.RunId); }
                    catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
                    await run.DisposeAsync();
                    await forwarding;
                    await connections.ReleaseAsync(r.ProfileId, r.Holder);
                }
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
        // Under the Stop policy the route is over once its tunnel is lost, and its hold on the channel with it: the channel
        // goes on retrying only if the person connected it for its proxy.
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
