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
/// directives, read from it: <see cref="ConnectionPolicy"/>). <see cref="RoundsMax"/> 0 is "never give up".</summary>
internal sealed record PolicyOverride(
    int? RetryWaitSeconds = null, int? RetryWaitMaxSeconds = null, int? RoundsMax = null, bool? RandomOrder = null,
    int? AttemptTimeoutSeconds = null, bool? RetryAfterAuthFailure = null, int? PingRestartSeconds = null)
{
    public bool IsEmpty => this == new PolicyOverride();

    /// <summary>The profile's policy with these over it. A ping-restart of the person's own outranks the server's, as
    /// OpenVPN's <c>pull-filter ignore "ping-restart"</c> makes it: shortening it is the point of setting one.</summary>
    public ConnectionPolicy Over(ConnectionPolicy policy) => policy with
    {
        RetryWait = RetryWaitSeconds is { } wait ? TimeSpan.FromSeconds(wait) : policy.RetryWait,
        RetryWaitMax = RetryWaitMaxSeconds is { } max ? TimeSpan.FromSeconds(max) : policy.RetryWaitMax,
        RoundsMax = RoundsMax is { } rounds ? rounds == 0 ? null : rounds : policy.RoundsMax,
        RandomOrder = RandomOrder ?? policy.RandomOrder,
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

    public PolicyOverride PolicyOf(string profileId)
    {
        lock (_gate) return _saved.Policies.GetValueOrDefault(profileId) ?? new PolicyOverride();
    }

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
