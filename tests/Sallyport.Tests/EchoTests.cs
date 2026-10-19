using System.Buffers.Binary;
using System.Net;
using Sallyport.Net;

namespace Sallyport.Tests;

/// <summary>A channel's own echoes (docs/design/window.md, step 7): answered, timed out, and never another's taken.</summary>
public class EchoTests
{
    private static readonly IPAddress Here = IPAddress.Parse("10.8.0.6");
    private static readonly IPAddress Target = IPAddress.Parse("1.1.1.1");
    private static readonly IPAddress Hop = IPAddress.Parse("10.8.0.1");

    // The reply a destination sends: the request with its addresses swapped and its type 0.
    private static byte[] Reply(byte[] request)
    {
        var reply = (byte[])request.Clone();
        request.AsSpan(12, 4).CopyTo(reply.AsSpan(16));
        request.AsSpan(16, 4).CopyTo(reply.AsSpan(12));
        reply[20] = 0;
        return reply;
    }

    // What a hop sends when the request's time ran out (11): its own header, then the request's header and first 8 bytes.
    private static byte[] TimeExceeded(byte[] request)
    {
        var error = new byte[20 + 8 + 28];
        error[0] = 0x45;
        error[9] = 1;
        Hop.GetAddressBytes().CopyTo(error, 12);
        Here.GetAddressBytes().CopyTo(error, 16);
        error[20] = 11;
        request.AsSpan(0, 28).CopyTo(error.AsSpan(28));
        return error;
    }

    [Fact]
    public async Task An_echo_reply_is_claimed_and_timed()
    {
        byte[]? sent = null;
        var echo = new TunnelEcho(Here, packet => { sent = packet; return ValueTask.CompletedTask; });

        var waiting = echo.PingAsync(Target, TimeSpan.FromSeconds(5));
        Assert.True(echo.Claim(Reply(sent!)));
        var answer = await waiting;

        Assert.Equal(Target, answer.From);
        Assert.True(answer.FromDestination);
        Assert.NotNull(answer.Milliseconds);
        Assert.Equal(8, sent![20]);
        Assert.Equal(Here, new IPAddress(sent.AsSpan(12, 4)));
    }

    [Fact]
    public async Task A_hop_answering_for_it_is_an_estimate_not_the_destination()
    {
        byte[]? sent = null;
        var echo = new TunnelEcho(Here, packet => { sent = packet; return ValueTask.CompletedTask; });

        var waiting = echo.PingAsync(Target, TimeSpan.FromSeconds(5), ttl: 1);
        Assert.True(echo.Claim(TimeExceeded(sent!)));
        var answer = await waiting;

        Assert.Equal(Hop, answer.From);
        Assert.False(answer.FromDestination);
    }

    [Fact]
    public async Task No_answer_in_time_is_an_answer_of_its_own()
    {
        var echo = new TunnelEcho(Here, _ => ValueTask.CompletedTask);

        var answer = await echo.PingAsync(Target, TimeSpan.FromMilliseconds(50));

        Assert.Equal(new EchoAnswer(null, null, false), answer);
    }

    [Fact]
    public async Task Another_identifiers_echo_is_left_for_the_router()
    {
        byte[]? sent = null;
        var echo = new TunnelEcho(Here, packet => { sent = packet; return ValueTask.CompletedTask; });
        var waiting = echo.PingAsync(Target, TimeSpan.FromMilliseconds(200));

        var someoneElses = Reply(sent!);
        BinaryPrimitives.WriteUInt16BigEndian(someoneElses.AsSpan(24), (ushort)(BinaryPrimitives.ReadUInt16BigEndian(sent!.AsSpan(24)) + 1));

        Assert.False(echo.Claim(someoneElses));
        Assert.Null((await waiting).Milliseconds);
    }
}
