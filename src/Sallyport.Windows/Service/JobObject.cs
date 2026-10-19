using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Sallyport.Windows.Service;

/// <summary>
/// A job object whose processes die with it (<c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>): the router's worker is put in
/// the supervisor's, so a supervisor that dies — however — takes the worker and its driver handle with it, and the driver
/// gives the adapter back (gate 1, measured).
/// </summary>
public sealed partial class JobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    // JOBOBJECT_EXTENDED_LIMIT_INFORMATION on x64: JOBOBJECT_BASIC_LIMIT_INFORMATION (64 bytes; LimitFlags at 16), then
    // IO_COUNTERS (48) and four SIZE_Ts (32) — 144 bytes.
    private const int ExtendedLimitSize = 144, LimitFlagsOffset = 16;

    private readonly SafeFileHandle _job;

    public unsafe JobObject()
    {
        _job = CreateJobObjectW(IntPtr.Zero, null);
        if (_job.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var info = stackalloc byte[ExtendedLimitSize];
        new Span<byte>(info, ExtendedLimitSize).Clear();
        *(uint*)(info + LimitFlagsOffset) = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!SetInformationJobObject(_job, JobObjectExtendedLimitInformation, info, ExtendedLimitSize))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    public void Add(Process process)
    {
        if (!AssignProcessToJobObject(_job, process.SafeHandle)) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    public void Dispose() => _job.Dispose();

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetInformationJobObject(SafeFileHandle job, int infoClass, void* info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
}
