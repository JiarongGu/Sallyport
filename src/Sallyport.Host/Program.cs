using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Sallyport.Hosting;
using Sallyport.Profile;

// Sallyport.Host serve <profile.ovpn> [--listen 127.0.0.1:1080] [--status <file.json>]
//   Keeps the tunnel up behind a loopback SOCKS5 proxy until stopped. Started hidden by a host (Kobo), so what it has
//   to say goes to the status file, not a console nobody sees.
// Sallyport.Host stop [--listen 127.0.0.1:1080]
//   Asks the instance serving that endpoint to leave (telling the server), and waits until the port is closed.
// Exit codes: 0 done; 1 failed; 2 could not start (bad arguments, unreadable or unsupported profile, port taken).

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: Sallyport.Host serve <profile.ovpn> [--listen 127.0.0.1:1080] [--status <file.json>]\n       Sallyport.Host stop [--listen 127.0.0.1:1080]");
    return 2;
}

var listen = ParseEndpoint(Option("--listen") ?? "127.0.0.1:1080");
if (listen is null) { Console.Error.WriteLine("--listen must be a loopback address and port, e.g. 127.0.0.1:1080"); return 2; }

switch (args[0])
{
    case "serve" when args.Length >= 2 && !args[1].StartsWith("--", StringComparison.Ordinal):
        return await ServeAsync(args[1], listen, Option("--status"));
    case "stop":
        return await StopAsync(listen);
    default:
        Console.Error.WriteLine($"unknown command '{args[0]}'");
        return 2;
}

static async Task<int> ServeAsync(string profilePath, IPEndPoint listen, string? statusFile)
{
    OvpnProfile profile;
    try { profile = OvpnProfile.Parse(await File.ReadAllTextAsync(profilePath)); }
    catch (Exception e)
    {
        // The profile's contents are never echoed: it holds a private key.
        Console.Error.WriteLine($"could not read the profile: {e.GetType().Name}");
        return 2;
    }
    if (Accepts(listen))
    {
        Console.Error.WriteLine($"something already answers at {listen}; not starting a second proxy there");
        return 2;
    }

    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    using var signal = StopSignal.Create(listen);
    var waiting = signal is null ? Task.CompletedTask : Task.Run(() => { signal.WaitOne(); stop.Cancel(); });

    var status = new StatusWriter(statusFile);
    await using var service = new TunnelService(profile, listen, status.Write);
    var running = service.RunAsync(stop.Token);
    // The file is refreshed while up, so a reader can tell a live service from one that died without a word.
    while (!running.IsCompleted)
    {
        await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(10)));
        status.Write(service.Status);
    }
    await running;
    status.Write(service.Status);
    return service.Status.State == TunnelServiceStatus.Refused ? 2 : 0;
}

static async Task<int> StopAsync(IPEndPoint listen)
{
    if (!StopSignal.TrySet(listen))
    {
        Console.WriteLine(Accepts(listen)
            ? $"something answers at {listen}, but it is not a Sallyport instance this can stop"
            : $"nothing was serving {listen}");
        return Accepts(listen) ? 1 : 0;
    }
    for (var i = 0; i < 100 && Accepts(listen); i++) await Task.Delay(100);
    if (Accepts(listen)) { Console.WriteLine($"asked the instance at {listen} to stop; it still answers after 10 s"); return 1; }
    Console.WriteLine($"stopped the instance at {listen}");
    return 0;
}

static bool Accepts(IPEndPoint endpoint)
{
    try
    {
        using var client = new TcpClient();
        return client.ConnectAsync(endpoint).Wait(TimeSpan.FromSeconds(1)) && client.Connected;
    }
    catch (AggregateException) { return false; }
    catch (SocketException) { return false; }
}

static IPEndPoint? ParseEndpoint(string text) =>
    IPEndPoint.TryParse(text, out var endpoint) && IPAddress.IsLoopback(endpoint.Address) && endpoint.Port > 0 ? endpoint : null;

string? Option(string name)
{
    var at = Array.IndexOf(args, name);
    return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
}

/// <summary>How "stop" reaches "serve": a named event per endpoint, so stopping one route cannot stop another's.</summary>
internal static class StopSignal
{
    private static string Name(IPEndPoint endpoint) => $"Local\\Sallyport.{endpoint.Address}.{endpoint.Port}";

    public static EventWaitHandle? Create(IPEndPoint endpoint) =>
        OperatingSystem.IsWindows() ? new EventWaitHandle(false, EventResetMode.ManualReset, Name(endpoint)) : null;

    public static bool TrySet(IPEndPoint endpoint)
    {
        if (!OperatingSystem.IsWindows() || !EventWaitHandle.TryOpenExisting(Name(endpoint), out var handle)) return false;
        using (handle) return handle.Set();
    }
}

/// <summary>Writes the service's status as JSON, replacing the file whole so a reader never sees half of one.</summary>
internal sealed class StatusWriter(string? path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly object _gate = new();
    private string? _lastLine;

    public void Write(TunnelServiceStatus status)
    {
        lock (_gate)
        {
            // The file is rewritten on every refresh; the log line only when something changed, so it reads as a history.
            var line = $"{status.State}{(status.Remote is null ? "" : $" {status.Remote}")}{(status.LastError is null ? "" : $" — {status.LastError}")}";
            if (line != _lastLine) Console.Error.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} {line}");
            _lastLine = line;
            if (path is null) return;
            var record = new { status, pid = Environment.ProcessId, written = DateTimeOffset.UtcNow };
            var temporary = path + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(record, Options));
            File.Move(temporary, path, overwrite: true);
        }
    }
}
