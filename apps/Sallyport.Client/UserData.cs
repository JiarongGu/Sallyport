using System.Security.AccessControl;
using System.Security.Principal;
using Sallyport.Windows;

namespace Sallyport.Client;

/// <summary>
/// Where Sallyport keeps the people's part — sealed profiles, settings, apps, the window's cache (D19, D25). An installed
/// client keeps it in its own folder's <c>data</c>, in the areas setup shares with everyone on this computer
/// (<see cref="InstallLayout.Areas"/>); one setup has not laid out so yet keeps it in <c>data\users\&lt;the person's SID&gt;</c>
/// as D19 had it, and a development build in %LOCALAPPDATA%\Sallyport. What an earlier client kept in %LOCALAPPDATA%
/// is moved over once, the first time.
/// </summary>
internal static class UserData
{
    private static string Legacy => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sallyport");

    // Left in the new folder once the old one is copied, so nothing is copied twice. The old one is removed then, once:
    // a folder there later is a development build's, not this one's to take.
    private const string Moved = "moved-from-localappdata";

    // The window's cache: rebuilt by WebView2 when absent, and its files may still be held by the last window's helpers.
    private static readonly string[] NotMoved = ["webview2"];

    public static string Folder()
    {
        if (InstallLayout.InstalledHere() is not { } installed) return Legacy;
        // Setup moved the person's own folder in, %LOCALAPPDATA%'s earlier move with it: nothing more is moved here, and
        // nothing of data\ — the machine's part beside the areas — is ever the client's to empty.
        if (InstallLayout.Shared(installed) && Writable(InstallLayout.Area(installed, "config"))) return InstallLayout.Data(installed);
        var me = WindowsIdentity.GetCurrent().User!;
        var mine = Path.Combine(InstallLayout.Users(installed), me.Value);
        try
        {
            if (!Directory.Exists(mine)) Create(mine, me);
            // Any user may make folders here; one named for this person but made by someone else is not theirs to use.
            else if (!Trusted(mine, me)) return Legacy;
            MoveLegacy(mine, empty: !Directory.EnumerateFileSystemEntries(mine).Any());
            return mine;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Legacy;
        }
    }

    // Owned by the person and theirs alone (with the system and administrators, as their profile folder is), whoever
    // started this client: an elevated start would otherwise leave it owned by Administrators and shut to the person.
    private static void Create(string folder, SecurityIdentifier me)
    {
        var security = new DirectorySecurity();
        security.SetOwner(me);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { me, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(folder).Create(security);
    }

    private static bool Trusted(string folder, SecurityIdentifier me)
    {
        var owner = new DirectoryInfo(folder).GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        return owner is not null && (owner == me || owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
    }

    // Setup gave the areas a right to change them to every user; a folder this person cannot write is not one to use.
    private static bool Writable(string folder)
    {
        var probe = Path.Combine(folder, $".probe-{Environment.ProcessId}");
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static void MoveLegacy(string mine, bool empty)
    {
        var legacy = Legacy;
        if (!Directory.Exists(legacy)) return;
        var marker = Path.Combine(mine, Moved);
        // Only into a folder that holds nothing yet: two sets of settings are never merged.
        if (File.Exists(marker) || !empty) return;
        try
        {
            Copy(legacy, mine, top: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Half a copy is no copy: the old folder stays in use, and the move is tried again next time.
            foreach (var entry in Directory.EnumerateFileSystemEntries(mine))
                if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                else File.Delete(entry);
            throw;
        }
        File.WriteAllText(marker, legacy);
        try { Directory.Delete(legacy, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // Copied, not moved: %LOCALAPPDATA% is usually on another drive, and each copy takes the new folder's rights.
    private static void Copy(string from, string to, bool top)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: false);
        foreach (var folder in Directory.EnumerateDirectories(from))
        {
            var name = Path.GetFileName(folder);
            if (top && NotMoved.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            Copy(folder, Path.Combine(to, name), top: false);
        }
    }
}
