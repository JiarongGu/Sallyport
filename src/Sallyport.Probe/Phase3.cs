using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Sallyport;
using Sallyport.Net;
using Sallyport.Socks;

/// <summary>Phase 3's measurements: TCP through the tunnel against real servers, and the proxy a browser will use.</summary>
internal static class Phase3
{
    private const long DownloadBytes = 10_000_000;

    public static async Task<int> RunAsync(Tunnel tunnel, bool fetch, int serveSeconds)
    {
        await using var network = new TunnelNetwork(tunnel);
        var outcome = 0;
        if (fetch) outcome = await FetchAsync(network);
        if (serveSeconds > 0)
        {
            await using var proxy = new Socks5Server(new TunnelConnector(network));
            proxy.Start();
            Console.WriteLine($"SOCKS5 proxy on {proxy.Endpoint} for {serveSeconds} s");
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < serveSeconds && !tunnel.Completion.IsCompleted)
            {
                await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(10)), tunnel.Completion);
                var n = network.Counters;
                Console.WriteLine($"  {clock.Elapsed.TotalSeconds:0} s: proxy {proxy.ActiveConnections} active, up {proxy.BytesUp} B, down {proxy.BytesDown} B; "
                    + $"tcp {n.OpenConnections} open / {n.ConnectionsOpened} opened / {n.ConnectsFailed} failed, {n.Retransmits} retransmits; "
                    + $"dns {n.Lookups} ({n.LookupsFromCache} cached)");
            }
            if (tunnel.Completion.IsFaulted) Console.WriteLine($"the tunnel ended: {tunnel.Completion.Exception?.InnerException?.Message}");
        }
        var c = network.Counters;
        Console.WriteLine($"network: {c.ConnectionsOpened} connection(s), {c.ConnectsFailed} failed to connect, {c.Retransmits} retransmits, "
            + $"{c.Lookups} lookup(s), {c.PacketsDropped} packet(s) dropped; segments in {c.SegmentsIn}, out of order {c.SegmentsOutOfOrder}, "
            + $"repeated {c.SegmentsRepeated}; ACKs sent {c.AcksSent}");
        var t = tunnel.Counters;
        Console.WriteLine($"tunnel: in {t.PacketsIn} pkt, out {t.PacketsOut} pkt, rejected {t.Rejected}, replayed {t.Replayed}, missing {t.MissingIn}");
        return outcome;
    }

    private static async Task<int> FetchAsync(TunnelNetwork network)
    {
        using var http = new HttpClient(new SocketsHttpHandler
        {
            // Off: a proxy lookup (WPAD) on this machine costs seconds and could send the request around the tunnel.
            UseProxy = false,
            ConnectCallback = async (context, ct) =>
            {
                var addresses = await network.ResolveAsync(context.DnsEndPoint.Host, ct);
                return await network.ConnectAsync(addresses[0], context.DnsEndPoint.Port, ct);
            },
        }) { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Sallyport-Probe/0.1");

        try
        {
            var clock = Stopwatch.StartNew();
            using var info = JsonDocument.Parse(await http.GetStringAsync("https://ipinfo.io/json"));
            var root = info.RootElement;
            Console.WriteLine($"ipinfo.io over the tunnel's TCP: {root.GetProperty("ip")} {root.GetProperty("country")} {root.GetProperty("city")} "
                + $"({(root.TryGetProperty("org", out var org) ? org : default)}) in {clock.ElapsedMilliseconds} ms");

            clock.Restart();
            using var response = await http.GetAsync($"https://speed.cloudflare.com/__down?bytes={DownloadBytes}", HttpCompletionOption.ResponseHeadersRead);
            var claimed = response.Content.Headers.ContentLength;
            await using var body = await response.Content.ReadAsStreamAsync();
            var buffer = new byte[81920];
            long received = 0;
            int n;
            while ((n = await body.ReadAsync(buffer)) > 0) received += n;
            var seconds = clock.Elapsed.TotalSeconds;
            Console.WriteLine($"download: {received} of {claimed?.ToString() ?? "unstated"} bytes claimed in {seconds:0.0} s = {received / seconds / 1e6:0.00} MB/s");
            if (received != DownloadBytes || claimed is { } c && c != received)
            {
                Console.WriteLine("outcome: failed — the download came back short of what the server stated");
                return 1;
            }
            Console.WriteLine("outcome: holds — TCP through the tunnel carried TLS and a whole 10 MB download");
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine($"outcome: failed — {e.GetType().Name}: {e.Message}{(e.InnerException is { } inner ? $" ({inner.Message})" : "")}");
            return 1;
        }
    }
}
