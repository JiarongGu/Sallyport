using System.Diagnostics;

namespace Sallyport.Client.Tests;

/// <summary>Setup's own checks, which run as administrator: what it will take as a folder, and how it walks one.</summary>
public sealed class SetupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sallyport-setup-tests-" + Guid.NewGuid().ToString("N"));

    public SetupTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void A_folder_whose_path_holds_a_percent_sign_is_refused()
    {
        // Windows' command line, which takes what was in use after setup exits, would read %NAME% as a variable.
        Assert.Contains("%", Installer.Unfit(Path.Combine(_root, "a%USERNAME%b")));
        Assert.Null(Installer.Unfit(Path.Combine(_root, "Sallyport")));
    }

    [Fact]
    public void A_walk_through_a_folder_lists_a_junction_and_never_enters_it()
    {
        var area = Directory.CreateDirectory(Path.Combine(_root, "area"));
        var elsewhere = Directory.CreateDirectory(Path.Combine(_root, "elsewhere"));
        File.WriteAllText(Path.Combine(elsewhere.FullName, "not-ours.txt"), "x");
        File.WriteAllText(Path.Combine(area.FullName, "ours.txt"), "x");
        var link = Path.Combine(area.FullName, "link");
        // A junction needs no privilege: any account can make one where it may write, as in the shared areas.
        using (var made = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{elsewhere.FullName}\"")
               { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
            made.WaitForExit();
        Assert.True(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint));

        var walked = Installer.Within(area).Select(e => e.Name).Order().ToList();

        Assert.Equal(["link", "ours.txt"], walked);
    }

    public void Dispose()
    {
        // The junction first, as itself: a recursive delete of the root must not reach through it.
        var link = Path.Combine(_root, "area", "link");
        if (Directory.Exists(link)) Directory.Delete(link);
        Directory.Delete(_root, recursive: true);
    }
}
