using Sallyport.Profile;
using Sallyport.Runtime;
using Sallyport.Windows.Routing;
using Sallyport.Windows.Service;

namespace Sallyport.Client;

/// <summary>What should be routed now (D23, D24): for each profile the person connected, the apps switched on in its
/// section, each app's programs with its loss policy. An app on in two connected sections is in the first only.</summary>
internal sealed record RoutePlan(IReadOnlyList<PlannedLane> Lanes)
{
    /// <summary>Every lane's programs as one selection, each in its lane, the strictest rule first where two apps name one
    /// program: a program held by one app is never let out by another's.</summary>
    public IReadOnlyList<ProgramRule> Programs(Func<string, int> laneOf) =>
        [.. Lanes.SelectMany(l => l.Apps.SelectMany(a => a.Programs).Select(p => p with { Lane = laneOf(l.ProfileId) }))
            .OrderBy(Strictness).DistinctBy(p => p.Path, StringComparer.OrdinalIgnoreCase)];

    private static int Strictness(ProgramRule rule) => rule switch
    {
        { Loss: LossMode.Hold, DirectAfterSeconds: null } => 0,
        { Loss: LossMode.Hold } => 1,
        { Loss: LossMode.Direct } => 2,
        _ => 3,
    };

    public bool Same(RoutePlan? other) =>
        other is not null && other.Lanes.Count == Lanes.Count && other.Lanes.Zip(Lanes).All(pair => pair.First.Same(pair.Second));
}

internal sealed record PlannedLane(string ProfileId, IReadOnlyList<PlannedApp> Apps)
{
    public bool Same(PlannedLane other) =>
        other.ProfileId == ProfileId && other.Apps.Count == Apps.Count
        && other.Apps.Zip(Apps).All(pair => pair.First.Id == pair.Second.Id && pair.First.Programs.SequenceEqual(pair.Second.Programs));
}

internal sealed record PlannedApp(string Id, IReadOnlyList<ProgramRule> Programs);

/// <summary>One profile's part of the route: its lane, the apps it carries and since when each joined, its tunnel, and
/// its phase as the service reports it.</summary>
internal sealed record LaneFacts(string ProfileId, int Lane, IReadOnlyDictionary<string, DateTimeOffset> Apps, SessionStatus? Tunnel, RoutePhase Phase,
    DateTimeOffset? PhaseSince);

/// <summary>Where the route is, for the view: each profile's lane, from the client's side (preparing, its tunnel) and the
/// service's (the phases and counts). <see cref="Ended"/> is set once it is over, and why.</summary>
internal sealed record RunFacts(
    IReadOnlyList<LaneFacts> Lanes, DateTimeOffset Started, bool Preparing, RouteState? Route, string? Ended, DateTimeOffset? EndedAt)
{
    /// <summary>The lane carrying the app, while the route lasts.</summary>
    public LaneFacts? LaneOf(string appId) => Ended is null ? Lanes.FirstOrDefault(l => l.Apps.ContainsKey(appId)) : null;

    public LaneFacts? LaneFor(string profileId) => Ended is null ? Lanes.FirstOrDefault(l => l.ProfileId == profileId) : null;

    public bool Carries(string appId) => LaneOf(appId) is not null;
}

/// <summary>
/// The client's route (docs/design/product.md §1, §3; D23, D24): it keeps the router service's one run in step with what
/// should be routed (<see cref="Want"/>) — started when an app is on in a connected profile's section, a lane added for
/// each profile that carries apps and let go when it carries none, the programs changed while it runs, stopped when
/// nothing is left. It never connects a profile itself: the person's Connect is the connection, and a lane only holds it
/// while it lasts. Each lane pumps its own tunnel's generations to and from the run. A run lives no longer than the
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
    // A profile whose lane, or whose run, ended on its own is not routed again by itself — a service that refuses, a
    // driver missing, the guard — until the person asks again (Retry): an app turned on, a connect. Why, by profile.
    private readonly Dictionary<string, string> _failed = [];

    private sealed class Running
    {
        public DateTimeOffset Started { get; } = DateTimeOffset.Now;
        public CancellationTokenSource Stop { get; } = new();
        public Task Work { get; set; } = Task.CompletedTask;
        public RouterRun? Run;
        public string? Ended;
        /// <summary>The selection the worker has (or was prepared with).</summary>
        public IReadOnlyList<ProgramRule> Sent = [];
        public readonly Dictionary<string, Lane> Lanes = [];
        /// <summary>The same lanes by index, for the packet path to read without the lock.</summary>
        public readonly Lane?[] ByIndex = new Lane?[Router.Lanes];
        /// <summary>Lanes asked for while the run was being prepared, joined once it is attached.</summary>
        public bool Attached;
    }

    private sealed class Lane(string profileId, int index)
    {
        public string ProfileId { get; } = profileId;
        public int Index { get; } = index;
        public string Holder { get; } = $"route:{Guid.NewGuid():N}";
        public Dictionary<string, DateTimeOffset> Apps = [];
        public TunnelSession? Session;
        public RunPump? Current;
        public RoutePhase Phase = RoutePhase.Armed;
        public DateTimeOffset? PhaseSince;
        public Action? Leave;
    }

    /// <summary>Raised whenever the run's state may have changed, on whichever thread changed it.</summary>
    public event Action? Changed;

    /// <summary>Raised when a lane's tunnel is lost (the lane holds), with the apps it carries: an app whose policy is to
    /// turn its VPN off is turned off by its owner.</summary>
    public event Action<string, IReadOnlyCollection<string>>? Lost;

    /// <summary>Raised when a lane, or the run, ends on its own, with its profile and why.</summary>
    public event Action<string, string>? Failed;

    public bool IsRunning
    {
        get { lock (_gate) return _running is not null; }
    }

    /// <summary>The profiles whose lanes are held now.</summary>
    public IReadOnlyCollection<string> ProfilesInUse
    {
        get { lock (_gate) return _running is { } r ? [.. r.Lanes.Keys] : []; }
    }

    /// <summary>Why a profile's lane ended on its own and is not started again until the person asks; null otherwise.</summary>
    public string? FailureOf(string profileId)
    {
        lock (_gate) return _failed.GetValueOrDefault(profileId);
    }

    /// <summary>The run now, or the last one that ended (until another starts).</summary>
    public RunFacts? Facts()
    {
        lock (_gate)
        {
            if (_running is not { } r) return _last;
            return new RunFacts(Lanes(r), r.Started, r.Run is null && r.Ended is null, r.Run?.State, r.Ended, null);
        }
    }

    // Under _gate.
    private static List<LaneFacts> Lanes(Running r) =>
        [.. r.Lanes.Values.OrderBy(l => l.Index).Select(l => new LaneFacts(l.ProfileId, l.Index, new Dictionary<string, DateTimeOffset>(l.Apps),
            l.Session?.Status, r.Run?.State?.PhaseOf(l.Index) ?? RoutePhase.Armed, l.PhaseSince))];

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

    /// <summary>The person asked again (an app turned on, a profile connected, the driver put in): what ended on its own
    /// may start again.</summary>
    public void Retry()
    {
        lock (_gate) _failed.Clear();
        _ = ConvergeAsync();
    }

    public async Task StopAsync()
    {
        Want(null);
        await ConvergeAsync();
    }

    // The plan less what failed and waits for the person.
    private RoutePlan? Wanted()
    {
        lock (_gate)
            return _wanted is { } plan && plan.Lanes.Where(l => !_failed.ContainsKey(l.ProfileId)).ToList() is { Count: > 0 } lanes ? new RoutePlan(lanes) : null;
    }

    private async Task ConvergeAsync()
    {
        await _converging.WaitAsync();
        try
        {
            var want = Wanted();
            Running? running;
            lock (_gate) running = _running;
            if (running is not null && want is null)
            {
                End(running, StoppedByPerson);
                await running.Work;
                return;
            }
            if (want is null) return;
            if (running is null)
            {
                lock (_gate)
                {
                    _running = running = new Running();
                    _last = null;
                    foreach (var planned in want.Lanes) Add(running, planned);
                    running.Sent = want.Programs(id => running.Lanes[id].Index);
                }
                var programs = running.Sent;
                running.Work = Task.Run(() => RunAsync(running, programs));
                Changed?.Invoke();
                return;
            }
            await SelectAsync(running, want);
        }
        finally { _converging.Release(); }
    }

    // Under _gate: a lane for the profile, on the lowest index free.
    private static Lane Add(Running r, PlannedLane planned)
    {
        var index = Enumerable.Range(0, Router.Lanes).First(i => r.Lanes.Values.All(l => l.Index != i));
        var lane = new Lane(planned.ProfileId, index) { Apps = planned.Apps.ToDictionary(a => a.Id, _ => DateTimeOffset.Now) };
        r.Lanes[planned.ProfileId] = lane;
        Volatile.Write(ref r.ByIndex[index], lane);
        return lane;
    }

    // The worker and the lanes given the plan, once the run is attached: lanes joined or let go, apps joined, the
    // programs sent. Before then, what it was prepared with is the plan's at the time, and this runs again when it attaches.
    private async Task SelectAsync(Running r, RoutePlan want)
    {
        List<Lane> joining = [], leaving = [];
        RouterRun? run;
        IReadOnlyList<ProgramRule> programs;
        lock (_gate)
        {
            if (r.Ended is not null) return;
            foreach (var lane in r.Lanes.Values.Where(l => want.Lanes.All(p => p.ProfileId != l.ProfileId)).ToList())
            {
                Remove(r, lane);
                leaving.Add(lane);
            }
            foreach (var planned in want.Lanes)
            {
                if (r.Lanes.TryGetValue(planned.ProfileId, out var lane))
                    lane.Apps = planned.Apps.ToDictionary(a => a.Id, a => lane.Apps.GetValueOrDefault(a.Id) is var since && since != default ? since : DateTimeOffset.Now);
                else joining.Add(Add(r, planned));
            }
            programs = want.Programs(id => r.Lanes[id].Index);
            run = r.Attached ? r.Run : null;
        }
        if (run is not null)
        {
            // The programs first: a lane let go stops taking traffic before its tunnel's generation is retired.
            if (!programs.SequenceEqual(r.Sent))
                try
                {
                    await run.SelectAsync(programs);
                    r.Sent = programs;
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { End(r, L.T("The router stopped.")); }
            foreach (var lane in leaving) await LeaveAsync(lane);
            foreach (var lane in joining) Join(r, run, lane);
        }
        Changed?.Invoke();
    }

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
                List<Lane> lanes;
                lock (_gate)
                {
                    r.Run = run;
                    r.Attached = true;
                    lanes = [.. r.Lanes.Values];
                }
                foreach (var lane in lanes) Join(r, run, lane);
                Changed?.Invoke();
                // What changed while it was being prepared.
                _ = ConvergeAsync();
                var forwarding = RunPump.ForwardAsync(run, PumpOf(r));
                // The worker going (its pipe closed) is the run's end too, whatever the supervisor says of it.
                _ = run.Completion.ContinueWith(_ => End(r, L.T("The router stopped.")), TaskScheduler.Default);
                try { await Task.Delay(Timeout.Infinite, r.Stop.Token); }
                catch (OperationCanceledException) { }
                finally
                {
                    lock (_gate) lanes = [.. r.Lanes.Values];
                    // The router side goes first, so each channel's proxy keeps its tunnel and the run retires its generations.
                    foreach (var lane in lanes) await LeaveAsync(lane);
                    try { await client.StopAsync(run.RunId); }
                    catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
                    await run.DisposeAsync();
                    await forwarding;
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

    // The pump of a lane's tunnel now, by its index: what the router routed for that lane goes there and nowhere else.
    private static Func<int, RunPump?> PumpOf(Running r) => index =>
        index < Router.Lanes && Volatile.Read(ref r.ByIndex[index]) is { } lane ? Volatile.Read(ref lane.Current) : null;

    // Under _gate.
    private static bool Remove(Running r, Lane lane)
    {
        if (!r.Lanes.Remove(lane.ProfileId)) return false;
        Volatile.Write(ref r.ByIndex[lane.Index], null);
        return true;
    }

    // A lane joins its profile's connection — the person's, never one of its own: a profile disconnected meanwhile is not
    // connected again, and its lane ends instead.
    private void Join(Running r, RouterRun run, Lane lane)
    {
        TunnelSession session;
        try { session = connections.Acquire(lane.ProfileId, lane.Holder, open: false); }
        catch (Shenora.Core.Ipc.ShenoraException e)
        {
            Drop(r, lane, Sentence(e.Message));
            return;
        }
        // Each generation's router side, attached to its dispatcher and let go with it — the one up already, when the
        // channel was, and each one after.
        void Attach(Sallyport.Net.TunnelDispatcher dispatcher)
        {
            lock (_gate)
            {
                if (r.Ended is not null || !r.Lanes.ContainsKey(lane.ProfileId) || Volatile.Read(ref lane.Current)?.Generation >= GameRoute.Wire(lane.Index, dispatcher.Generation)) return;
                try { Volatile.Write(ref lane.Current, new RunPump(dispatcher, run, lane.Index)); }
                catch (InvalidOperationException) { /* this generation went, or has its route already */ }
            }
        }
        void Gone(string id, string why) { if (id == lane.ProfileId) Drop(r, lane, why); }
        void Moved(SessionStatus _) => Changed?.Invoke();
        session.DispatcherUp += Attach;
        session.StatusChanged += Moved;
        connections.ChannelEnded += Gone;
        lock (_gate) lane.Session = session;
        lane.Leave = () =>
        {
            session.DispatcherUp -= Attach;
            session.StatusChanged -= Moved;
            connections.ChannelEnded -= Gone;
        };
        if (session.Dispatcher is { } up) Attach(up);
    }

    // A lane whose connection ended on its own (the person disconnected, the profile gave up): it goes, and the others stay.
    private void Drop(Running r, Lane lane, string why)
    {
        lock (_gate)
        {
            if (!Remove(r, lane)) return;
            _failed[lane.ProfileId] = why;
        }
        Failed?.Invoke(lane.ProfileId, why);
        _ = Task.Run(async () =>
        {
            await LeaveAsync(lane);
            await ConvergeAsync();
        });
        Changed?.Invoke();
    }

    // Its generation retired, its hold on the channel let go.
    private async Task LeaveAsync(Lane lane)
    {
        Action? leave;
        RunPump? pump;
        lock (_gate)
        {
            (leave, lane.Leave) = (lane.Leave, null);
            pump = Volatile.Read(ref lane.Current);
            Volatile.Write(ref lane.Current, null);
        }
        leave?.Invoke();
        if (pump is not null) await pump.DisposeAsync();
        if (lane.Session is not null) await connections.ReleaseAsync(lane.ProfileId, lane.Holder);
    }

    private void OnState(Running r, RouteState state)
    {
        List<(string Profile, string[] Apps)> lost = [];
        lock (_gate)
        {
            foreach (var lane in r.Lanes.Values)
            {
                var phase = state.PhaseOf(lane.Index);
                if (phase == lane.Phase) continue;
                if (phase == RoutePhase.Held && lane.Phase == RoutePhase.Routing) lost.Add((lane.ProfileId, [.. lane.Apps.Keys]));
                lane.Phase = phase;
                lane.PhaseSince = DateTimeOffset.Now;
            }
        }
        foreach (var (profile, apps) in lost) Lost?.Invoke(profile, apps);
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
        List<string> profiles;
        lock (_gate)
        {
            if (_running != r) return;
            _running = null;
            why = r.Ended ?? StoppedByPerson;
            profiles = [.. r.Lanes.Keys];
            if (why != StoppedByPerson) foreach (var profile in profiles) _failed[profile] = why;
            _last = new RunFacts(Lanes(r), r.Started, false, r.Run?.State, why, DateTimeOffset.Now);
        }
        r.Stop.Dispose();
        if (why != StoppedByPerson) foreach (var profile in profiles) Failed?.Invoke(profile, why);
        Changed?.Invoke();
        // Whatever is wanted now follows this run's end.
        _ = ConvergeAsync();
    }

    private static string Sentence(string? text) =>
        string.IsNullOrEmpty(text) ? "" : char.ToUpperInvariant(text[0]) + text[1..] + (text.EndsWith('.') ? "" : ".");

    /// <summary>Exiting ends the run first, so every app goes direct before anything else closes.</summary>
    public void Dispose() => Task.Run(StopAsync).Wait(TimeSpan.FromSeconds(10));
}
