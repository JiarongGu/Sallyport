using Sallyport.Observer;

namespace Sallyport.Observer.Tests;

public sealed class LearnOptionsTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("3601")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("forever")]
    public void DeadlineMustBeBoundedWholeSeconds(string duration) => Assert.Throws<ArgumentException>(() =>
        LearnOptions.Parse(["learn", "--exe", Path.GetFullPath("game.exe"), "--for", duration]));

    [Fact]
    public void MultipleExecutablesAndRepeatedExeFlagsAreSupported()
    {
        string game = Path.GetFullPath("game.exe");
        string helper = Path.GetFullPath("helper.exe");
        LearnOptions options = LearnOptions.Parse(["learn", "--exe", game, helper, "--exe", game.ToUpperInvariant(),
            "--for", "3600", "--json", "observations.json"]);
        Assert.Equal(new[] { game, helper }, options.Executables);
        Assert.Equal(3600, options.Seconds);
        Assert.Equal(Path.GetFullPath("observations.json"), options.JsonPath);
    }

    [Fact]
    public void MissingAmbiguousOrUnknownArgumentsFailBeforeNativeWork()
    {
        string game = Path.GetFullPath("game.exe");
        string[][] invalid = [[], ["learn", "--for", "1"], ["learn", "--exe", game],
            ["learn", "--exe", "relative.exe", "--for", "1"],
            ["learn", "--exe", game, "--for", "1", "--for", "2"],
            ["learn", "--exe", game, "--for", "1", "--json"],
            ["learn", "--exe", game, "--for", "1", "--other"],
            ["learn", "--exe", game, "--for", "1", "--json", game]];
        foreach (string[] args in invalid) Assert.Throws<ArgumentException>(() => LearnOptions.Parse(args));
    }
}
