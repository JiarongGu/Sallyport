using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sallyport.Runtime;

namespace Sallyport.Client;

/// <summary>What Export and Import ask: which of the three parts, and the password the profiles are sealed with, if any
/// (D26). <see cref="Sealed"/>: whether a password is asked for — always offered on export, asked on import only for a
/// file whose profiles were sealed.</summary>
internal sealed record MoveValues(bool Profiles, bool Apps, bool Connection, string Password, string Confirm, bool Sealed = true);

/// <summary>A profile as an export carries it: its own text sealed with the export's password, its name and when it was
/// first imported in the clear.</summary>
internal sealed record SealedProfile(string Id, string Name, DateTimeOffset Imported, string Sealed);

/// <summary>The profiles of an export, and how their key comes from the password; <see cref="Unsealed"/> when the person
/// exported them without one, each text then as it is.</summary>
internal sealed record SealedProfiles(string Kdf, int Iterations, string Salt, IReadOnlyList<SealedProfile> Items)
{
    public const string Unsealed = "none";

    [JsonIgnore]
    public bool Sealed => Kdf != Unsealed;
}

/// <summary>Settings' connection choices, each profile's own changes, and the defaults for new apps.</summary>
internal sealed record ConnectionPart(PolicyOverride Connection, IReadOnlyDictionary<string, PolicyOverride> Policies, AppDefaults Defaults);

/// <summary>A file of what the person exported, to import on another computer or again on this one.</summary>
internal sealed record BackupFile(string Format, int Version, DateTimeOffset Exported, SealedProfiles? Profiles, IReadOnlyList<RoutedItem>? Apps,
    ConnectionPart? Connection);

/// <summary>
/// The file Export writes and Import reads (D26; the owner: "a export feature so the config can be reimported (and export
/// import can be different area profile/app/connection)"). A stored profile is sealed to the Windows account that imported
/// it, which no other computer has, so an export carries each profile's own text sealed again with a password the person
/// chooses: AES-GCM under a key PBKDF2-SHA256 makes from it, the base library's both (rule 4). The apps and the
/// connection settings hold no secret and are written as they are.
/// </summary>
internal static class Backup
{
    public const string Extension = "sallyport";
    private const string FormatName = "sallyport-export";
    private const int Version = 1;
    // OWASP's figure for PBKDF2-SHA256 (2023): a guess costs as many hashes, for a file that may travel.
    private const int Iterations = 600_000;
    private const int NonceSize = 12, TagSize = 16, KeySize = 32, SaltSize = 16;
    /// <summary>The largest file Import reads: profiles of a few kilobytes and apps' paths come nowhere near it.</summary>
    public const long Largest = 16 << 20;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>The key a password makes for a salt: what seals and opens every profile of one file.</summary>
    public static byte[] Key(string password, byte[] salt, int iterations = Iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, KeySize);

    /// <summary>The profiles sealed with <paramref name="password"/>, or as they are when there is none (the person's
    /// choice, said to them when they make it).</summary>
    public static SealedProfiles Seal(IEnumerable<(StoredProfile Stored, string Text)> profiles, string? password)
    {
        if (password is null)
            return new SealedProfiles(SealedProfiles.Unsealed, 0, "", [.. profiles.Select(p =>
                new SealedProfile(p.Stored.Id, p.Stored.Name, p.Stored.Imported, Convert.ToBase64String(Encoding.UTF8.GetBytes(p.Text))))]);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Key(password, salt);
        try
        {
            using var aes = new AesGcm(key, TagSize);
            return new SealedProfiles("PBKDF2-SHA256", Iterations, Convert.ToBase64String(salt), [.. profiles.Select(p =>
            {
                var plain = Encoding.UTF8.GetBytes(p.Text);
                var sealedBytes = new byte[NonceSize + TagSize + plain.Length];
                var nonce = sealedBytes.AsSpan(0, NonceSize);
                RandomNumberGenerator.Fill(nonce);
                // Its id and name bound in: a sealed text moved onto another entry of the file does not open.
                aes.Encrypt(nonce, plain, sealedBytes.AsSpan(NonceSize + TagSize), sealedBytes.AsSpan(NonceSize, TagSize), Bound(p.Stored.Id, p.Stored.Name));
                CryptographicOperations.ZeroMemory(plain);
                return new SealedProfile(p.Stored.Id, p.Stored.Name, p.Stored.Imported, Convert.ToBase64String(sealedBytes));
            })]);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>Every profile's text, or null when the password does not open them.</summary>
    public static IReadOnlyList<(SealedProfile Entry, string Text)>? Open(SealedProfiles profiles, string password)
    {
        if (!profiles.Sealed) return [.. profiles.Items.Select(e => (e, Encoding.UTF8.GetString(Convert.FromBase64String(e.Sealed))))];
        if (profiles.Kdf != "PBKDF2-SHA256" || profiles.Iterations is < 100_000 or > 10_000_000) throw new InvalidDataException("its profiles are sealed in a way this version does not know");
        var key = Key(password, Convert.FromBase64String(profiles.Salt), profiles.Iterations);
        try
        {
            using var aes = new AesGcm(key, TagSize);
            var opened = new List<(SealedProfile, string)>();
            foreach (var entry in profiles.Items)
            {
                var sealedBytes = Convert.FromBase64String(entry.Sealed);
                if (sealedBytes.Length < NonceSize + TagSize) throw new InvalidDataException("a profile in it is cut short");
                var plain = new byte[sealedBytes.Length - NonceSize - TagSize];
                try { aes.Decrypt(sealedBytes.AsSpan(0, NonceSize), sealedBytes.AsSpan(NonceSize + TagSize), sealedBytes.AsSpan(NonceSize, TagSize), plain, Bound(entry.Id, entry.Name)); }
                catch (AuthenticationTagMismatchException) { return null; }
                opened.Add((entry, Encoding.UTF8.GetString(plain)));
                CryptographicOperations.ZeroMemory(plain);
            }
            return opened;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static byte[] Bound(string id, string name) => Encoding.UTF8.GetBytes($"{FormatName}/{Version}/{id}/{name}");

    public static byte[] Write(SealedProfiles? profiles, IReadOnlyList<RoutedItem>? apps, ConnectionPart? connection) =>
        JsonSerializer.SerializeToUtf8Bytes(new BackupFile(FormatName, Version, DateTimeOffset.Now, profiles, apps, connection), Json);

    /// <summary>The file, read. Throws <see cref="InvalidDataException"/> for anything that is not one this version reads,
    /// saying why without quoting it.</summary>
    public static BackupFile Read(byte[] bytes)
    {
        BackupFile? file;
        try { file = JsonSerializer.Deserialize<BackupFile>(bytes, Json); }
        catch (JsonException) { throw new InvalidDataException("it is not a Sallyport export"); }
        if (file is null || file.Format != FormatName) throw new InvalidDataException("it is not a Sallyport export");
        if (file.Version > Version) throw new InvalidDataException($"it was written by a newer Sallyport (version {file.Version})");
        if (file.Profiles is null && file.Apps is null && file.Connection is null) throw new InvalidDataException("it holds nothing to import");
        return file;
    }
}
