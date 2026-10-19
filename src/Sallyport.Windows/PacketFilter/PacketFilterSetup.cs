using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Sallyport.Windows.PacketFilter;

/// <summary>Where the driver's service stands, as Windows says it.</summary>
public enum ServiceState { NotInstalled, Stopped, Starting, Running, Stopping, Other, Unknown }

/// <summary>
/// The driver as this program finds it. Each part keeps its own answer (rule 6): a device that could not be opened is
/// <see cref="DeviceError"/>, not "not installed"; access denied there means the router needs administrator.
/// </summary>
public sealed record PacketFilterStatus(ServiceState Service, uint? DriverVersion, int? DeviceError, string? InstallerPath)
{
    public bool Usable => DriverVersion is not null;

    /// <summary>The driver's version word as it answers it — 0x06023000 for release 3.6.2.1 (measured 2026-10-20), so its
    /// encoding is the driver's own; shown raw rather than guessed at.</summary>
    public string? VersionText => DriverVersion is { } v ? $"0x{v:X8}" : null;
}

/// <summary>What an install or uninstall came to. <see cref="RebootNeeded"/>: done, finished by Windows on restart.</summary>
public sealed record SetupOutcome(bool Succeeded, bool RebootNeeded, bool Cancelled, int ExitCode, string Text, string? LogPath);

/// <summary>
/// Installs and removes Windows Packet Filter for the person, from NT Kernel's own pinned release (D18): fetched into
/// Sallyport's own folder, checked against <see cref="PacketFilterRelease.InstallerSha256"/>, run by Windows Installer
/// with one administrator prompt. The installer is kept, so removing the driver needs nothing fetched again.
/// </summary>
public sealed class PacketFilterSetup(string folder)
{
    private readonly string _folder = Path.GetFullPath(folder);

    public string InstallerPath => Path.Combine(_folder, PacketFilterRelease.InstallerFileName);

    public PacketFilterStatus Status()
    {
        var service = QueryService();
        uint? version = null;
        int? deviceError = null;
        using (var device = Native.CreateFile(PacketFilterRelease.DevicePath, Native.GENERIC_READ | Native.GENERIC_WRITE, 0, IntPtr.Zero,
                   Native.OPEN_EXISTING, 0, IntPtr.Zero))
        {
            if (device.IsInvalid) deviceError = Marshal.GetLastPInvokeError();
            else if (Native.DeviceIoControl(device, Native.IoctlGetVersion, IntPtr.Zero, 0, out var answer, sizeof(uint), out var returned, IntPtr.Zero)
                     && returned == sizeof(uint))
                version = answer;
            else deviceError = Marshal.GetLastPInvokeError();
        }
        return new(service, version, deviceError, File.Exists(InstallerPath) ? InstallerPath : null);
    }

    /// <summary>The pinned installer, fetched if it is not already here whole. Throws <see cref="InvalidDataException"/>
    /// when what arrived is not those bytes — nothing that is not them is ever run.</summary>
    public async Task<string> FetchAsync(HttpClient http, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_folder);
        if (File.Exists(InstallerPath) && await MatchesAsync(InstallerPath, ct)) return InstallerPath;

        var partial = InstallerPath + ".part";
        await using (var source = await http.GetStreamAsync(PacketFilterRelease.Installer, ct))
        await using (var target = File.Create(partial))
            await source.CopyToAsync(target, ct);
        if (!await MatchesAsync(partial, ct))
        {
            File.Delete(partial);
            throw new InvalidDataException("the downloaded installer is not the pinned release; it was deleted, not run");
        }
        File.Move(partial, InstallerPath, overwrite: true);
        return InstallerPath;
    }

    public Task<SetupOutcome> InstallAsync(CancellationToken ct = default) => RunInstallerAsync("/i", "install", ct);

    public Task<SetupOutcome> UninstallAsync(CancellationToken ct = default) => RunInstallerAsync("/x", "uninstall", ct);

    /// <summary>What Windows Installer's exit code means here: 0 done; 3010 and 1641 done, a restart finishes it; 1602
    /// cancelled in the installer; 1223 the administrator prompt refused; anything else failed, its log saying why.</summary>
    internal static SetupOutcome Interpret(int exitCode, string action, string? log) => exitCode switch
    {
        0 => new(true, false, false, 0, $"{action} done", log),
        3010 or 1641 => new(true, true, false, exitCode, $"{action} done; Windows finishes it on the next restart", log),
        1602 or 1223 => new(false, false, true, exitCode, $"{action} cancelled", log),
        1618 => new(false, false, false, exitCode, $"{action} not started: another installation is running; try again when it ends", log),
        _ => new(false, false, false, exitCode, $"{action} failed (Windows Installer {exitCode}); its log says why", log),
    };

    private async Task<SetupOutcome> RunInstallerAsync(string verb, string action, CancellationToken ct)
    {
        if (!File.Exists(InstallerPath) || !await MatchesAsync(InstallerPath, ct))
            throw new InvalidOperationException("the pinned installer is not here; fetch it first");
        var log = Path.Combine(_folder, $"{action}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        var start = new ProcessStartInfo("msiexec.exe", $"{verb} \"{InstallerPath}\" /qn /norestart /l*v \"{log}\"")
        {
            // Through the shell so Windows asks for administrator: installing a driver needs it, and nothing else of
            // Sallyport does.
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        Process? installer;
        try { installer = Process.Start(start); }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { return Interpret(1223, action, null); }
        if (installer is null) return new(false, false, false, -1, $"{action} could not be started", null);
        using (installer)
        {
            await installer.WaitForExitAsync(ct);
            return Interpret(installer.ExitCode, action, File.Exists(log) ? log : null);
        }
    }

    internal static async Task<bool> MatchesAsync(string path, CancellationToken ct, string expected = PacketFilterRelease.InstallerSha256)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, ct)) == expected;
    }

    private static ServiceState QueryService()
    {
        var manager = Native.OpenSCManager(null, null, Native.SC_MANAGER_CONNECT);
        if (manager == IntPtr.Zero) return ServiceState.Unknown;
        try
        {
            var service = Native.OpenService(manager, PacketFilterRelease.ServiceName, Native.SERVICE_QUERY_STATUS);
            if (service == IntPtr.Zero)
                return Marshal.GetLastPInvokeError() == Native.ERROR_SERVICE_DOES_NOT_EXIST ? ServiceState.NotInstalled : ServiceState.Unknown;
            try
            {
                if (!Native.QueryServiceStatus(service, out var status)) return ServiceState.Unknown;
                return status.CurrentState switch
                {
                    1 => ServiceState.Stopped,
                    2 => ServiceState.Starting,
                    3 => ServiceState.Stopping,
                    4 => ServiceState.Running,
                    _ => ServiceState.Other,
                };
            }
            finally { Native.CloseServiceHandle(service); }
        }
        finally { Native.CloseServiceHandle(manager); }
    }
}
