using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sallyport.Client;

/// <summary>
/// Where updates are looked for (D34; the owner: "support app update (via github for now) and the address should be able
/// to change in config (so we might use other host in future)"): a GitHub release feed (<c>…/releases/latest</c>, whose
/// assets hold <c>manifest.json</c>), or any host's <c>manifest.json</c>, the release's files beside it. A download may
/// come only from the address's own host and <see cref="AssetHosts"/>.
/// </summary>
internal sealed record UpdateSource(Uri Address, IReadOnlyList<string> AssetHosts)
{
    public const string DefaultFeed = "https://api.github.com/repos/JiarongGu/Sallyport/releases/latest";

    /// <summary>Where GitHub serves a release's assets from, after its redirects.</summary>
    public static readonly IReadOnlyList<string> GitHubHosts = ["github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"];

    public static UpdateSource Default { get; } = new(new Uri(DefaultFeed), GitHubHosts);

    public bool IsGitHub => Address.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>The source in force: <paramref name="environment"/> (<c>SALLYPORT_UPDATE_SOURCE</c>, for a rehearsal),
    /// else <paramref name="file"/> (<c>data\update.json</c>, which only an administrator can change: what it names is run
    /// as administrator), else GitHub's releases of Sallyport. An address and its hosts are taken from one place, never
    /// mixed. Null with <paramref name="why"/> where the one given is not an address an update may come from.</summary>
    public static UpdateSource? Resolve(string? environment, string? file, out string? why)
    {
        why = null;
        if (!string.IsNullOrWhiteSpace(environment)) return From(environment.Trim(), null, out why);
        if (file is null) return Default;
        try
        {
            var read = JsonSerializer.Deserialize<UpdateFile>(file, Json);
            if (read?.Source is not { } source) { why = L.T("data\\update.json names no source"); return null; }
            return From(source, read.AssetHosts, out why);
        }
        catch (JsonException) { why = L.T("data\\update.json could not be read"); return null; }
    }

    private static UpdateSource? From(string address, IReadOnlyList<string>? hosts, out string? why)
    {
        why = null;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || !Secure(uri)) { why = L.T("{0} is not an https address", address); return null; }
        var source = new UpdateSource(uri, []);
        // GitHub's feed brings GitHub's asset hosts with it; any other host serves its own files unless its file names more.
        return source with { AssetHosts = hosts ?? (source.IsGitHub ? GitHubHosts : []) };
    }

    /// <summary>HTTPS, or plain HTTP to this computer alone (a rehearsal's own server).</summary>
    public static bool Secure(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);

    /// <summary>Whether a download may come from <paramref name="uri"/>: securely, from the address's host or one named.</summary>
    public bool Allows(Uri uri) => Secure(uri)
        && (uri.Host.Equals(Address.Host, StringComparison.OrdinalIgnoreCase) || AssetHosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase)));

    private sealed record UpdateFile(string? Source, IReadOnlyList<string>? AssetHosts);

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

/// <summary>One file of the installed folder, as the release's manifest lists it (D33).</summary>
internal sealed record ReleaseFile(string Path, long Size, string Sha256);

/// <summary>One of the release's downloads.</summary>
internal sealed record ReleaseAsset(string Name, long Size, string Sha256);

internal sealed record ReleaseAssets(ReleaseAsset Setup, ReleaseAsset? Zip);

/// <summary>A release's <c>manifest.json</c> (D33): its version, each installed file's size and SHA-256, and its downloads'.</summary>
internal sealed record ReleaseManifest(string Version, string? GeneratedAt, IReadOnlyList<ReleaseFile> Files, ReleaseAssets Assets)
{
    /// <summary>The installed program, which is the setup (D30): the same bytes, so one digest checks both.</summary>
    public const string Program = "Sallyport.Client.exe";

    /// <summary>The largest manifest read: three files' lines come nowhere near it.</summary>
    public const int Largest = 1 << 20;

    /// <summary>A manifest read and held to what an update needs of it; <see cref="InvalidDataException"/> saying why not.</summary>
    public static ReleaseManifest Parse(ReadOnlySpan<byte> json)
    {
        ReleaseManifest? read;
        try { read = JsonSerializer.Deserialize<ReleaseManifest>(json, UpdateSource.Json); }
        catch (JsonException e) { throw new InvalidDataException($"the manifest is not JSON ({e.Message})"); }
        if (read is null || string.IsNullOrWhiteSpace(read.Version) || read.Files is not { Count: > 0 } || read.Assets?.Setup is null)
            throw new InvalidDataException("the manifest names no version, files or setup");
        foreach (var file in read.Files)
            if (!Inside(file.Path) || !Digest(file.Sha256) || file.Size <= 0) throw new InvalidDataException($"the manifest's entry {file.Path} is not a file of the folder");
        var setup = read.Assets.Setup;
        if (!Plain(setup.Name) || !Digest(setup.Sha256) || setup.Size <= 0) throw new InvalidDataException("the manifest's setup is not a download");
        if (read.Files.FirstOrDefault(f => f.Path == Program) is not { } program || !program.Sha256.Equals(setup.Sha256, StringComparison.OrdinalIgnoreCase)
            || program.Size != setup.Size)
            throw new InvalidDataException("the manifest's setup is not the program it lists");
        return read;
    }

    public Version? Parsed => UpdateVersion.Parse(Version);

    private static bool Digest(string? text) => text is { Length: 64 } && text.All(Uri.IsHexDigit);

    // A path inside the installed folder: no root, no drive, no step up.
    private static bool Inside(string? path) => !string.IsNullOrWhiteSpace(path) && !System.IO.Path.IsPathRooted(path) && !path.Contains(':')
        && !path.Split('/', '\\').Any(part => part is "" or "." or "..");

    // A download's own name, nothing of a path.
    private static bool Plain(string? name) => !string.IsNullOrWhiteSpace(name) && name.IndexOfAny(['/', '\\', ':']) < 0 && name is not "." and not "..";
}

internal static class UpdateVersion
{
    /// <summary>"v0.1.2", "0.1.2" or "0.1.2+commit": its three numbers; null for anything else (a preview's "-beta" is not
    /// an update's).</summary>
    public static Version? Parse(string? text)
    {
        var plain = (text ?? "").Trim().TrimStart('v', 'V').Split('+')[0];
        return System.Version.TryParse(plain, out var version) && version.Build >= 0 ? version : null;
    }
}

/// <summary>A newer release found: its version, its manifest, and where its setup is downloaded from.</summary>
internal sealed record UpdateOffer(Version Version, ReleaseManifest Manifest, Uri Setup);

/// <summary>Why an update could not be checked or fetched, in a sentence; never that there is none.</summary>
internal sealed class UpdateUnavailable(string message) : Exception(message);

/// <summary>
/// The update's network half, as the person's window runs it (no administrator): what the source offers, and the setup
/// fetched and checked against the manifest. HTTPS only (loopback aside); redirects followed here, at most six, each to a
/// host the source allows; a release's files from that same release, never "latest" again. Applying it is setup's: the
/// fetched setup, run as a quiet upgrade.
/// </summary>
internal sealed class Updater(UpdateSource source, HttpMessageHandler? handler = null)
{
    private const int Hops = 6;
    private readonly HttpClient _http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };

    public UpdateSource Source => source;

    /// <summary>The newer release, or null when <paramref name="current"/> is the newest. <see cref="UpdateUnavailable"/>
    /// when it could not be told (rule 6): no answer, or an answer that is not a release.</summary>
    public async Task<UpdateOffer?> CheckAsync(Version current, CancellationToken ct = default)
    {
        try
        {
            if (!source.IsGitHub)
            {
                var manifest = ReleaseManifest.Parse(await ReadAsync(source.Address, ct));
                if (manifest.Parsed is not { } offered) throw new UpdateUnavailable(L.T("the release's version {0} is not one Sallyport reads", manifest.Version));
                return offered > current ? new UpdateOffer(offered, manifest, new Uri(source.Address, manifest.Assets.Setup.Name)) : null;
            }

            var release = JsonSerializer.Deserialize<GitHubRelease>(await ReadAsync(source.Address, ct), UpdateSource.Json)
                ?? throw new UpdateUnavailable(L.T("the release feed said nothing"));
            if (UpdateVersion.Parse(release.TagName) is not { } version) throw new UpdateUnavailable(L.T("the release's version {0} is not one Sallyport reads", release.TagName ?? ""));
            if (version <= current) return null;
            // Everything from this release's own assets: "latest" asked again could be another release.
            var assets = release.Assets ?? [];
            if (assets.FirstOrDefault(a => a.Name == "manifest.json")?.BrowserDownloadUrl is not { } manifestUri)
                throw new UpdateUnavailable(L.T("release {0} has no manifest.json", release.TagName!));
            var read = ReleaseManifest.Parse(await ReadAsync(manifestUri, ct));
            if (read.Parsed != version) throw new UpdateUnavailable(L.T("release {0}'s manifest is for {1}", release.TagName!, read.Version));
            if (assets.FirstOrDefault(a => a.Name == read.Assets.Setup.Name) is not { BrowserDownloadUrl: { } setup } asset || asset.Size != read.Assets.Setup.Size)
                throw new UpdateUnavailable(L.T("release {0} has not the setup its manifest names", release.TagName!));
            return new UpdateOffer(version, read, setup);
        }
        catch (InvalidDataException e) { throw new UpdateUnavailable(e.Message); }
        catch (JsonException) { throw new UpdateUnavailable(L.T("the release feed's answer is not a release")); }
        catch (HttpRequestException e) { throw new UpdateUnavailable(e.StatusCode is { } status ? L.T("the server answered {0}", (int)status) : L.T("no answer ({0})", e.HttpRequestError)); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new UpdateUnavailable(L.T("no answer in time")); }
    }

    /// <summary>The offer's setup, fetched into <paramref name="folder"/> and checked — its size and SHA-256 those of the
    /// manifest's setup and program — then held open so that nothing changes it before it runs: the held file. A file that
    /// is not those bytes is deleted, never kept (<see cref="InvalidDataException"/>).</summary>
    public async Task<FileStream> DownloadAsync(UpdateOffer offer, string folder, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var expected = offer.Manifest.Assets.Setup;
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, expected.Name);
        var partial = path + ".part";
        try
        {
            using (var response = await SendAsync(offer.Setup, ct))
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long total = 0;
                for (int read; (read = await source.ReadAsync(buffer, ct)) > 0;)
                {
                    total += read;
                    // More than the manifest says is not it, however it ends.
                    if (total > expected.Size) throw new InvalidDataException(L.T("the download is larger than the release says"));
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    sha.AppendData(buffer, 0, read);
                    progress?.Report((double)total / expected.Size);
                }
                if (total != expected.Size) throw new InvalidDataException(L.T("the download ended at {0} of {1} bytes", total, expected.Size));
                if (!Convert.ToHexStringLower(sha.GetHashAndReset()).Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(L.T("the download is not the setup the release lists"));
            }
            File.Move(partial, path, overwrite: true);
            // Held, letting others read it and nothing else, until it has run: in a folder others may write (D25), no one can
            // put other bytes in its place between the check and the start. Checked again as held.
            var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!Convert.ToHexStringLower(await SHA256.HashDataAsync(held, ct)).Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                await held.DisposeAsync();
                File.Delete(path);
                throw new InvalidDataException(L.T("the download is not the setup the release lists"));
            }
            return held;
        }
        // No answer, or one that is not the file, is the update unavailable now — said as a check's is (rule 6), never
        // left as a download still under way.
        catch (HttpRequestException e)
        {
            Discard(partial);
            throw new UpdateUnavailable(e.StatusCode is { } status ? L.T("the server answered {0}", (int)status) : L.T("no answer ({0})", e.HttpRequestError));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            Discard(partial);
            throw new UpdateUnavailable(L.T("no answer in time"));
        }
        catch
        {
            Discard(partial);
            throw;
        }
    }

    private static void Discard(string partial)
    {
        try { File.Delete(partial); } catch (IOException) { }
    }

    private async Task<byte[]> ReadAsync(Uri uri, CancellationToken ct)
    {
        using var response = await SendAsync(uri, ct);
        if (response.Content.Headers.ContentLength > ReleaseManifest.Largest) throw new UpdateUnavailable(L.T("the answer is too large"));
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        return bytes.Length > ReleaseManifest.Largest ? throw new UpdateUnavailable(L.T("the answer is too large")) : bytes;
    }

    // One request, its redirects followed here, each hop's host one the source allows.
    private async Task<HttpResponseMessage> SendAsync(Uri uri, CancellationToken ct)
    {
        for (var hop = 0; ; hop++)
        {
            if (!source.Allows(uri)) throw new UpdateUnavailable(L.T("{0} is not a host this update comes from", uri.Host));
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd($"Sallyport/{WindowViews.Version}");
            if (source.IsGitHub && uri.Host.Equals(source.Address.Host, StringComparison.OrdinalIgnoreCase)) request.Headers.Accept.ParseAdd("application/vnd.github+json");
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } next)
            {
                response.Dispose();
                if (hop == Hops) throw new UpdateUnavailable(L.T("too many redirects"));
                uri = next.IsAbsoluteUri ? next : new Uri(uri, next);
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode;
                response.Dispose();
                throw new HttpRequestException(null, null, status);
            }
            return response;
        }
    }

    private sealed record GitHubRelease([property: JsonPropertyName("tag_name")] string? TagName, bool Draft, bool Prerelease, IReadOnlyList<GitHubAsset>? Assets);

    private sealed record GitHubAsset(string? Name, long Size, [property: JsonPropertyName("browser_download_url")] Uri? BrowserDownloadUrl);
}
