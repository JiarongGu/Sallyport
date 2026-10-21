using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Win32;
using Sallyport.Setup;
using Sallyport.Windows;
using Sallyport.Windows.PacketFilter;
using Sallyport.Windows.Service;

/// <summary>What the person chose: the packet driver (needed to route games) and their own data on removal.</summary>
sealed record SetupChoices(bool Driver, bool RemoveData);

/// <summary>What setup left, so removal takes away only what setup put there: the driver above all, which Sallyport
/// removes only if it installed it.</summary>
sealed record SetupRecord(string Version, bool InstalledDriver, DateTimeOffset At);

/// <summary>The record's JSON made when it is built, not by reflection: the uninstaller is compiled ahead of time (D32),
/// where reflection cannot be relied on. The same shape as before, so a record an earlier setup wrote reads.</summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(SetupRecord))]
partial class SetupJson : System.Text.Json.Serialization.JsonSerializerContext;

/// <summary>Setup as it goes: a step begun (<see cref="Text"/>) or only further along, and how much of the whole is done.</summary>
sealed record SetupProgress(string? Text, double Done);

/// <summary>
/// Installing and removing Sallyport (docs/design/product.md §6), every step said as it is taken — the order of the
/// steps here, each step in the part of it that knows how: the folder swapped in whole (Installer.Swap.cs), the folders'
/// rights (Installer.Rights.cs), Windows' entries for it (Installer.Registration.cs), and what removal takes
/// (Installer.Removal.cs). The order keeps a half-done run harmless: the new files are staged beside the old and swapped
/// in only whole, the service is registered after the files it runs, and the shortcuts and the uninstall entry last.
/// Everything Sallyport writes goes in the folder the person chose — its data (logs, the driver's installer) in a
/// <c>data</c> folder inside it; only Windows' own entries (the service, the Start menu, the installed-apps list) live
/// where Windows keeps them. What did not happen but leaves Sallyport working (the driver, a file in use) is returned,
/// not thrown.
/// </summary>
sealed partial class Installer
{
    private readonly Action<SetupProgress> _report;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly string _folder;
    private readonly List<string> _left = [];

    /// <summary>Every step is also written, timed, to %TEMP%\Sallyport-setup.log while setup runs — what a failed or slow
    /// run did — and an install that ends moves it to its own <c>data\logs\setup.log</c> (D19).</summary>
    public static string TempLog => Path.Combine(Path.GetTempPath(), "Sallyport-setup.log");

    /// <summary>Where this run's steps are written now.</summary>
    public string LogFile { get; private set; } = TempLog;

    public Installer(Action<SetupProgress> report, string folder)
    {
        _report = report;
        _folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        Log($"setup {Environment.ProcessPath} started, folder {_folder}");
    }

    private void say(string line, double done)
    {
        Log(line);
        _report(new SetupProgress(line, done));
    }

    // Done, but not all of it: said now, and returned so the end says it again.
    private void leave(string line)
    {
        Log(line);
        _left.Add(line);
    }

    private void Log(string line)
    {
        try { File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} +{_clock.Elapsed.TotalSeconds,6:F1}s {line}{Environment.NewLine}"); }
        catch (IOException) { }
    }

    public const string Name = "Sallyport";

    /// <summary>Sallyport's one program (D30): the window, the service and its routers, and this setup's removal.</summary>
    public const string Program = "Sallyport.Client.exe";

    /// <summary>WebView2's loader, beside the program rather than inside it (which would unpack it to %TEMP%).</summary>
    public const string Loader = "WebView2Loader.dll";

    /// <summary>The small program that removes Sallyport (D32; the owner: "after install the setup/uninstall should be a
    /// small exe"), beside the program; every way to remove Sallyport starts it.</summary>
    public const string Uninstaller = "uninstall.exe";

    /// <summary>The start that asked for this one to be administrator, and waits for it (<c>--asker</c>): not a window
    /// to close.</summary>
    public static int Asker { get; set; }

    /// <summary>Where a first install goes unless the person chooses otherwise.</summary>
    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Name);

    /// <summary>Where Sallyport is installed now, as its uninstall entry says; null when it is not.</summary>
    public static string? ExistingFolder() =>
        Registry.LocalMachine.OpenSubKey(UninstallKey)?.GetValue("InstallLocation") is string folder && IsSallyport(folder) ? folder : null;

    /// <summary>The version installed now, as its uninstall entry says.</summary>
    public static string? ExistingVersion() => Registry.LocalMachine.OpenSubKey(UninstallKey)?.GetValue("DisplayVersion") as string;

    /// <summary>Why <paramref name="folder"/> cannot take Sallyport, or null when it can: a folder (not a whole drive,
    /// not Windows' own) on a fixed NTFS drive of this computer — the only kind that can be kept for administrators —
    /// and empty, new, Sallyport already, or what a removal kept of it: setup replaces a folder whole, so it never takes
    /// one that holds anything else.</summary>
    public static string? Unfit(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder)) return L.T("Type a full folder path, such as D:\\Program Files\\Sallyport.");
        // Windows' own command line, which removes what is in use once setup has exited, would read %NAME% in it as a
        // variable to put in its place.
        if (folder.Contains('%')) return L.T("Choose a folder whose path has no % in it.");
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return L.T("That is not a folder path Windows accepts."); }
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) return L.T("Choose a folder on this computer, not on a network share.");
        var root = Path.GetPathRoot(full)!;
        if (Path.TrimEndingDirectorySeparator(root).Equals(full, StringComparison.OrdinalIgnoreCase)) return L.T("Choose a folder, not a whole drive.");
        var drive = new DriveInfo(root);
        if (!drive.IsReady) return L.T("Drive {0} is not ready.", root);
        if (drive.DriveType != DriveType.Fixed) return L.T("Choose a folder on one of this computer's own drives; {0} is {1}.", root, drive.DriveType.ToString().ToLowerInvariant());
        if (drive.DriveFormat is not ("NTFS" or "ReFS"))
            return L.T("Choose a folder on an NTFS drive: {0} is {1}, which cannot keep a folder for administrators only.", root, drive.DriveFormat);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (full.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) return L.T("Choose a folder outside Windows' own.");
        if (File.Exists(full)) return L.T("That is a file, not a folder.");
        if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any() && !IsSallyport(full) && !HoldsKeptData(full))
            return L.T("That folder already holds other files. Choose an empty folder or a new one.");
        return null;
    }

    /// <summary>Bytes free on the drive <paramref name="folder"/> is on; null when that cannot be read.</summary>
    public static long? FreeBytes(string folder)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace; }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>What the payload takes once unpacked.</summary>
    public static long UnpackedBytes(Stream payload)
    {
        using var zip = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: true);
        return zip.Entries.Sum(e => e.Length);
    }

    private static bool IsSallyport(string folder) => File.Exists(Path.Combine(folder, Program));

    /// <summary>Whether <paramref name="folder"/> holds only what removing Sallyport kept — the profiles, settings and
    /// apps, or an earlier install's users' folders — which installing there again takes up.</summary>
    public static bool HoldsKeptData(string folder)
    {
        try
        {
            if (!Directory.Exists(folder) || Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).ToList() is not ["data" or "Data"]) return false;
            var kept = Directory.EnumerateFileSystemEntries(InstallLayout.Data(folder)).Select(Path.GetFileName).ToList();
            return kept.Count > 0 && kept.All(name => InstallLayout.Kept.Append("users").Contains(name!, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    public string Folder => _folder;
    private string Data => InstallLayout.Data(_folder);
    private string Users => InstallLayout.Users(_folder);
    // What removing keeps unless the person says otherwise: the shared areas, and an earlier install's users' folders.
    private IReadOnlyList<string> KeptData => [.. InstallLayout.Kept.Select(area => InstallLayout.Area(_folder, area)), Users];
    public string Client => Path.Combine(_folder, Program);

    /// <summary>Whether removing Sallyport takes the packet driver too: only if Sallyport put it there.</summary>
    public bool RemovesDriver => ReadRecord()?.InstalledDriver == true;

    /// <summary>The packet driver as Windows has it: present, absent, or not known (rule 6).</summary>
    public static ServiceState DriverState() => new PacketFilterSetup(Path.GetTempPath()).Status().Service;

    /// <summary>Whether Windows has what the client needs: x64, Windows 10 1809 or later, and the WebView2 runtime
    /// (checked, not carried: product design change 5). Null when it does.</summary>
    public static string? Missing()
    {
        if (!Environment.Is64BitOperatingSystem || System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64)
            return L.T("Sallyport needs 64-bit Windows on an x64 processor.");
        if (Environment.OSVersion.Version.Build < 17763) return L.T("Sallyport needs Windows 10 version 1809 or later.");
        // The Evergreen WebView2 runtime registers its version under EdgeUpdate's client key, machine-wide or per user.
        const string WebView2 = @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        var version = Registry.LocalMachine.OpenSubKey(WebView2)?.GetValue("pv") as string
            ?? Registry.CurrentUser.OpenSubKey(WebView2.Replace(@"WOW6432Node\", ""))?.GetValue("pv") as string;
        if (string.IsNullOrEmpty(version) || version == "0.0.0.0")
            return L.T("Sallyport's window needs Microsoft's WebView2 Runtime, which this computer does not have. Install it from {0}, then run this setup again.", WebView2Download);
        return null;
    }

    public const string WebView2Download = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    public async Task<IReadOnlyList<string>> InstallAsync(Stream payload, string version, SetupChoices choices, CancellationToken ct)
    {
        say(L.T("Closing Sallyport…"), 0.01);
        CloseClients();
        say(L.T("Stopping the Sallyport service…"), 0.04);
        StopService();
        var staged = _folder + ".new";
        try { return await InstallStoppedAsync(payload, version, choices, staged, ct); }
        catch (Exception e)
        {
            // A failed install leaves the Sallyport that was there as it was: its service running again (stopped above,
            // it would stay stopped until someone started it), nothing half-made beside it, and why in the log — a
            // quiet run has no window to say it in.
            Log($"failed: {e.GetType().Name}: {e.Message}");
            try { if (Directory.Exists(staged)) Directory.Delete(staged, recursive: true); }
            catch (Exception left) when (left is IOException or UnauthorizedAccessException) { Log($"{staged} was not removed: {left.Message}"); }
            try { if (ServiceControl.Status(RouterProtocol.ServiceName).State == ServiceState.Stopped) ServiceControl.Start(RouterProtocol.ServiceName); }
            catch (Win32Exception started) { Log($"the service was not started again: {started.Message}"); }
            throw;
        }
    }

    private async Task<IReadOnlyList<string>> InstallStoppedAsync(Stream payload, string version, SetupChoices choices, string staged, CancellationToken ct)
    {
        // Before anything else: what an interrupted setup left aside is the person's install, and their data in it.
        RecoverInterrupted();
        if (Unfit(_folder) is { } why) throw new SetupFailed(why);
        say(L.T("Copying files…"), 0.08);
        SwapIn(payload, staged);

        Directory.CreateDirectory(Data);
        // Moved first, then given the areas' rights: what moves keeps the rights its owner's client gave it until then.
        MoveMineIn();
        ShareAreas();
        // The language setup ran in is the client's until a person chooses one in it.
        File.WriteAllText(InstallLayout.Language(_folder), L.Code);

        say(L.T("Registering the Sallyport service…"), 0.72);
        ServiceControl.Install(RouterProtocol.ServiceName, "Sallyport game routing",
            "Routes the programs you run in Sallyport through your VPN. Idle until you run one.", Client, RouterProtocol.ServiceArgument);
        ServiceControl.Start(RouterProtocol.ServiceName);

        var record = ReadRecord();
        var installedDriver = record?.InstalledDriver ?? false;
        if (choices.Driver)
        {
            var driver = new PacketFilterSetup(Path.Combine(Data, "drivers"));
            if (driver.Status().Service is ServiceState.NotInstalled or ServiceState.Unknown)
                try
                {
                    say(L.T("Fetching the packet driver from NT Kernel…"), 0.76);
                    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
                    await driver.FetchAsync(http, ct);
                    say(L.T("Installing the packet driver (the network pauses for a few seconds)…"), 0.84);
                    // Its own files beside Sallyport's folder, not in the system drive's Program Files (D19), in a folder
                    // given Sallyport's rights first: off the system drive it would inherit rights that let users change them.
                    Protect(InstallLayout.DriverFolder(_folder));
                    var outcome = await driver.InstallAsync(InstallLayout.DriverFolder(_folder), ct);
                    if (outcome.Succeeded) installedDriver = true;
                    else leave(L.T("The packet driver was not installed: {0}. Install it later from Sallyport's Settings.", outcome.Text));
                    if (outcome.RebootNeeded) leave(L.T("Windows finishes installing the packet driver when it next restarts."));
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or IOException)
                {
                    leave(L.T("The packet driver could not be fetched from NT Kernel ({0}). Install it later from Sallyport's Settings.", e.GetType().Name));
                }
        }
        WriteRecord(new SetupRecord(version, installedDriver, DateTimeOffset.Now));

        say(L.T("Adding Sallyport to the Start menu…"), 0.95);
        AddToStartMenu();
        RegisterOpenWith();
        RegisterUninstall(version);
        say(L.T("Done."), 1);
        KeepLog();
        return _left;
    }

    // The steps of an install that ended go with everything else Sallyport keeps (D19): %TEMP% keeps nothing of it.
    private void KeepLog()
    {
        var kept = Path.Combine(Data, "logs", "setup.log");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
            File.AppendAllText(kept, File.ReadAllText(TempLog));
            File.Delete(TempLog);
            LogFile = kept;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public async Task<IReadOnlyList<string>> UninstallAsync(SetupChoices choices, CancellationToken ct)
    {
        say(L.T("Closing Sallyport…"), 0.02);
        CloseClients();
        say(L.T("Removing the Sallyport service…"), 0.10);
        // Removing it stops it, and its routing with it: the adapter is given back before the service goes. A service
        // that would not stop is still Sallyport running from its folder: nothing else is removed, so the uninstall
        // entry is still there to try again.
        try { ServiceControl.Uninstall(RouterProtocol.ServiceName, TimeSpan.FromSeconds(30)); }
        catch (Win32Exception e) { throw new SetupFailed(L.T("The Sallyport service did not stop (Windows error {0}), so nothing was removed. Restart Windows, then remove Sallyport again.", e.NativeErrorCode)); }

        if (RemovesDriver)
        {
            var driver = new PacketFilterSetup(Path.Combine(Data, "drivers"));
            say(L.T("Removing the packet driver Sallyport installed (the network pauses for a few seconds)…"), 0.30);
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
                await driver.FetchAsync(http, ct);
                var outcome = await driver.UninstallAsync(ct);
                if (!outcome.Succeeded) leave(L.T("The packet driver was not removed: {0}.", outcome.Text));
                if (outcome.RebootNeeded) leave(L.T("Windows finishes removing the packet driver when it next restarts."));
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or IOException)
            {
                leave(L.T("The packet driver was not removed: its installer could not be fetched ({0}). Remove \"Windows Packet Filter\" from Windows' installed apps.", e.GetType().Name));
            }
        }

        say(L.T("Removing Sallyport from the Start menu and from Windows' lists…"), 0.70);
        RemoveFromWindows();

        say(L.T("Removing files…"), 0.80);
        RemoveFiles(choices.RemoveData);
        if (choices.RemoveData)
        {
            say(L.T("Removing the VPN profiles and settings…"), 0.92);
            // An earlier client kept them in the person's own %LOCALAPPDATA%.
            DeleteFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name));
        }
        say(L.T("Done."), 1);
        return _left;
    }

    // Stopped is what the service manager says; the service's process may still be closing its files a moment after,
    // and a folder with a file open in it cannot be moved.
    private static void StopService()
    {
        try
        {
            var (_, processId) = ServiceControl.Status(RouterProtocol.ServiceName);
            ServiceControl.Stop(RouterProtocol.ServiceName, TimeSpan.FromSeconds(20));
            if (processId != 0)
            {
                using var process = Process.GetProcessById(processId);
                process.WaitForExit(10_000);
            }
        }
        // Not installed, or already gone.
        catch (Exception e) when (e is Win32Exception or ArgumentException or InvalidOperationException) { }
    }

    // Every client of this user is asked to leave as its own tray's Exit does (routing stopped, tunnels closed), and any
    // left after a few seconds is ended.
    private void CloseClients()
    {
        // Run only from the folder an earlier setup registered and made administrators' alone: a program found in a
        // folder someone else made before this first install would run as administrator here.
        if (ExistingFolder() is { } registered && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(registered)), _folder, StringComparison.OrdinalIgnoreCase))
            try
            {
                using var exit = Process.Start(new ProcessStartInfo(Client, "--exit") { UseShellExecute = false });
                exit?.WaitForExit(5000);
            }
            catch (Win32Exception) { }
        var deadline = Environment.TickCount64 + 8000;
        // A person's windows, not the service and its routers (the same program, D30, in session 0 — stopped as the
        // service, never killed under the service manager, which would start it again), nor this setup and the start
        // that asked for it to be administrator.
        while (true)
        {
            var all = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Program));
            var left = all.Where(p => p.SessionId != 0 && p.Id != Environment.ProcessId && p.Id != Asker).ToArray();
            var done = left.Length == 0 || Environment.TickCount64 > deadline;
            if (left.Length > 0 && done)
                foreach (var p in left)
                    try { p.Kill(); } catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
            foreach (var p in all) p.Dispose();
            if (done) break;
            Thread.Sleep(200);
        }
    }
}

sealed class SetupFailed(string message) : Exception(message);
