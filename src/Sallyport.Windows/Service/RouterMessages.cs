using System.Text.Json.Serialization;
using Sallyport.Windows.Routing;

namespace Sallyport.Windows.Service;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Hello), "hello")]
[JsonDerivedType(typeof(Welcome), "welcome")]
[JsonDerivedType(typeof(Rejected), "rejected")]
[JsonDerivedType(typeof(PrepareRun), "prepareRun")]
[JsonDerivedType(typeof(RunPrepared), "runPrepared")]
[JsonDerivedType(typeof(Heartbeat), "heartbeat")]
[JsonDerivedType(typeof(StopRun), "stopRun")]
[JsonDerivedType(typeof(RunEnded), "runEnded")]
[JsonDerivedType(typeof(Attach), "attach")]
[JsonDerivedType(typeof(Attached), "attached")]
[JsonDerivedType(typeof(CommitGeneration), "commitGeneration")]
[JsonDerivedType(typeof(RetireGeneration), "retireGeneration")]
[JsonDerivedType(typeof(RouteState), "routeState")]
[JsonDerivedType(typeof(SelectPrograms), "selectPrograms")]
public abstract record RouterMessage;

/// <summary>Client → supervisor, first.</summary>
public sealed record Hello(int Version, string Build) : RouterMessage;

/// <summary>Supervisor → client: what the service is and what it can do now.</summary>
public sealed record Welcome(int Version, string Build, bool DriverReady, string? DriverProblem, string? RunId) : RouterMessage;

/// <summary>Either side: the request (or the client) is refused, and why — a sentence to show, never a secret.</summary>
public sealed record Rejected(string Reason) : RouterMessage;

/// <summary>Client → supervisor: route these programs (their full executable paths), each with its loss policy.</summary>
public sealed record PrepareRun(ProgramRule[] Programs) : RouterMessage;

/// <summary>Supervisor → client: the run's worker is ready behind its own pipe; <paramref name="Worker"/> its process,
/// which only the client's own check of that pipe is matched against.</summary>
public sealed record RunPrepared(string RunId, string Pipe, int Worker) : RouterMessage;

/// <summary>Client → supervisor, every second while a run lives: without it for <see cref="RouterProtocol.Lease"/>, the
/// run ends and the game goes direct.</summary>
public sealed record Heartbeat : RouterMessage;

public sealed record StopRun(string RunId) : RouterMessage;

/// <summary>Supervisor → client: the run is over, and why (stopped, the client's lease ran out, the guard tripped, the
/// worker died).</summary>
public sealed record RunEnded(string RunId, string Reason) : RouterMessage;

/// <summary>Client → worker, first on the run's pipe.</summary>
public sealed record Attach(string RunId) : RouterMessage;

/// <summary>Worker → client: attached; the adapter it routes on and its MTU.</summary>
public sealed record Attached(string Adapter, int AdapterMtu) : RouterMessage;

/// <summary>Client → worker: a tunnel is up — its generation (its lane in the top byte, <see cref="GameRoute.Wire"/>), its
/// IPv4 address and the largest packet it carries whole.</summary>
public sealed record CommitGeneration(int Generation, string Address, int Mtu) : RouterMessage;

/// <summary>Client → worker: a generation's tunnel has gone.</summary>
public sealed record RetireGeneration(int Generation) : RouterMessage;

/// <summary>Client → worker: route these programs from now on instead — an app turned on or off while its VPN is
/// connected. Never empty: a run with nothing to route is stopped instead.</summary>
public sealed record SelectPrograms(ProgramRule[] Programs) : RouterMessage;

/// <summary>Worker → client, every second: where the route stands — its first lane's phase, and each lane's — and its
/// counts so far.</summary>
public sealed record RouteState(RoutePhase Phase, int Generation, long Routed, long HandedUp, long Direct, long Unowned, long HeldWhileDown,
    long DirectWhileDown, long LateFlows, long Blocked, long Stale, long RoutedFlows, int Selected, int Unreadable,
    IReadOnlyList<ProgramRouted>? Programs = null, IReadOnlyList<LaneRoute>? Lanes = null, int UnreadableRunning = 0) : RouterMessage
{
    /// <summary>A lane's phase; a lane the worker has not said is waiting for its first tunnel.</summary>
    public RoutePhase PhaseOf(int lane) => Lanes?.FirstOrDefault(l => l.Lane == lane)?.Phase ?? (lane == 0 ? Phase : RoutePhase.Armed);
}

/// <summary>One lane of a route: its phase, and its generation now (0 while it has no tunnel).</summary>
public sealed record LaneRoute(int Lane, RoutePhase Phase, int Generation);

/// <summary>What one selected executable has had routed since the route began: the processes seen running it, the flows
/// that went to the tunnel, the packets it sent into them, the bytes each way, and its round trip through the tunnel
/// (<see cref="Routing.ProcessRouted.Latency"/>, its busiest process's). A path and counts, never what was sent.</summary>
public sealed record ProgramRouted(string Path, int Processes, long Flows, long Packets, long BytesOut = 0, long BytesIn = 0, double? LatencyMs = null);
