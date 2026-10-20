using System.Diagnostics;

namespace Sallyport.Windows.Routing;

/// <summary>One program as it is now: the processes running it, and their TCP connections and UDP sockets.
/// <see cref="Unreadable"/>: processes on the machine whose executable could not be read — one of them could be it.</summary>
public sealed record ProgramActivity(int Processes, int Tcp, int Udp, int Unreadable);

/// <summary>
/// Which of some programs are running, and what network they hold: their processes found by the executable each runs (as
/// <see cref="ProgramSelection"/> finds them), their sockets from Windows' owner tables. Read-only and unelevated — what
/// a window shows per program, routed or not; the router's own counts say what went through the VPN.
/// </summary>
public sealed class RunningPrograms
{
    private readonly Dictionary<int, (long Created, string? Path)> _known = [];
    private readonly OwnerTable _owners = new();

    /// <summary>Each of <paramref name="paths"/>, by its full path as given.</summary>
    public IReadOnlyDictionary<string, ProgramActivity> Sample(IReadOnlyCollection<string> paths)
    {
        var wanted = paths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var running = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var unreadable = 0;
        var alive = new HashSet<int>();
        foreach (var process in Process.GetProcesses())
        {
            var pid = process.Id;
            process.Dispose();
            // The idle process (0) and System (4) run no program's executable.
            if (pid <= 4) continue;
            alive.Add(pid);
            if (!ProcessImage.TryCreated(pid, out var created)) { unreadable++; continue; }
            if (!_known.TryGetValue(pid, out var known) || known.Created != created)
            {
                known = ProcessImage.TryRead(pid, out created, out var path) ? (created, path) : (created, null);
                _known[pid] = known;
            }
            if (known.Path is null) { unreadable++; continue; }
            if (wanted.Contains(known.Path))
            {
                if (!running.TryGetValue(known.Path, out var pids)) running[known.Path] = pids = [];
                pids.Add(pid);
            }
        }
        foreach (var gone in _known.Keys.Where(pid => !alive.Contains(pid)).ToList()) _known.Remove(gone);

        IReadOnlyDictionary<int, ProcessSockets> sockets = running.Count == 0 ? new Dictionary<int, ProcessSockets>() : Refreshed();
        return paths.ToDictionary(p => p, p =>
        {
            var pids = running.GetValueOrDefault(Path.GetFullPath(p)) ?? [];
            return new ProgramActivity(pids.Count, pids.Sum(pid => sockets.GetValueOrDefault(pid).Tcp), pids.Sum(pid => sockets.GetValueOrDefault(pid).Udp),
                unreadable);
        }, StringComparer.OrdinalIgnoreCase);
    }

    private IReadOnlyDictionary<int, ProcessSockets> Refreshed()
    {
        _owners.Refresh();
        return _owners.SocketsByProcess();
    }
}
