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
    // 2: a run's programs each with their own loss policy, and changed while it runs (D23). 3: lanes — each program
    // into its profile's tunnel, a generation saying its lane in its top byte (D24). 4: a run's worker named by its
    // process, for a client that cannot open the system's processes to see who answered. 5: each program's bytes both
    // ways and its round trip through the tunnel.
    public const int Version = 5;
    public const string ServiceName = "Sallyport";
    /// <summary>The argument that starts Sallyport's one program as the service (D30), as the service manager is told to.</summary>
    public const string ServiceArgument = "--service";
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

    /// <summary>What of <paramref name="programs"/> may be routed, each path made whole: a full path, and none of
    /// Sallyport's own — the client's tunnel must stay direct, and the service with it — however the path is written
    /// (<c>sub\..\</c> names the same folder).</summary>
    public static ProgramRule[] Routable(IEnumerable<ProgramRule> programs, string serviceFolder)
    {
        var own = Path.TrimEndingDirectorySeparator(Path.GetFullPath(serviceFolder));
        return [.. programs
            .Select(p => Whole(p.Path) is { } path ? p with { Path = path } : null)
            .OfType<ProgramRule>()
            .Where(p => !string.Equals(Path.GetDirectoryName(p.Path), own, StringComparison.OrdinalIgnoreCase))];
    }

    // A full path made whole, or null for one that is not a path at all (a client's word, unchecked till here).
    private static string? Whole(string? path)
    {
        if (path is null || !Path.IsPathFullyQualified(path)) return null;
        try { return Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
