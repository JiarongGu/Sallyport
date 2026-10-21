using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;

namespace Sallyport.Net;

/// <summary>An answer from a resolver reached through the tunnel.</summary>
internal sealed record DnsAnswer(IReadOnlyList<IPAddress> Addresses, int ResponseCode, TimeSpan Elapsed, int OtherPacketsSkipped);

/// <summary>
/// One DNS lookup through the tunnel, reading <see cref="Tunnel.Packets"/> itself — so only usable while nothing else
/// reads them. Phase 2's measurement and a probe's check; the proxy's resolver will share the packets with TCP.
/// </summary>
internal static class TunnelDns
{
    public static async Task<DnsAnswer> QueryAsync(Tunnel tunnel, IPAddress server, string name, TimeSpan timeout, CancellationToken ct = default)
    {
        if (tunnel.Config.Address is not { } local || !Ipv4.IsIpv4(local)) throw new InvalidOperationException("the server pushed no IPv4 address for this client");
        var id = (ushort)RandomNumberGenerator.GetInt32(1, 65536);
        var port = (ushort)RandomNumberGenerator.GetInt32(49152, 65536);
        var query = Ipv4.UdpPacket(local, port, server, 53, DnsMessage.Query(id, name, DnsMessage.TypeA), id);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var clock = Stopwatch.StartNew();
        var skipped = 0;
        var nextAsk = TimeSpan.Zero;
        while (true)
        {
            // Asked again every 2 s: one lost datagram must not read as "no answer".
            if (clock.Elapsed >= nextAsk)
            {
                await tunnel.SendAsync(query, deadline.Token);
                nextAsk = clock.Elapsed + TimeSpan.FromSeconds(2);
            }
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            wait.CancelAfter(nextAsk - clock.Elapsed);
            byte[] packet;
            try { packet = await tunnel.Packets.ReadAsync(wait.Token); }
            catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { continue; }
            if (Ipv4.TryReadUdp(packet, out var header, out var from, out var to, out var payload)
                && header.Source.Equals(server) && from == 53 && to == port
                && DnsMessage.TryReadAnswers(payload, id, DnsMessage.TypeA, out var code, out var addresses))
                return new DnsAnswer(addresses, code, clock.Elapsed, skipped);
            skipped++;
        }
    }
}
