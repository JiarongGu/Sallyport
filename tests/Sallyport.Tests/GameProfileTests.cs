using Sallyport.Runtime;

namespace Sallyport.Tests;

public sealed class GameProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sallyport-games-" + Guid.NewGuid().ToString("N"));
    private string Folder => Path.Combine(_root, "Game");
    private string Store => Path.Combine(_root, "store");

    public GameProfileTests()
    {
        // A game's folder as they come: the game, an in-game browser beside it, a crash handler, an uninstaller, a redist.
        Directory.CreateDirectory(Path.Combine(Folder, "Browser"));
        Directory.CreateDirectory(Path.Combine(Folder, "_Redist"));
        foreach (var file in new[] { "Game.exe", @"Browser\GameBrowser.exe", "UnityCrashHandler64.exe", "unins000.exe", @"_Redist\vc_redist.x64.exe", "notes.txt" })
            File.WriteAllText(Path.Combine(Folder, file), "");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void A_scan_finds_every_program_under_the_folder_and_leaves_the_utilities_out_with_a_reason()
    {
        var found = GameFolder.Scan(Folder);

        Assert.Equal(5, found.Count);
        Assert.True(Find(found, "Game.exe").Included);
        Assert.True(Find(found, "GameBrowser.exe").Included);
        Assert.Equal("reports crashes", Find(found, "UnityCrashHandler64.exe") is { Included: false } c ? c.Reason : "included");
        Assert.Equal("installs or removes software", Find(found, "unins000.exe") is { Included: false } u ? u.Reason : "included");
        Assert.Equal("a runtime redistributable", Find(found, "vc_redist.x64.exe") is { Included: false } r ? r.Reason : "included");
    }

    [Theory]
    [InlineData("ZenlessZoneZero.exe", null)]
    [InlineData("ZFGameBrowser.exe", null)]
    [InlineData("HYP.exe", null)]
    [InlineData("Uninstall.exe", "installs or removes software")]
    [InlineData("setup.exe", "installs or removes software")]
    [InlineData("vcredist_x64.exe", "a runtime redistributable")]
    [InlineData("CrashReportClient.exe", "reports crashes")]
    [InlineData("BugReporter.exe", "reports crashes")]
    [InlineData("APM4webCrashR.exe", "reports crashes")]
    [InlineData("crashreport.exe", "reports crashes")]
    public void Programs_are_left_out_by_name_only_when_they_cannot_be_the_game(string file, string? reason) =>
        Assert.Equal(reason, GameFolder.LeftOutReason(file));

    [Fact]
    public void A_profile_keeps_the_persons_choices_across_a_rescan_and_takes_new_programs_at_their_default()
    {
        var store = new GameProfileStore(Store);
        var game = store.Create("Test game", Folder);
        var browser = Find(game.Executables, "GameBrowser.exe").Path;
        var crash = Find(game.Executables, "UnityCrashHandler64.exe").Path;

        store.Choose(game.Id, browser, included: false);
        store.Choose(game.Id, crash, included: true);
        File.WriteAllText(Path.Combine(Folder, "GameUpdater.exe"), "");
        File.Delete(Path.Combine(Folder, "unins000.exe"));
        var rescanned = store.Rescan(game.Id);

        Assert.False(Find(rescanned.Executables, "GameBrowser.exe").Included);
        Assert.True(Find(rescanned.Executables, "UnityCrashHandler64.exe").Included);
        Assert.True(Find(rescanned.Executables, "GameUpdater.exe").Included);
        Assert.DoesNotContain(rescanned.Executables, e => e.Path.EndsWith("unins000.exe", StringComparison.OrdinalIgnoreCase));
        // What was saved is what is read back.
        Assert.Equal(rescanned.Executables, store.Find(game.Id)!.Executables);
    }

    [Fact]
    public void Only_included_programs_are_routed()
    {
        var store = new GameProfileStore(Store);
        var game = store.Create("Test game", Folder);

        Assert.Equal(["Game.exe", "GameBrowser.exe"], game.IncludedPaths.Select(Path.GetFileName).Order());
    }

    [Fact]
    public void A_program_from_elsewhere_can_be_added_and_a_stranger_cannot_be_chosen()
    {
        var store = new GameProfileStore(Store);
        var game = store.Create("Test game", Folder);
        var launcher = Path.Combine(_root, "Launcher.exe");
        File.WriteAllText(launcher, "");

        var added = store.Add(game.Id, launcher);

        Assert.True(Find(added.Executables, "Launcher.exe").Included);
        Assert.Throws<ArgumentException>(() => store.Choose(game.Id, Path.Combine(_root, "other.exe"), true));
        Assert.Throws<ArgumentException>(() => store.Add(game.Id, Path.Combine(_root, "missing.exe")));
    }

    [Fact]
    public void Ids_from_outside_are_checked_not_trusted()
    {
        var store = new GameProfileStore(Store);

        Assert.Null(store.Find(@"..\..\x"));
        Assert.False(store.Remove(@"..\..\x"));
        Assert.Throws<ArgumentException>(() => store.Create("", Folder));
        Assert.Throws<DirectoryNotFoundException>(() => store.Create("Missing", Path.Combine(_root, "nowhere")));
    }

    private static GameExecutable Find(IEnumerable<GameExecutable> executables, string file) =>
        executables.Single(e => string.Equals(Path.GetFileName(e.Path), file, StringComparison.OrdinalIgnoreCase));
}
