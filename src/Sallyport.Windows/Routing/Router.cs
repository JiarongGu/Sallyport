using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using Sallyport.Windows.PacketFilter;

namespace Sallyport.Windows.Routing;

/// <summary>What the router decided for a flow at its first packet, and kept.</summary>
public enum Verdict { Routed, Direct, Unowned }

public sealed class FlowDecision(Verdict verdict, int pid)
{
    public Verdict Verdict { get; internal set; } = verdict;
    public int Pid { get; internal set; } = pid;
    public long Packets { get; internal set; }
}

/// <summary>
/// The router (docs/design/router.md): every packet Windows sends on one adapter is matched to its process at its
/// flow's first packet; a selected process's IPv4 TCP and UDP go to the tunnel, NAT'd, and everything else on unchanged.
/// Replies from the tunnel are turned back and handed up to Windows. The tunnel itself is not here — the caller sends
/// what <c>toTunnel</c> is given and brings back what the tunnel returns (<see cref="FromTunnel"/>).
/// </summary>
public sealed class Router : IDisposable
{
    /// <summary>How stale the owner tables may grow before a UDP decision is checked again: a port let go and taken
    /// by another process was misattributed for 250 ms when nothing refreshed (measured, gate 3).</summary>
    public static readonly TimeSpan Recheck = TimeSpan.FromMilliseconds(250);

    // A miss refreshes the tables, but not more than once per this: a refresh costs 1-4 ms (measured, gate 3).
    private static readonly TimeSpan RefreshGap = TimeSpan.FromMilliseconds(2);

    private readonly PacketDriver _driver;
    private readonly Interception _interception;
    private readonly OwnerTable _owners = new();
    private readonly Func<int, bool> _selects;
    private readonly Action<byte[]> _toTunnel;
    private readonly Dictionary<Flow, FlowDecision> _flows = [];
    private readonly Lock _injectLock = new();
    private readonly PacketBatch _inject = new(1);
    private readonly Dictionary<InboundRefusal, long> _refused = [];
    private long _lastRefresh;
    private long _lastRecheck;

    public Router(PacketDriver driver, ulong adapter, Func<int, bool> selects, Nat nat, Action<byte[]> toTunnel)
    {
        _driver = driver;
        _selects = selects;
        Nat = nat;
        _toTunnel = toTunnel;
        _owners.Refresh();
        _lastRefresh = _lastRecheck = Stopwatch.GetTimestamp();
        _interception = new Interception(driver, adapter, 64, receivePath: false);
    }

    public Nat Nat { get; }
    public Interception Interception => _interception;
    public OwnerTable Owners => _owners;
    public long RoutedPackets { get; private set; }
    public long DirectPackets { get; private set; }
    public long UnownedPackets { get; private set; }
    /// <summary>Selected packets dropped rather than sent direct: a fragment, or no tunnel port free.</summary>
    public long HeldBack { get; private set; }
    /// <summary>Flows first decided direct or unowned that a later look found a selected process owning: what of a
    /// selected program left directly, counted (rule 6: coverage failed, not unknown).</summary>
    public long LateFlows { get; private set; }
    public long LatePackets { get; private set; }
    public long HandedUp { get; private set; }
    public long HandUpRefused { get; private set; }
    public IReadOnlyDictionary<InboundRefusal, long> Refused { get { lock (_injectLock) return new Dictionary<InboundRefusal, long>(_refused); } }

    public IReadOnlyDictionary<Flow, FlowDecision> Flows => _flows;

    /// <summary>Drains and decides until <paramref name="ct"/> is cancelled, then gives the adapter back.</summary>
    public void Run(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                _interception.Wait(50);
                if (Stopwatch.GetElapsedTime(_lastRecheck) >= Recheck) RecheckUdp();
                _interception.Drain(Decide);
            }
        }
        finally { _interception.Stop(); }
    }

    /// <summary>A packet the tunnel returned: turned back and handed up, or refused and counted.</summary>
    public void FromTunnel(byte[] packet)
    {
        var refusal = Nat.Inbound(packet, out var mapping);
        lock (_injectLock)
        {
            if (refusal != InboundRefusal.None)
            {
                _refused[refusal] = _refused.GetValueOrDefault(refusal) + 1;
                return;
            }
            _inject.Write(0, _interception.Adapter, Nat.Frame(mapping!, packet), PacketDirection.Receive);
            try
            {
                _driver.Send(_interception.Adapter, _inject, [0], toNetwork: false);
                HandedUp++;
            }
            catch (Win32Exception) { HandUpRefused++; }
        }
    }

    private bool Decide(DriverPacket packet)
    {
        if (packet.Direction != PacketDirection.Send) return false;
        var frame = packet.Frame;
        if (Frames.TryReadOutbound(frame, out var flow) != FrameKind.Flow) return false;

        if (!_flows.TryGetValue(flow, out var decision))
        {
            decision = Classify(flow);
            _flows[flow] = decision;
        }
        decision.Packets++;
        switch (decision.Verdict)
        {
            case Verdict.Direct: DirectPackets++; return false;
            case Verdict.Unowned: UnownedPackets++; return false;
        }

        var link = BinaryPrimitives.ReadUInt16BigEndian(frame[12..]) == 0x8100 ? 18 : 14;
        var ip = frame[link..];
        var total = BinaryPrimitives.ReadUInt16BigEndian(ip[2..]);
        // More-fragments on a first fragment: its checksum covers the whole datagram, which is not here (gate 5).
        if (total > ip.Length || (ip[6] & 0x20) != 0) { HeldBack++; return true; }
        // The frame may be padded past the packet to Ethernet's 60-byte minimum.
        var copy = ip[..total].ToArray();
        if (Nat.Outbound(copy, frame[..link]) is null) { HeldBack++; return true; }
        RoutedPackets++;
        _toTunnel(copy);
        return true;
    }

    private FlowDecision Classify(in Flow flow)
    {
        var answer = _owners.Owner(flow, out var pid);
        if (answer == Ownership.Unowned && Stopwatch.GetElapsedTime(_lastRefresh) >= RefreshGap)
        {
            Refresh();
            answer = _owners.Owner(flow, out pid);
        }
        return answer == Ownership.Owned ? new(_selects(pid) ? Verdict.Routed : Verdict.Direct, pid) : new(Verdict.Unowned, 0);
    }

    // A UDP port can change hands between snapshots; a TCP four-tuple cannot, so only UDP decisions are looked at again.
    private void RecheckUdp()
    {
        Refresh();
        _lastRecheck = _lastRefresh;
        foreach (var (flow, decision) in _flows)
        {
            if (flow.Protocol != Flow.Udp) continue;
            var answer = _owners.Owner(flow, out var pid);
            if (answer != Ownership.Owned || pid == decision.Pid) continue;
            var routed = _selects(pid);
            if (routed && decision.Verdict != Verdict.Routed)
            {
                LateFlows++;
                LatePackets += decision.Packets;
            }
            decision.Verdict = routed ? Verdict.Routed : Verdict.Direct;
            decision.Pid = pid;
        }
    }

    private void Refresh()
    {
        _owners.Refresh();
        _lastRefresh = Stopwatch.GetTimestamp();
    }

    public void Dispose()
    {
        _interception.Dispose();
        _inject.Dispose();
    }
}
