using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Sallyport.Windows.Routing;

/// <summary>The executable a process runs, and when it started: what tells one process from a later one with its PID.
/// Limited query access only, which an unelevated caller has to most processes; a protected process refuses even that.</summary>
internal static partial class ProcessImage
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    /// <summary>False when the process could not be opened or read: gone, or refusing.</summary>
    public static bool TryRead(int pid, out long created, out string? path)
    {
        path = null;
        created = 0;
        using var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process.IsInvalid || !GetProcessTimes(process, out created, out _, out _, out _)) return false;
        path = ImagePath(process);
        return path is not null;
    }

    /// <summary>When the process started, without its path: enough to know a PID still names the same process.</summary>
    public static bool TryCreated(int pid, out long created)
    {
        created = 0;
        using var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        return !process.IsInvalid && GetProcessTimes(process, out created, out _, out _, out _);
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
