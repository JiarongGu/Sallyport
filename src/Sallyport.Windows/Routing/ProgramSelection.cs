namespace Sallyport.Windows.Routing;

/// <summary>
/// The programs a game profile includes, recognised by the executable each process runs — read when a flow's first
/// packet names its PID, so a program is selected from its first packet however it was started (a launcher, a mod
/// loader, by hand). A PID is remembered with its creation time: a recycled PID is another process (game-routing §1).
/// </summary>
public sealed class ProgramSelection
{
    private readonly HashSet<string> _paths;
    private readonly Dictionary<int, (long Created, bool Selected)> _known = [];
    private readonly HashSet<int> _unreadable = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _pathOf = new();

    public ProgramSelection(IEnumerable<string> executables) =>
        _paths = new(executables.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Executables => _paths;

    /// <summary>Processes found running a selected executable.</summary>
    public IEnumerable<int> Selected => _known.Where(k => k.Value.Selected).Select(k => k.Key);

    /// <summary>Processes whose executable could not be read: not selected, and kept apart from "not a game's"
    /// (rule 6) — a protected process refusing even limited query access lands here.</summary>
    public IReadOnlyCollection<int> Unreadable => _unreadable;

    public bool Selects(int pid)
    {
        // The idle process (0) and System (4) run no program's executable.
        if (pid <= 4) return false;
        if (!ProcessImage.TryCreated(pid, out var created))
        {
            _unreadable.Add(pid);
            return false;
        }
        if (_known.TryGetValue(pid, out var known) && known.Created == created) return known.Selected;
        if (!ProcessImage.TryRead(pid, out created, out var path) || path is null)
        {
            _unreadable.Add(pid);
            return false;
        }
        var selected = _paths.TryGetValue(path, out var chosen);
        _known[pid] = (created, selected);
        if (selected) _pathOf[pid] = chosen!;
        else _pathOf.TryRemove(pid, out _);
        _unreadable.Remove(pid);
        return selected;
    }

    /// <summary>The selected executable a process was found running — as the selection names it — for counting what is
    /// routed by program; null for a process not selected. Read from any thread.</summary>
    public string? PathOf(int pid) => _pathOf.GetValueOrDefault(pid);
}
