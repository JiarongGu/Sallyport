using System.Text;

namespace Sallyport.Protocol;

/// <summary>What has arrived over TLS and not yet been read: the key-method-2 message, then NUL-terminated text messages.</summary>
internal sealed class TlsInbox(Stream tls)
{
    /// <summary>The longest message read: a push reply is a few kilobytes, and nothing a server says needs a megabyte —
    /// past it, a peer that never ends its message would be held in memory for as long as it kept sending.</summary>
    public const int MostMessage = 1 << 20;

    private byte[] _buffer = new byte[32768];
    private int _length;

    public ReadOnlySpan<byte> Data => _buffer.AsSpan(0, _length);

    public async Task FillAsync(CancellationToken ct)
    {
        if (_length >= MostMessage) throw new InvalidDataException("the server sent a control message over 1 MiB");
        // Room for a whole TLS record (16 KB) on every read, so a message written in one piece is read in one piece.
        if (_buffer.Length - _length < 16384) Array.Resize(ref _buffer, _buffer.Length * 2);
        var n = await tls.ReadAsync(_buffer.AsMemory(_length), ct);
        if (n == 0) throw new EndOfStreamException("the server closed the control channel");
        _length += n;
    }

    public void Consume(int count)
    {
        Buffer.BlockCopy(_buffer, count, _buffer, 0, _length - count);
        _length -= count;
    }

    public async Task<string> ReadTextAsync(CancellationToken ct)
    {
        while (true)
        {
            var end = Data.IndexOf((byte)0);
            if (end >= 0)
            {
                var text = Encoding.UTF8.GetString(Data[..end]);
                Consume(end + 1);
                return text;
            }
            await FillAsync(ct);
        }
    }
}
