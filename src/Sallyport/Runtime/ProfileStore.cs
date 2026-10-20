using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Sallyport.Profile;

namespace Sallyport.Runtime;

/// <summary>Seals a profile at rest and opens it again. The service's seals with DPAPI under its own account (D11), so
/// a copied file opens nowhere else.</summary>
public interface IProfileProtector
{
    byte[] Protect(byte[] plain);
    byte[] Unprotect(byte[] sealedBytes);
}

/// <summary>A stored profile as anyone may see it: never its keys, never its servers' names. <see cref="Remotes"/> are
/// transport and port ("udp/1194"); <see cref="Unsupported"/> says why this version cannot connect with it, or is null.</summary>
public sealed record StoredProfile(string Id, string Name, IReadOnlyList<string> Remotes, DateTimeOffset Imported, string? Unsupported,
    bool NeedsLogin = false);

/// <summary>A profile's text as <see cref="ProfileStore.Preview"/> reads it: its servers' names in the order it lists them
/// (said to the person while it is imported; never written beside its sealed copy), its transports ("udp", "tcp"), why
/// this version cannot connect it or null, whether it signs in with a username and password, the username it carries to
/// sign in with (OpenVPN Access Server writes the person's into the profiles it makes), and <see cref="Account"/>: whose
/// it is, as OpenVPN Connect shows under every profile — that username, else the name on its own certificate.</summary>
public sealed record ProfilePreview(IReadOnlyList<string> Servers, IReadOnlyList<string> Transports, string? Unsupported,
    bool NeedsLogin = false, string? Username = null, string? Account = null);

/// <summary>What is kept of a profile's sign-in, as the window may show it: the username, and whether a password is
/// kept — never the password itself.</summary>
public sealed record LoginState(string? Username, bool PasswordSaved);

/// <summary>
/// Imported copies of profiles (D11). Each is two files: <c>{id}.profile</c>, the text sealed by the protector, and
/// <c>{id}.json</c>, the <see cref="StoredProfile"/> that may be shown; a profile that signs in with a username and
/// password has a third, <c>{id}.login</c>, sealed the same way (D29). The text and the login are never written anywhere
/// unsealed and never appear in an exception's message.
/// </summary>
public sealed class ProfileStore
{
    private const int LongestName = 64;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _directory;
    private readonly IProfileProtector _protector;
    private readonly object _gate = new();

    public ProfileStore(string directory, IProfileProtector protector)
    {
        _directory = Path.GetFullPath(directory);
        _protector = protector;
        Directory.CreateDirectory(_directory);
    }

    public IReadOnlyList<StoredProfile> List()
    {
        lock (_gate)
        {
            var found = new List<StoredProfile>();
            foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
            {
                // An entry whose sealed half is gone, or whose description does not read, is not offered: it could not be used.
                if (!IsId(Path.GetFileNameWithoutExtension(file)) || !File.Exists(Path.ChangeExtension(file, ".profile"))) continue;
                try { if (JsonSerializer.Deserialize<StoredProfile>(File.ReadAllBytes(file), Json) is { } entry) found.Add(entry); }
                catch (JsonException) { }
            }
            return [.. found.OrderBy(p => p.Imported)];
        }
    }

    public StoredProfile? Find(string id) => List().FirstOrDefault(p => p.Id == id);

    /// <summary>Keeps a sealed copy of <paramref name="text"/> under <paramref name="name"/>. Throws
    /// <see cref="InvalidDataException"/> for text that is not a profile — saying why, never quoting it.</summary>
    public StoredProfile Import(string name, string text)
    {
        name = name.Trim();
        if (name.Length is 0 or > LongestName) throw new ArgumentException($"a profile's name is 1 to {LongestName} characters", nameof(name));
        var profile = Read(text);

        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var entry = new StoredProfile(id, name, [.. profile.Remotes.Select(r => $"{r.Proto}/{r.Port}")], DateTimeOffset.UtcNow,
            Tunnel.Unsupported(profile), profile.AuthUserPass);
        lock (_gate)
        {
            // The sealed text first: a description without it is never listed, so a crash in between leaves nothing usable half-made.
            WriteWhole(FileOf(id, ".profile"), _protector.Protect(Encoding.UTF8.GetBytes(text)));
            WriteWhole(FileOf(id, ".json"), JsonSerializer.SerializeToUtf8Bytes(entry, Json));
        }
        return entry;
    }

    /// <summary>What <paramref name="text"/> holds, read before anything is kept: for the person to look at as they import
    /// it, as OpenVPN Connect shows a profile's server before adding it. Nothing is written; throws as
    /// <see cref="Import"/> does.</summary>
    public static ProfilePreview Preview(string text)
    {
        var profile = Read(text);
        var carried = CarriedUsername.Match(text) is { Success: true } found ? found.Groups[1].Value.Trim() : null;
        return new ProfilePreview([.. profile.Remotes.Select(r => r.Host).Distinct(StringComparer.OrdinalIgnoreCase)],
            [.. profile.Remotes.Select(r => r.Proto).Distinct()], Tunnel.Unsupported(profile), profile.AuthUserPass,
            profile.AuthUserPass ? carried : null, carried ?? CertificateName(profile.CertPem));
    }

    // The name a profile's own certificate gives its holder: OpenVPN Access Server makes it the person's username, and
    // other servers name the client by it.
    private static string? CertificateName(string? pem)
    {
        if (pem is null) return null;
        try
        {
            using var certificate = X509Certificate2.CreateFromPem(pem);
            var name = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException) { return null; }
    }

    // The comment OpenVPN Access Server puts in a person's profile: their username, to sign in with.
    private static readonly System.Text.RegularExpressions.Regex CarriedUsername =
        new(@"^\s*#\s*OVPN_ACCESS_SERVER_USERNAME=(\S+)", System.Text.RegularExpressions.RegexOptions.Multiline);

    /// <summary>The username a profile signs in with and whether its password is kept, from its sealed login.</summary>
    public LoginState LoginOf(string id) => ReadLogin(id) is { } kept ? new(kept.Username, kept.Password is not null) : new(null, false);

    /// <summary>The username and password to connect with, when both are kept; null when either is not.</summary>
    public Login? SavedLogin(string id) =>
        ReadLogin(id) is { Username.Length: > 0, Password: { } password } kept ? new Login(kept.Username, password) : null;

    /// <summary>Keeps <paramref name="username"/>, and <paramref name="password"/> when the person chose to remember it;
    /// null forgets one kept before.</summary>
    public void SetLogin(string id, string username, string? password)
    {
        if (Find(id) is null) throw new FileNotFoundException("no such profile");
        var plain = JsonSerializer.SerializeToUtf8Bytes(new KeptLogin(username.Trim(), password), Json);
        try { lock (_gate) WriteWhole(FileOf(id, ".login"), _protector.Protect(plain)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    /// <summary>The profile under a new name; everything else as it was.</summary>
    public StoredProfile Rename(string id, string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > LongestName) throw new ArgumentException($"a profile's name is 1 to {LongestName} characters", nameof(name));
        lock (_gate)
        {
            var renamed = (Find(id) ?? throw new FileNotFoundException("no such profile")) with { Name = name };
            WriteWhole(FileOf(id, ".json"), JsonSerializer.SerializeToUtf8Bytes(renamed, Json));
            return renamed;
        }
    }

    private KeptLogin? ReadLogin(string id)
    {
        var path = FileOf(id, ".login");
        byte[] sealedBytes;
        lock (_gate)
        {
            if (!File.Exists(path)) return null;
            sealedBytes = File.ReadAllBytes(path);
        }
        var plain = _protector.Unprotect(sealedBytes);
        try { return JsonSerializer.Deserialize<KeptLogin>(plain, Json); }
        catch (JsonException) { return null; }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    // A class, not a record: nothing may print the password it holds.
    private sealed class KeptLogin(string username, string? password)
    {
        public string Username { get; } = username;
        public string? Password { get; } = password;
    }

    private static OvpnProfile Read(string text)
    {
        OvpnProfile profile;
        try { profile = OvpnProfile.Parse(text); }
        catch (FormatException) { throw new InvalidDataException("the text is not a profile this version can read: a key block is malformed"); }
        if (profile.Remotes.Count == 0) throw new InvalidDataException("the text is not an OpenVPN profile: it names no remote");
        return profile;
    }

    /// <summary>The profile, opened. Throws <see cref="FileNotFoundException"/> for an id the store does not hold.</summary>
    public OvpnProfile Load(string id) => OvpnProfile.Parse(Text(id));

    /// <summary>The profile's own text, opened — for an export to seal again, never to show or log (rule 2). Throws
    /// <see cref="FileNotFoundException"/> for an id the store does not hold.</summary>
    public string Text(string id)
    {
        byte[] sealedBytes;
        lock (_gate) sealedBytes = File.ReadAllBytes(FileOf(id, ".profile"));
        return Encoding.UTF8.GetString(_protector.Unprotect(sealedBytes));
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            var description = FileOf(id, ".json");
            var sealedFile = FileOf(id, ".profile");
            var existed = File.Exists(description) || File.Exists(sealedFile);
            // The description first, so a half-removed profile is already no longer offered; its login goes with it.
            File.Delete(description);
            File.Delete(sealedFile);
            File.Delete(FileOf(id, ".login"));
            return existed;
        }
    }

    /// <summary>An id is the store's own 16 hex digits and nothing else: one arriving from a management API must not be
    /// able to name a path.</summary>
    private string FileOf(string id, string extension) =>
        IsId(id) ? Path.Combine(_directory, id + extension) : throw new ArgumentException("not a profile id", nameof(id));

    private static bool IsId(string id) => id.Length == 16 && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>Replaces the file whole, so a reader never sees half of one.</summary>
    private static void WriteWhole(string path, byte[] bytes)
    {
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, path, overwrite: true);
    }
}
