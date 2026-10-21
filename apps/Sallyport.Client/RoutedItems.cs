using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sallyport.Runtime;
using Sallyport.Windows.Apps;
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

/// <summary>An app in a profile's section (D24): it goes through that profile whenever the person connects it, while
/// <see cref="On"/> — the switch beside it, kept whether or not the profile is connected (D23).</summary>
internal sealed record AppUse(string ProfileId, bool On = true);

/// <summary>
/// What the person routes, called an app in the window: a program, or a folder of them (a game). What its packets do while
/// a tunnel is lost applies to every program that does not say otherwise. <see cref="Uses"/>: the profiles it goes
/// through, each in that profile's section with a switch of its own — an app in many, a profile with many (D24); the
/// first that is connected carries it. <see cref="ProfileId"/> and <see cref="UseVpn"/> are an earlier client's one
/// profile and switch, read into <see cref="Uses"/> and never written since. <see cref="IconProgramId"/>: the program
/// whose icon is the app's, when the person chose one.
/// </summary>
internal sealed record RoutedItem(
    string Id, string Name, string? Folder, DateTimeOffset Created, string? ProfileId, LossMode Loss, int? DirectAfterSeconds,
    IReadOnlyList<RoutedProgram> Programs, string? LaunchProgramId = null, bool UseVpn = false, string? IconProgramId = null,
    IReadOnlyList<AppUse>? Uses = null)
{
    /// <summary>Its sections, an earlier client's one profile read as one.</summary>
    [JsonIgnore]
    public IReadOnlyList<AppUse> Sections => Uses ?? (ProfileId is { } profile ? [new AppUse(profile, UseVpn)] : []);

    public AppUse? UseOf(string profileId) => Sections.FirstOrDefault(u => u.ProfileId == profileId);

    /// <summary>In that profile's section, or taken out of it; its switch set, or on when it joins.</summary>
    public RoutedItem WithUse(string profileId, bool? on, bool remove = false) => this with
    {
        ProfileId = null,
        UseVpn = false,
        Uses = remove ? [.. Sections.Where(u => u.ProfileId != profileId)]
            : UseOf(profileId) is null ? [.. Sections, new AppUse(profileId, on ?? true)]
            : [.. Sections.Select(u => u.ProfileId == profileId ? u with { On = on ?? u.On } : u)],
    };

    /// <summary>The profile a Launch connects for it: the first of its sections that is connected, else its first.</summary>
    public string? LaunchProfile(Func<string, bool> connected) =>
        Sections.FirstOrDefault(u => connected(u.ProfileId))?.ProfileId ?? Sections.FirstOrDefault()?.ProfileId;
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
    public GameChoice Choice => new(Sections.FirstOrDefault()?.ProfileId, Loss, DirectAfterSeconds);

    /// <summary>Its programs as the router service takes them: each sending all its traffic, with the app's loss policy.</summary>
    [JsonIgnore]
    public IReadOnlyList<ProgramRule> Rules =>
        [.. PacketPrograms.Select(p => new ProgramRule(p.Path, Loss, Loss == LossMode.Hold ? DirectAfterSeconds : null))];

    /// <summary>As the packet route and its views take a game: the programs of the packet method, each included or not.</summary>
    public GameProfile AsGame() =>
        new(Id, Name, Folder, [.. Programs.Where(p => p.Packets).Select(p => new GameExecutable(p.Path, p.Included, p.Reason))], Created);

    /// <summary>As the proxy launcher and its views take an app; null when nothing of it is launched with proxy settings.</summary>
    public RoutedApp? AsApp() => ProxyProgram is { Proxy: { } proxy } program
        ? new RoutedApp(Id, Name, program.Path, proxy.Kind, program.ProfileId ?? Sections.FirstOrDefault()?.ProfileId ?? "", proxy.Arguments)
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
        var item = new RoutedItem(NewId(8), Named(name), Path.GetFullPath(folder), DateTimeOffset.UtcNow, null, LossMode.Hold, null,
            [.. GameFolder.Scan(folder).Select(e => new RoutedProgram(NewId(6), e.Path, e.Included, e.Reason, false, Packets: true, Proxy: null))], Uses: Sections(profileId));
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
        var item = new RoutedItem(NewId(8), Named(name), folder, DateTimeOffset.UtcNow, null, LossMode.Hold, null, programs, launch, Uses: Sections(profileId));
        Put(item);
        return item;
    }

    /// <summary>A new app from one program, by its methods: all its traffic by packets, or launched with proxy settings
    /// (its own browser profile folder named for the app), or both.</summary>
    public RoutedItem CreateFromProgram(string name, string path, AppKind kind, string? profileId, bool packets = false, bool proxy = true)
    {
        var id = NewId(8);
        var item = new RoutedItem(id, Named(name), null, DateTimeOffset.UtcNow, null, LossMode.Hold, null,
            [new RoutedProgram(NewId(6), Path.GetFullPath(path), true, null, true, packets, proxy ? new ProxyLaunch(kind, "", id) : null)], Uses: Sections(profileId));
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

    /// <summary>Whether the app is where it was added from: its folder, or for an app of programs alone, any of them.
    /// An app imported from another computer may not be (D26).</summary>
    public static bool IsHere(RoutedItem item) =>
        item.Folder is { } folder ? Directory.Exists(folder) : item.Programs.Count == 0 || item.Programs.Any(p => File.Exists(p.Path));

    /// <summary>The app where it is now (D26): <paramref name="place"/> its new folder, or one of its programs there. Each
    /// program keeps its id, choice and methods, moved by where it sat in the old folder; the new folder is then scanned
    /// as Scan folder again does. A program given for an app of programs alone takes the place of the one of its name,
    /// else of its first.</summary>
    public RoutedItem Relocate(string id, string place)
    {
        var item = Find(id) ?? throw new FileNotFoundException("no such app");
        var full = Path.GetFullPath(place);
        if (item.Folder is { } old)
        {
            var folder = Directory.Exists(full) ? full : Path.GetDirectoryName(full)!;
            // A program picked inside the folder: the folder is where that program sat in the old one, above it.
            if (File.Exists(full) && item.Programs.FirstOrDefault(p => string.Equals(Path.GetFileName(p.Path), Path.GetFileName(full), StringComparison.OrdinalIgnoreCase)
                    && IsUnder(p.Path, old)) is { } same)
            {
                var depth = Path.GetRelativePath(old, Path.GetDirectoryName(same.Path)!);
                if (depth != "." && folder.EndsWith(Path.DirectorySeparatorChar + depth, StringComparison.OrdinalIgnoreCase))
                    folder = folder[..^(depth.Length + 1)];
            }
            var moved = item with
            {
                Folder = folder,
                Programs = [.. item.Programs.Select(p => IsUnder(p.Path, old) ? p with { Path = Path.Combine(folder, Path.GetRelativePath(old, p.Path)) } : p)],
            };
            Put(moved);
            return Rescan(id);
        }
        if (!File.Exists(full) || !full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("not a program", nameof(place));
        var target = item.Programs.FirstOrDefault(p => string.Equals(Path.GetFileName(p.Path), Path.GetFileName(full), StringComparison.OrdinalIgnoreCase))
            ?? item.Programs.FirstOrDefault();
        var relocated = item with { Programs = [.. item.Programs.Select(p => p == target ? p with { Path = full } : p)] };
        Put(relocated);
        return relocated;
    }

    /// <summary>Where an app that is not here may be: the same folder on another of this computer's drives — a library
    /// on D: there and E: here is the usual move. Null when none holds it.</summary>
    public static string? Seek(RoutedItem item, IEnumerable<string>? roots = null)
    {
        var where = item.Folder ?? item.Programs.FirstOrDefault()?.Path;
        if (where is null || Path.GetPathRoot(where) is not { Length: > 0 } root) return null;
        var rest = where[root.Length..];
        foreach (var drive in roots ?? DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName))
        {
            if (string.Equals(drive, root, StringComparison.OrdinalIgnoreCase)) continue;
            var candidate = Path.Combine(drive, rest);
            if (item.Folder is not null ? Directory.Exists(candidate) : File.Exists(candidate)) return candidate;
        }
        return null;
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

    /// <summary>Changes one app, from the app as it is now, under the store's lock; null when it is no longer listed. Two
    /// changes at once — the person saving its page while a lost connection turns its Use VPN off — each see the other's,
    /// where a copy taken, changed and put back would quietly undo the first. <paramref name="change"/> must not call
    /// back into the store.</summary>
    public RoutedItem? Update(string id, Func<RoutedItem, RoutedItem> change)
    {
        lock (_gate)
        {
            var at = _items.FindIndex(i => i.Id == id);
            if (at < 0) return null;
            var changed = change(_items[at]);
            if (changed.Id != id) throw new ArgumentException("an app's change may not change its id", nameof(change));
            if (ReferenceEquals(changed, _items[at])) return changed;
            var next = new List<RoutedItem>(_items) { [at] = changed };
            Write(next, verify: false);
            _items = next;
            return changed;
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

    // A new app's first section, on: it was added to go through it.
    private static IReadOnlyList<AppUse> Sections(string? profileId) => profileId is null ? [] : [new AppUse(profileId)];

    /// <summary>Takes a profile out of every app's sections (the profile removed): the apps stay, going through their others.</summary>
    public int Forget(string profileId)
    {
        lock (_gate)
        {
            var next = _items.Select(i => i.UseOf(profileId) is null ? i : i.WithUse(profileId, null, remove: true)).ToList();
            var changed = next.Zip(_items).Count(pair => !ReferenceEquals(pair.First, pair.Second));
            if (changed == 0) return 0;
            Write(next, verify: false);
            _items = next;
            return changed;
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
