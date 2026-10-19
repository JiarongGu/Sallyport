using System.Net;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Xml.Linq;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Networking.Vpn;
using Windows.Storage;

namespace Sallyport.VpnSpike.Plugin;

/// <summary>
/// The spike's first question only: does a filter on one program's path bring that program's packets — and no other
/// program's — to <see cref="Encapsulate"/>? No tunnel yet: each packet is logged by its addresses and dropped.
/// </summary>
/// <remarks>
/// A plug-in that drops what it is given is a black hole for whatever the platform routes into it. The first run routed
/// 0.0.0.0/0 — which makes the connection force-tunnelled — and took the whole machine's network down (2026-10-19). So
/// this plug-in guards the machine itself: it routes only the prefixes its profile names and refuses a default route; it
/// ends its own connection at a deadline; and it ends it at once when a packet arrives for a destination outside those
/// prefixes, which would mean the platform sent it traffic it was never meant to carry.
/// </remarks>
internal sealed partial class SpikePlugin : IVpnPlugIn
{
    private DatagramSocket? _socket;
    private Timer? _deadline;
    private List<(uint Network, uint Mask, string Text)> _prefixes = [];
    private long _encapsulated, _decapsulated;
    private int _terminating;
    private bool _allowDefault;

    public void Connect(VpnChannel channel)
    {
        try
        {
            // <spike><server/><port/><route>C:\…\routed.exe</route><prefix>198.51.100.0/24</prefix><deadline>20</deadline></spike>.
            // The server is carried here, not read from ServerHostNameList: that threw E_INVALIDARG ("hostName") for a
            // profile whose server URI was https://1.1.1.1 (measured).
            var config = XElement.Parse(channel.Configuration.CustomField);
            var port = (string?)config.Element("port") ?? "1194";
            var apps = config.Elements("route").Select(e => e.Value).ToList();
            var server = new HostName((string?)config.Element("server") ?? throw new InvalidOperationException("the profile names no server"));
            _prefixes = [.. config.Elements("prefix").Select(e => ParsePrefix(e.Value))];
            if (_prefixes.Count == 0) throw new InvalidOperationException("the profile routes no prefix: refusing a force tunnel");
            // A default route is refused unless the profile says <defaultRoute>allow</defaultRoute>: only a run that asks
            // whether the app filter confines it to the listed programs may have one, and only under the guard.
            _allowDefault = (string?)config.Element("defaultRoute") == "allow";
            if (_prefixes.Any(p => p.Mask == 0) && !_allowDefault) throw new InvalidOperationException("the profile routes a default route: refusing a force tunnel");
            var deadline = TimeSpan.FromSeconds(int.TryParse((string?)config.Element("deadline"), out var s) ? Math.Clamp(s, 1, 300) : 20);
            Log.Write($"connect: server {server.CanonicalName}:{port}; {apps.Count} program(s): {string.Join(" | ", apps)}; "
                + $"prefixes {string.Join(", ", _prefixes.Select(p => p.Text))}; deadline {deadline.TotalSeconds:0} s");

            // Armed before anything is started, so a connect that hangs half-way is ended too.
            Interlocked.Exchange(ref _terminating, 0);
            _deadline = new Timer(_ => Terminate(channel, "the deadline passed"), null, deadline, Timeout.InfiniteTimeSpan);

            _socket = new DatagramSocket();
            channel.AssociateTransport(_socket, null);
            _socket.ConnectAsync(server, port).AsTask().Wait();
            Log.Write("connect: outer socket connected");

            // <allow>: the assignment's AllowOutbound/AllowInbound, documented only as "whether … traffic is allowed".
            var allow = (string?)config.Element("allow") != "false";
            var filters = new VpnTrafficFilterAssignment { AllowOutbound = allow, AllowInbound = allow };
            Log.Write($"connect: app filter with AllowOutbound/AllowInbound = {allow}");
            // <policy>force</policy>: the filter's RoutingPolicyType — the app's traffic forced through the VPN rather than
            // following the routes.
            var policy = (string?)config.Element("policy") == "force" ? VpnRoutingPolicyType.ForceAllTrafficOverVpn : VpnRoutingPolicyType.SplitRouting;
            // <protocols>tcp,udp</protocols>: one filter per protocol — a filter's Protocol defaults to None (0), which
            // may be compared as protocol 0 rather than read as "any".
            var protocols = ((string?)config.Element("protocols"))?.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim() switch { "tcp" => VpnIPProtocol.Tcp, "udp" => VpnIPProtocol.Udp, "icmp" => VpnIPProtocol.Icmp, _ => VpnIPProtocol.None })
                .ToList() ?? [];
            if (protocols.Count == 0) protocols.Add(VpnIPProtocol.None);
            foreach (var path in apps)
                foreach (var protocol in protocols)
                    filters.TrafficFilterList.Add(new VpnTrafficFilter(new VpnAppId(VpnAppIdType.FilePath, path)) { RoutingPolicyType = policy, Protocol = protocol });
            Log.Write($"connect: {filters.TrafficFilterList.Count} filter(s), protocols {string.Join("/", protocols)}");
            Log.Write($"connect: filters' routing policy {policy}");
            var routes = new VpnRouteAssignment();
            foreach (var (network, mask, _) in _prefixes)
                routes.Ipv4InclusionRoutes.Add(new VpnRoute(new HostName(ToAddress(network)), (byte)System.Numerics.BitOperations.PopCount(mask)));

            // A termination asked for before the channel starts is not acted on by the platform (measured: the leak
            // detector called TerminateConnection during Connect and the channel started anyway), so it aborts the start.
            if (Volatile.Read(ref _terminating) == 1) throw new OperationCanceledException("terminated before the channel started");
            // <filter>none</filter> starts a plain split tunnel: the baseline that says whether the routes work at all.
            if ((string?)config.Element("filter") == "none")
            {
                channel.Start([new HostName("10.99.0.2")], null, null, routes, null, 1400, 1500, false, _socket, null);
                Log.Write("connect: channel started with no app filter");
            }
            else
            {
                channel.StartWithTrafficFilter(
                    [new HostName("10.99.0.2")], null, null, routes, null,
                    1400, 1500, false, _socket, null, filters);
                Log.Write("connect: channel started with the app filter");
            }
        }
        catch (Exception e)
        {
            Log.Write($"connect failed: {e}");
            _deadline?.Dispose();
            throw;
        }
    }

    public void Disconnect(VpnChannel channel)
    {
        Log.Write($"disconnect: {_encapsulated} packet(s) offered to encapsulate, {_decapsulated} buffer(s) to decapsulate");
        _deadline?.Dispose();
        _deadline = null;
        channel.Stop();
        _socket?.Dispose();
        _socket = null;
    }

    // No keepalive of the platform's: the spike has no server to keep.
    public void GetKeepAlivePayload(VpnChannel channel, out VpnPacketBuffer keepAlivePacket) => keepAlivePacket = null!;

    public void Encapsulate(VpnChannel channel, VpnPacketBufferList packets, VpnPacketBufferList encapulatedPackets)
    {
        // Every packet is taken off the list and none is handed on: the spike only asks which packets arrive.
        while (packets.Size > 0)
        {
            var bytes = packets.RemoveAtBegin().Buffer.ToArray();
            var n = Interlocked.Increment(ref _encapsulated);
            if (n <= 200) Log.Write($"encapsulate #{n}: {Describe(bytes)}");
            if (bytes.Length >= 20 && bytes[0] >> 4 == 4 && Leaked(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16))))
                Terminate(channel, $"a packet for a destination outside the routed prefixes arrived: {Describe(bytes)}");
        }
    }

    public void Decapsulate(VpnChannel channel, VpnPacketBuffer encapBuffer, VpnPacketBufferList decapsulatedPackets, VpnPacketBufferList controlPacketsToSend) =>
        Interlocked.Increment(ref _decapsulated);

    /// <summary>Ends the connection from the plug-in's side, once: the deadline, or traffic it was never meant to see.</summary>
    private void Terminate(VpnChannel channel, string why)
    {
        if (Interlocked.Exchange(ref _terminating, 1) == 1) return;
        Log.Write($"terminating: {why}");
        try { channel.TerminateConnection($"Sallyport spike: {why}"); }
        catch (Exception e) { Log.Write($"terminate failed: {e.Message}"); }
    }

    /// <summary>Unicast to a destination outside the routed prefixes: traffic the platform should never have given this
    /// plug-in. Multicast and the limited broadcast are not counted — Windows sends its own (IGMP, mDNS, NetBIOS,
    /// WS-Discovery) on every new interface, the VPN's included, and dropping them costs the machine nothing.</summary>
    private bool Leaked(uint destination) =>
        destination >> 28 != 0xE && destination != uint.MaxValue && !_prefixes.Any(p => (destination & p.Mask) == p.Network);

    private static (uint Network, uint Mask, string Text) ParsePrefix(string text)
    {
        var parts = text.Split('/');
        var length = int.Parse(parts[1]);
        var mask = length == 0 ? 0u : uint.MaxValue << (32 - length);
        var address = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(IPAddress.Parse(parts[0]).GetAddressBytes());
        return (address & mask, mask, $"{parts[0]}/{length}");
    }

    private static string ToAddress(uint network)
    {
        var bytes = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, network);
        return new IPAddress(bytes).ToString();
    }

    /// <summary>An IPv4 packet as "proto src → dst" with ports for TCP and UDP: the 20-byte header, then the ports.</summary>
    private static string Describe(byte[] p)
    {
        if (p.Length < 20 || p[0] >> 4 != 4) return $"{p.Length} bytes, not IPv4";
        var headerLength = (p[0] & 0x0f) * 4;
        var proto = p[9];
        string Port(int at) => p.Length >= at + 2 ? $":{p[at] << 8 | p[at + 1]}" : "";
        var ports = proto is 6 or 17;
        var name = proto switch { 6 => "tcp", 17 => "udp", 1 => "icmp", _ => $"proto{proto}" };
        return $"{name} {p[12]}.{p[13]}.{p[14]}.{p[15]}{(ports ? Port(headerLength) : "")} -> {p[16]}.{p[17]}.{p[18]}.{p[19]}{(ports ? Port(headerLength + 2) : "")} ({p.Length} B)";
    }
}

/// <summary>A log the package's own folder holds, which the control console can print: the plug-in runs where no console is.</summary>
internal static class Log
{
    private static readonly object Gate = new();

    public static void Write(string line)
    {
        try
        {
            var path = Path.Combine(ApplicationData.Current.LocalFolder.Path, "plugin.log");
            lock (Gate) File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} [{Environment.ProcessId}] {line}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Logging must never be what breaks the plug-in.
        }
    }
}
