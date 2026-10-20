using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sallyport.Profile;
using Sallyport.Windows.Routing;
using Shenora.Core.Ipc;

namespace Sallyport.Client;

/// <summary>How an application is told about the proxy: each family reads it from a different place.</summary>
internal enum AppKind { Chromium, Electron, Firefox, Environment }

/// <summary>An application the client starts through a profile. A path and flags, never anything secret.</summary>
internal sealed record RoutedApp(string Id, string Name, string Path, AppKind Kind, string ProfileId, string Arguments);

/// <summary>What a game is run with: its VPN profile, and what its traffic does while the tunnel is lost (the loss policy,
/// per game: docs/design/product.md §3).</summary>
internal sealed record GameChoice(string? ProfileId, LossMode Loss, int? DirectAfterSeconds)
{
    public LossPolicy Policy => new(Loss, DirectAfterSeconds is > 0 and var s ? TimeSpan.FromSeconds(s) : null);
}

/// <summary>What a new app starts with: its VPN profile (null: the first that can connect) and loss policy. Apps made
/// before keep their own.</summary>
internal sealed record AppDefaults(string? ProfileId = null, LossMode Loss = LossMode.Hold, int? DirectAfterSeconds = null);

/// <summary>A profile's connection settings the person changed, each null where the profile's own stands (OpenVPN's
/// directives, read from it: <see cref="ConnectionPolicy"/>). <see cref="RoundsMax"/> 0 is "never give up";
/// <see cref="Protocol"/> "udp" or "tcp" holds it to those servers, and <see cref="Any"/> lets a profile use all of its
/// own where Settings holds the others to one.</summary>
/// <summary>A profile's choices as <see cref="ClientSettings.ChoicesFor"/> finds them: <see cref="Protocol"/> the one it
/// is held to, null for its servers in order; <see cref="RoundsMax"/> 0 to keep trying.</summary>
internal sealed record ProfileChoices(IReadOnlyCollection<string> Protocols, string? Protocol, bool ProtocolOwn, int RoundsMax, bool RoundsOwn, bool OtherOwn);

internal sealed record PolicyOverride(
    int? RetryWaitSeconds = null, int? RetryWaitMaxSeconds = null, int? RoundsMax = null, bool? RandomOrder = null,
    int? AttemptTimeoutSeconds = null, bool? RetryAfterAuthFailure = null, int? PingRestartSeconds = null, string? Protocol = null)
{
    public const string Any = "any";

    public bool IsEmpty => this == new PolicyOverride();

    /// <summary>The profile's policy with these over it. A ping-restart of the person's own outranks the server's, as
    /// OpenVPN's <c>pull-filter ignore "ping-restart"</c> makes it: shortening it is the point of setting one.</summary>
    public ConnectionPolicy Over(ConnectionPolicy policy) => policy with
    {
        RetryWait = RetryWaitSeconds is { } wait ? TimeSpan.FromSeconds(wait) : policy.RetryWait,
        RetryWaitMax = RetryWaitMaxSeconds is { } max ? TimeSpan.FromSeconds(max) : policy.RetryWaitMax,
        RoundsMax = RoundsMax is { } rounds ? rounds == 0 ? null : rounds : policy.RoundsMax,
        RandomOrder = RandomOrder ?? policy.RandomOrder,
        Protocol = Protocol switch { null => policy.Protocol, Any => null, var only => only },
        AttemptTimeout = AttemptTimeoutSeconds is { } timeout ? TimeSpan.FromSeconds(timeout) : policy.AttemptTimeout,
        RetryAfterAuthFailure = RetryAfterAuthFailure ?? policy.RetryAfterAuthFailure,
        PingRestart = PingRestartSeconds ?? policy.PingRestart,
        Ping = PingRestartSeconds is { } restart ? Math.Max(1, restart / 4) : policy.Ping,
        KeepaliveOverridesServer = PingRestartSeconds is not null || policy.KeepaliveOverridesServer,
    };
}

/// <summary>
/// What the client remembers besides the profiles and the apps: each profile's connection settings, the language. One
/// JSON file, rewritten whole. The apps and games an earlier client kept here are read once, into
/// <see cref="RoutedItemStore"/>, and left as they were.
/// </summary>
internal sealed class ClientSettings
{

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
        public List<RoutedApp> Apps { get; set; } = [];
        public Dictionary<string, GameChoice> Games { get; set; } = [];
        public Dictionary<string, PolicyOverride> Policies { get; set; } = [];
        /// <summary>Settings' connection choices, for every profile; a profile's own changes go over them.</summary>
        public PolicyOverride Connection { get; set; } = new();
        public AppDefaults Defaults { get; set; } = new();
        /// <summary>"en", "zh-Hans", or null for Windows' own display language.</summary>
        public string? Language { get; set; }
    }

    public ClientSettings(string file)
    {
        _file = file;
        try { _saved = File.Exists(file) ? JsonSerializer.Deserialize<Saved>(File.ReadAllBytes(file), Json) ?? new() : new(); }
        // A file that does not read loses only settings, never a profile: start clean rather than not start.
        catch (JsonException) { _saved = new(); }
    }

    public void Forget(string profileId)
    {
        lock (_gate)
        {
            // Applications and games bound to it are kept: they show the profile as gone and can be pointed at another.
            if (_saved.Policies.Remove(profileId)) Save();
        }
    }

    public string? Language
    {
        get { lock (_gate) return _saved.Language; }
        set
        {
            lock (_gate)
            {
                _saved.Language = value;
                Save();
            }
        }
    }

    public AppDefaults Defaults
    {
        get { lock (_gate) return _saved.Defaults ?? new(); }
        set
        {
            lock (_gate)
            {
                _saved.Defaults = value;
                Save();
            }
        }
    }

    /// <summary>Each game's profile and loss policy as an earlier client kept them, for <see cref="RoutedItemStore"/>'s
    /// migration; never written since.</summary>
    public IReadOnlyDictionary<string, GameChoice> LegacyChoices
    {
        get { lock (_gate) return new Dictionary<string, GameChoice>(_saved.Games); }
    }

    /// <summary>Every profile's own changes, by profile.</summary>
    public IReadOnlyDictionary<string, PolicyOverride> Policies
    {
        get { lock (_gate) return new Dictionary<string, PolicyOverride>(_saved.Policies); }
    }

    public PolicyOverride PolicyOf(string profileId)
    {
        lock (_gate) return _saved.Policies.GetValueOrDefault(profileId) ?? new PolicyOverride();
    }

    /// <summary>How every profile connects unless it is changed for itself (Settings, as OpenVPN Connect's "Protocol" and
    /// "Connection Timeout" are).</summary>
    public PolicyOverride Connection
    {
        get { lock (_gate) return _saved.Connection ?? new(); }
        set
        {
            lock (_gate)
            {
                _saved.Connection = value;
                Save();
            }
        }
    }

    /// <summary>What a profile connects with before changes of its own: its directives with Settings' choices over them.
    /// Settings' protocol only where the profile has servers of it — one without keeps all of its own rather than none.</summary>
    public ConnectionPolicy BaseFor(ConnectionPolicy own, IReadOnlyCollection<string> protocols)
    {
        var all = Connection;
        if (all.Protocol is { } only && !protocols.Contains(only)) all = all with { Protocol = null };
        return all.Over(own);
    }

    public ConnectionPolicy PolicyFor(string profileId, ConnectionPolicy own, IReadOnlyCollection<string> protocols) =>
        PolicyOf(profileId).Over(BaseFor(own, protocols));

    /// <summary>The one transport a profile is held to, or null for all of its servers: known without opening it, since a
    /// profile itself never holds one (<see cref="ConnectionPolicy.Protocol"/>).</summary>
    public string? ProtocolFor(string profileId, IReadOnlyCollection<string> protocols) => PolicyOf(profileId).Protocol switch
    {
        PolicyOverride.Any => null,
        { } only => only,
        null => Connection.Protocol is { } all && protocols.Contains(all) ? all : null,
    };

    /// <summary>What a profile connects with, as its row says it: the VPN protocol and the rounds before it stops, each
    /// Settings' or changed for it, and whether it has other changes of its own (its connection settings view).</summary>
    public ProfileChoices ChoicesFor(string profileId, IReadOnlyCollection<string> protocols)
    {
        var own = PolicyOf(profileId);
        return new(protocols, ProtocolFor(profileId, protocols), own.Protocol is not null, own.RoundsMax ?? Connection.RoundsMax ?? 0,
            own.RoundsMax is not null, !(own with { Protocol = null, RoundsMax = null }).IsEmpty);
    }

    /// <summary>The transports of a stored profile's remotes, which it says as "udp/1194".</summary>
    public static IReadOnlyCollection<string> ProtocolsOf(IEnumerable<string> remotes) => [.. remotes.Select(r => r.Split('/')[0]).Distinct()];

    public void PutPolicy(string profileId, PolicyOverride policy)
    {
        lock (_gate)
        {
            if (policy.IsEmpty) _saved.Policies.Remove(profileId);
            else _saved.Policies[profileId] = policy;
            Save();
        }
    }

    /// <summary>The proxy apps as an earlier client kept them, for <see cref="RoutedItemStore"/>'s migration; never
    /// written since.</summary>
    public IReadOnlyList<RoutedApp> Apps
    {
        get { lock (_gate) return [.. _saved.Apps]; }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var temporary = _file + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(_saved, Json));
        File.Move(temporary, _file, overwrite: true);
    }
}
