using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sallyport.Runtime;

namespace Sallyport.Client;

// The window's view model, one record per type in web/src/contract.ts, and the words in it: the page renders this and
// decides nothing (docs/design/client-ui.md §4). Every state-dependent sentence the window shows is written here.

internal enum Tone { Off, Busy, Up, Trouble, Info }

internal enum ProfilePhase { Idle, Connecting, Up, Reconnecting, Disconnecting, Stopped, Refused, Unsupported, Removing }

internal enum LocationPhase { Unchecked, Checking, Checked, Unavailable, Disconnected }

internal enum AppPhase { Ready, Blocked, Connecting, Starting }

internal enum NoticeKind { Info, Warning, Error }

internal sealed record UiAction(bool Visible, bool Enabled, bool Busy, string Label, string? Reason)
{
    public static readonly UiAction Hidden = new(false, false, false, "", null);

    /// <summary>A shown button. A busy one is not pressable; a disabled one says why.</summary>
    public static UiAction Shown(string label, bool enabled = true, string? reason = null, bool busy = false) =>
        new(true, enabled && !busy, busy, label, enabled ? null : reason);
}

internal sealed record Fact(string Label, string Value);

internal sealed record EmptyView(string Title, string Text, string Command);

internal sealed record LocationView(LocationPhase Phase, string Text, IReadOnlyList<Fact> Facts, DateTimeOffset? CheckedAt, bool PreviousResult);

internal sealed record ProfileActions(UiAction Connect, UiAction Disconnect, UiAction Copy, UiAction EditPort, UiAction Locate, UiAction Remove,
    UiAction Settings);

internal sealed record ProfileView(
    string Id, string Name, ProfilePhase Phase, Tone Tone, string StatusText, string? DetailText, DateTimeOffset? ConnectedSince,
    string EndpointText, int Port, IReadOnlyList<Fact> Facts, LocationView Location, ProfileActions Actions);

internal sealed record PortValues(string PortText);

internal sealed record EditorView(
    string Token, string SubjectId, string Phase, IReadOnlyDictionary<string, string> Errors, string? Hint, UiAction Save,
    UiAction Cancel, UiAction Reset, string Kind, object Values, IReadOnlyList<EditorOption>? Options = null);

internal sealed record ConfirmationView(string Token, string Title, string Text, UiAction Confirm, UiAction Cancel);

internal sealed record NoticeSubject(string Kind, string Id);

/// <summary>An outcome the person should hear about. The host writes it; the page only shows it.</summary>
internal sealed record Notice(string Id, NoticeKind Kind, string Text, NoticeSubject? Subject, DateTimeOffset CreatedAt, UiAction Dismiss);

internal sealed record ClientState(
    long Sequence,
    ClientState.SummaryView Summary,
    ClientState.TopActions Actions,
    IReadOnlyList<ProfileView> Profiles,
    IReadOnlyList<AppItemView> Apps,
    EmptyView? ProfilesEmpty,
    EmptyView? AppsEmpty,
    NetworkView Network,
    SettingsView Settings,
    Page InitialPage,
    NavigationView? Navigation,
    IReadOnlyList<ClientState.ProfileChoice> ProfileChoices,
    IReadOnlyList<ClientState.KindChoice> KindChoices,
    IReadOnlyList<LossChoice> LossChoices,
    EditorView? Editor,
    ConfirmationView? Confirmation,
    IReadOnlyList<Notice> Notices)
{
    internal sealed record SummaryView(string Text, Tone Tone);
    internal sealed record TopActions(UiAction Import, UiAction AddProgram, UiAction AddFolder);
    internal sealed record ProfileChoice(string Id, string Label, bool Enabled);
    internal sealed record KindChoice(AppKind Value, string Label, string Help);
    internal sealed record GuideView(string EndpointText, string CurlExample, UiAction CopyExample);
}

/// <summary>What one profile is, as the host knows it, for <see cref="ClientView.Profile"/>. <see cref="UsedBy"/>: the
/// game being routed through it, which a proxy may not share.</summary>
internal sealed record ProfileFacts(
    StoredProfile Stored, int Port, TunnelServiceStatus? Status, Location? Location, bool Locating, bool LocationFailed,
    int? AttemptsSinceUp, bool Disconnecting, bool Removing, string? UsedBy = null, bool PolicyChanged = false);

internal static class ClientView
{
    /// <summary>The contract's JSON: camelCase, enums by camelCase name, and a null written as null — the page tells a
    /// missing reason from an empty one by it, so nothing is left out.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    public static string Endpoint(int port) => $"127.0.0.1:{port}";

    public static ProfileView Profile(ProfileFacts f)
    {
        var status = f.Status;
        var running = status is { State: TunnelServiceStatus.Connecting or TunnelServiceStatus.Up or TunnelServiceStatus.Reconnecting };
        var up = status?.State == TunnelServiceStatus.Up;

        var (phase, tone, text, detail) = f switch
        {
            { Removing: true } => (ProfilePhase.Removing, Tone.Busy, "Removing profile…", (string?)null),
            { Disconnecting: true } => (ProfilePhase.Disconnecting, Tone.Busy, "Disconnecting…", null),
            { Stored.Unsupported: { } why } => (ProfilePhase.Unsupported, Tone.Off, "Unsupported profile", $"This version cannot connect this profile: {why}."),
            _ => status?.State switch
            {
                TunnelServiceStatus.Connecting when status.Attempts > 1 =>
                    (ProfilePhase.Connecting, Tone.Busy, "Retrying connection…", Join($"{Plural(status.Attempts, "attempt")} so far.", LastAttempt(status))),
                TunnelServiceStatus.Connecting => (ProfilePhase.Connecting, Tone.Busy, "Connecting…", null),
                TunnelServiceStatus.Up => (ProfilePhase.Up, Tone.Up, "Connected", null),
                TunnelServiceStatus.Reconnecting => (ProfilePhase.Reconnecting, Tone.Busy, "Reconnecting…",
                    f.AttemptsSinceUp is int n and > 0
                        ? Join($"{Plural(n, "attempt")} since the connection was lost.", LastAttempt(status))
                        : "The connection was lost."),
                TunnelServiceStatus.Refused => (ProfilePhase.Refused, Tone.Trouble, "Could not connect", Sentence(status.LastError)),
                TunnelServiceStatus.Stopped => (ProfilePhase.Stopped, Tone.Trouble, "Connection stopped", Sentence(status.LastError)),
                _ => (ProfilePhase.Idle, Tone.Off, "Not connected", null),
            },
        };

        var busy = f.Removing || f.Disconnecting;
        var facts = new List<Fact>
        {
            new("Transport", string.Join(", ", f.Stored.Remotes)),
            new("Imported", f.Stored.Imported.ToLocalTime().ToString("d MMM yyyy", English)),
            new("SOCKS5 port", f.Port.ToString(English)),
        };
        if (up && status is not null)
        {
            if (status.Remote is { } remote) facts.Add(new("Connected over", remote));
            if (status.Address is { } address) facts.Add(new("Tunnel address", address));
            facts.Add(new("Open connections", status.OpenConnections.ToString("N0", English)));
            facts.Add(new("Downloaded", Bytes(status.BytesDown)));
            facts.Add(new("Uploaded", Bytes(status.BytesUp)));
        }

        if (f.UsedBy is { } game) facts.Add(new("Routing through it", game));
        if (f.PolicyChanged) facts.Add(new("Connection settings", "Changed by you"));

        var location = Located(f, up);
        var actions = new ProfileActions(
            Connect: running || busy ? UiAction.Hidden
                : UiAction.Shown(phase is ProfilePhase.Refused or ProfilePhase.Stopped ? "Try again" : "Connect", f.Stored.Unsupported is null,
                    "This version cannot connect this profile."),
            Disconnect: running || f.Disconnecting
                ? UiAction.Shown(status?.State == TunnelServiceStatus.Connecting ? "Cancel" : "Disconnect", busy: f.Disconnecting)
                : UiAction.Hidden,
            Copy: UiAction.Shown("Copy address", !busy),
            EditPort: UiAction.Shown("Change port", !running && !busy, running ? "Disconnect before changing the port." : null),
            Locate: up
                ? UiAction.Shown(location.Phase switch { LocationPhase.Checked => "Check again", LocationPhase.Unavailable => "Try again", _ => "Check location" },
                    busy: f.Locating)
                : UiAction.Hidden,
            Remove: UiAction.Shown("Remove profile", !running && !busy && f.UsedBy is null,
                f.UsedBy is not null ? "Stop routing what goes through it first." : running ? "Disconnect before removing it." : null),
            Settings: UiAction.Shown("Connection settings", !busy && f.Stored.Unsupported is null, "This version cannot connect this profile."));

        return new ProfileView(f.Stored.Id, f.Stored.Name, phase, tone, text, detail, up ? status!.Since : null, Endpoint(f.Port), f.Port,
            facts, location, actions);
    }

    /// <summary>Failure first, then work under way, then connected: one healthy tunnel must not hide another's
    /// failure. The tray's icon and tooltip use the same.</summary>
    public static ClientState.SummaryView Summary(IReadOnlyList<ProfileView> profiles)
    {
        int Count(params ProfilePhase[] phases) => profiles.Count(p => phases.Contains(p.Phase));
        var failed = Count(ProfilePhase.Refused, ProfilePhase.Stopped);
        var reconnecting = Count(ProfilePhase.Reconnecting);
        var connecting = Count(ProfilePhase.Connecting, ProfilePhase.Disconnecting);
        var connected = Count(ProfilePhase.Up);

        var parts = new List<string>();
        if (connected > 0) parts.Add($"{connected} connected");
        if (connecting > 0) parts.Add($"{connecting} connecting");
        if (reconnecting > 0) parts.Add($"{reconnecting} reconnecting");
        if (failed > 0) parts.Add($"{failed} could not connect");
        var tone = failed > 0 ? Tone.Trouble : connecting + reconnecting > 0 ? Tone.Busy : connected > 0 ? Tone.Up : Tone.Off;
        return new(parts.Count == 0 ? "Not connected" : string.Join(" · ", parts), tone);
    }

    public static EmptyView? ProfilesEmpty(int profiles) => profiles > 0 ? null : new(
        "No profiles yet",
        "Import an OpenVPN profile from your VPN provider. Sallyport keeps an encrypted copy for your Windows account.",
        "IMPORT");

    public static string KindLabel(AppKind kind) => kind switch
    {
        AppKind.Chromium => "Chromium browser",
        AppKind.Firefox => "Firefox browser",
        AppKind.Electron => "Electron app",
        _ => "Proxy environment",
    };

    public static string KindHelp(AppKind kind) => kind switch
    {
        AppKind.Chromium => "Uses a separate browser profile. Your usual browser stays unchanged.",
        AppKind.Firefox => "Uses a separate browser profile with SOCKS5 and remote DNS.",
        AppKind.Electron => "Quit the app completely, including its tray icon, before launching here. An existing instance may ignore these settings.",
        _ => "Sets proxy environment variables. Only programs that honour them use the tunnel.",
    };

    public static string CurlExample(string endpoint) => $"curl --socks5-hostname {endpoint} https://ipinfo.io/json";

    public static string Bytes(long n)
    {
        if (n < 1024) return $"{n} B";
        string[] units = ["KB", "MB", "GB", "TB"];
        double value = n / 1024.0;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return (value < 10 ? value.ToString("0.0", English) : Math.Round(value).ToString(English)) + " " + units[unit];
    }

    private static LocationView Located(ProfileFacts f, bool up)
    {
        if (!up) return new(LocationPhase.Disconnected, "Connect to check location.", [], null, false);
        var found = f.Location;
        var facts = found is null ? [] : new List<Fact>
        {
            new("IP", found.Ip ?? "not provided"),
        };
        if (found?.Org is { } org) facts.Add(new("Organisation", org));

        if (f.Locating)
            return new(LocationPhase.Checking, "Checking location…", facts, found?.Checked, found is not null);
        if (f.LocationFailed)
            return new(LocationPhase.Unavailable, "Location could not be checked. The lookup did not answer; the tunnel may still work.", [], null, false);
        if (found is null)
            return new(LocationPhase.Unchecked, "Checks your exit IP using ipinfo.io through this tunnel.", [], null, false);
        var place = string.Join(", ", new[] { found.City, found.Region, found.Country }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return new(LocationPhase.Checked, place.Length > 0 ? $"Exit location: {place}" : "Location not provided.", facts, found.Checked, false);
    }

    private static string? LastAttempt(TunnelServiceStatus status) => status.LastError is { } why ? $"Last attempt: {why}" : null;

    private static string? Sentence(string? reason) => reason is null ? null : char.ToUpperInvariant(reason[0]) + reason[1..];

    private static string Join(string first, string? second) => second is null ? first : $"{first} {second}";

    private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n:N0} {noun}s";
}
