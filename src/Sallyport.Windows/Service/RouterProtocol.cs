using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Sallyport.Windows.Routing;

namespace Sallyport.Windows.Service;

/// <summary>
/// What the client and the router service say to each other (docs/design/product.md §2, with its change 1). Two pipes:
/// the supervisor's, which every client opens, for who may run and the run's lease; and each run's own, opened only by the
/// client the run was prepared for, for the tunnel's generations and the packets themselves. A frame is a 4-byte
/// little-endian length (of what follows), a 1-byte kind, and its payload: a control message as UTF-8 JSON, or a packet —
/// its 4-byte generation, then the IP packet. No profile, key or credential crosses either pipe.
/// </summary>
public static class RouterProtocol
{
    // 2: a run's programs each with their own loss policy, and changed while it runs (D23). 3: lanes — each program
    // into its profile's tunnel, a generation saying its lane in its top byte (D24).
    public const int Version = 3;
    public const string ServiceName = "Sallyport";
    public const string ControlPipe = "Sallyport.Router.v1";
    public static string RunPipe(string runId) => $"Sallyport.Router.v1.run.{runId}";

    public const byte ControlFrame = 1, PacketFrame = 2;
    /// <summary>The largest frame either side accepts: an IP packet's 65,535 bytes, its generation and kind, rounded.</summary>
    public const int MostFrame = 70_000;
    /// <summary>How long the supervisor waits for a client's heartbeat before it ends the run (sent every second).</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromSeconds(3);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>What of <paramref name="programs"/> may be routed: a full path, and none of Sallyport's own — the client's
    /// tunnel must stay direct, and the service with it.</summary>
    public static ProgramRule[] Routable(IEnumerable<ProgramRule> programs, string serviceFolder) =>
        [.. programs.Where(p => Path.IsPathFullyQualified(p.Path) && !string.Equals(Path.GetDirectoryName(p.Path),
            Path.TrimEndingDirectorySeparator(serviceFolder), StringComparison.OrdinalIgnoreCase))];
}

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

/// <summary>Supervisor → client: the run's worker is ready behind its own pipe.</summary>
public sealed record RunPrepared(string RunId, string Pipe) : RouterMessage;

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
    IReadOnlyList<ProgramRouted>? Programs = null, IReadOnlyList<LaneRoute>? Lanes = null) : RouterMessage
{
    /// <summary>A lane's phase; a lane the worker has not said is waiting for its first tunnel.</summary>
    public RoutePhase PhaseOf(int lane) => Lanes?.FirstOrDefault(l => l.Lane == lane)?.Phase ?? (lane == 0 ? Phase : RoutePhase.Armed);
}

/// <summary>One lane of a route: its phase, and its generation now (0 while it has no tunnel).</summary>
public sealed record LaneRoute(int Lane, RoutePhase Phase, int Generation);

/// <summary>What one selected executable has had routed since the route began: the processes seen running it, the flows
/// that went to the tunnel and the packets it sent into them. A path, never what was sent.</summary>
public sealed record ProgramRouted(string Path, int Processes, long Flows, long Packets);

/// <summary>One frame read: its kind, and its payload (a control message's JSON, or a packet's generation and bytes).</summary>
public readonly record struct Frame(byte Kind, byte[] Payload)
{
    public RouterMessage? Message => Kind == RouterProtocol.ControlFrame ? JsonSerializer.Deserialize<RouterMessage>(Payload, RouterProtocol.Json) : null;

    public int Generation => BinaryPrimitives.ReadInt32LittleEndian(Payload);

    public byte[] Packet => Payload[4..];
}

/// <summary>Frames over a pipe: one reader, and writers serialised so frames never interleave.</summary>
public sealed class FrameStream(Stream stream) : IAsyncDisposable
{
    private readonly SemaphoreSlim _writing = new(1, 1);
    private readonly byte[] _head = new byte[5];

    public Stream Stream => stream;

    /// <summary>The next frame, or null when the other end closed the pipe.</summary>
    public async ValueTask<Frame?> ReadAsync(CancellationToken ct)
    {
        if (!await Fill(_head, ct)) return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(_head);
        if (length < 1 || length > RouterProtocol.MostFrame) throw new InvalidDataException($"a frame of {length} bytes");
        var payload = new byte[length - 1];
        if (!await Fill(payload, ct)) throw new EndOfStreamException("the pipe closed inside a frame");
        return new Frame(_head[4], payload);
    }

    public ValueTask SendAsync(RouterMessage message, CancellationToken ct = default) =>
        WriteAsync(RouterProtocol.ControlFrame, JsonSerializer.SerializeToUtf8Bytes(message, RouterProtocol.Json), ReadOnlyMemory<byte>.Empty, ct);

    public ValueTask SendPacketAsync(int generation, ReadOnlyMemory<byte> packet, CancellationToken ct = default)
    {
        var head = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(head, generation);
        return WriteAsync(RouterProtocol.PacketFrame, head, packet, ct);
    }

    /// <summary>Writes every packet <paramref name="reader"/> holds now, as many frames in one write, waiting only when
    /// it holds none: a pipe write per packet halved a download through the service (measured, item 2 step 4).</summary>
    public async Task PumpPacketsAsync(ChannelReader<(int Generation, byte[] Packet)> reader, CancellationToken ct)
    {
        var buffer = new byte[256 * 1024];
        while (await reader.WaitToReadAsync(ct))
        {
            var used = 0;
            while (used + 9 + 1600 <= buffer.Length && reader.TryRead(out var item))
            {
                if (used + 9 + item.Packet.Length > buffer.Length)
                {
                    await FlushAsync(buffer, used, ct);
                    used = 0;
                }
                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(used), 1 + 4 + item.Packet.Length);
                buffer[used + 4] = RouterProtocol.PacketFrame;
                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(used + 5), item.Generation);
                item.Packet.CopyTo(buffer.AsSpan(used + 9));
                used += 9 + item.Packet.Length;
            }
            if (used > 0) await FlushAsync(buffer, used, ct);
        }
    }

    private async Task FlushAsync(byte[] buffer, int used, CancellationToken ct)
    {
        await _writing.WaitAsync(ct);
        try { await stream.WriteAsync(buffer.AsMemory(0, used), ct); }
        finally { _writing.Release(); }
    }

    private async ValueTask WriteAsync(byte kind, ReadOnlyMemory<byte> first, ReadOnlyMemory<byte> second, CancellationToken ct)
    {
        var frame = new byte[5 + first.Length + second.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, 1 + first.Length + second.Length);
        frame[4] = kind;
        first.CopyTo(frame.AsMemory(5));
        second.CopyTo(frame.AsMemory(5 + first.Length));
        await _writing.WaitAsync(ct);
        try { await stream.WriteAsync(frame, ct); }
        finally { _writing.Release(); }
    }

    private async ValueTask<bool> Fill(byte[] buffer, CancellationToken ct)
    {
        var at = 0;
        while (at < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(at), ct);
            if (read == 0) return at == 0 && buffer.Length > 0 ? false : throw new EndOfStreamException("the pipe closed inside a frame");
            at += read;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await stream.DisposeAsync();
        _writing.Dispose();
    }
}
