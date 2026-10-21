using System.Security.AccessControl;
using System.Security.Principal;
using Sallyport.Windows;

/// <summary>The rights on what setup makes: the install folder administrators' alone, the shared areas everyone's to
/// write (D25) — and every walk through a folder that stops at a junction or a link, which in the shared areas any
/// account may have made, pointing anywhere.</summary>
sealed partial class Installer
{
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
            foreach (var inside in Within(info)) Inherit(inside);
        }
    }

    /// <summary>Everything under <paramref name="folder"/>, its subfolders' too, never through a reparse point: a
    /// junction or a link is listed, not entered. .NET's own recursive enumeration does enter a junction (measured,
    /// 2026-10-21): setup, as administrator, would then act on whatever the junction points at.</summary>
    internal static IEnumerable<FileSystemInfo> Within(DirectoryInfo folder)
    {
        // Hidden and system entries too, which the default options leave out.
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true };
        foreach (var entry in folder.EnumerateFileSystemInfos("*", options))
        {
            yield return entry;
            if (entry is DirectoryInfo inner && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                foreach (var deeper in Within(inner)) yield return deeper;
        }
    }

    private static void Inherit(FileSystemInfo entry)
    {
        // Setting a link's rights sets its target's: a link is left as it is.
        if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
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
}
