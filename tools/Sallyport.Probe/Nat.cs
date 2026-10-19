using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Sallyport;
using Sallyport.Net;

/// <summary>
/// How the VPN server's NAT maps the tunnel's UDP, which decides whether a host-based or peer-to-peer game can work
/// through it (docs/design/game-routing.md): STUN binding requests (RFC 5389) from one tunnel port to several servers.
/// The same public port for every server is endpoint-independent mapping, which NAT traversal needs; a new port per
/// server is symmetric NAT, which defeats it. The public address itself is never printed — it is the server's.
/// </summary>
internal static class Nat
{
    // Two operators, so one server's quirk cannot pass for the NAT's behaviour.
    private static readonly (string Host, int Port)[] Servers =
    [
        ("stun.l.google.com", 19302),
        ("stun1.l.google.com", 19302),
        ("stun.cloudflare.com", 3478),
    ];

    private const uint MagicCookie = 0x2112A442;

    public static async Task<int> RunAsync(Tunnel tunnel)
    {
        if (tunnel.Config.Address is not { } local || !Ipv4.IsIpv4(local))
        {
            Console.WriteLine("nat: unverifiable — the server pushed no IPv4 address");
            return 2;
        }

        var targets = new List<(string Name, IPEndPoint Endpoint)>();
        foreach (var (host, port) in Servers)
        {
            // Resolved directly, not through the tunnel: only the binding requests must take the tunnel's path.
            try
            {
                var address = (await Dns.GetHostAddressesAsync(host)).First(Ipv4.IsIpv4);
                targets.Add((host, new IPEndPoint(address, port)));
            }
            catch (Exception) { Console.WriteLine($"nat: {host} does not resolve; left out"); }
        }
        if (targets.Count < 2)
        {
            Console.WriteLine("nat: unverifiable — fewer than two STUN servers resolve");
            return 2;
        }

        var first = (ushort)RandomNumberGenerator.GetInt32(49152, 65000);
        var second = (ushort)(first + 1);
        var mapped = new List<(string Name, ushort From, IPEndPoint? Public)>();
        foreach (var (name, endpoint) in targets) mapped.Add((name, first, await BindAsync(tunnel, local, first, endpoint)));
        // A second local port to the first server: whether the NAT keeps port numbers, and how it allocates new ones.
        mapped.Add((targets[0].Name, second, await BindAsync(tunnel, local, second, targets[0].Endpoint)));

        foreach (var (name, from, at) in mapped)
            Console.WriteLine($"nat: tunnel port {from} -> {name}: {(at is null ? "no answer" : $"mapped to public port {at.Port}")}");

        var fromFirst = mapped.Where(m => m.From == first && m.Public is not null).Select(m => m.Public!).ToList();
        if (fromFirst.Count < 2)
        {
            Console.WriteLine("nat mapping: unverifiable — fewer than two servers answered through the tunnel");
            return 2;
        }
        var oneAddress = fromFirst.Select(p => p.Address).Distinct().Count() == 1;
        var onePort = fromFirst.Select(p => p.Port).Distinct().Count() == 1;
        Console.WriteLine($"nat: one public address for every server: {(oneAddress ? "yes" : "no")}");
        Console.WriteLine(onePort
            ? "nat mapping: endpoint-independent — one tunnel port keeps one public port whoever it talks to; peer-to-peer traversal can work"
            : "nat mapping: endpoint-dependent (symmetric) — each server saw a different public port; peers cannot reach a port learned from another, so traversal fails");
        if (mapped.Last().Public is { } next && fromFirst.Count > 0)
            Console.WriteLine($"nat: port preserved: {(fromFirst[0].Port == first ? "yes" : "no")}; the next tunnel port mapped {next.Port - fromFirst[0].Port:+0;-0;0} from the first");
        // Filtering — whether a peer that was never sent to may send in — needs a STUN server that answers from another
        // address on request (RFC 5780); the public ones do not, so it is said, not guessed.
        Console.WriteLine("nat filtering: could not be checked — no public STUN server here answers from a second address");
        return onePort ? 0 : 1;
    }

    /// <summary>One binding request, asked again each second for up to 5 s: one lost datagram must not read as "no NAT".</summary>
    private static async Task<IPEndPoint?> BindAsync(Tunnel tunnel, IPAddress local, ushort port, IPEndPoint server)
    {
        var transaction = RandomNumberGenerator.GetBytes(12);
        // The 20-byte header: type 0x0001 (binding request), length 0, the magic cookie, the 96-bit transaction id.
        var request = new byte[20];
        BinaryPrimitives.WriteUInt16BigEndian(request, 0x0001);
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(4), MagicCookie);
        transaction.CopyTo(request, 8);
        var packet = Ipv4.UdpPacket(local, port, server.Address, (ushort)server.Port, request, (ushort)RandomNumberGenerator.GetInt32(1, 65536));

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var clock = Stopwatch.StartNew();
        var nextAsk = TimeSpan.Zero;
        while (!deadline.IsCancellationRequested)
        {
            if (clock.Elapsed >= nextAsk)
            {
                await tunnel.SendAsync(packet, deadline.Token);
                nextAsk = clock.Elapsed + TimeSpan.FromSeconds(1);
            }
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            wait.CancelAfter(nextAsk - clock.Elapsed);
            byte[] reply;
            try { reply = await tunnel.Packets.ReadAsync(wait.Token); }
            catch (OperationCanceledException) { continue; }
            if (Ipv4.TryReadUdp(reply, out var header, out var from, out var to, out var payload)
                && header.Source.Equals(server.Address) && from == server.Port && to == port
                && Mapped(payload, transaction) is { } at)
                return at;
        }
        return null;
    }

    /// <summary>XOR-MAPPED-ADDRESS (0x0020), or MAPPED-ADDRESS (0x0001) from an older server, out of a binding success
    /// response (0x0101) carrying this transaction's id.</summary>
    private static IPEndPoint? Mapped(ReadOnlySpan<byte> message, ReadOnlySpan<byte> transaction)
    {
        if (message.Length < 20 || BinaryPrimitives.ReadUInt16BigEndian(message) != 0x0101 || !message.Slice(8, 12).SequenceEqual(transaction))
            return null;
        var length = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(message[2..]), message.Length - 20);
        var attributes = message.Slice(20, length);
        while (attributes.Length >= 4)
        {
            var type = BinaryPrimitives.ReadUInt16BigEndian(attributes);
            var size = BinaryPrimitives.ReadUInt16BigEndian(attributes[2..]);
            if (attributes.Length < 4 + size) break;
            var value = attributes.Slice(4, size);
            // Family 0x01 is IPv4: 1 reserved byte, 1 family byte, a 2-byte port, a 4-byte address.
            if ((type is 0x0020 or 0x0001) && size >= 8 && value[1] == 0x01)
            {
                var port = BinaryPrimitives.ReadUInt16BigEndian(value[2..]);
                var address = BinaryPrimitives.ReadUInt32BigEndian(value[4..]);
                if (type == 0x0020)
                {
                    port ^= (ushort)(MagicCookie >> 16);
                    address ^= MagicCookie;
                }
                var bytes = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(bytes, address);
                return new IPEndPoint(new IPAddress(bytes), port);
            }
            // Attributes are padded to a multiple of four bytes.
            attributes = attributes[(4 + (size + 3) / 4 * 4)..];
        }
        return null;
    }
}
