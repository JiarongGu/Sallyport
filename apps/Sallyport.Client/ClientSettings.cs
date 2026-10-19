using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shenora.Core.Ipc;

namespace Sallyport.Client;

/// <summary>How an application is told about the proxy: each family reads it from a different place.</summary>
internal enum AppKind { Chromium, Electron, Firefox, Environment }

/// <summary>An application the client starts through a profile. A path and flags, never anything secret.</summary>
internal sealed record RoutedApp(string Id, string Name, string Path, AppKind Kind, string ProfileId, string Arguments);

/// <summary>
/// What the client remembers besides the profiles themselves: each profile's SOCKS port and the routed applications.
/// One JSON file, rewritten whole.
/// </summary>
internal sealed class ClientSettings
{
    /// <summary>1080 is Kobo's Host (D14): the client starts above it so both can run.</summary>
    public const int FirstPort = 1088;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _file;
    private readonly object _gate = new();
    private readonly Saved _saved;

    private sealed class Saved
    {
        public Dictionary<string, int> Ports { get; set; } = [];
        public List<RoutedApp> Apps { get; set; } = [];
    }

    public ClientSettings(string file)
    {
        _file = file;
        try { _saved = File.Exists(file) ? JsonSerializer.Deserialize<Saved>(File.ReadAllBytes(file), Json) ?? new() : new(); }
        // A file that does not read loses only ports and shortcuts, never a profile: start clean rather than not start.
        catch (JsonException) { _saved = new(); }
    }

    public int PortOf(string profileId)
    {
        lock (_gate)
        {
            if (_saved.Ports.TryGetValue(profileId, out var port)) return port;
            port = FirstPort;
            while (_saved.Ports.ContainsValue(port) || !IsFree(port)) port++;
            _saved.Ports[profileId] = port;
            Save();
            return port;
        }
    }

    public void SetPort(string profileId, int port)
    {
        if (port is < 1024 or > 65535) throw new ShenoraException("PORT_RANGE", "port", port.ToString(), "a port is 1024 to 65535");
        lock (_gate)
        {
            if (_saved.Ports.Any(p => p.Value == port && p.Key != profileId))
                throw new ShenoraException("PORT_TAKEN", "port", port.ToString(), $"another profile already uses port {port}");
            _saved.Ports[profileId] = port;
            Save();
        }
    }

    public bool IsTakenByAnother(string profileId, int port)
    {
        lock (_gate) return _saved.Ports.Any(p => p.Value == port && p.Key != profileId);
    }

    public void Forget(string profileId)
    {
        lock (_gate)
        {
            // Applications bound to it are kept: they show the profile as gone and can be pointed at another.
            if (_saved.Ports.Remove(profileId)) Save();
        }
    }

    public IReadOnlyList<RoutedApp> Apps
    {
        get { lock (_gate) return [.. _saved.Apps]; }
    }

    public RoutedApp? FindApp(string id)
    {
        lock (_gate) return _saved.Apps.FirstOrDefault(a => a.Id == id);
    }

    public void PutApp(RoutedApp app)
    {
        lock (_gate)
        {
            var at = _saved.Apps.FindIndex(a => a.Id == app.Id);
            if (at < 0) _saved.Apps.Add(app);
            else _saved.Apps[at] = app;
            Save();
        }
    }

    public bool RemoveApp(string id)
    {
        lock (_gate)
        {
            if (_saved.Apps.RemoveAll(a => a.Id == id) == 0) return false;
            Save();
            return true;
        }
    }

    /// <summary>Whether a loopback listener could take <paramref name="port"/> now. Not a reservation: something can
    /// still take it before the tunnel binds, and the connect then says so.</summary>
    public static bool IsFree(int port)
    {
        try
        {
            var probe = new TcpListener(IPAddress.Loopback, port);
            probe.Start();
            probe.Stop();
            return true;
        }
        catch (SocketException) { return false; }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var temporary = _file + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(_saved, Json));
        File.Move(temporary, _file, overwrite: true);
    }
}
