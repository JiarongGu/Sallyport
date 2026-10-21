using System.Text.Json;
using Microsoft.Win32;
using Sallyport.Setup;

/// <summary>What Windows keeps of Sallyport where Windows keeps it — the Start menu's folder, Open with for .ovpn, the
/// installed-apps entry — and setup's own record of what it put there.</summary>
sealed partial class Installer
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Sallyport";
    private const string ProgId = "Sallyport.ovpn";

    private string Record => Path.Combine(Data, "setup.json");
    private static string Programs => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs");
    // A folder of its own in the Start menu: Sallyport, and its removal beside it where a person looks for one (the
    // owner: "we also missing an uninstall after the installation"). 0.1.0 put Sallyport at the top, alone.
    private static string MenuFolder => Path.Combine(Programs, Name);
    private static string Shortcut => Path.Combine(MenuFolder, "Sallyport.lnk");
    private static string RemovalShortcut => Path.Combine(MenuFolder, L.T("Uninstall Sallyport") + ".lnk");
    private static string OldShortcut => Path.Combine(Programs, "Sallyport.lnk");

    // What removes Sallyport: the small uninstaller once it is here, else the program itself (D30), as an install from a
    // payload without one has it.
    private (string Program, string Arguments) Remover =>
        File.Exists(Path.Combine(_folder, Uninstaller)) ? (Path.Combine(_folder, Uninstaller), "") : (Client, "/uninstall");

    private void AddToStartMenu()
    {
        try { File.Delete(OldShortcut); } catch (IOException) { }
        ShellLink.Create(Shortcut, Client, "", L.T("Send your games and apps through your VPN"));
        ShellLink.Create(RemovalShortcut, Remover.Program, Remover.Arguments, L.T("Remove Sallyport from this computer"));
    }

    private void RemoveFromWindows()
    {
        try { File.Delete(OldShortcut); } catch (IOException) { }
        try { if (Directory.Exists(MenuFolder)) Directory.Delete(MenuFolder, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { leave(L.T("The Start menu's Sallyport folder could not be removed: {0}", e.Message)); }
        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        Registry.LocalMachine.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
        Registry.LocalMachine.OpenSubKey(@"Software\Classes\.ovpn\OpenWithProgids", writable: true)?.DeleteValue(ProgId, throwOnMissingValue: false);
        Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)?.DeleteValue(Name, throwOnMissingValue: false);
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
        // Not the cache: what it holds is thrown away, an update's own download among it while that update installs
        // (Windows said 75 MB after the update to 0.1.1; without the cache, 34). Nor what a link points at.
        var cache = Path.Combine(Data, "cache") + Path.DirectorySeparatorChar;
        var kilobytes = Within(new DirectoryInfo(_folder)).OfType<FileInfo>()
            .Where(f => !f.Attributes.HasFlag(FileAttributes.ReparsePoint) && !f.FullName.StartsWith(cache, StringComparison.OrdinalIgnoreCase))
            .Sum(f => f.Length) / 1024;
        key.SetValue("EstimatedSize", (int)Math.Min(kilobytes, int.MaxValue), RegistryValueKind.DWord);
    }

    private SetupRecord? ReadRecord()
    {
        try { return File.Exists(Record) ? JsonSerializer.Deserialize(File.ReadAllBytes(Record), SetupJson.Default.SetupRecord) : null; }
        catch (JsonException) { return null; }
    }

    private void WriteRecord(SetupRecord record) => File.WriteAllBytes(Record, JsonSerializer.SerializeToUtf8Bytes(record, SetupJson.Default.SetupRecord));
}
