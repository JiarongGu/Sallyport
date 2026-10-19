namespace Sallyport.Protocol;

/// <summary>The control channel as the byte stream TLS expects: writes are cut into <see cref="Opcode.ControlV1"/>
/// packets, reads return their payloads in order.</summary>
internal sealed class ControlStream(ControlChannel channel) : Stream
{
    private byte[] _current = [];
    private int _offset;

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        while (_offset == _current.Length)
        {
            if (!await channel.Inbound.WaitToReadAsync(ct)) return 0;
            if (!channel.Inbound.TryRead(out var packet)) continue;
            if (packet.Opcode == Opcode.ControlSoftResetV1)
                throw new IOException("the server began a key renegotiation, which this version does not handle yet");
            if (packet.Opcode != Opcode.ControlV1) continue;
            _current = packet.Payload;
            _offset = 0;
        }
        var n = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsMemory(_offset, n).CopyTo(buffer);
        _offset += n;
        return n;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        for (var at = 0; at < buffer.Length; at += ControlChannel.MaxPayload)
            await channel.SendAsync(Opcode.ControlV1, buffer.Slice(at, Math.Min(ControlChannel.MaxPayload, buffer.Length - at)).ToArray(), ct);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, default).GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, default).GetAwaiter().GetResult();
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;

    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
