using System.Buffers.Binary;
using System.Net;
using Sallyport.Observer;

namespace Sallyport.Observer.Tests;

public sealed class EtwSchemaTests
{
    [Theory]
    [InlineData(10, "TCP", false, 4)]
    [InlineData(11, "TCP", true, 4)]
    [InlineData(12, "TCP", false, 4)]
    [InlineData(26, "TCP", false, 16)]
    [InlineData(27, "TCP", true, 16)]
    [InlineData(28, "TCP", false, 16)]
    [InlineData(42, "UDP", false, 4)]
    [InlineData(43, "UDP", true, 4)]
    [InlineData(58, "UDP", false, 16)]
    [InlineData(59, "UDP", true, 16)]
    public void DecodeUsesPayloadPidAndNetworkByteOrder(int id, string protocol, bool receive, int length)
    {
        EtwSchema schema = Assert.IsType<EtwSchema>(EtwSchema.Find((ushort)id));
        IPAddress address = IPAddress.Parse(length == 4 ? "8.8.4.4" : "2606:4700:4700::1111");
        byte[] payloadPid = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payloadPid, 12345);
        NetworkObservation observation = schema.Decode(payloadPid, address.GetAddressBytes(), [0x1f, 0x90], 100);
        Assert.Equal(protocol, observation.Protocol);
        Assert.Equal(receive, schema.Receive);
        Assert.Equal(12345u, observation.Pid);
        Assert.Equal(8080, observation.Port);
        Assert.Equal(address, observation.Address);
    }

    /// <summary>Measured 2026-10-19, against the manifest's own messages: every TCP event names the remote end daddr (curl
    /// to 34.117.59.81:443 — sent, received and connection attempted all carried it there, and this machine as saddr);
    /// a UDP receive names it saddr (nslookup's reply from 216.239.32.10). Reading saddr for a TCP receive would learn this
    /// machine's own address as a destination.</summary>
    [Theory]
    [InlineData(10, "daddr", "dport")]
    [InlineData(11, "daddr", "dport")]
    [InlineData(12, "daddr", "dport")]
    [InlineData(42, "daddr", "dport")]
    [InlineData(43, "saddr", "sport")]
    public void TheRemoteEndIsReadFromTheMeasuredProperty(int id, string address, string port)
    {
        EtwSchema schema = EtwSchema.Find((ushort)id)!;
        Assert.Equal(address, schema.AddressProperty);
        Assert.Equal(port, schema.PortProperty);
    }

    [Fact]
    public void UnknownAndConnectionCloseEventsAreNotDecoded()
    {
        Assert.Null(EtwSchema.Find(13));
        Assert.Null(EtwSchema.Find(49));
        Assert.Null(EtwSchema.Find(999));
    }

    [Fact]
    public void ChangedPropertySizesAreExplicitlyUnsupported()
    {
        EtwSchema schema = EtwSchema.Find(42)!;
        Assert.Throws<InvalidDataException>(() => schema.Decode([1, 2], [8, 8, 8, 8], [0, 53], 100));
        Assert.Throws<InvalidDataException>(() => schema.Decode([1, 2, 3, 4], new byte[16], [0, 53], 100));
        Assert.Throws<InvalidDataException>(() => schema.Decode([1, 2, 3, 4], [8, 8, 8, 8], [0, 53, 0, 0], 100));
    }
}
