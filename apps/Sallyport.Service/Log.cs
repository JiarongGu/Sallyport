/// <summary>The service's log: what it decided and why, never a packet's contents nor a profile's (rule 2). Under the
/// service manager, a file in %ProgramData%\Sallyport\logs that only administrators and the system can write.</summary>
sealed class Log(Action<string> write)
{
    private const long MostBytes = 4 * 1024 * 1024;
    private static readonly Lock Gate = new();

    public void Say(string line) => write($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {line}");

    public static Log ToConsole() => new(Console.WriteLine);

    public static Log ToFile()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Sallyport", "logs");
        Protect(Path.GetDirectoryName(folder)!);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "service.log");
        return new Log(line =>
        {
            lock (Gate)
            {
                try
                {
                    // One earlier log kept: enough to see what led up to a failure, bounded either way.
                    if (File.Exists(path) && new FileInfo(path).Length > MostBytes) File.Move(path, path + ".1", overwrite: true);
                    File.AppendAllText(path, line + Environment.NewLine);
                }
                catch (IOException) { }
            }
        });
    }

    // %ProgramData% lets any user create folders in it, so Sallyport's own is given its rights here, inheritance cut:
    // the system and administrators write it, users may read it. Run as LocalSystem, this succeeds whoever made it first.
    private static void Protect(string folder)
    {
        var security = new System.Security.AccessControl.DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var (sid, rights) in new[]
                 {
                     (System.Security.Principal.WellKnownSidType.LocalSystemSid, System.Security.AccessControl.FileSystemRights.FullControl),
                     (System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, System.Security.AccessControl.FileSystemRights.FullControl),
                     (System.Security.Principal.WellKnownSidType.BuiltinUsersSid, System.Security.AccessControl.FileSystemRights.ReadAndExecute),
                 })
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(new System.Security.Principal.SecurityIdentifier(sid, null), rights,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
        var info = new DirectoryInfo(folder);
        if (!info.Exists) info.Create(security);
        else info.SetAccessControl(security);
    }
}
