using System.Security.Cryptography;
using Sallyport.Control;
using Sallyport.Crypto;

namespace Sallyport.Data;

/// <summary>
/// The data channel with an AEAD cipher, in <see cref="Opcode.DataV2"/> packets: opcode‖key-id (1), peer id (3),
/// packet id (4), tag (16), ciphertext. The first 8 bytes are the associated data; the nonce is the packet id followed by
/// the direction's 8-byte implicit IV.
/// </summary>
internal sealed class DataChannel : IDisposable
{
    public const int HeaderLength = 8, TagLength = 16, Overhead = HeaderLength + TagLength;

    /// <summary>The ciphers this version implements, with their key lengths, as a server names them.</summary>
    public static readonly IReadOnlyDictionary<string, int> Ciphers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["AES-256-GCM"] = 32,
        ["AES-128-GCM"] = 16,
    };

    private readonly AesGcm _seal, _open;
    private readonly byte[] _sealIv, _openIv;
    // Kept only to diagnose a direction mix-up: a packet that opens under OUR sending key means both sides chose the same half.
    private readonly AesGcm _mirror;
    private readonly byte[] _header = new byte[4];
    private readonly ReplayWindow _replay = new();
    private long _sent;

    public DataChannel(DataKeys keys, uint peerId, byte keyId)
    {
        _seal = new AesGcm(keys.SendKey, TagLength);
        _open = new AesGcm(keys.ReceiveKey, TagLength);
        _mirror = new AesGcm(keys.SendKey, TagLength);
        _sealIv = keys.SendImplicitIv;
        _openIv = keys.ReceiveImplicitIv;
        _header[0] = (byte)(((byte)Opcode.DataV2 << 3) | (keyId & 0x07));
        _header[1] = (byte)(peerId >> 16);
        _header[2] = (byte)(peerId >> 8);
        _header[3] = (byte)peerId;
    }

    public byte[] Seal(ReadOnlySpan<byte> payload)
    {
        // A nonce must never repeat under one key; at 2^32 packets the key has to be renegotiated, not wrapped.
        var id = Interlocked.Increment(ref _sent);
        if (id > uint.MaxValue) throw new InvalidOperationException("the data channel's packet ids are spent; the key needs renegotiating");
        var packet = new byte[Overhead + payload.Length];
        _header.CopyTo(packet, 0);
        Wire.PutUInt32(packet.AsSpan(4), (uint)id);
        Span<byte> nonce = stackalloc byte[12];
        Nonce(nonce, (uint)id, _sealIv);
        _seal.Encrypt(nonce, payload, packet.AsSpan(Overhead), packet.AsSpan(HeaderLength, TagLength), packet.AsSpan(0, HeaderLength));
        return packet;
    }

    public enum Verdict { Opened, Unauthentic, Replayed, OppositeDirection, NotForThisKey }

    public Verdict Open(ReadOnlySpan<byte> packet, out byte[] payload)
    {
        payload = [];
        if (packet.Length < Overhead || (Opcode)(packet[0] >> 3) != Opcode.DataV2) return Verdict.Unauthentic;
        if ((packet[0] & 0x07) != (_header[0] & 0x07)) return Verdict.NotForThisKey;
        var id = Wire.GetUInt32(packet[4..]);
        Span<byte> nonce = stackalloc byte[12];
        Nonce(nonce, id, _openIv);
        var plaintext = new byte[packet.Length - Overhead];
        try
        {
            _open.Decrypt(nonce, packet[Overhead..], packet.Slice(HeaderLength, TagLength), plaintext, packet[..HeaderLength]);
        }
        catch (AuthenticationTagMismatchException)
        {
            Nonce(nonce, id, _sealIv);
            try
            {
                _mirror.Decrypt(nonce, packet[Overhead..], packet.Slice(HeaderLength, TagLength), plaintext, packet[..HeaderLength]);
                return Verdict.OppositeDirection;
            }
            catch (AuthenticationTagMismatchException) { return Verdict.Unauthentic; }
        }
        // The window moves only on authentic packets, or a forged id could push genuine ones out of it.
        if (!_replay.Accept(id)) return Verdict.Replayed;
        payload = plaintext;
        return Verdict.Opened;
    }

    private static void Nonce(Span<byte> nonce, uint id, byte[] implicitIv)
    {
        Wire.PutUInt32(nonce, id);
        implicitIv.CopyTo(nonce[4..]);
    }

    public void Dispose()
    {
        _seal.Dispose();
        _open.Dispose();
        _mirror.Dispose();
    }
}

/// <summary>Accepts each packet id once, within a window behind the highest seen; ids may arrive out of order, since UDP
/// reorders.</summary>
internal sealed class ReplayWindow
{
    public const int Size = 64;
    private readonly object _gate = new();
    private uint _highest;
    private ulong _seen;   // bit i: (_highest - i) has been accepted

    public bool Accept(uint id)
    {
        lock (_gate)
        {
            if (id == 0) return false;
            if (id > _highest)
            {
                var shift = id - _highest;
                _seen = shift >= Size ? 0 : _seen << (int)shift;
                _seen |= 1;
                _highest = id;
                return true;
            }
            var behind = _highest - id;
            if (behind >= Size) return false;
            var bit = 1UL << (int)behind;
            if ((_seen & bit) != 0) return false;
            _seen |= bit;
            return true;
        }
    }
}

/// <summary>Payloads the data channel carries that are not IP packets.</summary>
internal static class Signals
{
    /// <summary>A keepalive: the payload of a ping, recognised by these exact 16 bytes.</summary>
    public static readonly byte[] Ping = Convert.FromHexString("2a187bf3641eb4cb07ed2d0a981fc748");
    /// <summary>The prefix of an options-consistency message; the byte after it says which.</summary>
    public static readonly byte[] Occ = Convert.FromHexString("287f346bd4ef7a812d56b8d3afc5459c");
    public const byte OccExit = 6;

    public static bool IsPing(ReadOnlySpan<byte> payload) => payload.SequenceEqual(Ping);
    public static bool IsOcc(ReadOnlySpan<byte> payload, out byte kind)
    {
        kind = payload.Length > Occ.Length && payload.StartsWith(Occ) ? payload[Occ.Length] : (byte)0;
        return kind != 0;
    }
    public static byte[] OccExitMessage() => [.. Occ, OccExit];
}
