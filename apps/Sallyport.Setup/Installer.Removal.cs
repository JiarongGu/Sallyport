using System.Diagnostics;
using Sallyport.Setup;

/// <summary>What removal takes from the install folder: everything but what the person keeps (D25), and what is in use
/// by the uninstaller itself once it has exited.</summary>
sealed partial class Installer
{
    // Only a folder that is still Sallyport's: whatever else someone put there since is not setup's to take. Its users'
    // profiles and settings stay unless the person said otherwise, where installing again finds them.
    private void RemoveFiles(bool removeData)
    {
        if (!IsSallyport(_folder)) return;
        var keep = removeData ? null : KeptData;
        // This uninstaller may run from the folder it removes: what is in use goes once it has exited.
        if (Path.GetDirectoryName(Environment.ProcessPath)!.Equals(_folder, StringComparison.OrdinalIgnoreCase)) RemoveAfterExit(_folder, keep);
        else RemoveFolder(_folder, keep);
    }

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
        // What is not in use goes now — never what lies past a junction or a link in it, which any account could have
        // made in the shared areas: a file deleted there would be deleted as administrator, wherever it is.
        var files = removable.Where(File.Exists)
            .Concat(removable.Where(Directory.Exists).Select(d => new DirectoryInfo(d)).Where(d => !d.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .SelectMany(d => Within(d).OfType<FileInfo>().Select(f => f.FullName)));
        foreach (var file in files)
            try { File.Delete(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        // A command of Windows' own, detached, waits a few seconds for this process to end and takes the rest; rd removes
        // a junction without entering it. The folder's path has no % in it (Unfit), so nothing in it reads as a variable.
        List<string> commands = keep is null
            ? [$"rd /s /q \"{folder}\""]
            : removable.Select(entry => Directory.Exists(entry) ? $"rd /s /q \"{entry}\"" : $"del /f /q \"{entry}\"").ToList();
        using var cleanup = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c ping -n 4 127.0.0.1 >nul & {string.Join(" & ", commands)}")
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
