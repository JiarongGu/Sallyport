using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Sallyport.Windows.Routing;

/// <summary>
/// The programs a game profile includes, recognised by the executable each process runs — read when a flow's first
/// packet names its PID, so a program is selected from its first packet however it was started (a launcher, a mod
/// loader, by hand). A PID is remembered with its creation time: a recycled PID is another process (game-routing §1).
/// </summary>
public sealed partial class ProgramSelection
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    private readonly HashSet<string> _paths;
    private readonly Dictionary<int, (long Created, bool Selected)> _known = [];
    private readonly HashSet<int> _unreadable = [];

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
        using var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process.IsInvalid || !GetProcessTimes(process, out var created, out _, out _, out _))
        {
            _unreadable.Add(pid);
            return false;
        }
        if (_known.TryGetValue(pid, out var known) && known.Created == created) return known.Selected;
        if (ImagePath(process) is not { } path)
        {
            _unreadable.Add(pid);
            return false;
        }
        var selected = _paths.Contains(path);
        _known[pid] = (created, selected);
        _unreadable.Remove(pid);
        return selected;
    }

    private static unsafe string? ImagePath(SafeProcessHandle process)
    {
        var capacity = 512;
        while (true)
        {
            var buffer = new char[capacity];
            var size = (uint)capacity;
            fixed (char* p = buffer)
                if (QueryFullProcessImageName(process, 0, p, ref size)) return Path.GetFullPath(new string(buffer, 0, (int)size));
            if (Marshal.GetLastPInvokeError() != ERROR_INSUFFICIENT_BUFFER || capacity >= 32768) return null;
            capacity = 32768;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, char* path, ref uint size);
}
