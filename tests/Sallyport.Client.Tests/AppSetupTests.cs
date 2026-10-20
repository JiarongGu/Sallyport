namespace Sallyport.Client.Tests;

/// <summary>"Add an app" (AppSetup): what it says it found at the place the person chose, before anything is kept.</summary>
public sealed class AppSetupTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sallyport-tests", Guid.NewGuid().ToString("N"));

    public AppSetupTests()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "Game", "Tools"));
        File.WriteAllText(Path.Combine(_folder, "Game", "Game.exe"), "");
        File.WriteAllText(Path.Combine(_folder, "Game", "unins000.exe"), "");
        File.WriteAllText(Path.Combine(_folder, "Game", "Tools", "Helper.exe"), "");
        File.WriteAllText(Path.Combine(_folder, "Game", "readme.txt"), "");
        Directory.CreateDirectory(Path.Combine(_folder, "Empty"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void A_folder_is_its_programs_the_utilities_off_and_says_how_many_go_through()
    {
        var found = AppSetup.Preview(Path.Combine(_folder, "Game"), _ => true, out var why)!;

        Assert.Null(why);
        Assert.Equal((SetupKind.Folder, "Game"), (found.Kind, found.SuggestedName));
        Assert.Equal(["Game.exe", "Helper.exe"], found.Programs.Where(p => p.Included).Select(p => Path.GetFileName(p.Path)).Order());
        Assert.Contains(found.Programs, p => p is { Included: false } && p.Path.EndsWith("unins000.exe", StringComparison.Ordinal));
        Assert.StartsWith("A folder with 3 programs. 2 go through your VPN", found.Summary);
    }

    [Fact]
    public void A_program_in_a_folder_of_its_own_brings_its_folder_and_is_always_in()
    {
        var found = AppSetup.Preview(Path.Combine(_folder, "Game", "unins000.exe"), _ => true, out _)!;

        Assert.Equal(SetupKind.Folder, found.Kind);
        Assert.True(Assert.Single(found.Programs, p => p.Path.EndsWith("unins000.exe", StringComparison.Ordinal)).Included);
    }

    [Fact]
    public void A_program_in_a_shared_folder_is_itself_alone()
    {
        var found = AppSetup.Preview($"\"{Path.Combine(_folder, "Game", "Game.exe")}\"", _ => false, out _)!;

        Assert.Equal(SetupKind.Program, found.Kind);
        Assert.Single(found.Programs);
    }

    [Theory]
    [InlineData("", "Choose a game's folder or a program.")]
    [InlineData("Empty", "There is no program in that folder. Choose the folder the game is installed in.")]
    [InlineData("Game\\readme.txt", "That file is not a program. Choose a game's folder or its .exe.")]
    [InlineData("Nowhere", "Nothing is there. Check the path, or use Browse.")]
    public void What_is_not_an_app_says_why(string place, string reason)
    {
        Assert.Null(AppSetup.Preview(place.Length == 0 ? "" : Path.Combine(_folder, place), _ => true, out var why));
        Assert.Equal(reason, why);
    }
}
