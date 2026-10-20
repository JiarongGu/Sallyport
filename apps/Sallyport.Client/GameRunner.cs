using Sallyport.Profile;
using Sallyport.Runtime;
using Sallyport.Windows.Routing;
using Sallyport.Windows.Service;

namespace Sallyport.Client;

/// <summary>What should be routed now (D23): the apps turned on whose profile the person connected — one profile's, as
/// the service runs one route — and each app's programs with its loss policy.</summary>
internal sealed record RoutePlan(string ProfileId, IReadOnlyList<PlannedApp> Apps)
{
    /// <summary>Every app's programs as one selection, the strictest rule first where two apps name one program: a
    /// program held by one app is never let out by another's.</summary>
    public IReadOnlyList<ProgramRule> Programs =>
        [.. Apps.SelectMany(a => a.Programs).OrderBy(Strictness).DistinctBy(p => p.Path, StringComparer.OrdinalIgnoreCase)];

    private static int Strictness(ProgramRule rule) => rule switch
    {
        { Loss: LossMode.Hold, DirectAfterSeconds: null } => 0,
        { Loss: LossMode.Hold } => 1,
        { Loss: LossMode.Direct } => 2,
        _ => 3,
    };

    public bool Same(RoutePlan? other) =>
        other is not null && other.ProfileId == ProfileId && other.Apps.Select(a => a.Id).SequenceEqual(Apps.Select(a => a.Id))
        && other.Programs.SequenceEqual(Programs);
}

internal sealed record PlannedApp(string Id, IReadOnlyList<ProgramRule> Programs);

/// <summary>Where the route is, for the view: its profile, the apps it carries and since when each joined, from the
/// client's side (preparing, its tunnel) and the service's (the route's phase and counts). <see cref="Ended"/> is set
/// once it is over, and why.</summary>
internal sealed record RunFacts(
    string ProfileId, IReadOnlyDictionary<string, DateTimeOffset> Apps, DateTimeOffset Started, bool Preparing, SessionStatus? Tunnel,
    RouteState? Route, DateTimeOffset? PhaseSince, string? Ended, DateTimeOffset? EndedAt)
{
    public bool Carries(string appId) => Ended is null && Apps.ContainsKey(appId);
}

/// <summary>
/// The client's route (docs/design/product.md §1, §3; D23): it keeps the router service's one run in step with what
/// should be routed (<see cref="Want"/>) — started when an app turned on has its profile connected, its programs
/// changed while it runs when an app is turned on or off, stopped when none is left or the person disconnects. It never
/// connects a profile itself: the person's Connect is the connection, and the run only holds it while it lasts. It pumps
/// each generation's packets to and from the run through that channel's dispatcher. A run lives no longer than the
/// client: exiting, or this object going, ends it and every app goes direct.
/// </summary>
internal sealed class GameRunner(Connections connections) : IDisposable
{
    /// <summary>Why a run ended that was stopped because nothing is to be routed: said plainly, not as trouble.</summary>
    public const string StoppedByPerson = "Stopped.";

    private readonly object _gate = new();
    // One change at a time, in order: a selection sent after a newer one would route what the person just turned off.
    private readonly SemaphoreSlim _converging = new(1, 1);
    private RoutePlan? _wanted;
    private Running? _running;
    private RunFacts? _last;
    // A run that ended on its own is not started again for that profile by itself — a service that refuses, a guard that
    // tripped — until the person asks again (<see cref="Retry"/>): an app turned on, a connect.
    private string? _failedFor;

    private sealed class Running(string profileId)
    {
        /// <summary>This run's hold on its profile's channel.</summary>
        public string Holder { get; } = $"route:{Guid.NewGuid():N}";
        public string ProfileId { get; } = profileId;
        public DateTimeOffset Started { get; } = DateTimeOffset.Now;
        public CancellationTokenSource Stop { get; } = new();
        public Task Work { get; set; } = Task.CompletedTask;
        public TunnelSession? Session;
        public RouterRun? Run;
        public RunPump? Current;
        public RoutePhase? Phase;
        public DateTimeOffset? PhaseSince;
        public string? Ended;
        /// <summary>The selection the worker has (or was prepared with), and the apps it carries, each since it joined.</summary>
        public IReadOnlyList<ProgramRule> Sent = [];
        public Dictionary<string, DateTimeOffset> Apps = [];
    }

    /// <summary>Raised whenever the run's state may have changed, on whichever thread changed it.</summary>
    public event Action? Changed;

    /// <summary>Raised when the run's tunnel is lost (the route holds), with the apps it carries: an app whose policy is to
    /// turn its VPN off is turned off by its owner.</summary>
    public event Action<IReadOnlyCollection<string>>? Lost;

    /// <summary>Raised when a run ends on its own (not stopped for want of anything to route), with its profile and why.</summary>
    public event Action<string, string>? Failed;

    public bool IsRunning
    {
        get { lock (_gate) return _running is not null; }
    }

    /// <summary>The profile the run uses: its channel is held while the run lasts.</summary>
    public string? ProfileInUse
    {
        get { lock (_gate) return _running?.ProfileId; }
    }

    /// <summary>The profile whose run ended on its own and is not started again until the person asks, and why.</summary>
    public (string ProfileId, string Why)? Failure
    {
        get { lock (_gate) return _failedFor is { } id && _last is { Ended: { } why } last && last.ProfileId == id ? (id, why) : null; }
    }

    /// <summary>The run now, or the last one that ended (until another starts).</summary>
    public RunFacts? Facts()
    {
        lock (_gate)
        {
            if (_running is not { } r) return _last;
            return new RunFacts(r.ProfileId, new Dictionary<string, DateTimeOffset>(r.Apps), r.Started, r.Run is null && r.Ended is null, r.Session?.Status,
                r.Run?.State, r.PhaseSince, r.Ended, null);
        }
    }

    /// <summary>What should be routed from now on, or nothing. Returns at once; the run follows.</summary>
    public void Want(RoutePlan? plan)
    {
        lock (_gate)
        {
            if (plan is null ? _wanted is null : plan.Same(_wanted)) return;
            _wanted = plan;
        }
        _ = ConvergeAsync();
    }

    /// <summary>The person asked again (an app turned on, a profile connected, the driver put in): a run that ended on its
    /// own may start again.</summary>
    public void Retry()
    {
        lock (_gate) _failedFor = null;
        _ = ConvergeAsync();
    }

    public async Task StopAsync()
    {
        Want(null);
        await ConvergeAsync();
    }

    private async Task ConvergeAsync()
    {
        await _converging.WaitAsync();
        try
        {
            while (true)
            {
                RoutePlan? want;
                Running? running;
                lock (_gate) (want, running) = (_wanted, _running);
                if (running is not null && (want is null || want.ProfileId != running.ProfileId))
                {
                    End(running, StoppedByPerson);
                    await running.Work;
                    continue;
                }
                if (want is null) return;
                if (running is null)
                {
                    lock (_gate)
                    {
                        if (_failedFor == want.ProfileId) return;
                        _running = running = new Running(want.ProfileId) { Sent = want.Programs, Apps = Joined(want, null) };
                        _last = null;
                    }
                    running.Work = Task.Run(() => RunAsync(running, want.Programs));
                    Changed?.Invoke();
                    return;
                }
                await SelectAsync(running, want);
                return;
            }
        }
        finally { _converging.Release(); }
    }

    // The worker given the plan's programs, once it is attached (until then, what it was prepared with is the plan's at
    // the time, and this runs again when it attaches).
    private async Task SelectAsync(Running r, RoutePlan want)
    {
        RouterRun? run;
        lock (_gate)
        {
            r.Apps = Joined(want, r.Apps);
            run = r.Ended is null ? r.Run : null;
        }
        if (run is not null && !want.Programs.SequenceEqual(r.Sent))
        {
            try
            {
                await run.SelectAsync(want.Programs);
                r.Sent = want.Programs;
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { End(r, L.T("The router stopped.")); }
        }
        Changed?.Invoke();
    }

    // Each app of the plan since it joined: those already carried keep their time.
    private static Dictionary<string, DateTimeOffset> Joined(RoutePlan plan, Dictionary<string, DateTimeOffset>? before) =>
        plan.Apps.ToDictionary(a => a.Id, a => before?.GetValueOrDefault(a.Id) is { } since && since != default ? since : DateTimeOffset.Now);

    private async Task RunAsync(Running r, IReadOnlyList<ProgramRule> programs)
    {
        var folder = AppContext.BaseDirectory;
        RouterClient? client = null;
        try
        {
            try { client = await RouterClient.ConnectAsync(folder, "client", TimeSpan.FromSeconds(5), r.Stop.Token); }
            catch (TimeoutException) { End(r, L.T("The Sallyport service is not running. Reinstall Sallyport to put it back.")); return; }
            catch (RouterRefused e) { End(r, e.Message); return; }
            if (!client.Welcome.DriverReady) { End(r, client.Welcome.DriverProblem ?? L.T("The packet driver is not ready.")); return; }
            client.RunEnded += ended => End(r, Sentence(ended.Reason));

            RouterRun run;
            try { run = await client.PrepareAsync(programs, folder, r.Stop.Token); }
            catch (RouterRefused e) { End(r, e.Message); return; }
            await using (run)
            {
                run.StateChanged += state => OnState(r, state);
                lock (_gate) r.Run = run;
                Changed?.Invoke();
                // What changed while it was being prepared.
                _ = ConvergeAsync();
                TunnelSession session;
                // The person's connection, never one of its own: a profile disconnected meanwhile is not connected again.
                try { session = connections.Acquire(r.ProfileId, r.Holder, open: false); }
                catch (Shenora.Core.Ipc.ShenoraException e) { End(r, Sentence(e.Message)); return; }
                // Each generation's router side, attached to its dispatcher and let go with it — the one up already, when
                // the channel was, and each one after.
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
                _ = run.Completion.ContinueWith(_ => End(r, L.T("The router stopped.")), TaskScheduler.Default);
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
        catch (Exception e) { End(r, L.T("VPN turned off ({0}).", e.GetType().Name)); }
        finally
        {
            if (client is not null) await client.DisposeAsync();
            Finish(r);
        }
    }

    private void OnState(Running r, RouteState state)
    {
        bool lost;
        string[] carried;
        lock (_gate)
        {
            lost = state.Phase == RoutePhase.Held && r.Phase == RoutePhase.Routing;
            if (r.Phase != state.Phase)
            {
                r.Phase = state.Phase;
                r.PhaseSince = DateTimeOffset.Now;
            }
            carried = [.. r.Apps.Keys];
        }
        if (lost) Lost?.Invoke(carried);
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
        string why;
        lock (_gate)
        {
            if (_running != r) return;
            _running = null;
            why = r.Ended ?? StoppedByPerson;
            if (why != StoppedByPerson) _failedFor = r.ProfileId;
            _last = new RunFacts(r.ProfileId, new Dictionary<string, DateTimeOffset>(r.Apps), r.Started, false, r.Session?.Status, r.Run?.State, r.PhaseSince,
                why, DateTimeOffset.Now);
        }
        r.Stop.Dispose();
        if (why != StoppedByPerson) Failed?.Invoke(r.ProfileId, why);
        Changed?.Invoke();
        // Whatever is wanted now — another profile's apps, say — follows this one's end.
        _ = ConvergeAsync();
    }

    private static string Sentence(string? text) =>
        string.IsNullOrEmpty(text) ? "" : char.ToUpperInvariant(text[0]) + text[1..] + (text.EndsWith('.') ? "" : ".");

    /// <summary>Exiting ends the run first, so every app goes direct before anything else closes.</summary>
    public void Dispose() => Task.Run(StopAsync).Wait(TimeSpan.FromSeconds(10));
}
