using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Sallyport.Windows.Service;

/// <summary>Being a service: the dispatcher the manager calls into, the control handler and the status it is told — kept
/// apart from installing and querying one, which any process may do.</summary>
public static partial class ServiceControl
{
    private static string? _name;
    private static IntPtr _statusHandle;
    private static Func<CancellationToken, Task>? _body;
    private static readonly CancellationTokenSource Stopping = new();
    private static readonly ServiceMainProc Main = ServiceMain;
    private static readonly HandlerProc Handler = OnControl;

    /// <summary>Runs <paramref name="body"/> as the service <paramref name="name"/> until the manager stops it (or the
    /// machine shuts down) and the body returns. Blocks the calling thread for the service's life, as the manager's
    /// dispatcher does.</summary>
    public static void Host(string name, Func<CancellationToken, Task> body)
    {
        _name = name;
        _body = body;
        var table = new ServiceTableEntry[] { new() { Name = Marshal.StringToHGlobalUni(name), Proc = Marshal.GetFunctionPointerForDelegate(Main) }, default };
        if (!StartServiceCtrlDispatcherW(table)) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    private static void ServiceMain(int argc, IntPtr argv)
    {
        _statusHandle = RegisterServiceCtrlHandlerExW(_name!, Marshal.GetFunctionPointerForDelegate(Handler), IntPtr.Zero);
        Report(SERVICE_START_PENDING, 0, 3000);
        Report(SERVICE_RUNNING, SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_SHUTDOWN, 0);
        var exit = 0u;
        try { _body!(Stopping.Token).GetAwaiter().GetResult(); }
        catch (Exception) { exit = 1; }
        Report(SERVICE_STOPPED, 0, 0, exit);
    }

    private static uint OnControl(uint control, uint type, IntPtr data, IntPtr context)
    {
        if (control is SERVICE_CONTROL_STOP or SERVICE_CONTROL_SHUTDOWN)
        {
            Report(SERVICE_STOP_PENDING, 0, 10000);
            Stopping.Cancel();
        }
        return 0;
    }

    private static void Report(uint state, uint accepted, uint waitHint, uint exitCode = 0)
    {
        // SERVICE_STATUS: type, state, controls accepted, Win32 exit code, service exit code, checkpoint, wait hint.
        var status = new uint[] { SERVICE_WIN32_OWN_PROCESS, state, accepted, exitCode == 0 ? 0u : 1066u, exitCode, 0, waitHint };
        SetServiceStatus(_statusHandle, status);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceTableEntry
    {
        public IntPtr Name;
        public IntPtr Proc;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ServiceMainProc(int argc, IntPtr argv);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint HandlerProc(uint control, uint type, IntPtr data, IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool StartServiceCtrlDispatcherW(ServiceTableEntry[] table);

    [LibraryImport("advapi32.dll", EntryPoint = "RegisterServiceCtrlHandlerExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr RegisterServiceCtrlHandlerExW(string name, IntPtr handler, IntPtr context);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetServiceStatus(IntPtr handle, uint[] status);
}
