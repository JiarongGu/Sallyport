using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Sallyport.Windows.PacketFilter;

internal static partial class Native
{
    public const uint SC_MANAGER_CONNECT = 0x0001;
    public const uint SERVICE_QUERY_STATUS = 0x0004;
    public const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_FILE_NOT_FOUND = 2;

    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint OPEN_EXISTING = 3;

    /// <summary>CTL_CODE(DeviceType, Function, Method, Access): the 16-bit device type, then access (2 bits), function
    /// (12 bits) and method (2 bits) — winioctl.h.</summary>
    public static uint ControlCode(uint deviceType, uint function, uint method, uint access) =>
        (deviceType << 16) | (access << 14) | (function << 2) | method;

    /// <summary>NT Kernel's interface (ndisapi, MIT, include/Common.h): FILE_DEVICE_NDISRD 0x8300, NDISRD_IOCTL_INDEX
    /// 0x830, METHOD_BUFFERED and FILE_ANY_ACCESS both 0; the version is answered as one 4-byte ULONG.</summary>
    public static readonly uint IoctlGetVersion = ControlCode(0x8300, 0x830, 0, 0);

    [StructLayout(LayoutKind.Sequential)]
    public struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr OpenSCManager(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr OpenService(IntPtr manager, string name, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseServiceHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, uint inputSize,
        out uint output, uint outputSize, out uint returned, IntPtr overlapped);
}
