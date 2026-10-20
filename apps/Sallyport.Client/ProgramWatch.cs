using Sallyport.Windows.Routing;

namespace Sallyport.Client;

/// <summary>
/// The programs of the app whose page is open, looked at every two seconds while one is: running or not, and the
/// connections they hold (<see cref="RunningPrograms"/>). Nothing is looked at while no app's page is open — a list of
/// every process on the machine is not free.
/// </summary>
internal sealed class ProgramWatch : IDisposable
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(2);
    private static readonly IReadOnlyDictionary<string, ProgramActivity> None = new Dictionary<string, ProgramActivity>();

    private readonly RunningPrograms _sampler = new();
    private readonly Func<IReadOnlyCollection<string>> _paths;
    private readonly Action _changed;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _poke = new(0);
    private volatile IReadOnlyDictionary<string, ProgramActivity> _last = None;

    public ProgramWatch(Func<IReadOnlyCollection<string>> paths, Action changed)
    {
        _paths = paths;
        _changed = changed;
        _ = Task.Run(RunAsync);
    }

    /// <summary>The program as last seen; null while it is not watched.</summary>
    public ProgramActivity? Of(string path) => _last.GetValueOrDefault(path);

    /// <summary>Looks again now: another app's page was opened.</summary>
    public void Poke() => _poke.Release();

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var paths = _paths();
            IReadOnlyDictionary<string, ProgramActivity> seen;
            try { seen = paths.Count == 0 ? None : _sampler.Sample(paths); }
            // A process list or owner table Windows would not give this time: the last stands, and the next look tries again.
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { seen = _last; }
            if (!Same(seen, _last))
            {
                _last = seen;
                _changed();
            }
            try { await _poke.WaitAsync(Every, _stop.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    // What the window shows of each: the machine's unreadable processes come and go without changing it.
    private static bool Same(IReadOnlyDictionary<string, ProgramActivity> a, IReadOnlyDictionary<string, ProgramActivity> b) =>
        a.Count == b.Count && a.All(entry => b.TryGetValue(entry.Key, out var other) && other with { Unreadable = 0 } == entry.Value with { Unreadable = 0 });

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
