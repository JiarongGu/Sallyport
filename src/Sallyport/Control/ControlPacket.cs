using Sallyport.Crypto;

namespace Sallyport.Control;

/// <summary>The control-channel opcodes this client sends or must recognise (the top 5 bits of a packet's first byte).</summary>
public enum Opcode : byte
{
    ControlSoftResetV1 = 3,
    ControlV1 = 4,
    AckV1 = 5,
    DataV1 = 6,
    ControlHardResetClientV2 = 7,
    ControlHardResetServerV2 = 8,
    DataV2 = 9,
}

/// <summary>
/// One control-channel packet, as carried inside <c>tls-crypt</c>.
/// </summary>
/// <remarks>
/// Clear header (17 bytes): opcode‖key-id (1), this side's session id (8), replay packet id (4), time (4).
/// Encrypted body: ACK count (1), that many acknowledged message ids (4 each), the PEER's session id (8, present only when
/// there are ACKs), this packet's message id (4, absent in a bare <see cref="Opcode.AckV1"/>), then the payload — TLS
/// records for <see cref="Opcode.ControlV1"/>.
/// </remarks>
public sealed record ControlPacket(
    Opcode Opcode,
    byte KeyId,
    byte[] SessionId,
    uint ReplayId,
    uint Time,
    IReadOnlyList<uint> Acks,
    byte[]? RemoteSessionId,
    uint? MessageId,
    byte[] Payload)
{
    public const int SessionIdLength = 8;
    /// <summary>The most ACKs one packet carries; more wait for the next.</summary>
    public const int MaxAcks = 8;

    public byte[] Encode(TlsCrypt crypt)
    {
        Span<byte> header = stackalloc byte[TlsCrypt.HeaderLength];
        header[0] = (byte)(((byte)Opcode << 3) | (KeyId & 0x07));
        SessionId.CopyTo(header[1..]);
        Wire.PutUInt32(header[9..], ReplayId);
        Wire.PutUInt32(header[13..], Time);

        var bodyLength = 1 + 4 * Acks.Count + (Acks.Count > 0 ? SessionIdLength : 0) + (MessageId is null ? 0 : 4) + Payload.Length;
        var body = new byte[bodyLength];
        var at = 0;
        body[at++] = (byte)Acks.Count;
        foreach (var ack in Acks) { Wire.PutUInt32(body.AsSpan(at), ack); at += 4; }
        if (Acks.Count > 0)
        {
            if (RemoteSessionId is null) throw new InvalidOperationException("a packet that acknowledges must name the peer's session");
            RemoteSessionId.CopyTo(body, at); at += SessionIdLength;
        }
        if (MessageId is { } id) { Wire.PutUInt32(body.AsSpan(at), id); at += 4; }
        Payload.CopyTo(body, at);
        return crypt.Wrap(header, body);
    }

    /// <summary>The packet, or null when it does not verify or does not parse — dropped, never answered.</summary>
    public static ControlPacket? Decode(ReadOnlySpan<byte> packet, TlsCrypt crypt)
    {
        var body = crypt.Unwrap(packet);
        if (body is null) return null;
        var opcode = (Opcode)(packet[0] >> 3);
        var keyId = (byte)(packet[0] & 0x07);
        var session = packet.Slice(1, SessionIdLength).ToArray();
        var replay = Wire.GetUInt32(packet[9..]);
        var time = Wire.GetUInt32(packet[13..]);

        var at = 0;
        if (body.Length < 1) return null;
        int count = body[at++];
        if (count > MaxAcks || body.Length < at + 4 * count) return null;
        var acks = new uint[count];
        for (var i = 0; i < count; i++) { acks[i] = Wire.GetUInt32(body.AsSpan(at)); at += 4; }
        byte[]? remote = null;
        if (count > 0)
        {
            if (body.Length < at + SessionIdLength) return null;
            remote = body.AsSpan(at, SessionIdLength).ToArray(); at += SessionIdLength;
        }
        uint? messageId = null;
        if (opcode != Opcode.AckV1)
        {
            if (body.Length < at + 4) return null;
            messageId = Wire.GetUInt32(body.AsSpan(at)); at += 4;
        }
        return new ControlPacket(opcode, keyId, session, replay, time, acks, remote, messageId, body.AsSpan(at).ToArray());
    }
}
