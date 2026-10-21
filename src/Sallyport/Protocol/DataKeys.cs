using System.Security.Cryptography;
using System.Text;
using Sallyport.Protocol;

namespace Sallyport.Protocol;

/// <summary>TLS 1.0's pseudo-random function (RFC 2246 §5): P_MD5 over the secret's first half XOR P_SHA1 over its
/// second half. Key method 2 derives the data-channel keys with it (D3).</summary>
internal static class Prf
{
    public static byte[] Tls10(ReadOnlySpan<byte> secret, string label, ReadOnlySpan<byte> seed, int length)
    {
        // An odd-length secret shares its middle byte between the halves.
        var half = (secret.Length + 1) / 2;
        byte[] labelSeed = [.. Encoding.ASCII.GetBytes(label), .. seed];
        var output = PHash(HashAlgorithmName.MD5, secret[..half], labelSeed, length);
        var sha1 = PHash(HashAlgorithmName.SHA1, secret[(secret.Length - half)..], labelSeed, length);
        for (var i = 0; i < length; i++) output[i] ^= sha1[i];
        return output;
    }

    private static byte[] PHash(HashAlgorithmName hash, ReadOnlySpan<byte> secret, byte[] seed, int length)
    {
        var output = new byte[length];
        using var hmac = IncrementalHash.CreateHMAC(hash, secret);
        var a = seed;
        for (var at = 0; at < length;)
        {
            hmac.AppendData(a);
            a = hmac.GetHashAndReset();
            hmac.AppendData(a);
            hmac.AppendData(seed);
            var block = hmac.GetHashAndReset();
            var n = Math.Min(block.Length, length - at);
            block.AsSpan(0, n).CopyTo(output.AsSpan(at));
            at += n;
        }
        return output;
    }
}

/// <summary>The data channel's keys for one direction pair, as key method 2 derives them.</summary>
/// <remarks>
/// <para>The master secret is the PRF over the client's pre-master secret with both sides' first randoms; the key block
/// is the PRF over the master with both second randoms and both session ids, client's first each time. The block is
/// two keys of 128 bytes, each a 64-byte cipher area then a 64-byte HMAC area — the static key's layout again.</para>
/// <para>For an AEAD cipher the HMAC area is not an HMAC key: its first 8 bytes are the implicit half of the nonce.</para>
/// </remarks>
internal sealed class DataKeys
{
    public const int BlockLength = 256, ImplicitIvLength = 8;

    public required byte[] SendKey { get; init; }
    public required byte[] SendImplicitIv { get; init; }
    public required byte[] ReceiveKey { get; init; }
    public required byte[] ReceiveImplicitIv { get; init; }

    public static byte[] Expand(KeySource client, KeySource server, byte[] clientSession, byte[] serverSession)
    {
        if (client.PreMaster is null) throw new ArgumentException("the client's key source carries the pre-master secret", nameof(client));
        var master = Prf.Tls10(client.PreMaster, "OpenVPN master secret", [.. client.Random1, .. server.Random1], 48);
        // Not needed once the block exists: cleared, as far as the collector has not copied it already.
        try { return Prf.Tls10(master, "OpenVPN key expansion", [.. client.Random2, .. server.Random2, .. clientSession, .. serverSession], BlockLength); }
        finally { CryptographicOperations.ZeroMemory(master); }
    }

    /// <param name="block">The 256-byte key block.</param>
    /// <param name="sendHalf">The half this side sends with; it receives with the other.</param>
    /// <param name="keyLength">The cipher's key length: 32 for AES-256-GCM, 16 for AES-128-GCM.</param>
    public static DataKeys FromBlock(byte[] block, int sendHalf, int keyLength)
    {
        var receiveHalf = 1 - sendHalf;
        return new DataKeys
        {
            SendKey = block.AsSpan(sendHalf * 128, keyLength).ToArray(),
            SendImplicitIv = block.AsSpan(sendHalf * 128 + 64, ImplicitIvLength).ToArray(),
            ReceiveKey = block.AsSpan(receiveHalf * 128, keyLength).ToArray(),
            ReceiveImplicitIv = block.AsSpan(receiveHalf * 128 + 64, ImplicitIvLength).ToArray(),
        };
    }
}
