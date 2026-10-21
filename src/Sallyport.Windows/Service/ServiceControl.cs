using System.ComponentModel;
using System.Runtime.InteropServices;
using Sallyport.Windows.PacketFilter;

namespace Sallyport.Windows.Service;

/// <summary>
/// Windows' service control manager, reached directly (rule 4: .NET ships no <c>ServiceBase</c> without a package):
/// installing, starting, stopping and removing a service, and being one (<see cref="Host"/>). Its structures are the
/// SDK's — <c>SERVICE_STATUS</c> seven DWORDs, <c>SERVICE_STATUS_PROCESS</c> nine.
/// </summary>
public static partial class ServiceControl
{
    private const uint SC_MANAGER_ALL_ACCESS = 0xF003F, SC_MANAGER_CONNECT = 0x0001;
    private const uint SERVICE_ALL_ACCESS = 0xF01FF, SERVICE_QUERY_CONFIG = 0x0001, SERVICE_QUERY_STATUS = 0x0004, DELETE = 0x00010000;
    private const uint SERVICE_WIN32_OWN_PROCESS = 0x10, SERVICE_AUTO_START = 2, SERVICE_ERROR_NORMAL = 1;
    private const uint SERVICE_CONFIG_DESCRIPTION = 1, SERVICE_CONFIG_FAILURE_ACTIONS = 2;
    private const uint SC_ACTION_RESTART = 1;
    private const uint SERVICE_CONTROL_STOP = 1, SERVICE_CONTROL_SHUTDOWN = 5;
    private const uint SERVICE_ACCEPT_STOP = 1, SERVICE_ACCEPT_SHUTDOWN = 4;
    private const uint SERVICE_STOPPED = 1, SERVICE_START_PENDING = 2, SERVICE_STOP_PENDING = 3, SERVICE_RUNNING = 4;
    private const int ERROR_SERVICE_DOES_NOT_EXIST = 1060, ERROR_SERVICE_NOT_ACTIVE = 1062, ERROR_SERVICE_ALREADY_RUNNING = 1056;
    private const int ERROR_SERVICE_MARKED_FOR_DELETE = 1072, ERROR_INSUFFICIENT_BUFFER = 122;

    /// <summary>Registers <paramref name="binaryPath"/> as an automatic LocalSystem service that the manager restarts
    /// 1, 5 and 30 s after a failure (docs/design/product.md §6), started with <paramref name="arguments"/>. Replaces an
    /// earlier registration's path.</summary>
    public static void Install(string name, string displayName, string description, string binaryPath, string? arguments = null)
    {
        using var scm = Manager(SC_MANAGER_ALL_ACCESS);
        var quoted = arguments is null ? $"\"{binaryPath}\"" : $"\"{binaryPath}\" {arguments}";
        var service = CreateServiceW(scm, name, displayName, SERVICE_ALL_ACCESS, SERVICE_WIN32_OWN_PROCESS, SERVICE_AUTO_START,
            SERVICE_ERROR_NORMAL, quoted, null, IntPtr.Zero, null, null, null);
        if (service.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            service.Dispose();
            // ERROR_SERVICE_EXISTS: an upgrade, the same name pointing at the new path.
            if (error != 1073) throw new Win32Exception(error);
            service = OpenServiceW(scm, name, SERVICE_ALL_ACCESS);
            if (service.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (!ChangeServiceConfigW(service, SERVICE_WIN32_OWN_PROCESS, SERVICE_AUTO_START, SERVICE_ERROR_NORMAL, quoted, null, IntPtr.Zero, null, null, null, displayName))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        using (service)
        {
            Describe(service, description);
            Recover(service);
        }
    }

    /// <summary>Removes the service, stopping it first; nothing to do when it is not installed. Throws when it did not
    /// stop in <paramref name="wait"/>: a service still running is not removed, and nothing after it should be.</summary>
    public static void Uninstall(string name, TimeSpan wait)
    {
        // ERROR_SERVICE_REQUEST_TIMEOUT: what the service manager itself says of a service that did not answer in time.
        if (!Stop(name, wait)) throw new Win32Exception(1053);
        using var scm = Manager(SC_MANAGER_ALL_ACCESS);
        using var service = OpenServiceW(scm, name, DELETE);
        if (service.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == ERROR_SERVICE_DOES_NOT_EXIST) return;
            throw new Win32Exception(error);
        }
        if (!DeleteService(service) && Marshal.GetLastPInvokeError() is var e && e != ERROR_SERVICE_MARKED_FOR_DELETE) throw new Win32Exception(e);
    }

    public static void Start(string name)
    {
        using var scm = Manager(SC_MANAGER_CONNECT);
        using var service = OpenServiceW(scm, name, SERVICE_ALL_ACCESS);
        if (service.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (!StartServiceW(service, 0, IntPtr.Zero) && Marshal.GetLastPInvokeError() is var e && e != ERROR_SERVICE_ALREADY_RUNNING)
            throw new Win32Exception(e);
    }

    /// <summary>Asks the service to stop and waits up to <paramref name="wait"/> for it to: true when it is stopped (or
    /// not installed), false when it is still running after the wait. Throws when it could not be asked (rule 6: that
    /// is neither).</summary>
    public static bool Stop(string name, TimeSpan wait)
    {
        using var scm = Manager(SC_MANAGER_CONNECT);
        using var service = OpenServiceW(scm, name, SERVICE_ALL_ACCESS);
        if (service.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            return error == ERROR_SERVICE_DOES_NOT_EXIST ? true : throw new Win32Exception(error);
        }
        var status = new uint[7];
        if (!ControlService(service, SERVICE_CONTROL_STOP, status) && Marshal.GetLastPInvokeError() is var e && e != ERROR_SERVICE_NOT_ACTIVE)
            throw new Win32Exception(e);
        var deadline = Environment.TickCount64 + (long)wait.TotalMilliseconds;
        while (Query(service).State != ServiceState.Stopped)
        {
            if (Environment.TickCount64 > deadline) return false;
            Thread.Sleep(100);
        }
        return true;
    }

    /// <summary>The service's state and, while it runs, its process — what a client checks its pipe's server against.</summary>
    public static (ServiceState State, int ProcessId) Status(string name)
    {
        using var scm = Manager(SC_MANAGER_CONNECT);
        using var service = OpenServiceW(scm, name, SERVICE_QUERY_STATUS);
        if (service.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == ERROR_SERVICE_DOES_NOT_EXIST) return (ServiceState.NotInstalled, 0);
            throw new Win32Exception(error);
        }
        return Query(service);
    }

    /// <summary>The account the manager runs the service as and the executable it starts: a record only an administrator
    /// can change, which a person may read (a service's default DACL gives interactive users SERVICE_QUERY_CONFIG) where the
    /// service's own process is closed to them.</summary>
    public static unsafe (string Account, string Executable)? Config(string name)
    {
        using var scm = Manager(SC_MANAGER_CONNECT);
        using var service = OpenServiceW(scm, name, SERVICE_QUERY_CONFIG);
        if (service.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == ERROR_SERVICE_DOES_NOT_EXIST) return null;
            throw new Win32Exception(error);
        }
        if (!QueryServiceConfigW(service, null, 0, out var needed) && Marshal.GetLastPInvokeError() != ERROR_INSUFFICIENT_BUFFER)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        var buffer = new byte[needed];
        fixed (byte* p = buffer)
        {
            if (!QueryServiceConfigW(service, p, needed, out _)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            var config = (QueryServiceConfig*)p;
            return (Marshal.PtrToStringUni(config->ServiceStartName) ?? "", Executable(Marshal.PtrToStringUni(config->BinaryPathName) ?? ""));
        }
    }

    /// <summary>The executable of a service's command line: quoted as <see cref="Install"/> writes it, or up to its
    /// ".exe" where it is not.</summary>
    internal static string Executable(string commandLine)
    {
        var line = commandLine.Trim();
        if (line.StartsWith('"')) return line.IndexOf('"', 1) is var end and > 0 ? line[1..end] : line[1..];
        var exe = line.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe < 0 ? line : line[..(exe + 4)];
    }

    private static unsafe (ServiceState State, int ProcessId) Query(SafeServiceHandle service)
    {
        // SERVICE_STATUS_PROCESS: type, state, controls accepted, exit code, specific exit code, checkpoint, wait hint,
        // process id, flags.
        var status = new uint[9];
        fixed (uint* p = status)
            if (!QueryServiceStatusEx(service, 0, p, (uint)(status.Length * 4), out _)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var state = status[1] switch
        {
            SERVICE_STOPPED => ServiceState.Stopped,
            SERVICE_START_PENDING => ServiceState.Starting,
            SERVICE_STOP_PENDING => ServiceState.Stopping,
            SERVICE_RUNNING => ServiceState.Running,
            _ => ServiceState.Other,
        };
        return (state, (int)status[7]);
    }

    private static SafeServiceHandle Manager(uint access)
    {
        var scm = OpenSCManagerW(null, null, access);
        if (scm.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return scm;
    }

    private static unsafe void Describe(SafeServiceHandle service, string description)
    {
        fixed (char* text = description)
        {
            var info = (IntPtr)text;
            if (!ChangeServiceConfig2W(service, SERVICE_CONFIG_DESCRIPTION, &info)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    // SERVICE_FAILURE_ACTIONS: reset period (s), reboot message, command, action count, actions — each SC_ACTION a type
    // and a delay in ms. Restarted 1, 5 and 30 s after failures; the count of failures resets after a day.
    private static unsafe void Recover(SafeServiceHandle service)
    {
        var actions = stackalloc uint[] { SC_ACTION_RESTART, 1000, SC_ACTION_RESTART, 5000, SC_ACTION_RESTART, 30000 };
        var failure = new FailureActions { ResetPeriod = 86400, Actions = 3, ActionList = (IntPtr)actions };
        if (!ChangeServiceConfig2W(service, SERVICE_CONFIG_FAILURE_ACTIONS, &failure)) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    // QUERY_SERVICE_CONFIGW: type, start type, error control, binary path, load order group, tag, dependencies, account,
    // display name; the strings point into the same buffer.
    [StructLayout(LayoutKind.Sequential)]
    private struct QueryServiceConfig
    {
        public uint ServiceType;
        public uint StartType;
        public uint ErrorControl;
        public IntPtr BinaryPathName;
        public IntPtr LoadOrderGroup;
        public uint TagId;
        public IntPtr Dependencies;
        public IntPtr ServiceStartName;
        public IntPtr DisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FailureActions
    {
        public uint ResetPeriod;
        public IntPtr RebootMessage;
        public IntPtr Command;
        public uint Actions;
        public IntPtr ActionList;
    }

    private sealed class SafeServiceHandle() : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeServiceHandle OpenSCManagerW(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeServiceHandle OpenServiceW(SafeServiceHandle scm, string name, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "CreateServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeServiceHandle CreateServiceW(SafeServiceHandle scm, string name, string displayName, uint access, uint type,
        uint start, uint errorControl, string binaryPath, string? loadOrderGroup, IntPtr tagId, string? dependencies, string? account, string? password);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfigW(SafeServiceHandle service, uint type, uint start, uint errorControl, string binaryPath,
        string? loadOrderGroup, IntPtr tagId, string? dependencies, string? account, string? password, string displayName);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool ChangeServiceConfig2W(SafeServiceHandle service, uint level, void* info);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteService(SafeServiceHandle service);

    [LibraryImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StartServiceW(SafeServiceHandle service, uint argc, IntPtr argv);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ControlService(SafeServiceHandle service, uint control, [Out] uint[] status);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryServiceConfigW(SafeServiceHandle service, void* config, uint size, out uint needed);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryServiceStatusEx(SafeServiceHandle service, int level, uint* buffer, uint size, out uint needed);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr handle);
}
