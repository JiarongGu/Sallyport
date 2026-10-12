using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Sallyport.Control;

/// <summary>Each side's random contribution to the data-channel keys. Only the client sends a pre-master secret.</summary>
internal sealed class KeySource(byte[]? preMaster, byte[] random1, byte[] random2)
{
    public const int PreMasterLength = 48, RandomLength = 32;

    public byte[]? PreMaster { get; } = preMaster;
    public byte[] Random1 { get; } = random1;
    public byte[] Random2 { get; } = random2;

    public static KeySource ForClient() => new(
        RandomNumberGenerator.GetBytes(PreMasterLength), RandomNumberGenerator.GetBytes(RandomLength), RandomNumberGenerator.GetBytes(RandomLength));

    public static KeySource ForServer() => new(null, RandomNumberGenerator.GetBytes(RandomLength), RandomNumberGenerator.GetBytes(RandomLength));
}

/// <summary>A key-method-2 message: the first thing each side writes once TLS is up.</summary>
/// <remarks>
/// Layout: a 4-byte zero, the byte 2, the key source (pre-master only from the client), the options string, then —
/// from the client — username, password and peer info. A string is a 2-byte length that counts a trailing NUL, the
/// bytes, and the NUL; an empty string is the length 0 alone.
/// </remarks>
internal sealed class KeyMethod2Message(KeySource source, string options, IReadOnlyList<string> trailing)
{
    public KeySource Source { get; } = source;
    public string Options { get; } = options;
    /// <summary>The strings after the options — username, password, peer info from a client; whatever a server adds.</summary>
    public IReadOnlyList<string> Trailing { get; } = trailing;

    public static byte[] Write(KeySource source, string options, params string[] trailing)
    {
        using var buffer = new MemoryStream();
        buffer.Write([0, 0, 0, 0, 2]);
        if (source.PreMaster is not null) buffer.Write(source.PreMaster);
        buffer.Write(source.Random1);
        buffer.Write(source.Random2);
        WriteString(buffer, options);
        foreach (var s in trailing) WriteString(buffer, s);
        return buffer.ToArray();
    }

    /// <summary>Parses a whole message. False when <paramref name="data"/> does not yet hold one; a malformed one throws.</summary>
    /// <remarks>The trailing strings are read greedily from what is there: a message is written in one piece, and arrives
    /// as one TLS record, so nothing of the next message can already be waiting behind it.</remarks>
    public static bool TryRead(ReadOnlySpan<byte> data, bool fromClient, out KeyMethod2Message? message, out int consumed)
    {
        message = null;
        consumed = 0;
        var keyLength = (fromClient ? KeySource.PreMasterLength : 0) + 2 * KeySource.RandomLength;
        if (data.Length < 5 + keyLength) return false;
        if (BinaryPrimitives.ReadUInt32BigEndian(data) != 0 || data[4] != 2)
            throw new FormatException("not a key-method-2 message");
        var at = 5;
        var preMaster = fromClient ? data.Slice(at, KeySource.PreMasterLength).ToArray() : null;
        at += fromClient ? KeySource.PreMasterLength : 0;
        var random1 = data.Slice(at, KeySource.RandomLength).ToArray(); at += KeySource.RandomLength;
        var random2 = data.Slice(at, KeySource.RandomLength).ToArray(); at += KeySource.RandomLength;
        if (!TryReadString(data, ref at, out var options)) return false;
        var trailing = new List<string>();
        while (TryReadString(data, ref at, out var s)) trailing.Add(s);
        message = new KeyMethod2Message(new KeySource(preMaster, random1, random2), options, trailing);
        consumed = at;
        return true;
    }

    private static void WriteString(Stream to, string value)
    {
        if (value.Length == 0) { to.Write([0, 0]); return; }
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)(bytes.Length + 1)));
        to.Write(length);
        to.Write(bytes);
        to.WriteByte(0);
    }

    private static bool TryReadString(ReadOnlySpan<byte> data, ref int at, out string value)
    {
        value = "";
        if (data.Length < at + 2) return false;
        int length = BinaryPrimitives.ReadUInt16BigEndian(data[at..]);
        if (data.Length < at + 2 + length) return false;
        var bytes = data.Slice(at + 2, length);
        if (bytes.Length > 0 && bytes[^1] == 0) bytes = bytes[..^1];
        value = Encoding.UTF8.GetString(bytes);
        at += 2 + length;
        return true;
    }
}
