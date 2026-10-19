using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Sallyport.Windows.Service;

/// <summary>
/// Who is at the other end of a named pipe, from Windows rather than from anything it says (docs/design/product.md §2):
/// its process, session, account and executable. A process's word for itself carries no authority.
/// </summary>
public sealed partial record PipePeer(int ProcessId, int SessionId, SecurityIdentifier? User, string? Image)
{
    public bool IsSystem => User?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true;

    /// <summary>Whether the peer's executable lies in <paramref name="folder"/> — the install folder, which only
    /// administrators can write: until Sallyport is signed, what an ordinary process cannot pretend to be (product
    /// design, change 2).</summary>
    public bool RunsFrom(string folder) =>
        Image is not null && string.Equals(Path.GetDirectoryName(Image), Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), StringComparison.OrdinalIgnoreCase);

    /// <summary>The client of a server end.</summary>
    public static PipePeer ClientOf(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (!GetNamedPipeClientSessionId(pipe.SafePipeHandle, out var session)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return Of((int)pid, (int)session);
    }

    /// <summary>The server of a client end.</summary>
    public static PipePeer ServerOf(NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (!GetNamedPipeServerSessionId(pipe.SafePipeHandle, out var session)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return Of((int)pid, (int)session);
    }

    private static PipePeer Of(int pid, int session)
    {
        using var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (process.IsInvalid) return new PipePeer(pid, session, null, null);
        return new PipePeer(pid, session, UserOf(process), ImageOf(process));
    }

    private static unsafe string? ImageOf(SafeProcessHandle process)
    {
        var buffer = stackalloc char[1024];
        var length = 1024u;
        return QueryFullProcessImageNameW(process, 0, buffer, ref length) ? new string(buffer, 0, (int)length) : null;
    }

    private static SecurityIdentifier? UserOf(SafeProcessHandle process)
    {
        if (!OpenProcessToken(process, TOKEN_QUERY, out var token)) return null;
        using (token)
        {
            try
            {
                using var identity = new WindowsIdentity(token.DangerousGetHandle());
                return identity.User;
            }
            catch (Exception e) when (e is UnauthorizedAccessException or ArgumentException or SecurityException) { return null; }
        }
    }

    /// <summary>The pipe's DACL: LocalSystem may do anything; an interactive user may read and write but not create
    /// instances (so cannot stand in as the server); nobody over the network may open it at all.</summary>
    public static PipeSecurity Security()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return security;
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, TOKEN_QUERY = 0x0008;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint session);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerSessionId(SafePipeHandle pipe, out uint session);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, char* name, ref uint size);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}
