using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Sallyport.Windows.PacketFilter;
using Sallyport.Windows.Service;

/// <summary>
/// The guard outside every interceptor (D13): it probes the direct path twice a second and, when it has been dead 3 s,
/// undoes interception — the tool's own handle first, then killing the interceptors (both measured to give the adapter
/// back, gate 1). Removing the driver, also measured to give the network back (D18), only with <c>--may-remove</c>: over
/// a game session the direct path can die for reasons of its own (Wi-Fi), and removal is not undone by the path coming
/// back. A remedy that fails is said and the watch goes on: the guard is what must not stop while the network is dead.
/// </summary>
internal static class Guard
{
    private static readonly TimeSpan Round = TimeSpan.FromMilliseconds(500);

    public static async Task<int> RunAsync(int seconds, bool mayRemove, IPAddress internet, Func<int> reset, PacketFilterSetup setup)
    {
        var targets = new[] { new IPEndPoint(internet, 443), new IPEndPoint(IPAddress.Parse("8.8.8.8"), 443) };
        var clock = Stopwatch.StartNew();
        TimeSpan? deadSince = null;
        var stage = 0;
        int rounds = 0, failed = 0;
        var longest = TimeSpan.Zero;
        Console.WriteLine($"{Now()} guard: {seconds} s, probing {string.Join(" and ", targets.Select(t => t.ToString()))}");
        // What it could not do if it had to, said before it has to (rule 6): a removal with no installer to run.
        if (mayRemove && setup.Status().InstallerPath is null)
            Console.WriteLine($"{Now()} guard: --may-remove, but no installer beside this tool: the driver could NOT be removed (run fetch first)");
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            var round = clock.Elapsed;
            var times = await Task.WhenAll(targets.Select(ProbeAsync));
            rounds++;
            var best = times.Where(t => t is not null).Select(t => t!.Value).DefaultIfEmpty(-1).Min();
            if (best >= 0)
            {
                Console.WriteLine($"{Now()} ok {best:F1} ms");
                if (deadSince is { } since)
                {
                    var outage = round - since;
                    longest = outage > longest ? outage : longest;
                    Console.WriteLine($"{Now()} guard: direct path back after {outage.TotalSeconds:F1} s");
                }
                deadSince = null;
                stage = 0;
            }
            else
            {
                failed++;
                deadSince ??= round;
                var dead = round - deadSince.Value;
                Console.WriteLine($"{Now()} FAIL (dead {dead.TotalSeconds:F1} s)");
                if (stage == 0 && dead >= TimeSpan.FromSeconds(3)) { stage = 1; Remedy("reset the adapter", () => { reset(); return Task.CompletedTask; }); }
                else if (stage == 1 && dead >= TimeSpan.FromSeconds(8)) { stage = 2; Remedy("kill the interceptors", () => { KillInterceptors(); return Task.CompletedTask; }); }
                else if (stage == 2 && mayRemove && dead >= TimeSpan.FromSeconds(18))
                {
                    stage = 3;
                    Console.WriteLine($"{Now()} guard: removing the driver");
                    await RemedyAsync("remove the driver", async () =>
                    {
                        var outcome = await setup.UninstallAsync();
                        Console.WriteLine($"{outcome.Text} (exit {outcome.ExitCode}){(outcome.LogPath is { } log ? $"; log {log}" : "")}");
                    });
                }
            }
            var wait = Round - (clock.Elapsed - round);
            if (wait > TimeSpan.Zero) await Task.Delay(wait);
        }
        Console.WriteLine($"{Now()} guard: {rounds} rounds, {failed} failed, longest outage {longest.TotalSeconds:F1} s"
            + (deadSince is not null ? ", STILL DEAD at the end" : ""));
        return deadSince is null ? 0 : 1;
    }

    private static void Remedy(string what, Func<Task> remedy) => RemedyAsync(what, remedy).GetAwaiter().GetResult();

    private static async Task RemedyAsync(string what, Func<Task> remedy)
    {
        try { await remedy(); }
        catch (Exception e) { Console.WriteLine($"{Now()} guard: REMEDY FAILED ({what}): {e.GetType().Name}: {e.Message}; still watching"); }
    }

    private static async Task<double?> ProbeAsync(IPEndPoint target)
    {
        using var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var started = Stopwatch.GetTimestamp();
        try
        {
            await socket.ConnectAsync(target, timeout.Token);
            return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException) { return null; }
    }

    // What intercepts: this tool's other runs, and since D30 the service's workers — Sallyport.Client in session 0 that
    // is not the service's own process. Each said as killed, as refused, or that there was none.
    private static void KillInterceptors()
    {
        var self = Environment.ProcessId;
        int? service = null;
        try { if (ServiceControl.Status(RouterProtocol.ServiceName) is { ProcessId: not 0 and var pid }) service = pid; }
        catch (Win32Exception) { }
        var found = 0;
        foreach (var process in Process.GetProcessesByName("Sallyport.Driver").Concat(Process.GetProcessesByName("Sallyport.Client")))
            using (process)
            {
                bool interceptor;
                try { interceptor = process.ProcessName == "Sallyport.Driver" ? process.Id != self : process.SessionId == 0 && process.Id != service; }
                catch (InvalidOperationException) { continue; }
                if (!interceptor) continue;
                found++;
                try
                {
                    process.Kill();
                    process.WaitForExit(2000);
                    Console.WriteLine($"{Now()} guard: killed interceptor {process.Id} ({process.ProcessName})");
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException)
                {
                    Console.WriteLine($"{Now()} guard: COULD NOT kill interceptor {process.Id} ({process.ProcessName}): {e.Message}");
                }
            }
        if (found == 0) Console.WriteLine($"{Now()} guard: no interceptor running to kill");
    }

    private static string Now() => DateTime.Now.ToString("HH:mm:ss.fff");
}
