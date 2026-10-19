using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    public const int Version = 1;
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
public abstract record RouterMessage;

/// <summary>Client → supervisor, first.</summary>
public sealed record Hello(int Version, string Build) : RouterMessage;

/// <summary>Supervisor → client: what the service is and what it can do now.</summary>
public sealed record Welcome(int Version, string Build, bool DriverReady, string? DriverProblem, string? RunId) : RouterMessage;

/// <summary>Either side: the request (or the client) is refused, and why — a sentence to show, never a secret.</summary>
public sealed record Rejected(string Reason) : RouterMessage;

/// <summary>Client → supervisor: route these programs (their full executable paths), with this loss policy.</summary>
public sealed record PrepareRun(string[] Executables, LossMode Loss, int? DirectAfterSeconds) : RouterMessage;

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

/// <summary>Client → worker: a tunnel is up — its generation, its IPv4 address and the largest packet it carries whole.</summary>
public sealed record CommitGeneration(int Generation, string Address, int Mtu) : RouterMessage;

/// <summary>Client → worker: a generation's tunnel has gone.</summary>
public sealed record RetireGeneration(int Generation) : RouterMessage;

/// <summary>Worker → client, every second: where the route stands, and its counts so far.</summary>
public sealed record RouteState(RoutePhase Phase, int Generation, long Routed, long HandedUp, long Direct, long Unowned, long HeldWhileDown,
    long DirectWhileDown, long LateFlows, long Blocked, long Stale, long RoutedFlows, int Selected, int Unreadable) : RouterMessage;

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
