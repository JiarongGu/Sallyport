using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Sallyport.Net;

/// <summary>DNS queries and answers (RFC 1035), just the A/AAAA lookups a proxy needs. Names go to the tunnel's resolver,
/// never the machine's, so a lookup cannot leak outside the tunnel.</summary>
internal static class DnsMessage
{
    public const ushort TypeA = 1, TypeAaaa = 28;

    public static byte[] Query(ushort id, string name, ushort type)
    {
        var buffer = new List<byte>(32 + name.Length);
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        header[2] = 0x01;   // recursion desired
        header[5] = 1;      // one question
        buffer.AddRange(header);
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length is 0 or > 63) throw new ArgumentException($"\"{name}\" is not a DNS name", nameof(name));
            buffer.Add((byte)bytes.Length);
            buffer.AddRange(bytes);
        }
        buffer.Add(0);
        buffer.AddRange([(byte)(type >> 8), (byte)type, 0, 1]);
        return [.. buffer];
    }

    /// <summary>Reads the response to query <paramref name="id"/>: its response code and every answer of
    /// <paramref name="type"/>. False when the message is not that response or does not parse.</summary>
    public static bool TryReadAnswers(ReadOnlySpan<byte> message, ushort id, ushort type, out int responseCode, out List<IPAddress> addresses) =>
        TryReadAnswers(message, id, type, out responseCode, out addresses, out _);

    /// <summary>As above, with <c>ttl</c>: the shortest time-to-live among the matching answers, in seconds — how long
    /// they may be cached.</summary>
    public static bool TryReadAnswers(ReadOnlySpan<byte> message, ushort id, ushort type, out int responseCode, out List<IPAddress> addresses, out uint ttl) =>
        TryReadAnswers(message, id, type, out responseCode, out addresses, out ttl, out _, out _);

    /// <summary>As above, with what it answers — its first question's name, without the trailing dot — and whether the
    /// server cut it short (TC): a truncated answer may hold none of the addresses there are.</summary>
    public static bool TryReadAnswers(ReadOnlySpan<byte> message, ushort id, ushort type, out int responseCode, out List<IPAddress> addresses, out uint ttl,
        out string? question, out bool truncated)
    {
        responseCode = -1;
        addresses = [];
        ttl = uint.MaxValue;
        question = null;
        truncated = false;
        if (message.Length < 12 || BinaryPrimitives.ReadUInt16BigEndian(message) != id || (message[2] & 0x80) == 0) return false;
        truncated = (message[2] & 0x02) != 0;
        responseCode = message[3] & 0x0f;
        int questions = BinaryPrimitives.ReadUInt16BigEndian(message[4..]);
        int answers = BinaryPrimitives.ReadUInt16BigEndian(message[6..]);
        var at = 12;
        for (var i = 0; i < questions; i++)
        {
            if (i == 0) question = ReadName(message, at);
            if (!SkipName(message, ref at) || at + 4 > message.Length) return false;
            at += 4;
        }
        for (var i = 0; i < answers; i++)
        {
            if (!SkipName(message, ref at) || at + 10 > message.Length) return false;
            var answerType = BinaryPrimitives.ReadUInt16BigEndian(message[at..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(message[(at + 8)..]);
            at += 10;
            if (at + length > message.Length) return false;
            if (answerType == type && length == (type == TypeA ? 4 : 16))
            {
                addresses.Add(new IPAddress(message.Slice(at, length)));
                ttl = Math.Min(ttl, BinaryPrimitives.ReadUInt32BigEndian(message[(at - 6)..]));
            }
            at += length;
        }
        return true;
    }

    // A name's labels joined by dots, following compression pointers backwards only, so a loop of them cannot hold the
    // reader; null for one that does not read.
    private static string? ReadName(ReadOnlySpan<byte> message, int at)
    {
        var name = new StringBuilder();
        for (var limit = at; at < message.Length;)
        {
            var length = message[at];
            if (length == 0) return name.ToString();
            if ((length & 0xc0) == 0xc0)
            {
                if (at + 1 >= message.Length) return null;
                var target = (length & 0x3f) << 8 | message[at + 1];
                if (target >= limit) return null;
                at = limit = target;
                continue;
            }
            if ((length & 0xc0) != 0 || at + 1 + length > message.Length) return null;
            if (name.Length > 0) name.Append('.');
            name.Append(Encoding.ASCII.GetString(message.Slice(at + 1, length)));
            at += 1 + length;
        }
        return null;
    }

    private static bool SkipName(ReadOnlySpan<byte> message, ref int at)
    {
        while (at < message.Length)
        {
            var length = message[at];
            if (length == 0) { at += 1; return true; }
            // A compression pointer ends the name: two bytes, pointing elsewhere.
            if ((length & 0xc0) == 0xc0) { at += 2; return at <= message.Length; }
            at += 1 + length;
        }
        return false;
    }
}
