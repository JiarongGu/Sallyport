using Sallyport.Runtime;
using Sallyport.Windows.Routing;

namespace Sallyport.Client.Tests;

/// <summary>The one store of apps (docs/design/window.md, step 2): what migrating the earlier games and proxy apps keeps,
/// that it happens once, and that what cannot be read is said rather than lost.</summary>
public sealed class RoutedItemStoreTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sallyport-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        try { Directory.Delete(Path.GetDirectoryName(_folder)!); }
        catch (IOException) { }
    }

    private static readonly GameProfile Zenless = new("0123456789abcdef", "Zenless Zone Zero", @"E:\Games\ZZZ",
        [new(@"E:\Games\ZZZ\ZenlessZoneZero.exe", true), new(@"E:\Games\ZZZ\unins000.exe", false, "installs or removes software"),
         new(@"E:\Games\ZZZ\Helper.exe", false)], At);

    private static readonly RoutedApp Firefox = new("a1b2c3d4e5f6", "Firefox", @"C:\Program Files\Mozilla Firefox\firefox.exe", AppKind.Firefox,
        "ad50a53b0da826ad", "--private-window");

    private RoutedItemStore Open(IReadOnlyList<GameProfile>? games = null, int unreadable = 0, IReadOnlyDictionary<string, GameChoice>? choices = null,
        IReadOnlyList<RoutedApp>? apps = null) =>
        new(_folder, () => (games ?? [], unreadable, choices ?? new Dictionary<string, GameChoice>(), apps ?? []));

    [Fact]
    public void Migration_keeps_ids_choices_profiles_and_a_browsers_own_folder()
    {
        var store = Open([Zenless], choices: new Dictionary<string, GameChoice> { [Zenless.Id] = new("ad50a53b0da826ad", LossMode.Direct, null) },
            apps: [Firefox]);

        Assert.Equal(new Migration(1, 1, 0), store.Migrated);
        var game = store.Find(Zenless.Id)!;
        Assert.Equal(("Zenless Zone Zero", @"E:\Games\ZZZ", "ad50a53b0da826ad", LossMode.Direct), (game.Name, game.Folder, game.ProfileId, game.Loss));
        Assert.Equal([@"E:\Games\ZZZ\ZenlessZoneZero.exe"], game.PacketPrograms.Select(p => p.Path));
        // Left out by the scan, with its reason, is the scan's choice; left out without one was the person's.
        Assert.Equal([false, false, true], game.Programs.Select(p => p.ChosenByPerson));
        Assert.Equal(Zenless.Executables, game.AsGame().Executables);

        var app = store.Find(Firefox.Id)!;
        Assert.Equal(Firefox, app.AsApp());
        Assert.Equal(Firefox.Id, app.ProxyProgram!.Proxy!.DataKey);
        Assert.False(app.RoutesPackets);
        Assert.Equal(game.Programs.Count + app.Programs.Count, store.List().SelectMany(i => i.Programs).Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public void Migration_happens_once_and_reads_back_after_a_restart()
    {
        Open([Zenless], apps: [Firefox]);

        var again = Open([Zenless], apps: [Firefox]);

        Assert.Null(again.Migrated);
        Assert.Equal([Zenless.Id, Firefox.Id], again.List().Select(i => i.Id));
    }

    [Fact]
    public void A_game_that_could_not_be_read_is_counted_and_a_missing_profile_stays_for_repair()
    {
        var store = Open([Zenless], unreadable: 2, choices: new Dictionary<string, GameChoice> { [Zenless.Id] = new("ffffffffffffffff", LossMode.Hold, null) });

        Assert.Equal(new Migration(1, 0, 2), store.Migrated);
        Assert.Equal("ffffffffffffffff", store.Find(Zenless.Id)!.ProfileId);
    }

    [Fact]
    public void Nothing_to_migrate_writes_nothing()
    {
        var store = Open();

        Assert.Null(store.Migrated);
        Assert.False(File.Exists(Path.Combine(_folder, "apps.json")));
    }

    [Fact]
    public void An_unreadable_store_is_said_and_set_aside_before_it_is_written_over()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "apps.json"), "{ not json");

        var store = Open([Zenless]);

        Assert.StartsWith("it could not be read", store.Unreadable);
        Assert.Empty(store.List());
        store.Put(Assert.Single(RoutedItemStore.Migrate([Zenless], new Dictionary<string, GameChoice>(), [])));
        Assert.Single(Directory.EnumerateFiles(_folder, "apps.unreadable-*.json"));
        Assert.Equal("{ not json", File.ReadAllText(Directory.EnumerateFiles(_folder, "apps.unreadable-*.json").Single()));
    }

    [Fact]
    public void A_folder_rescan_keeps_the_persons_choices_and_programs_from_elsewhere()
    {
        var folder = Path.Combine(_folder, "game");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Game.exe"), "");
        File.WriteAllText(Path.Combine(folder, "unins000.exe"), "");
        var store = Open();
        var item = store.CreateFromFolder("Game", folder, null);
        var outside = Path.Combine(_folder, "Launcher.exe");
        store.Put(item with { Programs = [.. item.Programs, new RoutedProgram("0a0b0c0d0e0f", outside, true, null, true, true, null)] });
        store.Choose(item.Id, Path.Combine(folder, "Game.exe"), included: false);
        File.WriteAllText(Path.Combine(folder, "New.exe"), "");

        var after = store.Rescan(item.Id);

        Assert.Equal(new[] { ("Game.exe", false), ("New.exe", true), ("unins000.exe", false), ("Launcher.exe", true) },
            after.Programs.Select(p => (Path.GetFileName(p.Path), p.Included)));
        Assert.True(after.Programs[0].ChosenByPerson);
    }

    [Fact]
    public void An_app_made_from_its_program_is_its_folder_with_that_program_included_and_launched()
    {
        var folder = Path.Combine(_folder, "game");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Game.exe"), "");
        File.WriteAllText(Path.Combine(folder, "unins000.exe"), "");
        File.WriteAllText(Path.Combine(folder, "Helper.exe"), "");
        var store = Open();

        // The one picked is in even where the scan would leave it out: the person chose it.
        var item = store.CreateFromProgramFolder("Game", Path.Combine(folder, "unins000.exe"), "ad50a53b0da826ad");

        Assert.Equal(Path.GetFullPath(folder), item.Folder);
        Assert.Equal("ad50a53b0da826ad", item.ProfileId);
        var picked = Assert.Single(item.Programs, p => p.Path.EndsWith("unins000.exe", StringComparison.Ordinal));
        Assert.True(picked is { Included: true, ChosenByPerson: true, Packets: true });
        Assert.Equal(picked.Id, item.LaunchProgramId);
        Assert.Equal(new[] { "Game.exe", "Helper.exe", "unins000.exe" }, item.Programs.Select(p => Path.GetFileName(p.Path)).Order());
        Assert.Equal(item, store.Find(item.Id));
    }

    [Theory]
    [InlineData("0123456789abcdef", true)]
    [InlineData("a1b2c3d4e5f6", true)]
    [InlineData("..\\..\\x", false)]
    [InlineData("0123456789ABCDEF", false)]
    public void Only_store_ids_are_ids(string id, bool valid) => Assert.Equal(valid, RoutedItemStore.IsId(id));
}
