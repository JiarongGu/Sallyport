using System.Net;
using Sallyport.Protocol;
using Sallyport.Net;

namespace Sallyport.Tests;

public class PrfTests
{
    [Fact]
    public void MatchesThePublishedTls10Vector()
    {
        // The widely reproduced TLS 1.0 PRF test vector: secret 48 × 0xab, label "PRF Testvector", seed 64 × 0xcd.
        var output = Prf.Tls10(Enumerable.Repeat((byte)0xab, 48).ToArray(), "PRF Testvector", Enumerable.Repeat((byte)0xcd, 64).ToArray(), 104);
        Assert.Equal("d3d4d1e349b5d515044666d51de32bab", Convert.ToHexStringLower(output, 0, 16));
    }

    [Fact]
    public void BothSidesDeriveOneBlockAndUseOppositeHalves()
    {
        var client = KeySource.ForClient();
        var server = KeySource.ForServer();
        byte[] clientSession = [1, 1, 1, 1, 1, 1, 1, 1], serverSession = [2, 2, 2, 2, 2, 2, 2, 2];
        var block = DataKeys.Expand(client, server, clientSession, serverSession);

        var mine = DataKeys.FromBlock(block, 0, 32);
        var theirs = DataKeys.FromBlock(block, 1, 32);

        Assert.Equal(mine.SendKey, theirs.ReceiveKey);
        Assert.Equal(mine.SendImplicitIv, theirs.ReceiveImplicitIv);
        Assert.NotEqual(mine.SendKey, mine.ReceiveKey);
        // The session ids are part of the derivation: another session gets other keys.
        Assert.NotEqual(block, DataKeys.Expand(client, server, serverSession, clientSession));
    }
}

public class DataChannelTests
{
    private static (DataChannel Client, DataChannel Server) Pair(uint peerId = 0x0a0b0c)
    {
        var block = DataKeys.Expand(KeySource.ForClient(), KeySource.ForServer(), new byte[8], new byte[8]);
        return (new DataChannel(DataKeys.FromBlock(block, 0, 32), peerId, 0), new DataChannel(DataKeys.FromBlock(block, 1, 32), peerId, 0));
    }

    [Fact]
    public void ASealedPacketOpensOnTheOtherSide()
    {
        var (client, server) = Pair();
        var packet = client.Seal("an IPv4 packet"u8);

        Assert.Equal(DataChannel.Verdict.Opened, server.Open(packet, out var payload));
        Assert.Equal("an IPv4 packet"u8.ToArray(), payload);
        // Opcode 9 above key id 0, then the 24-bit peer id, then packet id 1.
        Assert.Equal([0x48, 0x0a, 0x0b, 0x0c, 0, 0, 0, 1], packet[..8]);
        Assert.Equal(DataChannel.Overhead + 14, packet.Length);
    }

    [Theory]
    [InlineData(1)]     // the peer id, authenticated as associated data
    [InlineData(7)]     // the packet id, which is also the nonce
    [InlineData(10)]    // the tag
    [InlineData(30)]    // the ciphertext
    public void AnyChangedByteIsRejected(int at)
    {
        var (client, server) = Pair();
        var packet = client.Seal(new byte[16]);
        packet[at] ^= 0x01;
        Assert.Equal(DataChannel.Verdict.Unauthentic, server.Open(packet, out _));
    }

    [Fact]
    public void APacketIsAcceptedOnce()
    {
        var (client, server) = Pair();
        var packet = client.Seal([1, 2, 3]);
        Assert.Equal(DataChannel.Verdict.Opened, server.Open(packet, out _));
        Assert.Equal(DataChannel.Verdict.Replayed, server.Open(packet, out _));
    }

    [Fact]
    public void APacketReflectedBackIsNamedAsTheOppositeDirection()
    {
        var (client, _) = Pair();
        Assert.Equal(DataChannel.Verdict.OppositeDirection, client.Open(client.Seal([1, 2, 3]), out _));
    }

    [Fact]
    public void AnotherKeyIdIsNotThisChannels()
    {
        var (client, server) = Pair();
        var packet = client.Seal([1]);
        packet[0] |= 0x01;
        Assert.Equal(DataChannel.Verdict.NotForThisKey, server.Open(packet, out _));
    }
}

public class ReplayWindowTests
{
    [Fact]
    public void AcceptsOutOfOrderOnceAndRefusesTheTooOld()
    {
        var window = new ReplayWindow();
        Assert.False(window.Accept(0));
        Assert.True(window.Accept(5));
        Assert.True(window.Accept(3));
        Assert.False(window.Accept(3));
        Assert.True(window.Accept(4));
        Assert.False(window.Accept(5));
        Assert.True(window.Accept(100));
        Assert.True(window.Accept(100 - ReplayWindow.Size + 1));
        Assert.False(window.Accept(100 - ReplayWindow.Size));
        Assert.True(window.Accept(1000));
        Assert.False(window.Accept(100));
    }
}

public class PushedConfigTests
{
    [Fact]
    public void ReadsWhatTheClientActsOn()
    {
        var config = PushedConfig.Parse([
            "explicit-exit-notify", "topology subnet", "ping 12", "ping-restart 50", "redirect-gateway def1",
            "route-gateway 10.9.0.1", "dhcp-option DNS 1.1.1.1", "dhcp-option DNS 8.8.8.8", "dhcp-option DOMAIN example.test",
            "ifconfig 10.9.0.6 255.255.248.0", "peer-id 7", "auth-token SECRET", "cipher AES-256-GCM", "protocol-flags cc-exit tls-ekm"]);

        Assert.Equal(IPAddress.Parse("10.9.0.6"), config.Address);
        Assert.Equal(IPAddress.Parse("255.255.248.0"), config.IfconfigSecond);
        Assert.Equal("subnet", config.Topology);
        Assert.Equal(IPAddress.Parse("10.9.0.1"), config.Gateway);
        Assert.Equal([IPAddress.Parse("1.1.1.1"), IPAddress.Parse("8.8.8.8")], config.DnsServers);
        Assert.Equal(12, config.Ping);
        Assert.Equal(50, config.PingRestart);
        Assert.Equal(7u, config.PeerId);
        Assert.Equal("AES-256-GCM", config.Cipher);
        Assert.True(config.ExplicitExitNotify);
        Assert.Equal(["cc-exit", "tls-ekm"], config.ProtocolFlags);
    }

    [Fact]
    public void AbsentOptionsStayAbsent()
    {
        var config = PushedConfig.Parse([]);
        Assert.Null(config.Address);
        Assert.Null(config.PeerId);
        Assert.Equal(0, config.Ping);
        Assert.Empty(config.DnsServers);
    }
}

public class Ipv4Tests
{
    [Fact]
    public void TheHeaderChecksumMatchesTheTextbookExample()
    {
        // The standard worked example: this header's checksum is 0xb861.
        var header = Convert.FromHexString("450000730000400040110000c0a80001c0a800c7");
        Assert.Equal(0xb861, Ipv4.Checksum(header));
    }

    [Fact]
    public void AUdpPacketReadsBackAndVerifies()
    {
        var packet = Ipv4.UdpPacket(IPAddress.Parse("10.9.0.6"), 50000, IPAddress.Parse("1.1.1.1"), 53, "query"u8, 77);

        Assert.Equal(0, Ipv4.Checksum(packet.AsSpan(0, Ipv4.HeaderLength)));
        Assert.True(Ipv4.TryReadUdp(packet, out var header, out var from, out var to, out var payload));
        Assert.Equal(IPAddress.Parse("10.9.0.6"), header.Source);
        Assert.Equal(IPAddress.Parse("1.1.1.1"), header.Destination);
        Assert.Equal((50000, 53), (from, to));
        Assert.Equal("query"u8.ToArray(), payload.ToArray());
    }

    [Fact]
    public void ACorruptedDatagramIsRefused()
    {
        var packet = Ipv4.UdpPacket(IPAddress.Parse("10.9.0.6"), 50000, IPAddress.Parse("1.1.1.1"), 53, "query"u8, 77);
        packet[^1] ^= 0xff;
        Assert.False(Ipv4.TryReadUdp(packet, out _, out _, out _, out _));
    }
}

public class DnsMessageTests
{
    [Fact]
    public void AQueryIsHeaderThenLabels()
    {
        var query = DnsMessage.Query(0x1234, "example.com", DnsMessage.TypeA);
        Assert.Equal(Convert.FromHexString("123401000001000000000000076578616d706c6503636f6d0000010001"), query);
    }

    [Fact]
    public void ReadsAnswersThroughACompressionPointer()
    {
        var query = DnsMessage.Query(0x1234, "example.com", DnsMessage.TypeA);
        byte[] response =
        [
            .. query[..2], 0x81, 0x80, 0, 1, 0, 2, 0, 0, 0, 0,
            .. query[12..],
            // A CNAME first, which must be skipped, then the A record; both name the question by pointer (0xc00c).
            0xc0, 0x0c, 0, 5, 0, 1, 0, 0, 0, 60, 0, 2, 0xc0, 0x0c,
            0xc0, 0x0c, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4, 93, 184, 215, 14,
        ];

        Assert.True(DnsMessage.TryReadAnswers(response, 0x1234, DnsMessage.TypeA, out var code, out var addresses));
        Assert.Equal(0, code);
        Assert.Equal([IPAddress.Parse("93.184.215.14")], addresses);
        Assert.False(DnsMessage.TryReadAnswers(response, 0x9999, DnsMessage.TypeA, out _, out _));
    }

    [Fact]
    public void SaysWhichNameItAnswersAndWhetherItWasCutShort()
    {
        var query = DnsMessage.Query(0x1234, "Example.com", DnsMessage.TypeA);
        byte[] whole = [.. query[..2], 0x81, 0x80, 0, 1, 0, 0, 0, 0, 0, 0, .. query[12..]];
        byte[] cut = [.. query[..2], 0x83, 0x80, 0, 1, 0, 0, 0, 0, 0, 0, .. query[12..]];

        Assert.True(DnsMessage.TryReadAnswers(whole, 0x1234, DnsMessage.TypeA, out _, out var none, out _, out var asked, out var truncated));
        Assert.Equal("Example.com", asked);
        Assert.Empty(none);
        Assert.False(truncated);
        Assert.True(DnsMessage.TryReadAnswers(cut, 0x1234, DnsMessage.TypeA, out _, out _, out _, out _, out truncated));
        Assert.True(truncated);
    }

    [Fact]
    public void AQuestionWhosePointerLoopsReadsAsNoName()
    {
        // The question's name a pointer to itself (0xc00c at offset 12).
        byte[] looped = [0x12, 0x34, 0x81, 0x80, 0, 1, 0, 0, 0, 0, 0, 0, 0xc0, 0x0c, 0, 1, 0, 1];
        Assert.True(DnsMessage.TryReadAnswers(looped, 0x1234, DnsMessage.TypeA, out _, out _, out _, out var asked, out _));
        Assert.Null(asked);
    }
}
