using System.Net;
using System.Net.Sockets;

namespace Sallyport.Observer;

public readonly record struct InstanceId(uint Pid, long CreationTimeFileTime);
public sealed record ProcessIdentity(InstanceId Instance, string ImagePath);
public enum IdentityState { Available, Exited, Unavailable }
public sealed record IdentityResult(IdentityState State, ProcessIdentity? Identity = null, int Error = 0);
public sealed record ProcessSample(uint Pid, uint ParentPid, IdentityResult Result);

public sealed class ProcessTracker(IEnumerable<string> executablePaths)
{
    private readonly HashSet<string> paths = new(executablePaths.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<InstanceId, Tracked> watched = [];
    private Dictionary<uint, ProcessSample> current = [];
    private readonly object gate = new();

    private sealed class Tracked(ProcessIdentity identity, long validatedAt)
    {
        public ProcessIdentity Identity { get; } = identity;
        public long LastValidated { get; set; } = validatedAt;
    }

    public void Update(IEnumerable<ProcessSample> samples, long snapshotStartedAt)
    {
        lock (gate)
        {
            // A PID recycled between snapshot capture and handle lookup cannot inherit the snapshot's parent PID.
            current = samples.Select(p => p.Result.Identity is { } identity &&
                identity.Instance.CreationTimeFileTime > snapshotStartedAt
                    ? p with { Result = new(IdentityState.Unavailable) } : p).ToDictionary(p => p.Pid);
            foreach (ProcessSample sample in current.Values)
            {
                if (sample.Result.Identity is { } identity && sample.Result.State == IdentityState.Available &&
                    (paths.Contains(identity.ImagePath) || watched.ContainsKey(identity.Instance)))
                    Watch(identity, snapshotStartedAt);
            }

            bool changed;
            do
            {
                changed = false;
                foreach (ProcessSample child in current.Values)
                {
                    if (child.Result.State != IdentityState.Available || child.Result.Identity is not { } identity ||
                        watched.ContainsKey(identity.Instance)) continue;
                    // A parent's PID alone cannot establish ancestry after that PID has been recycled.
                    bool hasParent = current.TryGetValue(child.ParentPid, out ProcessSample? parent) &&
                        parent.Result.State == IdentityState.Available && parent.Result.Identity is { } parentIdentity &&
                        parentIdentity.Instance.CreationTimeFileTime <= identity.Instance.CreationTimeFileTime &&
                        watched.ContainsKey(parentIdentity.Instance);
                    // A vanished parent's last validated lifetime can still prove an older child's ancestry.
                    hasParent |= watched.Values.Any(p => p.Identity.Instance.Pid == child.ParentPid &&
                        p.Identity.Instance.CreationTimeFileTime <= identity.Instance.CreationTimeFileTime &&
                        identity.Instance.CreationTimeFileTime <= p.LastValidated);
                    if (!hasParent) continue;
                    Watch(identity, snapshotStartedAt);
                    changed = true;
                }
            } while (changed);
        }
    }

    private void Watch(ProcessIdentity identity, long validatedAt)
    {
        if (watched.TryGetValue(identity.Instance, out Tracked? tracked))
            tracked.LastValidated = Math.Max(tracked.LastValidated, validatedAt);
        else watched.Add(identity.Instance, new(identity, validatedAt));
    }

    public bool KnowsPid(uint pid)
    {
        lock (gate) return watched.Keys.Any(id => id.Pid == pid);
    }

    public ProcessIdentity? Attribute(uint pid, long eventTime, IdentityResult live, long validatedAt)
    {
        lock (gate)
        {
            if (live.State == IdentityState.Available && live.Identity is { } identity &&
                watched.ContainsKey(identity.Instance)) Watch(identity, validatedAt);
            // Buffered ETW events may predate the currently running instance; keep the old validated interval.
            Tracked[] matches = watched.Values.Where(p => p.Identity.Instance.Pid == pid &&
                p.Identity.Instance.CreationTimeFileTime <= eventTime && eventTime <= p.LastValidated).Take(2).ToArray();
            return matches.Length == 1 ? matches[0].Identity : null;
        }
    }

    public string SelectedPathState(string path, bool snapshotAvailable)
    {
        lock (gate)
        {
            if (!snapshotAvailable) return "identity-unavailable";
            if (current.Values.Any(p => p.Result.State == IdentityState.Available &&
                string.Equals(p.Result.Identity?.ImagePath, path, StringComparison.OrdinalIgnoreCase))) return "running";
            // Watched during the run and gone now is an answer of its own: a short-lived program learned from is not one
            // whose identity was never available. Gone means its PID is absent: a watched PID still present but unreadable
            // may be the program yet, and that stays unknown (rule 6).
            var seen = watched.Values.Where(t => string.Equals(t.Identity.ImagePath, path, StringComparison.OrdinalIgnoreCase)).ToList();
            if (seen.Count > 0 && !seen.Any(t => current.TryGetValue(t.Identity.Instance.Pid, out var now) && now.Result.State == IdentityState.Unavailable))
                return "seen, since exited";
            return current.Values.Any(p => p.Result.State == IdentityState.Unavailable)
                ? "identity-unavailable" : "not-running";
        }
    }
}

public static class EndpointClassifier
{
    public static string Classify(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6) return "ipv6-unsupported";
        byte[] b = address.GetAddressBytes();
        if (IPAddress.IsLoopback(address)) return "loopback";
        if (b[0] == 0) return "unspecified";
        if (b.All(o => o == 255)) return "broadcast";
        if (b[0] is >= 224 and <= 239) return "multicast";
        if (b[0] == 169 && b[1] == 254) return "link-local";
        if (b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) ||
            (b[0] == 100 && b[1] is >= 64 and <= 127)) return "private";
        if (b[0] >= 240) return "reserved";
        return "routable";
    }
}

public sealed record NetworkObservation(string Protocol, IPAddress Address, ushort Port, uint Pid,
    long TimestampFileTime, string Source);
public sealed record EndpointEvidence(string Protocol, string Address, ushort Port, string Classification,
    ProcessIdentity Process, IReadOnlyDictionary<string, long> Sources, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, long TimesSeen);

public sealed class EndpointAggregator
{
    private readonly record struct Key(string Protocol, IPAddress Address, ushort Port, InstanceId Instance);
    private readonly Dictionary<Key, EndpointEvidence> endpoints = [];

    public (bool IsNew, EndpointEvidence Evidence) Add(NetworkObservation observation, ProcessIdentity identity)
    {
        if (identity.Instance.Pid != observation.Pid || identity.Instance.CreationTimeFileTime > observation.TimestampFileTime)
            throw new ArgumentException("Observation does not belong to this process instance.");
        var key = new Key(observation.Protocol, observation.Address, observation.Port, identity.Instance);
        DateTimeOffset when = DateTimeOffset.FromFileTime(observation.TimestampFileTime).ToUniversalTime();
        bool isNew = !endpoints.TryGetValue(key, out EndpointEvidence? old);
        Dictionary<string, long> sources = old is null ? [] : new(old.Sources);
        sources[observation.Source] = sources.GetValueOrDefault(observation.Source) + 1;
        EndpointEvidence evidence = old is null
            ? new(observation.Protocol, observation.Address.ToString(), observation.Port,
                EndpointClassifier.Classify(observation.Address), identity, sources, when, when, 1)
            : old with { FirstSeen = when < old.FirstSeen ? when : old.FirstSeen,
                LastSeen = when > old.LastSeen ? when : old.LastSeen, TimesSeen = old.TimesSeen + 1, Sources = sources };
        endpoints[key] = evidence;
        return (isNew, evidence);
    }

    public IReadOnlyList<EndpointEvidence> Snapshot() => endpoints.Values.OrderBy(e => e.Protocol)
        .ThenBy(e => e.Address, StringComparer.Ordinal).ThenBy(e => e.Port).ThenBy(e => e.Process.Instance.Pid)
        .ThenBy(e => e.Process.Instance.CreationTimeFileTime).ToArray();
}
