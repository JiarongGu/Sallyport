using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Sallyport.Windows.Apps;

/// <summary>One executable of a game, and whether it is routed. <see cref="Reason"/> says why one is left out by default;
/// the person's choice overrides it either way.</summary>
public sealed record GameExecutable(string Path, bool Included, string? Reason = null);

/// <summary>
/// A game as an earlier Sallyport kept it: the executables that are the game, found by scanning its folder and each
/// included or left out by the person — what the router selects by exact path, never by folder. Read now only to move
/// a game into the apps (the client's <c>RoutedItemStore</c>).
/// </summary>
public sealed record GameProfile(string Id, string Name, string? Folder, IReadOnlyList<GameExecutable> Executables, DateTimeOffset Created)
{
    // Worked out from the choices each time, never stored beside them.
    [JsonIgnore]
    public IReadOnlyList<string> IncludedPaths => [.. Executables.Where(e => e.Included).Select(e => e.Path)];
}

/// <summary>What a game's folder holds that could be the game.</summary>
public static partial class GameFolder
{
    /// <summary>Every <c>.exe</c> under <paramref name="folder"/>, its subfolders included — a game's helpers (an in-game
    /// browser, a crash reporter) sit beside it, and a child the watcher never saw start must still be routable. Programs
    /// that install, remove or report crashes are listed but left out, with the reason.</summary>
    public static IReadOnlyList<GameExecutable> Scan(string folder)
    {
        var root = Path.GetFullPath(folder);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("the game's folder is not there");
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
        return [.. Directory.EnumerateFiles(root, "*.exe", options)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(path => LeftOutReason(Path.GetFileName(path)) is { } why ? new GameExecutable(path, false, why) : new GameExecutable(path, true))];
    }

    /// <summary>Why a program is left out by default, or null for one that may be the game. By name only: these never
    /// carry play, and routing one that shares the game's servers would refuse the person's other programs nothing — but
    /// an uninstaller or a redistributable has no business in the VPN.</summary>
    public static string? LeftOutReason(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (Installer().IsMatch(name)) return "installs or removes software";
        if (Redistributable().IsMatch(name)) return "a runtime redistributable";
        if (CrashReporter().IsMatch(name)) return "reports crashes";
        return null;
    }

    [GeneratedRegex(@"^(unins\d*|uninst(all)?.*|.*uninstall.*|setup|.*installer.*|install)$", RegexOptions.IgnoreCase)]
    private static partial Regex Installer();

    [GeneratedRegex(@"^(vc_?redist.*|vcredist.*|dxsetup|dxwebsetup|directx.*|dotnet.*(runtime|redist).*|oalinst|physx.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex Redistributable();

    // "crashr$": Zenless Zone Zero's web crash reporter is APM4webCrashR.exe (seen in its folder, 2026-10-19).
    [GeneratedRegex(@"(crash.*(handler|report|pad|sender)|crashr$|bug.?report|errorreport)", RegexOptions.IgnoreCase)]
    private static partial Regex CrashReporter();
}

/// <summary>
/// The earlier store of game profiles, one JSON file each, in a folder of their own beside the VPN profiles — read once,
/// to migrate it. Nothing in one is secret: paths, names and choices.
/// </summary>
public sealed class GameProfileStore
{
    private const int LongestName = 64;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _directory;
    private readonly object _gate = new();

    public GameProfileStore(string directory)
    {
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
    }

    public IReadOnlyList<GameProfile> List()
    {
        lock (_gate)
        {
            var found = new List<GameProfile>();
            foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
            {
                if (!IsId(Path.GetFileNameWithoutExtension(file))) continue;
                // A file that does not read is not offered: it could not be run.
                try { if (JsonSerializer.Deserialize<GameProfile>(File.ReadAllBytes(file), Json) is { } game) found.Add(game); }
                catch (JsonException) { }
            }
            return [.. found.OrderBy(g => g.Created)];
        }
    }

    public GameProfile? Find(string id) => IsId(id) ? List().FirstOrDefault(g => g.Id == id) : null;

    /// <summary>A new game from its folder: every executable found, the utilities left out (<see cref="GameFolder.Scan"/>).</summary>
    public GameProfile Create(string name, string folder)
    {
        var game = new GameProfile(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)), Named(name), Path.GetFullPath(folder),
            GameFolder.Scan(folder), DateTimeOffset.UtcNow);
        Save(game);
        return game;
    }

    /// <summary>Scans the folder again after an update: the person's choice stands for every executable still there, a new
    /// one takes the default, and one that is gone is dropped — it could not be routed by path.</summary>
    public GameProfile Rescan(string id)
    {
        var game = Find(id) ?? throw new FileNotFoundException("no such game profile");
        if (game.Folder is null) return game;
        var chosen = game.Executables.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        var executables = GameFolder.Scan(game.Folder)
            .Select(found => chosen.TryGetValue(found.Path, out var kept) ? kept : found)
            .ToList();
        var updated = game with { Executables = executables };
        Save(updated);
        return updated;
    }

    /// <summary>Includes or leaves out one executable. Only one the profile already lists: a path arriving from a window or
    /// a command line is checked, not trusted.</summary>
    public GameProfile Choose(string id, string path, bool included)
    {
        var game = Find(id) ?? throw new FileNotFoundException("no such game profile");
        if (!game.Executables.Any(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("that program is not one of this game's", nameof(path));
        var updated = game with
        {
            Executables = [.. game.Executables.Select(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase) ? e with { Included = included } : e)],
        };
        Save(updated);
        return updated;
    }

    /// <summary>Adds a program from outside the game's folder — a launcher elsewhere, say — included.</summary>
    public GameProfile Add(string id, string path)
    {
        var game = Find(id) ?? throw new FileNotFoundException("no such game profile");
        var full = Path.GetFullPath(path);
        if (!File.Exists(full) || !full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("not a program", nameof(path));
        if (game.Executables.Any(e => string.Equals(e.Path, full, StringComparison.OrdinalIgnoreCase))) return Choose(id, full, true);
        var updated = game with { Executables = [.. game.Executables, new GameExecutable(full, true)] };
        Save(updated);
        return updated;
    }

    public GameProfile Rename(string id, string name)
    {
        var game = Find(id) ?? throw new FileNotFoundException("no such game profile");
        var updated = game with { Name = Named(name) };
        Save(updated);
        return updated;
    }

    public bool Remove(string id)
    {
        if (!IsId(id)) return false;
        lock (_gate)
        {
            var file = Path.Combine(_directory, id + ".json");
            var existed = File.Exists(file);
            File.Delete(file);
            return existed;
        }
    }

    private void Save(GameProfile game)
    {
        lock (_gate)
        {
            var file = Path.Combine(_directory, game.Id + ".json");
            var temporary = file + ".tmp";
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(game, Json));
            File.Move(temporary, file, overwrite: true);
        }
    }

    private static string Named(string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > LongestName) throw new ArgumentException($"a game's name is 1 to {LongestName} characters", nameof(name));
        return name;
    }

    /// <summary>An id is the store's own 16 hex digits and nothing else: one arriving from outside must not name a path.</summary>
    private static bool IsId(string id) => id.Length == 16 && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
