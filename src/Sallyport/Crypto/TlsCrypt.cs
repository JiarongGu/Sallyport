using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Sallyport.Crypto;

/// <summary>
/// <c>tls-crypt</c> (v1): every control packet is authenticated and encrypted with a pre-shared static key before the
/// TLS handshake starts, so a server answers nothing to a client that does not hold the key.
/// </summary>
/// <remarks>
/// <para>Wire shape: <c>header ‖ tag ‖ ciphertext</c>. The header is the opcode byte, the 8-byte session id, a 4-byte
/// replay packet id and a 4-byte time (17 bytes, sent in the clear). The tag is HMAC-SHA256 over <c>header ‖
/// plaintext</c>; the ciphertext is AES-256-CTR over the plaintext with the tag's first 16 bytes as the counter block.</para>
/// <para>The 256-byte static key is two halves, each a 64-byte cipher area then a 64-byte HMAC area; AES-256 takes the first
/// 32 bytes of a cipher area and HMAC-SHA256 the first 32 of an HMAC area. The two sides use opposite halves: the
/// server sends with one and the client with the other.</para>
/// </remarks>
public sealed class TlsCrypt
{
    public const int HeaderLength = 1 + 8 + 4 + 4;
    public const int TagLength = 32;

    private readonly byte[] _sendCipher, _sendHmac, _receiveCipher, _receiveHmac;

    /// <param name="staticKey">The 256-byte static key.</param>
    /// <param name="sendHalf">Which half this side SENDS with (0 or 1); it receives with the other. A client sends
    /// with half 1 — <see cref="ForClient"/>.</param>
    public TlsCrypt(byte[] staticKey, int sendHalf)
    {
        if (staticKey.Length != 256) throw new ArgumentException("a tls-crypt key is 256 bytes", nameof(staticKey));
        if (sendHalf is not (0 or 1)) throw new ArgumentOutOfRangeException(nameof(sendHalf));
        var receiveHalf = 1 - sendHalf;
        _sendCipher = staticKey.AsSpan(sendHalf * 128, 32).ToArray();
        _sendHmac = staticKey.AsSpan(sendHalf * 128 + 64, 32).ToArray();
        _receiveCipher = staticKey.AsSpan(receiveHalf * 128, 32).ToArray();
        _receiveHmac = staticKey.AsSpan(receiveHalf * 128 + 64, 32).ToArray();
    }

    /// <summary>The client's side: it sends with the static key's second half (the server's "normal" direction is the first).</summary>
    public static TlsCrypt ForClient(byte[] staticKey) => new(staticKey, sendHalf: 1);

    public byte[] Wrap(ReadOnlySpan<byte> header, ReadOnlySpan<byte> plaintext)
    {
        if (header.Length != HeaderLength) throw new ArgumentException($"the header is {HeaderLength} bytes", nameof(header));
        var tag = Tag(_sendHmac, header, plaintext);
        var packet = new byte[HeaderLength + TagLength + plaintext.Length];
        header.CopyTo(packet);
        tag.CopyTo(packet.AsSpan(HeaderLength));
        AesCtr.Transform(_sendCipher, tag.AsSpan(0, 16), plaintext, packet.AsSpan(HeaderLength + TagLength));
        return packet;
    }

    /// <summary>The plaintext, or null when the packet is too short or its tag does not verify — a packet this side
    /// cannot have been meant to read, which is dropped rather than answered.</summary>
    public byte[]? Unwrap(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < HeaderLength + TagLength) return null;
        var header = packet[..HeaderLength];
        var tag = packet.Slice(HeaderLength, TagLength);
        var ciphertext = packet[(HeaderLength + TagLength)..];
        var plaintext = new byte[ciphertext.Length];
        AesCtr.Transform(_receiveCipher, tag[..16], ciphertext, plaintext);
        var expected = Tag(_receiveHmac, header, plaintext);
        return CryptographicOperations.FixedTimeEquals(expected, tag) ? plaintext : null;
    }

    private static byte[] Tag(byte[] key, ReadOnlySpan<byte> header, ReadOnlySpan<byte> plaintext)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        hmac.AppendData(header);
        hmac.AppendData(plaintext);
        return hmac.GetHashAndReset();
    }
}

/// <summary>AES in counter mode, which .NET does not offer directly: the key stream is the counter block encrypted with
/// ECB, the counter a 128-bit big-endian integer incremented per block.</summary>
internal static class AesCtr
{
    public static void Transform(byte[] key, ReadOnlySpan<byte> initialCounter, ReadOnlySpan<byte> input, Span<byte> output)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        Span<byte> counter = stackalloc byte[16];
        Span<byte> stream = stackalloc byte[16];
        initialCounter.CopyTo(counter);
        for (var offset = 0; offset < input.Length; offset += 16)
        {
            aes.EncryptEcb(counter, stream, PaddingMode.None);
            var n = Math.Min(16, input.Length - offset);
            for (var i = 0; i < n; i++) output[offset + i] = (byte)(input[offset + i] ^ stream[i]);
            for (var i = 15; i >= 0 && ++counter[i] == 0; i--) { }
        }
    }
}

/// <summary>Big-endian helpers the wire format is written in.</summary>
internal static class Wire
{
    public static void PutUInt32(Span<byte> to, uint value) => BinaryPrimitives.WriteUInt32BigEndian(to, value);
    public static uint GetUInt32(ReadOnlySpan<byte> from) => BinaryPrimitives.ReadUInt32BigEndian(from);
}
