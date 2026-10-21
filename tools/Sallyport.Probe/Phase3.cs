using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Sallyport;
using Sallyport.Net;
using Sallyport.Proxy;

/// <summary>Phase 3's measurements: TCP through the tunnel against real servers, and the proxy a browser will use.</summary>
internal static class Phase3
{
    private const long DownloadBytes = 10_000_000;
    private const int ConcurrentDownloadBytes = 1_000_000;
    // Past the proxy's minute for a half-closed relay whose server stays silent (Socks5Server.HalfClosedSilence), and
    // past the network's linger and time-wait after that: longer than this, a connection is not being let go at all.
    private static readonly TimeSpan ProxyLetsGo = TimeSpan.FromSeconds(75), NetworkForgets = TimeSpan.FromSeconds(45);
    // Each answers /cdn-cgi/trace with the address and country a request came from. Several names, so a burst also
    // asks the tunnel's DNS for several names at once, as a page does.
    private static readonly string[] TraceHosts =
        ["www.cloudflare.com", "speed.cloudflare.com", "one.one.one.one", "cloudflare-dns.com", "developers.cloudflare.com", "blog.cloudflare.com"];

    public static async Task<int> RunAsync(Tunnel tunnel, bool fetch, int serveSeconds, int concurrent)
    {
        await using var network = new TunnelNetwork(tunnel);
        var outcome = 0;
        if (fetch) outcome = await FetchAsync(network);
        if (concurrent > 0) outcome = Worse(outcome, await ConcurrentAsync(tunnel, network, concurrent));
        if (serveSeconds > 0)
        {
            await using var proxy = new Socks5Server(new TunnelConnector(network));
            proxy.Start();
            await using var peak = new PeakSampler(proxy);
            Console.WriteLine($"SOCKS5 proxy on {proxy.Endpoint} for {serveSeconds} s");
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < serveSeconds && !tunnel.Completion.IsCompleted)
            {
                await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(10)), tunnel.Completion);
                var n = network.Counters;
                Console.WriteLine($"  {clock.Elapsed.TotalSeconds:0} s: proxy {proxy.ActiveConnections} active ({peak.Reset()} at most since the last line), "
                    + $"up {proxy.BytesUp} B, down {proxy.BytesDown} B; tcp {n.OpenConnections} open / {n.ConnectionsOpened} opened / {n.ConnectsFailed} failed, "
                    + $"{n.Retransmits} retransmits; dns {n.Lookups} ({n.LookupsFromCache} cached)");
            }
            if (tunnel.Completion.IsFaulted) Console.WriteLine($"the tunnel ended: {tunnel.Completion.Exception?.InnerException?.Message}");
        }
        Console.WriteLine($"network: {Describe(network.Counters)}");
        Console.WriteLine($"tunnel: {Describe(tunnel.Counters)}");
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
            Console.WriteLine($"ipinfo.io over the tunnel's TCP: {Addresses.Masked(root.GetProperty("ip").GetString())} {root.GetProperty("country")} {root.GetProperty("city")} "
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
        // No answer in time says nothing of the tunnel's TCP either way (rule 6): a service on the far side may be down.
        catch (TaskCanceledException e)
        {
            Console.WriteLine($"outcome: unverifiable — no answer in time ({e.Message})");
            return 2;
        }
        catch (Exception e)
        {
            Console.WriteLine($"outcome: failed — {e.GetType().Name}: {e.Message}{(e.InnerException is { } inner ? $" ({inner.Message})" : "")}");
            return 1;
        }
    }

    private sealed record Fetched(bool Whole, long Bytes, long? Claimed, int Status, double Seconds, string? Ip, string? Loc, string? Error);

    /// <summary>Many connections at once through the proxy, as a browser page opens them: first each asking one of several
    /// hosts where it came from, then all downloading together. Every request has a handler of its own, so each is a
    /// connection of its own rather than a turn on a shared one.</summary>
    private static async Task<int> ConcurrentAsync(Tunnel tunnel, TunnelNetwork network, int count)
    {
        await using var proxy = new Socks5Server(new TunnelConnector(network));
        proxy.Start();
        await using var peak = new PeakSampler(proxy, tunnel);

        var before = network.Counters;
        var clock = Stopwatch.StartNew();
        var traces = await Task.WhenAll(Enumerable.Range(0, count).Select(i =>
            Task.Run(() => GetAsync(proxy, $"https://{TraceHosts[i % TraceHosts.Length]}/cdn-cgi/trace", keep: true))));
        var wall = clock.Elapsed.TotalSeconds;
        var egress = traces.Where(t => t.Ip is not null).GroupBy(t => (t.Ip, t.Loc)).Select(g => $"{Addresses.Masked(g.Key.Ip)} {g.Key.Loc} ×{g.Count()}").ToList();
        Console.WriteLine($"concurrent, round 1: {count} connections at once to {Math.Min(count, TraceHosts.Length)} host(s), /cdn-cgi/trace: "
            + $"{traces.Count(t => t.Whole)} answered whole, {traces.Count(t => t.Error is not null)} failed, in {wall:0.0} s "
            + $"({Spread(traces)}); at most {peak.Reset()} through the proxy at once");
        Console.WriteLine($"  left from: {(egress.Count == 0 ? "no answer said" : string.Join("; ", egress))}");
        Report(traces);
        Console.WriteLine($"  network: {Describe(network.Counters, before)}");
        // Round 2's count of connections at once is its own only once round 1's have closed.
        var (held, released) = await DrainAsync(proxy, ProxyLetsGo);
        Console.WriteLine($"  the proxy held {held} of them {released:0.0} s after the last response");

        before = network.Counters;
        var tunnelBefore = tunnel.Counters;
        var runtimeBefore = RuntimeLoad.Now();
        peak.ResetPending();
        clock.Restart();
        var downloads = await Task.WhenAll(Enumerable.Range(0, count).Select(_ =>
            Task.Run(() => GetAsync(proxy, $"https://speed.cloudflare.com/__down?bytes={ConcurrentDownloadBytes}", keep: false))));
        wall = clock.Elapsed.TotalSeconds;
        var bytes = downloads.Sum(d => d.Bytes);
        var whole = downloads.Count(d => d.Whole && d.Bytes == ConcurrentDownloadBytes && d.Status == 200);
        Console.WriteLine($"concurrent, round 2: {count} downloads of {ConcurrentDownloadBytes} bytes at once: {whole} whole, "
            + $"{downloads.Count(d => d.Error is null && !(d.Whole && d.Bytes == ConcurrentDownloadBytes && d.Status == 200))} short or refused, "
            + $"{downloads.Count(d => d.Error is not null)} failed; {bytes / 1e6:0.0} MB in {wall:0.0} s = {bytes / wall / 1e6:0.00} MB/s together "
            + $"({Spread(downloads)}); at most {peak.Reset()} through the proxy at once");
        Report(downloads);
        Console.WriteLine($"  network: {Describe(network.Counters, before)}");
        var t = tunnel.Counters;
        Console.WriteLine($"  tunnel: {t.PacketsIn - tunnelBefore.PacketsIn} packets in, {t.MissingIn - tunnelBefore.MissingIn} missing, "
            + $"{t.Overflowed - tunnelBefore.Overflowed} overflowed; the packet queue reached {peak.ResetQueue()} of 4096");
        Console.WriteLine($"  runtime: {RuntimeLoad.Now().Since(runtimeBefore)}; thread pool backlog at most {peak.ResetPending()}");

        // Every client has hung up: the proxy should let go of each connection once the far side answers our FIN, and
        // the network forget it once its time-wait is over — a session that keeps them only grows.
        var (stillHeld, after) = await DrainAsync(proxy, ProxyLetsGo);
        held += stillHeld;
        clock.Restart();
        while (network.Counters.OpenConnections > 0 && clock.Elapsed < NetworkForgets) await Task.Delay(250);
        var open = network.Counters.OpenConnections;
        Console.WriteLine($"  the proxy held {stillHeld} of them {after:0.0} s after the last response; "
            + $"the network kept {open} connection(s) {clock.Elapsed.TotalSeconds:0.0} s after that");

        // A request can fail only through the tunnel or the far server; which, the line above each failure says.
        if (tunnel.Completion.IsCompleted)
        {
            Console.WriteLine("outcome: unverifiable — the tunnel ended during the run, so a failure says nothing about concurrency");
            return 2;
        }
        var failures = traces.Count(r => !r.Whole) + downloads.Count(d => !(d.Whole && d.Bytes == ConcurrentDownloadBytes && d.Status == 200));
        if (failures > 0)
        {
            Console.WriteLine($"outcome: failed — {failures} of {2 * count} connections did not come back whole");
            return 1;
        }
        if (held > 0 || open > 0)
        {
            Console.WriteLine($"outcome: failed — every response came back whole, but {held} proxied and {open} tunnel connection(s) were never let go");
            return 1;
        }
        if (egress.Count != 1)
        {
            Console.WriteLine(egress.Count == 0
                ? "outcome: unverifiable — every connection came back whole, but no host said where they left from"
                : "outcome: failed — the connections did not all leave from the same place");
            return egress.Count == 0 ? 2 : 1;
        }
        // TCP repairs a packet this process dropped, so the run still holds; but the loss was ours, and is said.
        var overflowed = t.Overflowed - tunnelBefore.Overflowed;
        Console.WriteLine($"outcome: holds — {count} connections at once, twice, all whole, all leaving from one address"
            + (overflowed > 0 ? $"; {overflowed} packet(s) were dropped by this process's own full queues on the way" : ""));
        return 0;
    }

    /// <summary>Waits until the proxy holds no connection or the limit passes: how many it still held, and when.</summary>
    private static async Task<(int Held, double Seconds)> DrainAsync(Socks5Server proxy, TimeSpan limit)
    {
        var clock = Stopwatch.StartNew();
        while (proxy.ActiveConnections > 0 && clock.Elapsed < limit) await Task.Delay(50);
        return (proxy.ActiveConnections, clock.Elapsed.TotalSeconds);
    }

    private static async Task<Fetched> GetAsync(Socks5Server proxy, string url, bool keep)
    {
        using var http = new HttpClient(new SocketsHttpHandler
        {
            // socks5 (not socks4): the name goes to the proxy unresolved, so the tunnel's DNS answers it, as for a browser.
            Proxy = new WebProxy($"socks5://{proxy.Endpoint}"),
            UseProxy = true,
        }) { Timeout = TimeSpan.FromSeconds(120) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Sallyport-Probe/0.1");
        var clock = Stopwatch.StartNew();
        long received = 0;
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            var claimed = response.Content.Headers.ContentLength;
            await using var body = await response.Content.ReadAsStreamAsync();
            var kept = new MemoryStream();
            var buffer = new byte[81920];
            int n;
            while ((n = await body.ReadAsync(buffer)) > 0)
            {
                received += n;
                if (keep) kept.Write(buffer, 0, n);
            }
            string? ip = null, loc = null;
            if (keep)
                foreach (var line in System.Text.Encoding.ASCII.GetString(kept.ToArray()).Split('\n'))
                {
                    if (line.StartsWith("ip=", StringComparison.Ordinal)) ip = line[3..].Trim();
                    if (line.StartsWith("loc=", StringComparison.Ordinal)) loc = line[4..].Trim();
                }
            // The handler throws on a body that ends before its stated length, so reaching here is whole by its own framing.
            return new Fetched(claimed is null || claimed == received, received, claimed, (int)response.StatusCode, clock.Elapsed.TotalSeconds, ip, loc, null);
        }
        catch (Exception e)
        {
            var why = $"{e.GetType().Name}: {e.Message}{(e.InnerException is { } inner ? $" ({inner.GetType().Name}: {inner.Message})" : "")}";
            return new Fetched(false, received, null, 0, clock.Elapsed.TotalSeconds, null, null, why);
        }
    }

    private static string Spread(IReadOnlyCollection<Fetched> results)
    {
        var seconds = results.Select(r => r.Seconds).Order().ToList();
        return $"each took {seconds[0]:0.0}–{seconds[^1]:0.0} s, median {seconds[seconds.Count / 2]:0.0}";
    }

    private static void Report(IEnumerable<Fetched> results)
    {
        foreach (var group in results.Where(r => r.Error is not null || r.Status != 200).GroupBy(r => r.Error ?? $"HTTP {r.Status}"))
            Console.WriteLine($"  ×{group.Count()}: {group.Key}");
    }

    private static string Describe(NetworkCounters c, NetworkCounters? before = null)
    {
        var b = before ?? default;
        return $"{c.ConnectionsOpened - b.ConnectionsOpened} connection(s), {c.ConnectsFailed - b.ConnectsFailed} failed to connect, "
            + $"{c.Retransmits - b.Retransmits} retransmits, {c.Lookups - b.Lookups} lookup(s) ({c.LookupsFromCache - b.LookupsFromCache} cached), "
            + $"{c.PacketsDropped - b.PacketsDropped} packet(s) dropped; segments in {c.SegmentsIn - b.SegmentsIn}, "
            + $"out of order {c.SegmentsOutOfOrder - b.SegmentsOutOfOrder}, repeated {c.SegmentsRepeated - b.SegmentsRepeated}; ACKs sent {c.AcksSent - b.AcksSent}";
    }

    private static string Describe(TunnelCounters t) =>
        $"in {t.PacketsIn} pkt, out {t.PacketsOut} pkt, rejected {t.Rejected}, replayed {t.Replayed}, missing {t.MissingIn}, overflowed {t.Overflowed}";

    /// <summary>The probe's exit codes ranked: failed (1) outranks unverifiable (2), which outranks holds (0).</summary>
    private static int Worse(int a, int b) => a == 1 || b == 1 ? 1 : Math.Max(a, b);

    /// <summary>What the runtime spent while a round ran: a reader that falls behind on a machine with cores to spare is
    /// waiting on something — a collection, a lock, or a turn on the thread pool.</summary>
    private readonly record struct RuntimeLoad(TimeSpan Paused, int Gen0, int Gen1, int Gen2, long Contentions, int Threads, long Allocated)
    {
        public static RuntimeLoad Now() => new(GC.GetTotalPauseDuration(), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
            Monitor.LockContentionCount, ThreadPool.ThreadCount, GC.GetTotalAllocatedBytes());

        public string Since(RuntimeLoad b) =>
            $"GC paused {(Paused - b.Paused).TotalMilliseconds:0} ms over {Gen0 - b.Gen0}/{Gen1 - b.Gen1}/{Gen2 - b.Gen2} collections (gen 0/1/2), "
            + $"{(Allocated - b.Allocated) / 1e6:0} MB allocated, {Contentions - b.Contentions} contended locks, {Threads} pool threads";
    }

    /// <summary>The most connections the proxy held at once, sampled often: a count read every few seconds misses the
    /// burst a page opens and closes in under one.</summary>
    private sealed class PeakSampler : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private int _peak, _queue, _pending;

        public PeakSampler(Socks5Server proxy, Tunnel? tunnel = null) => _loop = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                Raise(ref _peak, proxy.ActiveConnections);
                if (tunnel is not null) Raise(ref _queue, tunnel.Packets.Count);
                Raise(ref _pending, (int)Math.Min(ThreadPool.PendingWorkItemCount, int.MaxValue));
                try { await Task.Delay(10, _stop.Token); } catch (OperationCanceledException) { }
            }
        });

        private static void Raise(ref int peak, int now)
        {
            int seen;
            while (now > (seen = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
        }

        public int Reset() => Interlocked.Exchange(ref _peak, 0);
        /// <summary>The deepest the tunnel's packet queue got, of its 4096: how far the network's reader fell behind.</summary>
        public int ResetQueue() => Interlocked.Exchange(ref _queue, 0);
        public int ResetPending() => Interlocked.Exchange(ref _pending, 0);

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _loop;
            _stop.Dispose();
        }
    }
}
