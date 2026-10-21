using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sallyport.Client.Tests;

/// <summary>The update (D34): where it looks, what a release's manifest must say, the check's three outcomes, and a
/// download kept only when it is the bytes the manifest lists. The network is a handler answering by address: nothing
/// listens anywhere.</summary>
public sealed class UpdateTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sallyport-update-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private static readonly byte[] Setup = Encoding.ASCII.GetBytes("the setup, which is the program");
    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string Manifest(string version, byte[]? setup = null, string programPath = "Sallyport.Client.exe", string? setupSha = null)
    {
        setup ??= Setup;
        return JsonSerializer.Serialize(new
        {
            version,
            generatedAt = "2026-10-21T00:00:00Z",
            files = new object[]
            {
                new { path = programPath, size = setup.Length, sha256 = Sha(setup) },
                new { path = "WebView2Loader.dll", size = 3, sha256 = Sha([1, 2, 3]) },
            },
            assets = new
            {
                setup = new { name = $"Sallyport-{version}-win-x64-setup.exe", size = setup.Length, sha256 = setupSha ?? Sha(setup) },
                zip = new { name = $"Sallyport-{version}-win-x64.zip", size = 10, sha256 = Sha([9]) },
            },
        });
    }

    // GitHub's release object, as its API answers it: the fields the update reads.
    private static string Release(string tag, bool withManifest = true)
    {
        var download = $"https://github.com/JiarongGu/Sallyport/releases/download/{tag}/";
        var setup = $"Sallyport-{tag.TrimStart('v')}-win-x64-setup.exe";
        var assets = new List<object> { new Dictionary<string, object> { ["name"] = setup, ["size"] = Setup.Length, ["browser_download_url"] = download + setup } };
        if (withManifest) assets.Insert(0, new Dictionary<string, object> { ["name"] = "manifest.json", ["size"] = 900, ["browser_download_url"] = download + "manifest.json" });
        return JsonSerializer.Serialize(new Dictionary<string, object> { ["tag_name"] = tag, ["draft"] = false, ["prerelease"] = false, ["assets"] = assets });
    }

    private sealed class Web(Dictionary<string, Func<HttpResponseMessage>> answers) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked.Add(request.RequestUri!.ToString());
            return Task.FromResult(answers.TryGetValue(request.RequestUri.ToString(), out var answer) ? answer() : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static HttpResponseMessage Body(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    private static HttpResponseMessage Body(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private static HttpResponseMessage Redirect(string to)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(to);
        return response;
    }

    private static readonly Version Current = new(0, 1, 1);

    [Fact]
    public void The_source_is_GitHub_unless_data_or_a_rehearsal_names_another_and_only_https()
    {
        Assert.Equal(UpdateSource.Default, UpdateSource.Resolve(null, null, out _));
        var file = UpdateSource.Resolve(null, """{"source":"https://updates.example.org/sallyport/manifest.json"}""", out _)!;
        Assert.False(file.IsGitHub);
        Assert.Empty(file.AssetHosts);
        // A rehearsal's own server on this computer may be plain HTTP; nothing else may.
        Assert.True(UpdateSource.Resolve("http://127.0.0.1:8123/manifest.json", null, out _) is { IsGitHub: false });
        Assert.Null(UpdateSource.Resolve(null, """{"source":"http://updates.example.org/manifest.json"}""", out var why));
        Assert.Equal("http://updates.example.org/manifest.json is not an https address", why);
        Assert.Null(UpdateSource.Resolve(null, "{not json", out why));
        Assert.Equal("data\\update.json could not be read", why);
        // Its downloads from its own host and those it names, nothing else.
        var github = UpdateSource.Default;
        Assert.True(github.Allows(new Uri("https://objects.githubusercontent.com/x")));
        Assert.False(github.Allows(new Uri("https://evil.example.com/x")));
        Assert.False(github.Allows(new Uri("http://github.com/x")));
    }

    [Theory]
    [InlineData("v0.1.2", "0.1.2")]
    [InlineData("0.1.2+3f9c2e1", "0.1.2")]
    [InlineData("0.2.0-beta", null)]
    [InlineData("latest", null)]
    public void A_version_is_its_three_numbers_and_a_preview_is_not_an_update(string text, string? read) =>
        Assert.Equal(read is null ? null : Version.Parse(read), UpdateVersion.Parse(text));

    [Fact]
    public void A_manifest_is_refused_whose_paths_leave_the_folder_or_whose_setup_is_not_the_program()
    {
        Assert.Equal("0.2.0", ReleaseManifest.Parse(Encoding.UTF8.GetBytes(Manifest("0.2.0"))).Version);
        Assert.Throws<InvalidDataException>(() => ReleaseManifest.Parse(Encoding.UTF8.GetBytes(Manifest("0.2.0", programPath: "../Sallyport.Client.exe"))));
        Assert.Throws<InvalidDataException>(() => ReleaseManifest.Parse(Encoding.UTF8.GetBytes(Manifest("0.2.0", setupSha: Sha([7])))));
        Assert.Throws<InvalidDataException>(() => ReleaseManifest.Parse("""{"version":"0.2.0","files":[]}"""u8));
    }

    [Fact]
    public async Task GitHubs_latest_no_newer_than_this_is_up_to_date()
    {
        var web = new Web(new() { [UpdateSource.DefaultFeed] = () => Body(Release("v0.1.0", withManifest: false)) });

        Assert.Null(await new Updater(UpdateSource.Default, web).CheckAsync(Current));
        // The manifest is not asked for when there is nothing newer.
        Assert.Single(web.Asked);
    }

    [Fact]
    public async Task A_newer_release_is_offered_with_the_setup_of_that_same_release()
    {
        var web = new Web(new()
        {
            [UpdateSource.DefaultFeed] = () => Body(Release("v0.2.0")),
            // GitHub's asset links redirect to its storage: followed here, to a host it allows.
            ["https://github.com/JiarongGu/Sallyport/releases/download/v0.2.0/manifest.json"] = () => Redirect("https://objects.githubusercontent.com/m/manifest.json"),
            ["https://objects.githubusercontent.com/m/manifest.json"] = () => Body(Manifest("0.2.0")),
        });

        var offer = await new Updater(UpdateSource.Default, web).CheckAsync(Current);

        Assert.Equal(new Version(0, 2, 0), offer!.Version);
        Assert.Equal("https://github.com/JiarongGu/Sallyport/releases/download/v0.2.0/Sallyport-0.2.0-win-x64-setup.exe", offer.Setup.ToString());
    }

    [Fact]
    public async Task What_cannot_be_told_is_said_as_that_never_as_up_to_date()
    {
        async Task<string> Why(Dictionary<string, Func<HttpResponseMessage>> answers) =>
            (await Assert.ThrowsAsync<UpdateUnavailable>(() => new Updater(UpdateSource.Default, new Web(answers)).CheckAsync(Current))).Message;

        Assert.Equal("the server answered 404", await Why([]));
        Assert.Equal("the release feed's answer is not a release", await Why(new() { [UpdateSource.DefaultFeed] = () => Body("<html>") }));
        Assert.Equal("release v0.2.0 has no manifest.json", await Why(new() { [UpdateSource.DefaultFeed] = () => Body(Release("v0.2.0", withManifest: false)) }));
        Assert.Equal("release v0.2.0's manifest is for 0.1.9", await Why(new()
        {
            [UpdateSource.DefaultFeed] = () => Body(Release("v0.2.0")),
            ["https://github.com/JiarongGu/Sallyport/releases/download/v0.2.0/manifest.json"] = () => Body(Manifest("0.1.9")),
        }));
        Assert.Equal("evil.example.com is not a host this update comes from", await Why(new()
        {
            [UpdateSource.DefaultFeed] = () => Body(Release("v0.2.0")),
            ["https://github.com/JiarongGu/Sallyport/releases/download/v0.2.0/manifest.json"] = () => Redirect("https://evil.example.com/manifest.json"),
        }));
    }

    [Fact]
    public async Task Another_hosts_manifest_names_its_setup_beside_it()
    {
        var source = UpdateSource.Resolve(null, """{"source":"https://updates.example.org/sallyport/manifest.json"}""", out _)!;
        var web = new Web(new() { ["https://updates.example.org/sallyport/manifest.json"] = () => Body(Manifest("0.3.0")) });

        var offer = await new Updater(source, web).CheckAsync(Current);

        Assert.Equal("https://updates.example.org/sallyport/Sallyport-0.3.0-win-x64-setup.exe", offer!.Setup.ToString());
    }

    [Fact]
    public async Task A_download_is_kept_only_when_it_is_the_bytes_the_manifest_lists_and_is_held_after()
    {
        var manifest = ReleaseManifest.Parse(Encoding.UTF8.GetBytes(Manifest("0.2.0")));
        var setupUri = new Uri("https://github.com/JiarongGu/Sallyport/releases/download/v0.2.0/Sallyport-0.2.0-win-x64-setup.exe");
        var offer = new UpdateOffer(new Version(0, 2, 0), manifest, setupUri);

        await using (var held = await new Updater(UpdateSource.Default, new Web(new() { [setupUri.ToString()] = () => Body(Setup) })).DownloadAsync(offer, _folder))
        {
            Assert.Equal(Setup, File.ReadAllBytes(held.Name));
            // Held: read it may be, changed it may not.
            Assert.Throws<IOException>(() => File.WriteAllBytes(held.Name, [0]));
        }

        var wrong = Setup.ToArray();
        wrong[0] ^= 1;
        var e = await Assert.ThrowsAsync<InvalidDataException>(() => new Updater(UpdateSource.Default, new Web(new() { [setupUri.ToString()] = () => Body(wrong) })).DownloadAsync(offer, _folder));
        Assert.Equal("the download is not the setup the release lists", e.Message);
        var longer = Setup.Concat(new byte[] { 0 }).ToArray();
        e = await Assert.ThrowsAsync<InvalidDataException>(() => new Updater(UpdateSource.Default, new Web(new() { [setupUri.ToString()] = () => Body(longer) })).DownloadAsync(offer, _folder));
        Assert.Equal("the download is larger than the release says", e.Message);
        // Neither is left behind, finished or not.
        Assert.Equal(["Sallyport-0.2.0-win-x64-setup.exe"], Directory.EnumerateFiles(_folder).Select(Path.GetFileName));
    }
}
