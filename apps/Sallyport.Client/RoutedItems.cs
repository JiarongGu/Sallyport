using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sallyport.Runtime;
using Sallyport.Windows.Routing;

namespace Sallyport.Client;

/// <summary>How a program is launched with a profile's proxy: its family, the arguments the person added, and the name
/// of its own browser profile folder under the data folder's <c>apps</c> (kept across migration, so a browser keeps its
/// sign-ins).</summary>
internal sealed record ProxyLaunch(AppKind Kind, string Arguments, string DataKey);

/// <summary>
/// One program of an app (docs/design/window.md): an exact executable path, whether it is part of the app — a folder's
/// scan suggests, the person decides (<see cref="ChosenByPerson"/>) — and its routing methods. <see cref="Packets"/>:
/// everything it sends goes through the packet driver; <see cref="Proxy"/>: launched from Sallyport with proxy settings.
/// <see cref="ProfileId"/>, when set, overrides the app's profile for this program alone. <see cref="Arguments"/>: what Launch
/// passes it, whichever its method (a launcher's own options, say).
/// </summary>
internal sealed record RoutedProgram(
    string Id, string Path, bool Included, string? Reason, bool ChosenByPerson, bool Packets, ProxyLaunch? Proxy, string? ProfileId = null,
    string Arguments = "")
{
    /// <summary>What Launch passes it: its own, or what an earlier client kept with its proxy launch.</summary>
    [JsonIgnore]
    public string LaunchArguments => Arguments.Length > 0 ? Arguments : Proxy?.Arguments ?? "";
}

/// <summary>
/// What the person routes, called an app in the window: a program, or a folder of them (a game). Its VPN profile and what
/// its packets do while that profile's tunnel is lost apply to every program that does not say otherwise.
/// </summary>
internal sealed record RoutedItem(
    string Id, string Name, string? Folder, DateTimeOffset Created, string? ProfileId, LossMode Loss, int? DirectAfterSeconds,
    IReadOnlyList<RoutedProgram> Programs, string? LaunchProgramId = null)
{
    /// <summary>Its programs routed by their packets, as the router service names them: the included ones whose method it is.</summary>
    [JsonIgnore]
    public IReadOnlyList<RoutedProgram> PacketPrograms => [.. Programs.Where(p => p.Included && p.Packets)];

    /// <summary>Whether it is routed by packets at all — a folder's programs, a game.</summary>
    [JsonIgnore]
    public bool RoutesPackets => Folder is not null || Programs.Any(p => p.Packets);

    /// <summary>The program launched with proxy settings, when it has one.</summary>
    [JsonIgnore]
    public RoutedProgram? ProxyProgram => Programs.FirstOrDefault(p => p.Included && p.Proxy is not null);

    [JsonIgnore]
    public GameChoice Choice => new(ProfileId, Loss, DirectAfterSeconds);

    /// <summary>As the packet route and its views take a game: the programs of the packet method, each included or not.</summary>
    public GameProfile AsGame() =>
        new(Id, Name, Folder, [.. Programs.Where(p => p.Packets).Select(p => new GameExecutable(p.Path, p.Included, p.Reason))], Created);

    /// <summary>As the proxy launcher and its views take an app; null when nothing of it is launched with proxy settings.</summary>
    public RoutedApp? AsApp() => ProxyProgram is { Proxy: { } proxy } program
        ? new RoutedApp(Id, Name, program.Path, proxy.Kind, program.ProfileId ?? ProfileId ?? "", proxy.Arguments)
        : null;
}

/// <summary>What migrating the earlier stores found: how many apps came over, and what could not be read (rule 6: an
/// unreadable game is said, not silently left behind).</summary>
internal sealed record Migration(int Games, int Apps, int Unreadable);

/// <summary>
/// The apps the person routes, one versioned JSON file (<c>apps.json</c>) rewritten whole. Nothing in it is secret:
/// paths, names, choices. The first start of a client that has it moves the earlier games (one file each) and their
/// choices and the proxy apps (both in the client's settings) into it, ids kept, and reads the result back before it
/// counts; the earlier files stay as they were, for recovery.
/// </summary>
internal sealed class RoutedItemStore
{
    public const int Version = 1;
    private const int LongestName = 64;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _file;
    private readonly object _gate = new();
    private List<RoutedItem> _items;
    // An unreadable file is set aside, not written over, by the first change after it was found.
    private bool _setAside;

    private sealed record Snapshot(int Version, List<RoutedItem> Items);

    /// <summary>What the first start migrated, or null when there was nothing to migrate (or it was done before).</summary>
    public Migration? Migrated { get; }

    /// <summary>Why the store's own file could not be read, when it could not; the apps then start empty and are not
    /// written over until the person changes one.</summary>
    public string? Unreadable { get; }

    public RoutedItemStore(string folder, Func<(IReadOnlyList<GameProfile> Games, int UnreadableGames, IReadOnlyDictionary<string, GameChoice> Choices, IReadOnlyList<RoutedApp> Apps)> legacy)
    {
        Directory.CreateDirectory(folder);
        _file = Path.Combine(folder, "apps.json");
        if (File.Exists(_file))
        {
            (_items, Unreadable) = Read(_file);
            _setAside = Unreadable is not null;
            return;
        }
        var (games, unreadableGames, choices, apps) = legacy();
        _items = [.. Migrate(games, choices, apps)];
        if (_items.Count == 0 && unreadableGames == 0) return;
        Write(_items, verify: true);
        Migrated = new Migration(games.Count, apps.Count, unreadableGames);
    }

    /// <summary>The earlier games, their choices and the proxy apps as apps: every id kept (a game's 16 hex digits, an
    /// app's 12), a browser's own profile folder kept, every program given an id of its own. Equal names or paths are
    /// never merged: the person made them apart.</summary>
    public static IEnumerable<RoutedItem> Migrate(IReadOnlyList<GameProfile> games, IReadOnlyDictionary<string, GameChoice> choices, IReadOnlyList<RoutedApp> apps)
    {
        foreach (var game in games)
        {
            var choice = choices.GetValueOrDefault(game.Id) ?? new GameChoice(null, LossMode.Hold, null);
            yield return new RoutedItem(game.Id, game.Name, game.Folder, game.Created, choice.ProfileId, choice.Loss, choice.DirectAfterSeconds,
                [.. game.Executables.Select(e => new RoutedProgram(NewId(6), e.Path, e.Included, e.Reason,
                    // The scan leaves out only what it gives a reason for; anything else differing from it was the person.
                    ChosenByPerson: e.Included == (e.Reason is not null), Packets: true, Proxy: null))]);
        }
        foreach (var app in apps)
            yield return new RoutedItem(app.Id, app.Name, null, DateTimeOffset.UtcNow, string.IsNullOrEmpty(app.ProfileId) ? null : app.ProfileId,
                LossMode.Hold, null, [new RoutedProgram(NewId(6), app.Path, true, null, ChosenByPerson: true, Packets: false, new ProxyLaunch(app.Kind, app.Arguments, app.Id))]);
    }

    public IReadOnlyList<RoutedItem> List()
    {
        lock (_gate) return [.. _items];
    }

    public RoutedItem? Find(string id)
    {
        if (!IsId(id)) return null;
        lock (_gate) return _items.FirstOrDefault(i => i.Id == id);
    }

    /// <summary>A new app from a folder: every program found, routed by its packets, the utilities left out
    /// (<see cref="GameFolder.Scan"/>).</summary>
    public RoutedItem CreateFromFolder(string name, string folder, string? profileId)
    {
        var item = new RoutedItem(NewId(8), Named(name), Path.GetFullPath(folder), DateTimeOffset.UtcNow, profileId, LossMode.Hold, null,
            [.. GameFolder.Scan(folder).Select(e => new RoutedProgram(NewId(6), e.Path, e.Included, e.Reason, false, Packets: true, Proxy: null))]);
        Put(item);
        return item;
    }

    /// <summary>A new app of a program's folder: every program found there, routed by its packets, the utilities left
    /// out (<see cref="GameFolder.Scan"/>) — the one picked included whatever the scan says, and what Launch starts.</summary>
    public RoutedItem CreateFromProgramFolder(string name, string program, string? profileId)
    {
        var full = Path.GetFullPath(program);
        var folder = Path.GetDirectoryName(full)!;
        var programs = GameFolder.Scan(folder)
            .Select(e => string.Equals(e.Path, full, StringComparison.OrdinalIgnoreCase)
                ? new RoutedProgram(NewId(6), e.Path, true, null, true, Packets: true, Proxy: null)
                : new RoutedProgram(NewId(6), e.Path, e.Included, e.Reason, false, Packets: true, Proxy: null))
            .ToList();
        if (!programs.Any(p => string.Equals(p.Path, full, StringComparison.OrdinalIgnoreCase)))
            programs.Insert(0, new RoutedProgram(NewId(6), full, true, null, true, Packets: true, Proxy: null));
        var launch = programs.First(p => string.Equals(p.Path, full, StringComparison.OrdinalIgnoreCase)).Id;
        var item = new RoutedItem(NewId(8), Named(name), folder, DateTimeOffset.UtcNow, profileId, LossMode.Hold, null, programs, launch);
        Put(item);
        return item;
    }

    /// <summary>A new app from one program, by its methods: all its traffic by packets, or launched with proxy settings
    /// (its own browser profile folder named for the app), or both.</summary>
    public RoutedItem CreateFromProgram(string name, string path, AppKind kind, string? profileId, bool packets = false, bool proxy = true)
    {
        var id = NewId(8);
        var item = new RoutedItem(id, Named(name), null, DateTimeOffset.UtcNow, profileId, LossMode.Hold, null,
            [new RoutedProgram(NewId(6), Path.GetFullPath(path), true, null, true, packets, proxy ? new ProxyLaunch(kind, "", id) : null)]);
        Put(item);
        return item;
    }

    /// <summary>Adds a program from anywhere — a launcher outside the folder, say — sending all its traffic; one the app
    /// lists already is included instead.</summary>
    public RoutedItem AddProgram(string id, string path)
    {
        var item = Find(id) ?? throw new FileNotFoundException("no such app");
        var full = Path.GetFullPath(path);
        if (!File.Exists(full) || !full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("not a program", nameof(path));
        if (item.Programs.Any(p => string.Equals(p.Path, full, StringComparison.OrdinalIgnoreCase))) return Choose(id, full, true);
        var updated = item with { Programs = [.. item.Programs, new RoutedProgram(NewId(6), full, true, null, true, Packets: true, Proxy: null)] };
        Put(updated);
        return updated;
    }

    /// <summary>Scans an app's folder again: the person's choice stands for every program still there, a new one takes
    /// the scan's suggestion, and one that is gone is dropped — it could not be routed by path.</summary>
    public RoutedItem Rescan(string id)
    {
        var item = Find(id) ?? throw new FileNotFoundException("no such app");
        if (item.Folder is null) return item;
        var known = item.Programs.ToDictionary(p => p.Path, StringComparer.OrdinalIgnoreCase);
        var programs = GameFolder.Scan(item.Folder)
            .Select(found => known.TryGetValue(found.Path, out var kept) ? kept
                : new RoutedProgram(NewId(6), found.Path, found.Included, found.Reason, false, Packets: true, Proxy: null))
            // Programs added from outside the folder are the person's, not the scan's to drop.
            .Concat(item.Programs.Where(p => !IsUnder(p.Path, item.Folder)))
            .ToList();
        var updated = item with { Programs = programs };
        Put(updated);
        return updated;
    }

    /// <summary>Includes or leaves out one program — only one the app already lists: a path arriving from a window is
    /// checked, not trusted.</summary>
    public RoutedItem Choose(string id, string path, bool included)
    {
        var item = Find(id) ?? throw new FileNotFoundException("no such app");
        if (!item.Programs.Any(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("that program is not one of this app's", nameof(path));
        var updated = item with
        {
            Programs = [.. item.Programs.Select(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase) ? p with { Included = included, ChosenByPerson = true } : p)],
        };
        Put(updated);
        return updated;
    }

    public void Put(RoutedItem item)
    {
        if (!IsId(item.Id)) throw new ArgumentException("not an app id", nameof(item));
        lock (_gate)
        {
            var next = new List<RoutedItem>(_items);
            var at = next.FindIndex(i => i.Id == item.Id);
            if (at < 0) next.Add(item);
            else next[at] = item;
            Write(next, verify: false);
            _items = next;
        }
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            var next = _items.Where(i => i.Id != id).ToList();
            if (next.Count == _items.Count) return false;
            Write(next, verify: false);
            _items = next;
            return true;
        }
    }

    public static string Named(string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > LongestName) throw new ArgumentException($"an app's name is 1 to {LongestName} characters", nameof(name));
        return name;
    }

    /// <summary>An id is 12 or 16 lowercase hex digits — an earlier app's or game's, or this store's — and nothing else:
    /// one arriving from outside must not name a path.</summary>
    public static bool IsId(string id) => id.Length is 12 or 16 && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string NewId(int bytes) => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(bytes));

    private static bool IsUnder(string path, string folder) =>
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static (List<RoutedItem>, string?) Read(string file)
    {
        try
        {
            var snapshot = JsonSerializer.Deserialize<Snapshot>(File.ReadAllBytes(file), Json);
            if (snapshot is null) return ([], "the file is empty");
            if (snapshot.Version > Version) return ([], $"it was written by a newer Sallyport (version {snapshot.Version})");
            return ([.. snapshot.Items.Where(i => IsId(i.Id))], null);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return ([], $"it could not be read ({e.GetType().Name})");
        }
    }

    // Written beside, read back, then put in place: a snapshot that does not read back as written never replaces one.
    private void Write(List<RoutedItem> items, bool verify)
    {
        if (_setAside && File.Exists(_file))
        {
            File.Copy(_file, Path.ChangeExtension(_file, $".unreadable-{DateTime.Now:yyyyMMdd-HHmmss}.json"), overwrite: false);
            _setAside = false;
        }
        var temporary = _file + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Snapshot(Version, items), Json);
        File.WriteAllBytes(temporary, bytes);
        if (verify)
        {
            var (back, why) = Read(temporary);
            if (why is not null || back.Count != items.Count || !back.Select(i => i.Id).SequenceEqual(items.Select(i => i.Id)))
            {
                File.Delete(temporary);
                throw new IOException("the migrated apps did not read back as written");
            }
        }
        File.Move(temporary, _file, overwrite: true);
    }
}
