using System.Security.Cryptography;
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
public sealed record StoredProfile(string Id, string Name, IReadOnlyList<string> Remotes, DateTimeOffset Imported, string? Unsupported);

/// <summary>
/// Imported copies of profiles (D11). Each is two files: <c>{id}.profile</c>, the text sealed by the protector, and
/// <c>{id}.json</c>, the <see cref="StoredProfile"/> that may be shown. The text is never written anywhere unsealed and
/// never appears in an exception's message.
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
        OvpnProfile profile;
        try { profile = OvpnProfile.Parse(text); }
        catch (FormatException) { throw new InvalidDataException("the text is not a profile this version can read: a key block is malformed"); }
        if (profile.Remotes.Count == 0) throw new InvalidDataException("the text is not an OpenVPN profile: it names no remote");

        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var entry = new StoredProfile(id, name, [.. profile.Remotes.Select(r => $"{r.Proto}/{r.Port}")], DateTimeOffset.UtcNow,
            Tunnel.Unsupported(profile));
        lock (_gate)
        {
            // The sealed text first: a description without it is never listed, so a crash in between leaves nothing usable half-made.
            WriteWhole(FileOf(id, ".profile"), _protector.Protect(Encoding.UTF8.GetBytes(text)));
            WriteWhole(FileOf(id, ".json"), JsonSerializer.SerializeToUtf8Bytes(entry, Json));
        }
        return entry;
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
            // The description first, so a half-removed profile is already no longer offered.
            File.Delete(description);
            File.Delete(sealedFile);
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
