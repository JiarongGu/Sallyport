using System.Net;
using Sallyport.Observer;

namespace Sallyport.Observer.Tests;

public sealed class EndpointTests
{
    [Theory]
    [InlineData("8.8.8.8", "routable")]
    [InlineData("127.0.0.2", "loopback")]
    [InlineData("10.0.0.1", "private")]
    [InlineData("172.16.0.1", "private")]
    [InlineData("172.31.255.255", "private")]
    [InlineData("172.15.255.255", "routable")]
    [InlineData("172.32.0.1", "routable")]
    [InlineData("192.168.1.1", "private")]
    [InlineData("192.169.1.1", "routable")]
    [InlineData("100.64.0.1", "private")]
    [InlineData("100.127.255.255", "private")]
    [InlineData("100.128.0.1", "routable")]
    [InlineData("169.254.1.1", "link-local")]
    [InlineData("224.0.0.1", "multicast")]
    [InlineData("239.255.255.255", "multicast")]
    [InlineData("255.255.255.255", "broadcast")]
    [InlineData("0.0.0.0", "unspecified")]
    [InlineData("240.0.0.1", "reserved")]
    [InlineData("::1", "ipv6-unsupported")]
    [InlineData("fe80::1", "ipv6-unsupported")]
    [InlineData("2606:4700:4700::1111", "ipv6-unsupported")]
    [InlineData("::ffff:8.8.8.8", "ipv6-unsupported")]
    public void ClassificationHasExplicitBoundaries(string address, string expected) =>
        Assert.Equal(expected, EndpointClassifier.Classify(IPAddress.Parse(address)));

    private static ProcessIdentity Process(long created = 10) => new(new(123, created), Path.GetFullPath("game.exe"));
    private static NetworkObservation Observation(long time, string source = "etw", string protocol = "TCP", ushort port = 443) =>
        new(protocol, IPAddress.Parse("8.8.8.8"), port, 123, time, source);

    [Fact]
    public void BufferedEventsAndBothSourcesMergeWithoutLosingEvidence()
    {
        var aggregator = new EndpointAggregator();
        var first = aggregator.Add(Observation(100, "tcp-table"), Process());
        var second = aggregator.Add(Observation(90), Process());
        var third = aggregator.Add(Observation(110), Process());
        Assert.True(first.IsNew);
        Assert.False(second.IsNew);
        Assert.False(third.IsNew);
        EndpointEvidence evidence = Assert.Single(aggregator.Snapshot());
        Assert.Equal(3, evidence.TimesSeen);
        Assert.Equal(DateTimeOffset.FromFileTime(90), evidence.FirstSeen);
        Assert.Equal(DateTimeOffset.FromFileTime(110), evidence.LastSeen);
        Assert.Equal(TimeSpan.Zero, evidence.FirstSeen.Offset);
        Assert.Equal(TimeSpan.Zero, evidence.LastSeen.Offset);
        Assert.Equal(2, evidence.Sources["etw"]);
        Assert.Equal(1, evidence.Sources["tcp-table"]);
        Assert.Single(first.Evidence.Sources);
    }

    [Fact]
    public void ProtocolPortAndPidReuseAreSeparateEvidence()
    {
        var aggregator = new EndpointAggregator();
        aggregator.Add(Observation(100), Process());
        Assert.True(aggregator.Add(Observation(100, protocol: "UDP"), Process()).IsNew);
        Assert.True(aggregator.Add(Observation(100, port: 80), Process()).IsNew);
        Assert.True(aggregator.Add(Observation(100), Process(99)).IsNew);
        Assert.Equal(4, aggregator.Snapshot().Count);
    }

    [Fact]
    public void AnObservationCannotPrecedeItsInstanceOrNameAnotherPid()
    {
        var aggregator = new EndpointAggregator();
        Assert.Throws<ArgumentException>(() => aggregator.Add(Observation(9), Process()));
        Assert.Throws<ArgumentException>(() => aggregator.Add(Observation(100) with { Pid = 124 }, Process()));
    }
}
