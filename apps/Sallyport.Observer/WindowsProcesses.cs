using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Sallyport.Observer;

internal static class WindowsProcesses
{
    public static IReadOnlyList<ProcessSample> Snapshot()
    {
        using SafeFileHandle snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Process snapshot unavailable");
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        List<ProcessSample> result = [];
        bool found = Process32FirstW(snapshot, ref entry);
        while (found)
        {
            result.Add(new(entry.ProcessId, entry.ParentProcessId, Query(entry.ProcessId)));
            entry.Size = (uint)Marshal.SizeOf<ProcessEntry>();
            found = Process32NextW(snapshot, ref entry);
        }
        int error = Marshal.GetLastWin32Error();
        if (error != 18) throw new Win32Exception(error, "Process enumeration unavailable");
        return result;
    }

    public static IdentityResult Query(uint pid)
    {
        if (pid == 0) return new(IdentityState.Unavailable, Error: 87);
        using SafeProcessHandle process = OpenProcess(0x1000, false, pid);
        if (process.IsInvalid) return Failure(Marshal.GetLastWin32Error());
        var path = new StringBuilder(512);
        uint length = (uint)path.Capacity;
        if (!QueryFullProcessImageNameW(process, 0, path, ref length))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != 122) return Failure(error);
            path.EnsureCapacity(32768);
            length = (uint)path.Capacity;
            if (!QueryFullProcessImageNameW(process, 0, path, ref length)) return Failure(Marshal.GetLastWin32Error());
        }
        if (!GetProcessTimes(process, out long creation, out long exit, out _, out _)) return Failure(Marshal.GetLastWin32Error());
        if (exit != 0) return new(IdentityState.Exited);
        return new(IdentityState.Available, new(new(pid, creation), Path.GetFullPath(path.ToString())));
    }

    private static IdentityResult Failure(int error) => new(error == 87 ? IdentityState.Exited : IdentityState.Unavailable, Error: error);

    public static bool IsElevated()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 8, out SafeFileHandle token))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Elevation check unavailable");
        using (token)
        {
            if (!GetTokenInformation(token, 20, out int elevated, sizeof(int), out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Elevation check unavailable");
            return elevated != 0;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder path, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeFileHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformation(SafeFileHandle token, int informationClass, out int information, int length, out int returned);
}
