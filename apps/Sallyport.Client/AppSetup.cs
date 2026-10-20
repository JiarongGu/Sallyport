using System.Diagnostics;
using Sallyport.Runtime;
using Sallyport.Windows.Routing;

namespace Sallyport.Client;

/// <summary>What "Add an app" holds while the person sets an app up: where it is, its name, its VPN profile, what its
/// traffic does when the VPN drops, and which of its programs go through the VPN (by path; a folder's).</summary>
internal sealed record NewAppValues(string Path, string Name, string? ProfileId, LossMode Loss, string DirectAfterText, IReadOnlyList<string> Included);

/// <summary>How an app at a place would be made: a folder of programs, a program on its own, or a browser launched with
/// proxy settings.</summary>
internal enum SetupKind { Folder, Program, Browser }

/// <summary>What is at the place the person chose, before anything is kept: its kind, a name for it, and — for a folder,
/// or a program in a folder of its own — the programs there and which go through the VPN unless the person says
/// otherwise.</summary>
internal sealed record SetupPreview(SetupKind Kind, string Path, string SuggestedName, AppKind ProgramKind, IReadOnlyList<GameExecutable> Programs,
    string Summary);

/// <summary>"Add an app" (the owner: "a proper setup page, so user can define its from begining"): one place takes a
/// folder or a program, and says what it found before the app is made.</summary>
internal static class AppSetup
{
    public const int LongestName = 64;

    /// <summary>What is at <paramref name="path"/>; null with a reason when it is neither a folder nor a program.</summary>
    public static SetupPreview? Preview(string path, Func<string, bool> ownFolder, out string? why)
    {
        why = null;
        var trimmed = path.Trim().Trim('"');
        if (trimmed.Length == 0) { why = L.T("Choose a game's folder or a program."); return null; }
        string full;
        try { full = System.IO.Path.GetFullPath(trimmed); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { why = L.T("That is not a place on this computer."); return null; }

        try
        {
            if (Directory.Exists(full))
            {
                var programs = GameFolder.Scan(full);
                if (programs.Count == 0) { why = L.T("There is no program in that folder. Choose the folder the game is installed in."); return null; }
                return new(SetupKind.Folder, full, Shorten(System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(full)) is { Length: > 0 } n ? n : full),
                    AppKind.Environment, programs, FolderSummary(programs));
            }
            if (File.Exists(full))
            {
                if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { why = L.T("That file is not a program. Choose a game's folder or its .exe."); return null; }
                var name = NameOf(full);
                var kind = AppLauncher.Guess(full);
                if (kind is AppKind.Chromium or AppKind.Firefox)
                    return new(SetupKind.Browser, full, name, kind, [], L.T("A browser. It opens through your VPN when you start it from Sallyport, in a browser profile of its own."));
                var folder = System.IO.Path.GetDirectoryName(full)!;
                if (ownFolder(folder))
                {
                    // Its folder's programs with it, the one chosen always in: a game's helpers start beside it.
                    var programs = GameFolder.Scan(folder)
                        .Select(e => string.Equals(e.Path, full, StringComparison.OrdinalIgnoreCase) ? e with { Included = true, Reason = null } : e).ToList();
                    if (!programs.Any(e => string.Equals(e.Path, full, StringComparison.OrdinalIgnoreCase))) programs.Insert(0, new GameExecutable(full, true));
                    return new(SetupKind.Folder, full, name, kind, programs, FolderSummary(programs));
                }
                return new(SetupKind.Program, full, name, kind, [new GameExecutable(full, true)],
                    L.T("A program. All its traffic goes through your VPN while it is routed."));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { why = L.T("That place could not be read ({0}).", e.GetType().Name); return null; }
        why = L.T("Nothing is there. Check the path, or use Browse.");
        return null;
    }

    private static string FolderSummary(IReadOnlyList<GameExecutable> programs)
    {
        var included = programs.Count(p => p.Included);
        return programs.Count == 1 ? L.T("A folder with one program. All its traffic goes through your VPN while it is routed.")
            : included == programs.Count ? L.T("A folder with {0} programs. All of them go through your VPN while it is routed. You can change this below.", programs.Count)
            : L.T("A folder with {0} programs. {1} go through your VPN while it is routed; the rest (installers, crash reporters) keep your own connection. You can change this below.",
                programs.Count, included);
    }

    // A program's own name for itself, as Explorer shows it, else its file's.
    private static string NameOf(string program)
    {
        var info = FileVersionInfo.GetVersionInfo(program);
        return Shorten(new[] { info.ProductName, info.FileDescription, System.IO.Path.GetFileNameWithoutExtension(program) }
            .First(n => !string.IsNullOrWhiteSpace(n))!.Trim());
    }

    private static string Shorten(string name) => name.Length > LongestName ? name[..LongestName] : name;
}
