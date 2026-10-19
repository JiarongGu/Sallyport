using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using Sallyport.Windows.PacketFilter;
using Sallyport.Windows.Service;

/// <summary>What the person chose: the packet driver (needed to route games) and their own data on removal.</summary>
sealed record SetupChoices(bool Driver, bool RemoveData);

/// <summary>What setup left, so removal takes away only what setup put there: the driver above all, which Sallyport
/// removes only if it installed it.</summary>
sealed record SetupRecord(string Version, bool InstalledDriver, DateTimeOffset At);

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

    /// <summary>Every step is also written, timed, to %TEMP%\Sallyport-setup.log: what a failed or slow run did.</summary>
    public static string LogPath => Path.Combine(Path.GetTempPath(), "Sallyport-setup.log");

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
        try { File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} +{_clock.Elapsed.TotalSeconds,6:F1}s {line}{Environment.NewLine}"); }
        catch (IOException) { }
    }

    public const string Name = "Sallyport";
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
    /// and empty, new, or Sallyport already: setup replaces a folder whole, so it never takes one that holds anything else.</summary>
    public static string? Unfit(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder)) return "Type a full folder path, such as D:\\Program Files\\Sallyport.";
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return "That is not a folder path Windows accepts."; }
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) return "Choose a folder on this computer, not on a network share.";
        var root = Path.GetPathRoot(full)!;
        if (Path.TrimEndingDirectorySeparator(root).Equals(full, StringComparison.OrdinalIgnoreCase)) return "Choose a folder, not a whole drive.";
        var drive = new DriveInfo(root);
        if (!drive.IsReady) return $"Drive {root} is not ready.";
        if (drive.DriveType != DriveType.Fixed) return $"Choose a folder on one of this computer's own drives; {root} is {drive.DriveType.ToString().ToLowerInvariant()}.";
        if (drive.DriveFormat is not ("NTFS" or "ReFS"))
            return $"Choose a folder on an NTFS drive: {root} is {drive.DriveFormat}, which cannot keep a folder for administrators only.";
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (full.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) return "Choose a folder outside Windows' own.";
        if (File.Exists(full)) return "That is a file, not a folder.";
        if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any() && !IsSallyport(full))
            return "That folder already holds other files. Choose an empty folder or a new one.";
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

    private static bool IsSallyport(string folder) =>
        File.Exists(Path.Combine(folder, "Sallyport.Client.exe")) && File.Exists(Path.Combine(folder, "Sallyport.Service.exe"));

    public string Folder => _folder;
    private string Data => Path.Combine(_folder, "data");
    private string Record => Path.Combine(Data, "setup.json");
    private static string Shortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs", "Sallyport.lnk");
    public string Client => Path.Combine(_folder, "Sallyport.Client.exe");

    /// <summary>Whether removing Sallyport takes the packet driver too: only if Sallyport put it there.</summary>
    public bool RemovesDriver => ReadRecord()?.InstalledDriver == true;

    /// <summary>The packet driver as Windows has it: present, absent, or not known (rule 6).</summary>
    public static ServiceState DriverState() => new PacketFilterSetup(Path.GetTempPath()).Status().Service;

    /// <summary>Whether Windows has what the client needs: x64, Windows 10 1809 or later, and the WebView2 runtime
    /// (checked, not carried: product design change 5). Null when it does.</summary>
    public static string? Missing()
    {
        if (!Environment.Is64BitOperatingSystem || System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64)
            return "Sallyport needs 64-bit Windows on an x64 processor.";
        if (Environment.OSVersion.Version.Build < 17763) return "Sallyport needs Windows 10 version 1809 or later.";
        // The Evergreen WebView2 runtime registers its version under EdgeUpdate's client key, machine-wide or per user.
        const string WebView2 = @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        var version = Registry.LocalMachine.OpenSubKey(WebView2)?.GetValue("pv") as string
            ?? Registry.CurrentUser.OpenSubKey(WebView2.Replace(@"WOW6432Node\", ""))?.GetValue("pv") as string;
        if (string.IsNullOrEmpty(version) || version == "0.0.0.0")
            return $"Sallyport's window needs Microsoft's WebView2 Runtime, which this computer does not have. Install it from {WebView2Download}, then run this setup again.";
        return null;
    }

    public const string WebView2Download = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    public async Task<IReadOnlyList<string>> InstallAsync(Stream payload, string version, SetupChoices choices, CancellationToken ct)
    {
        say("Closing Sallyport…", 0.01);
        CloseClients();
        say("Stopping the Sallyport service…", 0.04);
        try { ServiceControl.Stop(RouterProtocol.ServiceName, TimeSpan.FromSeconds(20)); }
        catch (Win32Exception) { }

        if (Unfit(_folder) is { } why) throw new SetupFailed(why);
        say("Copying files…", 0.08);
        var staged = _folder + ".new";
        var previous = _folder + ".old";
        // The folder it goes in is not setup's to protect: only Sallyport's own is.
        Directory.CreateDirectory(Path.GetDirectoryName(_folder)!);
        if (Directory.Exists(staged)) Directory.Delete(staged, recursive: true);
        // Protected before anything is put in it: off the system drive a new folder lets every user write, and the
        // service believes only clients that run from a folder ordinary users cannot change (product design change 2).
        Protect(staged);
        Unpack(payload, staged, 0.08, 0.70);
        // The payload carries a light uninstaller of its own, on the runtime installed beside the client; a payload
        // without one (a development folder) gets this setup instead.
        if (!File.Exists(Path.Combine(staged, "Sallyport.Setup.exe")))
            File.Copy(Environment.ProcessPath!, Path.Combine(staged, "Sallyport.Setup.exe"), overwrite: true);
        if (Directory.Exists(previous)) Directory.Delete(previous, recursive: true);
        var hadPrevious = Directory.Exists(_folder);
        if (hadPrevious) Directory.Move(_folder, previous);
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
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { leave($"The old version's files could not all be removed from {previous}."); }

        Directory.CreateDirectory(Data);

        say("Registering the Sallyport service…", 0.72);
        ServiceControl.Install(RouterProtocol.ServiceName, "Sallyport game routing",
            "Routes the programs you run in Sallyport through your VPN. Idle until you run one.", Path.Combine(_folder, "Sallyport.Service.exe"));
        ServiceControl.Start(RouterProtocol.ServiceName);

        var record = ReadRecord();
        var installedDriver = record?.InstalledDriver ?? false;
        if (choices.Driver)
        {
            var driver = new PacketFilterSetup(Path.Combine(Data, "drivers"));
            if (driver.Status().Service is ServiceState.NotInstalled or ServiceState.Unknown)
                try
                {
                    say("Fetching the packet driver from NT Kernel…", 0.76);
                    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
                    await driver.FetchAsync(http, ct);
                    say("Installing the packet driver (the network pauses for a few seconds)…", 0.84);
                    var outcome = await driver.InstallAsync(ct);
                    if (outcome.Succeeded) installedDriver = true;
                    else leave($"The packet driver was not installed: {outcome.Text}. Install it later from Sallyport's Settings.");
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or IOException)
                {
                    leave($"The packet driver could not be fetched from NT Kernel ({e.GetType().Name}). Install it later from Sallyport's Settings.");
                }
        }
        WriteRecord(new SetupRecord(version, installedDriver, DateTimeOffset.Now));

        say("Adding Sallyport to the Start menu…", 0.95);
        ShellLink.Create(Shortcut, Client, "", "Route your games and apps through your VPN");
        RegisterOpenWith();
        RegisterUninstall(version);
        say("Done.", 1);
        return _left;
    }

    public async Task<IReadOnlyList<string>> UninstallAsync(SetupChoices choices, CancellationToken ct)
    {
        say("Closing Sallyport…", 0.02);
        CloseClients();
        say("Removing the Sallyport service…", 0.10);
        // Removing it stops it, and its routing with it: the adapter is given back before the service goes.
        try { ServiceControl.Uninstall(RouterProtocol.ServiceName, TimeSpan.FromSeconds(30)); }
        catch (Win32Exception e) { leave($"The Sallyport service was not removed (Windows error {e.NativeErrorCode})."); }

        if (RemovesDriver)
        {
            var driver = new PacketFilterSetup(Path.Combine(Data, "drivers"));
            say("Removing the packet driver Sallyport installed (the network pauses for a few seconds)…", 0.30);
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
                await driver.FetchAsync(http, ct);
                var outcome = await driver.UninstallAsync(ct);
                if (!outcome.Succeeded) leave($"The packet driver was not removed: {outcome.Text}.");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or IOException)
            {
                leave($"The packet driver was not removed: its installer could not be fetched ({e.GetType().Name}). Remove \"Windows Packet Filter\" from Windows' installed apps.");
            }
        }

        say("Removing Sallyport from the Start menu and from Windows' lists…", 0.70);
        try { File.Delete(Shortcut); } catch (IOException) { }
        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        Registry.LocalMachine.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
        Registry.LocalMachine.OpenSubKey(@"Software\Classes\.ovpn\OpenWithProgids", writable: true)?.DeleteValue(ProgId, throwOnMissingValue: false);
        Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)?.DeleteValue(Name, throwOnMissingValue: false);

        say("Removing files…", 0.80);
        // Only a folder that is still Sallyport's: whatever else someone put there since is not setup's to take.
        if (IsSallyport(_folder))
        {
            DeleteFolder(Data);
            // This uninstaller may run from the folder it removes: what is in use goes once it has exited.
            if (Path.GetDirectoryName(Environment.ProcessPath)!.Equals(_folder, StringComparison.OrdinalIgnoreCase)) RemoveAfterExit(_folder);
            else DeleteFolder(_folder);
        }
        if (choices.RemoveData)
        {
            say("Removing your profiles and settings…", 0.92);
            DeleteFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name));
        }
        say("Done.", 1);
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
        while (Process.GetProcessesByName("Sallyport.Client") is { Length: > 0 } left)
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

    // Written by the system and administrators only, read and run by users, inherited by everything inside, whatever the
    // drive's own rights would have given it. Owned by Administrators: whoever made the folder first (anyone may, where
    // the drive lets users create folders) keeps no right to change it back.
    private static void Protect(string folder)
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
        key.SetValue("UninstallString", $"\"{Path.Combine(_folder, "Sallyport.Setup.exe")}\" /uninstall");
        key.SetValue("QuietUninstallString", $"\"{Path.Combine(_folder, "Sallyport.Setup.exe")}\" /uninstall /quiet");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        var kilobytes = new DirectoryInfo(_folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1024;
        key.SetValue("EstimatedSize", (int)Math.Min(kilobytes, int.MaxValue), RegistryValueKind.DWord);
    }

    private SetupRecord? ReadRecord()
    {
        try { return File.Exists(Record) ? JsonSerializer.Deserialize<SetupRecord>(File.ReadAllBytes(Record)) : null; }
        catch (JsonException) { return null; }
    }

    private void WriteRecord(SetupRecord record) => File.WriteAllBytes(Record, JsonSerializer.SerializeToUtf8Bytes(record));

    private static void RemoveAfterExit(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            try { File.Delete(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        // A command of Windows' own, detached, waits a few seconds for this process to end and takes the rest.
        Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c ping -n 4 127.0.0.1 >nul & rd /s /q \"{folder}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    private void DeleteFolder(string folder)
    {
        if (!Directory.Exists(folder)) return;
        try { Directory.Delete(folder, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { leave($"{folder} could not all be removed ({e.GetType().Name})."); }
    }
}

sealed class SetupFailed(string message) : Exception(message);
