using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Xml.Linq;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Networking.Vpn;
using Windows.Storage;

namespace Sallyport.Vpn.Plugin;

/// <summary>
/// The plug-in as a relay, not a tunnel: the programs' IP packets go out as they are, one per datagram, to the bridge
/// on this machine's loopback, which carries them through the OpenVPN tunnel; what comes back is handed to Windows as it
/// is. No key, no profile and no server is known here. The platform suspends this process between its events (D12), so
/// it cannot keep a tunnel alive; the bridge, an ordinary process, can.
/// </summary>
/// <remarks>
/// Only the routes and programs the bridge names are carried: a default route is refused (D13), and a packet for a
/// destination outside the routes — which the platform should never send here — is dropped and counted, not forwarded.
/// </remarks>
internal sealed partial class RelayPlugin : IVpnPlugIn
{
    /// <summary>"SPRT" then the run's token: the bridge takes packets only from the endpoint that said this first.</summary>
    private static readonly byte[] HelloMagic = "SPRT"u8.ToArray();

    private DatagramSocket? _socket;
    private List<(uint Network, uint Mask)> _prefixes = [];
    private List<(uint Network, uint Mask)> _excluded = [];
    private long _out, _in, _dropped;

    public void Connect(VpnChannel channel)
    {
        try
        {
            // <relay port="…" token="…"/><address>…</address><mtu>…</mtu><prefix>a.b.c.d/n</prefix>…<app>C:\…\x.exe</app>…
            var config = XElement.Parse(channel.Configuration.CustomField);
            var relay = config.Element("relay") ?? throw new InvalidOperationException("the profile names no relay");
            var port = (string?)relay.Attribute("port") ?? throw new InvalidOperationException("the relay has no port");
            var token = Convert.FromHexString((string?)relay.Attribute("token") ?? throw new InvalidOperationException("the relay has no token"));
            var address = (string?)config.Element("address") ?? throw new InvalidOperationException("the profile names no tunnel address");
            var mtu = (uint)Math.Clamp((int?)config.Element("mtu") ?? 1400, 576, 1500);
            var apps = config.Elements("app").Select(e => e.Value).ToList();
            _prefixes = [.. config.Elements("prefix").Select(e => Prefix(e.Value))];
            _excluded = [.. config.Elements("exclude").Select(e => Prefix(e.Value))];
            if (_prefixes.Count == 0) throw new InvalidOperationException("the profile routes no prefix");
            // A default route took the whole machine in the spike (D12): only when the profile names it, which the bridge
            // does only when asked to measure it, under its guard.
            if (_prefixes.Any(p => p.Mask == 0) && (string?)config.Element("defaultRoute") != "allow")
                throw new InvalidOperationException("the profile routes a default route without allowing one: refused");
            var policy = (string?)config.Element("policy") == "force" ? VpnRoutingPolicyType.ForceAllTrafficOverVpn : VpnRoutingPolicyType.SplitRouting;
            if (apps.Count == 0) throw new InvalidOperationException("the profile names no program");
            _out = _in = _dropped = 0;

            _socket = new DatagramSocket();
            channel.AssociateTransport(_socket, null);
            _socket.ConnectAsync(new HostName("127.0.0.1"), port).AsTask().Wait();
            _socket.OutputStream.WriteAsync(HelloMagic.Concat(token).ToArray().AsBuffer()).AsTask().Wait();

            var filters = new VpnTrafficFilterAssignment { AllowOutbound = true, AllowInbound = true };
            // A filter's Protocol defaults to None (0), which matches nothing (measured, D12): one filter per protocol.
            foreach (var app in apps)
                foreach (var protocol in new[] { VpnIPProtocol.Tcp, VpnIPProtocol.Udp })
                    filters.TrafficFilterList.Add(new VpnTrafficFilter(new VpnAppId(VpnAppIdType.FilePath, app))
                    {
                        RoutingPolicyType = policy,
                        Protocol = protocol,
                    });
            var routes = new VpnRouteAssignment();
            foreach (var (network, mask) in _prefixes)
                routes.Ipv4InclusionRoutes.Add(new VpnRoute(new HostName(Text(network)), (byte)System.Numerics.BitOperations.PopCount(mask)));
            foreach (var (network, mask) in _excluded)
                routes.Ipv4ExclusionRoutes.Add(new VpnRoute(new HostName(Text(network)), (byte)System.Numerics.BitOperations.PopCount(mask)));

            channel.StartWithTrafficFilter([new HostName(address)], null, null, routes, null, mtu, mtu + 100, false, _socket, null, filters);
            Log.Write($"connected: relay 127.0.0.1:{port}, address {address}, mtu {mtu}, {_prefixes.Count} prefix(es), {_excluded.Count} excluded, {policy}, {apps.Count} program(s)");
        }
        catch (Exception e)
        {
            Log.Write($"connect failed: {e}");
            throw;
        }
    }

    public void Disconnect(VpnChannel channel)
    {
        Log.Write($"disconnect: {_out} packet(s) out, {_in} in, {_dropped} dropped");
        channel.Stop();
        _socket?.Dispose();
        _socket = null;
    }

    // The bridge keeps the tunnel alive; the loopback leg needs no keepalive of the platform's.
    public void GetKeepAlivePayload(VpnChannel channel, out VpnPacketBuffer keepAlivePacket) => keepAlivePacket = null!;

    public void Encapsulate(VpnChannel channel, VpnPacketBufferList packets, VpnPacketBufferList encapulatedPackets)
    {
        try
        {
            while (packets.Size > 0)
            {
                var packet = packets.RemoveAtBegin();
                // The IPv4 header's destination, bytes 16..19: only what the routes cover is carried.
                var head = packet.Buffer.Length >= 20 ? packet.Buffer.ToArray(0, 20) : [];
                if (head.Length < 20 || head[0] >> 4 != 4 || !Routed(BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(16))))
                {
                    Interlocked.Increment(ref _dropped);
                    continue;
                }
                encapulatedPackets.Append(packet);
                Interlocked.Increment(ref _out);
            }
        }
        // An exception out of a callback ends the host process, and the platform starts another (measured): said here.
        catch (Exception e) { Log.Write($"encapsulate failed: {e}"); }
    }

    public void Decapsulate(VpnChannel channel, VpnPacketBuffer encapBuffer, VpnPacketBufferList decapsulatedPackets, VpnPacketBufferList controlPacketsToSend)
    {
        try
        {
            var datagram = encapBuffer.Buffer.ToArray();
            // The bridge sends whole IP packets; anything else on the loopback leg is not one, and is not given to Windows.
            if (datagram.Length < 20 || datagram[0] >> 4 != 4)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }
            // Into a buffer of the channel's own: the one the datagram arrived in belongs to the transport's receive, and
            // handing it back as a packet ended the process (measured).
            var packet = channel.GetVpnReceivePacketBuffer();
            datagram.CopyTo(packet.Buffer);
            packet.Buffer.Length = (uint)datagram.Length;
            decapsulatedPackets.Append(packet);
            if (Interlocked.Increment(ref _in) <= 3) Log.Write($"decapsulated {datagram.Length} bytes");
        }
        catch (Exception e) { Log.Write($"decapsulate failed: {e}"); }
    }

    /// <summary>Unicast inside the routes. Multicast and the limited broadcast Windows sends on every new interface (IGMP,
    /// mDNS, NetBIOS) are not the programs' and are dropped quietly with the rest.</summary>
    private bool Routed(uint destination) =>
        destination >> 28 != 0xE && destination != uint.MaxValue && _prefixes.Any(p => (destination & p.Mask) == p.Network)
        && !_excluded.Any(p => (destination & p.Mask) == p.Network);

    private static (uint Network, uint Mask) Prefix(string text)
    {
        var parts = text.Split('/');
        var length = int.Parse(parts[1]);
        var mask = length == 0 ? 0u : uint.MaxValue << (32 - length);
        return (BinaryPrimitives.ReadUInt32BigEndian(IPAddress.Parse(parts[0]).GetAddressBytes()) & mask, mask);
    }

    private static string Text(uint network)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, network);
        return new IPAddress(bytes).ToString();
    }
}

/// <summary>A log in the package's own folder, which the bridge prints: the plug-in runs where no console is.</summary>
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
