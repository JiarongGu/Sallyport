using System.Text.Json.Serialization;

namespace Sallyport.Windows.Routing;

/// <summary>A program a route selects, by its executable's full path, and what its packets do while the route's tunnel
/// is lost: apps of one route may each say otherwise (D23). <see cref="Lane"/>: the profile's tunnel it goes into, one lane
/// per profile carrying apps (D24).</summary>
public sealed record ProgramRule(string Path, LossMode Loss = LossMode.Hold, int? DirectAfterSeconds = null, int Lane = 0)
{
    [JsonIgnore]
    public LossPolicy Policy => new(Loss, DirectAfterSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null);
}

/// <summary>
/// The programs a game profile includes, recognised by the executable each process runs — read when a flow's first
/// packet names its PID, so a program is selected from its first packet however it was started (a launcher, a mod
/// loader, by hand). A PID is remembered with its creation time: a recycled PID is another process (game-routing §1).
/// With a <see cref="Session"/>, only that session's processes are selected: a program another person on this computer
/// runs is theirs, never routed into this person's tunnel (product design §2).
/// </summary>
public sealed class ProgramSelection
{
    private Dictionary<string, ProgramRule> _rules;
    private readonly Dictionary<int, (long Created, bool Selected)> _known = [];
    // Written by the router's thread, read by whatever reports the route: the set's own lock-free dictionary.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> _unreadable = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _pathOf = new();

    public ProgramSelection(IEnumerable<string> executables) : this(executables.Select(e => new ProgramRule(e))) { }

    public ProgramSelection(IEnumerable<ProgramRule> programs, int? session = null)
    {
        _rules = Rules(programs);
        Session = session;
    }

    /// <summary>The Windows session whose processes may be selected; null, any (a measuring tool's own run).</summary>
    public int? Session { get; }

    public IReadOnlyCollection<string> Executables => _rules.Keys;

    /// <summary>Processes found running a selected executable. Read from any thread: the window's counts are taken while
    /// the router decides.</summary>
    public IEnumerable<int> Selected => _pathOf.Keys;

    /// <summary>Processes whose executable could not be read: not selected, and kept apart from "not a game's"
    /// (rule 6) — a protected process refusing even limited query access lands here.</summary>
    public IReadOnlyCollection<int> Unreadable => [.. _unreadable.Keys];

    public bool Selects(int pid)
    {
        // The idle process (0) and System (4) run no program's executable.
        if (pid <= 4) return false;
        if (!ProcessImage.TryCreated(pid, out var created))
        {
            _unreadable.TryAdd(pid, 0);
            return false;
        }
        if (_known.TryGetValue(pid, out var known) && known.Created == created) return known.Selected;
        if (!ProcessImage.TryRead(pid, out created, out var path) || path is null)
        {
            _unreadable.TryAdd(pid, 0);
            return false;
        }
        var selected = _rules.TryGetValue(path, out var chosen);
        if (selected && Session is { } session)
        {
            // Whose it is cannot be read: not selected, and said so (rule 6), as a process whose image cannot be.
            if (!ProcessImage.TrySession(pid, out var its))
            {
                _unreadable.TryAdd(pid, 0);
                return false;
            }
            selected = its == session;
        }
        _known[pid] = (created, selected);
        if (selected) _pathOf[pid] = chosen!.Path;
        else _pathOf.TryRemove(pid, out _);
        _unreadable.TryRemove(pid, out _);
        return selected;
    }

    /// <summary>The selected executable a process was found running — as the selection names it — for counting what is
    /// routed by program; null for a process not selected. Read from any thread.</summary>
    public string? PathOf(int pid) => _pathOf.GetValueOrDefault(pid);

    /// <summary>The rule of the program a selected process runs; null for one not selected. On the thread that calls
    /// <see cref="Selects"/>.</summary>
    public ProgramRule? RuleOf(int pid) => _pathOf.TryGetValue(pid, out var path) ? _rules.GetValueOrDefault(path) : null;

    /// <summary>The lane of the program a selected process runs; 0 for one not selected. On the thread that calls
    /// <see cref="Selects"/>.</summary>
    public int LaneOf(int pid) => RuleOf(pid)?.Lane ?? 0;

    /// <summary>Another set of programs from now on, every process looked at afresh. Only on the thread that calls
    /// <see cref="Selects"/> — the router's, through <see cref="Router.Reselect"/> — so no decision is made half on
    /// the old set.</summary>
    public void Replace(IEnumerable<ProgramRule> programs)
    {
        _rules = Rules(programs);
        _known.Clear();
        _pathOf.Clear();
    }

    private static Dictionary<string, ProgramRule> Rules(IEnumerable<ProgramRule> programs)
    {
        var rules = new Dictionary<string, ProgramRule>(StringComparer.OrdinalIgnoreCase);
        // The first rule for a path stands: two apps naming one program agree on it by order, not by chance.
        foreach (var program in programs) rules.TryAdd(Path.GetFullPath(program.Path), program with { Path = Path.GetFullPath(program.Path) });
        return rules;
    }
}
