using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
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
/// Installing and removing Sallyport (docs/design/product.md §6), every step said as it is taken. The order keeps a
/// half-done run harmless: the new files are staged beside the old and swapped in only whole, the service is
/// registered after the files it runs, and the shortcuts and the uninstall entry last. Everything Sallyport writes goes
/// in the folder the person chose — its data (logs, the driver's installer) in a <c>data</c> folder inside it; only
/// Windows' own entries (the service, the Start menu, the installed-apps list) live where Windows keeps them. What did
/// not happen but leaves Sallyport working (the driver, a file in use) is returned, not thrown.
/// </summary>
sealed class Installer
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
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Sallyport";
    private const string ProgId = "Sallyport.ovpn";

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
    private string Record => Path.Combine(Data, "setup.json");
    private static string Programs => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs");
    // A folder of its own in the Start menu: Sallyport, and its removal beside it where a person looks for one (the
    // owner: "we also missing an uninstall after the installation"). 0.1.0 put Sallyport at the top, alone.
    private static string MenuFolder => Path.Combine(Programs, Name);
    private static string Shortcut => Path.Combine(MenuFolder, "Sallyport.lnk");
    private static string RemovalShortcut => Path.Combine(MenuFolder, L.T("Uninstall Sallyport") + ".lnk");
    private static string OldShortcut => Path.Combine(Programs, "Sallyport.lnk");
    public string Client => Path.Combine(_folder, Program);

    // What removes Sallyport: the small uninstaller once it is here, else the program itself (D30), as an install from a
    // payload without one has it.
    private (string Program, string Arguments) Remover =>
        File.Exists(Path.Combine(_folder, Uninstaller)) ? (Path.Combine(_folder, Uninstaller), "") : (Client, "/uninstall");

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
        if (Unfit(_folder) is { } why) throw new SetupFailed(why);
        say(L.T("Copying files…"), 0.08);
        var previous = _folder + ".old";
        // The folder it goes in is not setup's to protect: only Sallyport's own is.
        Directory.CreateDirectory(Path.GetDirectoryName(_folder)!);
        if (Directory.Exists(staged)) Directory.Delete(staged, recursive: true);
        // Protected before anything is put in it: off the system drive a new folder lets every user write, and the
        // service believes only clients that run from a folder ordinary users cannot change (product design change 2).
        Protect(staged);
        Unpack(payload, staged, 0.08, 0.70);
        if (Directory.Exists(previous)) Directory.Delete(previous, recursive: true);
        var hadPrevious = Directory.Exists(_folder);
        if (hadPrevious) MoveWhenFree(_folder, previous);
        try { Directory.Move(staged, _folder); }
        catch
        {
            // The old files back as they were: the machine keeps the Sallyport it had.
            if (hadPrevious && !Directory.Exists(_folder)) Directory.Move(previous, _folder);
            throw;
        }
        Protect(_folder);
        // The old version's data (its record above all) goes with the new files: a later removal still knows the driver.
        if (hadPrevious && Directory.Exists(Path.Combine(previous, "data")) && !Directory.Exists(Data))
            Directory.Move(Path.Combine(previous, "data"), Data);
        try { if (hadPrevious) Directory.Delete(previous, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { leave(L.T("The old version's files could not all be removed from {0}.", previous)); }

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
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or IOException)
                {
                    leave(L.T("The packet driver could not be fetched from NT Kernel ({0}). Install it later from Sallyport's Settings.", e.GetType().Name));
                }
        }
        WriteRecord(new SetupRecord(version, installedDriver, DateTimeOffset.Now));

        say(L.T("Adding Sallyport to the Start menu…"), 0.95);
        try { File.Delete(OldShortcut); } catch (IOException) { }
        ShellLink.Create(Shortcut, Client, "", L.T("Send your games and apps through your VPN"));
        ShellLink.Create(RemovalShortcut, Remover.Program, Remover.Arguments, L.T("Remove Sallyport from this computer"));
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
        // Removing it stops it, and its routing with it: the adapter is given back before the service goes.
        try { ServiceControl.Uninstall(RouterProtocol.ServiceName, TimeSpan.FromSeconds(30)); }
        catch (Win32Exception e) { leave(L.T("The Sallyport service was not removed (Windows error {0}).", e.NativeErrorCode)); }

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
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or IOException)
            {
                leave(L.T("The packet driver was not removed: its installer could not be fetched ({0}). Remove \"Windows Packet Filter\" from Windows' installed apps.", e.GetType().Name));
            }
        }

        say(L.T("Removing Sallyport from the Start menu and from Windows' lists…"), 0.70);
        try { File.Delete(OldShortcut); } catch (IOException) { }
        try { if (Directory.Exists(MenuFolder)) Directory.Delete(MenuFolder, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { leave(L.T("The Start menu's Sallyport folder could not be removed: {0}", e.Message)); }
        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        Registry.LocalMachine.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
        Registry.LocalMachine.OpenSubKey(@"Software\Classes\.ovpn\OpenWithProgids", writable: true)?.DeleteValue(ProgId, throwOnMissingValue: false);
        Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)?.DeleteValue(Name, throwOnMissingValue: false);

        say(L.T("Removing files…"), 0.80);
        // Only a folder that is still Sallyport's: whatever else someone put there since is not setup's to take. Its
        // users' profiles and settings stay unless the person said otherwise, where installing again finds them.
        var keep = choices.RemoveData ? null : KeptData;
        if (IsSallyport(_folder))
        {
            // This uninstaller may run from the folder it removes: what is in use goes once it has exited.
            if (Path.GetDirectoryName(Environment.ProcessPath)!.Equals(_folder, StringComparison.OrdinalIgnoreCase)) RemoveAfterExit(_folder, keep);
            else RemoveFolder(_folder, keep);
        }
        if (choices.RemoveData)
        {
            say(L.T("Removing the VPN profiles and settings…"), 0.92);
            // An earlier client kept them in the person's own %LOCALAPPDATA%.
            DeleteFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name));
        }
        say(L.T("Done."), 1);
        return _left;
    }

    // Unpacked entry by entry so the window can show how far it is: the payload is most of setup's time.
    private void Unpack(Stream payload, string folder, double from, double to)
    {
        using var zip = new ZipArchive(payload, ZipArchiveMode.Read);
        var total = Math.Max(1, zip.Entries.Sum(e => e.Length));
        var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        long done = 0;
        foreach (var entry in zip.Entries)
        {
            var path = Path.GetFullPath(Path.Combine(folder, entry.FullName));
            // A payload names only paths inside the folder it is unpacked into.
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"The payload names a path outside its folder: {entry.FullName}");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(path);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, overwrite: true);
            done += entry.Length;
            _report(new SetupProgress(null, from + (to - from) * done / total));
        }
    }

    // Every client of this user is asked to leave as its own tray's Exit does (routing stopped, tunnels closed), and any
    // left after a few seconds is ended.
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

    // The old folder moved aside once nothing has a file open in it. WebView2's processes outlive the client by a few
    // seconds and keep their cache in data (measured 2026-10-20: still running 3 s after the client had gone, and the
    // folder could not be moved until the last of them had); an indexer or a scanner may hold a file a moment too.
    private void MoveWhenFree(string from, string to)
    {
        var deadline = Environment.TickCount64 + 30_000;
        for (var waiting = false; ; Thread.Sleep(500))
            try
            {
                Directory.Move(from, to);
                if (waiting) Log($"{from} moved aside once free");
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (Environment.TickCount64 > deadline)
                    throw new SetupFailed(L.T("A file in {0} is still open in another program. Close it, or restart Windows, and run setup again.", from));
                if (!waiting) Log($"{from} is in use ({e.Message}); waiting for it");
                waiting = true;
            }
    }

    private void CloseClients()
    {
        if (File.Exists(Client))
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
        while (Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Program))
                   .Where(p => p.SessionId != 0 && p.Id != Environment.ProcessId && p.Id != Asker).ToArray() is { Length: > 0 } left)
        {
            if (Environment.TickCount64 > deadline)
            {
                foreach (var p in left)
                    try { p.Kill(); } catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
                break;
            }
            Thread.Sleep(200);
        }
    }

    // The shared areas (D25): everyone who uses Sallyport on this computer reads and writes them — the owner's "One
    // shared folder". Owned by Administrators, so whoever made one first keeps no right over its rights. Nothing in
    // them is ever run: the programs stay in the folder only administrators can change (product design change 2).
    private void ShareAreas()
    {
        var security = new DirectorySecurity();
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var (sid, rights) in new[]
                 {
                     (WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl),
                     (WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl),
                     (WellKnownSidType.BuiltinUsersSid, FileSystemRights.Modify),
                 })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), rights,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        foreach (var area in InstallLayout.Areas)
        {
            var info = new DirectoryInfo(InstallLayout.Area(_folder, area));
            if (!info.Exists) info.Create(security);
            info.SetAccessControl(security);
            // What was moved in, or made by an earlier client, takes the area's rights from here on.
            foreach (var inside in info.EnumerateFileSystemInfos("*", SearchOption.AllDirectories)) Inherit(inside);
        }
    }

    // The person running setup kept their part in a folder of their own (D19): it moves into the shared areas, area by
    // area, into one still empty — two sets of settings are never merged. The window's cache is made again. Another
    // person's folder stays where it is, as it was.
    private void MoveMineIn()
    {
        if (System.Security.Principal.WindowsIdentity.GetCurrent().User is not { } me) return;
        var mine = Path.Combine(Users, me.Value);
        if (!Directory.Exists(mine)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(mine).ToList())
        {
            var name = Path.GetFileName(entry);
            var into = Path.Combine(Data, name);
            try
            {
                if (Directory.Exists(entry) && !InstallLayout.Areas.Contains(name, StringComparer.OrdinalIgnoreCase) && name is not "games")
                    Directory.Delete(entry, recursive: true);
                else if (Directory.Exists(entry) && (!Directory.Exists(into) || !Directory.EnumerateFileSystemEntries(into).Any()))
                {
                    if (Directory.Exists(into)) Directory.Delete(into);
                    Directory.Move(entry, into);
                    // An earlier client's games, not an area: read once, for its migration, with data's own rights.
                    if (name is "games") foreach (var inside in new DirectoryInfo(into).EnumerateFileSystemInfos("*", SearchOption.AllDirectories).Prepend(new DirectoryInfo(into))) Inherit(inside);
                }
                else if (File.Exists(entry)) File.Delete(entry);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                leave(L.T("{0} could not be moved into {1} ({2}).", entry, Data, e.GetType().Name));
            }
        }
        try
        {
            if (!Directory.EnumerateFileSystemEntries(mine).Any()) Directory.Delete(mine);
            if (!Directory.EnumerateFileSystemEntries(Users).Any()) Directory.Delete(Users);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void Inherit(FileSystemInfo entry)
    {
        if (entry is DirectoryInfo directory)
        {
            var security = directory.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier))) security.RemoveAccessRuleSpecific(rule);
            directory.SetAccessControl(security);
        }
        else if (entry is FileInfo file)
        {
            var security = file.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier))) security.RemoveAccessRuleSpecific(rule);
            file.SetAccessControl(security);
        }
    }

    // Written by the system and administrators only, read and run by users, inherited by everything inside, whatever the
    // drive's own rights would have given it. Owned by Administrators: whoever made the folder first (anyone may, where
    // the drive lets users create folders) keeps no right to change it back.
    internal static void Protect(string folder)
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var security = new DirectorySecurity();
        security.SetOwner(administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var (sid, rights) in new[]
                 {
                     (WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl),
                     (WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl),
                     (WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute),
                 })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), rights,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var info = new DirectoryInfo(folder);
        if (!info.Exists) info.Create(security);
        info.SetAccessControl(security);
    }

    // .ovpn files gain "Sallyport" under Open with, without taking them from whatever opens them now.
    private void RegisterOpenWith()
    {
        using (var prog = Registry.LocalMachine.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            prog.SetValue("", "OpenVPN profile");
            using (var icon = prog.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{Client}\",0");
            using var command = prog.CreateSubKey(@"shell\open\command");
            command.SetValue("", $"\"{Client}\" \"%1\"");
        }
        using var with = Registry.LocalMachine.CreateSubKey(@"Software\Classes\.ovpn\OpenWithProgids");
        with.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
    }

    private void RegisterUninstall(string version)
    {
        using var key = Registry.LocalMachine.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", "Sallyport");
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", "Sallyport");
        key.SetValue("InstallLocation", _folder);
        key.SetValue("DisplayIcon", $"\"{Client}\",0");
        var (remover, arguments) = Remover;
        key.SetValue("UninstallString", $"\"{remover}\" {arguments}".TrimEnd());
        key.SetValue("QuietUninstallString", $"\"{remover}\" {arguments} /quiet".Replace("  ", " "));
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        var kilobytes = new DirectoryInfo(_folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1024;
        key.SetValue("EstimatedSize", (int)Math.Min(kilobytes, int.MaxValue), RegistryValueKind.DWord);
    }

    private SetupRecord? ReadRecord()
    {
        try { return File.Exists(Record) ? JsonSerializer.Deserialize(File.ReadAllBytes(Record), SetupJson.Default.SetupRecord) : null; }
        catch (JsonException) { return null; }
    }

    private void WriteRecord(SetupRecord record) => File.WriteAllBytes(Record, JsonSerializer.SerializeToUtf8Bytes(record, SetupJson.Default.SetupRecord));

    // Everything in the folder but what is kept: a folder inside it, and the folders that lead to it.
    private static IEnumerable<string> Removable(string folder, IReadOnlyList<string>? keep)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
        {
            if (keep is null || !keep.Any(k => Leads(entry, k))) yield return entry;
            else if (!keep.Any(k => Same(entry, k)))
                foreach (var inner in Removable(entry, keep)) yield return inner;
        }
    }

    private static bool Same(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    private static bool Leads(string entry, string keep) =>
        (Path.TrimEndingDirectorySeparator(keep) + Path.DirectorySeparatorChar).StartsWith(Path.TrimEndingDirectorySeparator(entry) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private void RemoveFolder(string folder, IReadOnlyList<string>? keep)
    {
        keep = keep?.Where(Directory.Exists).ToList();
        if (keep is null || keep.Count == 0)
        {
            DeleteFolder(folder);
            return;
        }
        foreach (var entry in Removable(folder, keep).ToList())
            try
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                else File.Delete(entry);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { leave(L.T("{0} could not be removed ({1}).", entry, e.GetType().Name)); }
    }

    private static void RemoveAfterExit(string folder, IReadOnlyList<string>? keep)
    {
        keep = keep?.Where(Directory.Exists).ToList() is { Count: > 0 } kept ? kept : null;
        var removable = Removable(folder, keep).ToList();
        foreach (var file in removable.Where(File.Exists).Concat(removable.Where(Directory.Exists).SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))))
            try { File.Delete(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        // A command of Windows' own, detached, waits a few seconds for this process to end and takes the rest.
        List<string> commands = keep is null
            ? [$"rd /s /q \"{folder}\""]
            : removable.Select(entry => Directory.Exists(entry) ? $"rd /s /q \"{entry}\"" : $"del /f /q \"{entry}\"").ToList();
        Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c ping -n 4 127.0.0.1 >nul & {string.Join(" & ", commands)}")
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    private void DeleteFolder(string folder)
    {
        if (!Directory.Exists(folder)) return;
        try { Directory.Delete(folder, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { leave(L.T("{0} could not all be removed ({1}).", folder, e.GetType().Name)); }
    }
}

sealed class SetupFailed(string message) : Exception(message);
