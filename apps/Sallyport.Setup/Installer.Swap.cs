using System.IO.Compression;
using Sallyport.Setup;
using Sallyport.Windows;

/// <summary>The install folder replaced whole: the new files unpacked beside it (<c>.new</c>), the old moved aside
/// (<c>.old</c>), the new moved in, the old one's data brought over, the old let go — and what an interrupted run left
/// of that put back before anything else is done.</summary>
sealed partial class Installer
{
    private string Previous => _folder + ".old";

    // A run that stopped between moving the old folder aside and finishing with it left the person's install, or their
    // data, in .old: put back where it belongs. A .old still holding data after that is never deleted — it is theirs.
    private void RecoverInterrupted()
    {
        if (!Directory.Exists(Previous)) return;
        var previousData = Path.Combine(Previous, "data");
        if (!Directory.Exists(_folder) && IsSallyport(Previous))
        {
            Directory.Move(Previous, _folder);
            Log($"{Previous} put back as {_folder}: an earlier setup stopped between the two folders");
            return;
        }
        if (Directory.Exists(previousData) && !Directory.Exists(Data))
        {
            Directory.Move(previousData, Data);
            Log($"{previousData} moved into {_folder}: an earlier setup stopped before it had");
        }
        if (Directory.Exists(previousData))
            throw new SetupFailed(L.T("{0} holds data an interrupted setup left beside Sallyport. Move what you need from it, delete it, then run setup again.", Previous));
        Directory.Delete(Previous, recursive: true);
    }

    // Protected before anything is put in it: off the system drive a new folder lets every user write, and the service
    // believes only clients that run from a folder ordinary users cannot change (product design change 2).
    private void SwapIn(Stream payload, string staged)
    {
        // The folder it goes in is not setup's to protect: only Sallyport's own is.
        Directory.CreateDirectory(Path.GetDirectoryName(_folder)!);
        if (Directory.Exists(staged)) Directory.Delete(staged, recursive: true);
        Protect(staged);
        Unpack(payload, staged, 0.08, 0.70);
        var hadPrevious = Directory.Exists(_folder);
        if (hadPrevious) MoveWhenFree(_folder, Previous);
        try { Directory.Move(staged, _folder); }
        catch
        {
            // The old files back as they were: the machine keeps the Sallyport it had.
            if (hadPrevious && !Directory.Exists(_folder)) Directory.Move(Previous, _folder);
            throw;
        }
        Protect(_folder);
        // The old version's data (its record above all) goes with the new files: a later removal still knows the driver.
        // A payload never holds data of its own (Unpack), so this never leaves the person's behind.
        if (hadPrevious && Directory.Exists(Path.Combine(Previous, "data")))
            Directory.Move(Path.Combine(Previous, "data"), Data);
        try { if (hadPrevious) Directory.Delete(Previous, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { leave(L.T("The old version's files could not all be removed from {0}.", Previous)); }
    }

    // Unpacked entry by entry so the window can show how far it is: the payload is most of setup's time.
    private void Unpack(Stream payload, string folder, double from, double to)
    {
        using var zip = new ZipArchive(payload, ZipArchiveMode.Read);
        var total = Math.Max(1, zip.Entries.Sum(e => e.Length));
        var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        var data = Path.Combine(root, "data") + Path.DirectorySeparatorChar;
        long done = 0;
        foreach (var entry in zip.Entries)
        {
            var path = Path.GetFullPath(Path.Combine(folder, entry.FullName));
            // A payload names only paths inside the folder it is unpacked into — and none in its data, which is the
            // person's, brought over from the version before.
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"The payload names a path outside its folder: {entry.FullName}");
            if ((path + Path.DirectorySeparatorChar).StartsWith(data, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"The payload names a path in the data folder: {entry.FullName}");
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
                    if (name is "games") foreach (var inside in Within(new DirectoryInfo(into)).Prepend(new DirectoryInfo(into))) Inherit(inside);
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
}
