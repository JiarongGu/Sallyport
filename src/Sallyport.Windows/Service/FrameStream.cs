using System.Buffers.Binary;
using System.Text.Json;
using System.Threading.Channels;

namespace Sallyport.Windows.Service;

/// <summary>One frame read: its kind, and its payload (a control message's JSON, or a packet's generation and bytes).</summary>
public readonly record struct Frame(byte Kind, byte[] Payload)
{
    public RouterMessage? Message => Kind == RouterProtocol.ControlFrame ? JsonSerializer.Deserialize<RouterMessage>(Payload, RouterProtocol.Json) : null;

    public int Generation => BinaryPrimitives.ReadInt32LittleEndian(Payload);

    public byte[] Packet => Payload[4..];
}

/// <summary>Frames over a pipe: one reader, and writers serialised so frames never interleave.</summary>
public sealed class FrameStream(Stream stream) : IAsyncDisposable
{
    private readonly SemaphoreSlim _writing = new(1, 1);
    private readonly byte[] _head = new byte[5];

    public Stream Stream => stream;

    /// <summary>The next frame, or null when the other end closed the pipe.</summary>
    public async ValueTask<Frame?> ReadAsync(CancellationToken ct)
    {
        if (!await Fill(_head, ct)) return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(_head);
        if (length < 1 || length > RouterProtocol.MostFrame) throw new InvalidDataException($"a frame of {length} bytes");
        // A packet frame holds at least its 4-byte generation: one without would throw where it is read, past the
        // readers' own handling of a bad frame.
        if (_head[4] == RouterProtocol.PacketFrame && length < 5) throw new InvalidDataException($"a packet frame of {length} bytes");
        var payload = new byte[length - 1];
        if (!await Fill(payload, ct)) throw new EndOfStreamException("the pipe closed inside a frame");
        return new Frame(_head[4], payload);
    }

    public ValueTask SendAsync(RouterMessage message, CancellationToken ct = default) =>
        WriteAsync(RouterProtocol.ControlFrame, JsonSerializer.SerializeToUtf8Bytes(message, RouterProtocol.Json), ReadOnlyMemory<byte>.Empty, ct);

    public ValueTask SendPacketAsync(int generation, ReadOnlyMemory<byte> packet, CancellationToken ct = default)
    {
        var head = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(head, generation);
        return WriteAsync(RouterProtocol.PacketFrame, head, packet, ct);
    }

    /// <summary>Writes every packet <paramref name="reader"/> holds now, as many frames in one write, waiting only when
    /// it holds none: a pipe write per packet halved a download through the service (measured, item 2 step 4).</summary>
    public async Task PumpPacketsAsync(ChannelReader<(int Generation, byte[] Packet)> reader, CancellationToken ct)
    {
        var buffer = new byte[256 * 1024];
        while (await reader.WaitToReadAsync(ct))
        {
            var used = 0;
            while (used + 9 + 1600 <= buffer.Length && reader.TryRead(out var item))
            {
                if (used + 9 + item.Packet.Length > buffer.Length)
                {
                    await FlushAsync(buffer, used, ct);
                    used = 0;
                }
                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(used), 1 + 4 + item.Packet.Length);
                buffer[used + 4] = RouterProtocol.PacketFrame;
                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(used + 5), item.Generation);
                item.Packet.CopyTo(buffer.AsSpan(used + 9));
                used += 9 + item.Packet.Length;
            }
            if (used > 0) await FlushAsync(buffer, used, ct);
        }
    }

    private async Task FlushAsync(byte[] buffer, int used, CancellationToken ct)
    {
        await _writing.WaitAsync(ct);
        try { await stream.WriteAsync(buffer.AsMemory(0, used), ct); }
        finally { _writing.Release(); }
    }

    private async ValueTask WriteAsync(byte kind, ReadOnlyMemory<byte> first, ReadOnlyMemory<byte> second, CancellationToken ct)
    {
        var frame = new byte[5 + first.Length + second.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, 1 + first.Length + second.Length);
        frame[4] = kind;
        first.CopyTo(frame.AsMemory(5));
        second.CopyTo(frame.AsMemory(5 + first.Length));
        await _writing.WaitAsync(ct);
        try { await stream.WriteAsync(frame, ct); }
        finally { _writing.Release(); }
    }

    private async ValueTask<bool> Fill(byte[] buffer, CancellationToken ct)
    {
        var at = 0;
        while (at < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(at), ct);
            if (read == 0) return at == 0 && buffer.Length > 0 ? false : throw new EndOfStreamException("the pipe closed inside a frame");
            at += read;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await stream.DisposeAsync();
        _writing.Dispose();
    }
}
