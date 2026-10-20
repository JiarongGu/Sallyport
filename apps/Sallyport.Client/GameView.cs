using System.Globalization;
using Sallyport.Profile;
using Sallyport.Runtime;
using Sallyport.Windows.Routing;
using Sallyport.Windows.Service;

namespace Sallyport.Client;

// A route's view (its run, whether all of it went through the VPN), the packet driver's, the loss choices and the
// connection settings' words, as ClientView.cs is the rest's: the page renders them and decides nothing (D15).

internal enum RunPhase { Preparing, Connecting, Routing, Held, Direct, Ended }

/// <summary>Three outcomes, never two (rule 6).</summary>
internal enum CheckResult { Holds, Failed, Unavailable }

internal enum DriverPhase { Ready, NotInstalled, ServiceMissing, Working, Unknown }

internal sealed record CheckView(CheckResult Result, string Text);

internal sealed record RunView(RunPhase Phase, Tone Tone, string StatusText, string? DetailText, DateTimeOffset? Since, IReadOnlyList<Fact> Facts,
    CheckView Coverage);

internal sealed record DriverView(DriverPhase Phase, Tone Tone, string StatusText, string? DetailText, IReadOnlyList<Fact> Facts,
    UiAction Install, UiAction Remove);

/// <summary>The connection settings as the editor holds them. <see cref="Protocol"/>: "" for every server as listed, or
/// "udp"/"tcp"; <see cref="RoundsMaxText"/> counts only when <see cref="KeepTrying"/> is off.</summary>
internal sealed record PolicyValues(
    string RetryWaitText, string RetryWaitMaxText, string RoundsMaxText, bool RandomOrder, string AttemptTimeoutText,
    bool RetryAfterAuthFailure, string PingRestartText, string Protocol = "", bool KeepTrying = true);

internal sealed record LossChoice(LossMode Value, string Label, string Help);

/// <summary>What the driver and the service are, as the client can see them unelevated.</summary>
internal sealed record DriverFacts(bool ServiceInstalled, bool ServiceRunning, bool DriverInstalled, bool DriverRunning, string? Version, bool Working, string? WorkingText);

internal static class GameViews
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    public static IReadOnlyList<LossChoice> LossChoices =>
    [
        new(LossMode.Hold, L.T("Block its internet until the VPN is back"), L.T("Nothing of the app goes out without the VPN. Your game may lag or disconnect while it reconnects.")),
        new(LossMode.Direct, L.T("Use my own connection meanwhile"), L.T("The app keeps going over your own connection while the VPN reconnects, then goes back through the VPN.")),
        new(LossMode.Stop, L.T("Turn the VPN off for it"), L.T("Its Use VPN switch turns off, and the app carries on over your own connection until you turn it on again.")),
    ];

    /// <summary>What a loss policy does, as the end of a sentence: "your own connection until it is back".</summary>
    public static string LossShort(GameChoice choice) => choice.Loss switch
    {
        LossMode.Direct => L.T("your own connection until it is back"),
        LossMode.Stop => L.T("its VPN turns off"),
        _ when choice.DirectAfterSeconds is > 0 and var s => L.T("blocked, then your own connection after {0} s", s),
        _ => L.T("its internet is blocked until it is back"),
    };

    public static string LossText(GameChoice choice) => L.T("If the VPN drops: {0}", LossShort(choice));

    /// <summary>One app's part of the route: its own words for a loss (its policy, not the route's), its own programs'
    /// counts, and the route's own figures only when it carries this app alone.</summary>
    public static RunView Run(RoutedItem app, StoredProfile? profile, RunFacts run, LaneFacts lane)
    {
        var name = profile?.Name ?? L.T("its profile");
        var route = run.Route;
        var tunnel = lane.Tunnel;
        var held = lane.PhaseSince is { } lostAt && app.Loss == LossMode.Hold
            && (app.DirectAfterSeconds is not { } after || lostAt + TimeSpan.FromSeconds(after) > DateTimeOffset.Now);
        var (phase, tone, text, detail) = run switch
        {
            { Ended: GameRunner.StoppedByPerson } => (RunPhase.Ended, Tone.Off, L.T("VPN off"), (string?)null),
            { Ended: { } why } => (RunPhase.Ended, Tone.Trouble, L.T("VPN turned off"), why),
            { Preparing: true } => (RunPhase.Preparing, Tone.Busy, L.T("Preparing…"), L.T("Asking the Sallyport service to send the app's programs through the VPN.")),
            _ => (route is null ? (RoutePhase?)null : lane.Phase) switch
            {
                RoutePhase.Routing => (RunPhase.Routing, Tone.Up, L.T("VPN on · {0}", name), L.T("Every connection of the app's programs goes through the VPN.")),
                RoutePhase.Held when held => (RunPhase.Held, Tone.Busy, L.T("Reconnecting — its internet is blocked meanwhile"),
                    Join(Attempts(tunnel), DirectAfter(app, lane))),
                RoutePhase.Held or RoutePhase.Direct => (RunPhase.Direct, Tone.Busy, L.T("Reconnecting — on your own connection meanwhile"),
                    Join(Attempts(tunnel), L.T("It goes back through the VPN once it is up."))),
                _ => (RunPhase.Connecting, Tone.Busy, L.T("Connecting {0}…", name), L.T("Its internet is blocked until the VPN is up.")),
            },
        };

        var facts = new List<Fact>();
        if (route is { } s)
        {
            var mine = (s.Programs ?? []).Where(p => app.PacketPrograms.Any(a => string.Equals(a.Path, p.Path, StringComparison.OrdinalIgnoreCase))).ToList();
            // The route's own figures are every lane's and app's: said as this app's only when it is the route's one app.
            var alone = run.Lanes.Sum(l => l.Apps.Count) == 1;
            facts.Add(new(L.T("Its processes"), (alone ? s.Selected : mine.Sum(p => p.Processes)).ToString("N0", English)));
            facts.Add(new(L.T("Connections through the VPN"), (alone ? s.RoutedFlows : mine.Sum(p => p.Flows)).ToString("N0", English)));
            if (alone)
            {
                facts.Add(new(L.T("Packets out / in"), $"{s.Routed:N0} / {s.HandedUp:N0}"));
                if (s.HeldWhileDown > 0) facts.Add(new(L.T("Blocked while reconnecting"), s.HeldWhileDown.ToString("N0", English)));
                if (s.DirectWhileDown > 0) facts.Add(new(L.T("Sent directly while reconnecting"), s.DirectWhileDown.ToString("N0", English)));
                if (s.Blocked > 0) facts.Add(new(L.T("Blocked (IPv6, local network)"), s.Blocked.ToString("N0", English)));
            }
            else facts.Add(new(L.T("Packets sent through the VPN"), mine.Sum(p => p.Packets).ToString("N0", English)));
        }
        if (tunnel is { State: SessionState.Up })
        {
            if (tunnel.Remote is { } remote) facts.Add(new(L.T("Connected over"), remote));
            if (tunnel.Address is { } address) facts.Add(new(L.T("Tunnel address"), address));
            if (tunnel.Reconnects > 0) facts.Add(new(L.T("Reconnects"), tunnel.Reconnects.ToString("N0", English)));
        }
        // Since it joined, or since the route came back, whichever is later.
        var since = phase != RunPhase.Routing ? null : lane.Apps.GetValueOrDefault(app.Id) is var joined && joined > lane.PhaseSince ? joined : lane.PhaseSince;
        return new RunView(phase, tone, text, detail, since, facts, Coverage(route, run.Lanes.Sum(l => l.Apps.Count)));
    }

    /// <summary>Whether everything of the app went through the VPN: holds, failed (with how many), or could not be
    /// checked (and why) — never collapsed (rule 6).</summary>
    public static CheckView Coverage(RouteState? route, int apps = 1) => route switch
    {
        null => new(CheckResult.Unavailable, L.T("Not checked yet.")),
        // The router counts what left late for the route, not by app: with several apps it cannot say whose it was.
        { LateFlows: > 0 and var late } when apps > 1 => new(CheckResult.Failed,
            L.Count((int)late, "1 connection of the apps through this VPN left before it was routed.", "{0} connections of the apps through this VPN left before they were routed.")),
        { LateFlows: > 0 and var late } => new(CheckResult.Failed,
            L.Count((int)late, "1 connection of the app left before it was routed.", "{0} connections of the app left before it was routed.")),
        { Selected: 0 } => new(CheckResult.Unavailable, L.T("No program of the app has run yet, so nothing could be checked.")),
        { Unreadable: > 0 and var n } => new(CheckResult.Unavailable,
            L.Count(n, "Every connection seen from the app went through the VPN, but 1 process could not be read, so whether it was the app could not be checked.",
                "Every connection seen from the app went through the VPN, but {0} processes could not be read, so whether they were the app could not be checked.")),
        _ => new(CheckResult.Holds, L.T("Every connection of the app's programs went through the VPN.")),
    };

    public static DriverView Driver(DriverFacts f)
    {
        var (phase, tone, text, detail) = f switch
        {
            { Working: true } => (DriverPhase.Working, Tone.Busy, f.WorkingText ?? L.T("Working…"), L.T("Windows may ask for permission. The network pauses for a few seconds.")),
            { ServiceInstalled: false } => (DriverPhase.ServiceMissing, Tone.Trouble, L.T("The Sallyport service is not installed"),
                L.T("Packet routing needs it. Install Sallyport with its setup to put it in.")),
            { DriverInstalled: false } => (DriverPhase.NotInstalled, Tone.Off, L.T("The packet driver is not installed"),
                L.T("Packet routing takes each app's packets through NT Kernel's free Windows Packet Filter driver. Sallyport fetches NT Kernel's own release and installs it.")),
            { ServiceRunning: false } => (DriverPhase.Unknown, Tone.Trouble, L.T("The Sallyport service is not running"),
                L.T("Windows starts it at boot; restart the computer if it stays stopped.")),
            _ => (DriverPhase.Ready, Tone.Up, L.T("Packet routing is ready"), (string?)null),
        };
        var facts = new List<Fact>
        {
            new(L.T("Packet driver"), f.DriverInstalled ? f.DriverRunning ? L.T("Installed, running") : L.T("Installed, not running") : L.T("Not installed")),
            new(L.T("Sallyport service"), f.ServiceInstalled ? f.ServiceRunning ? L.T("Running") : L.T("Stopped") : L.T("Not installed")),
        };
        if (f.Version is { } version) facts.Add(new(L.T("Driver version"), version));
        return new DriverView(phase, tone, text, detail, facts,
            Install: f.DriverInstalled ? UiAction.Hidden : UiAction.Shown(L.T("Install driver…"), !f.Working, busy: f.Working),
            Remove: f.DriverInstalled ? UiAction.Shown(L.T("Remove driver…"), !f.Working, busy: f.Working) : UiAction.Hidden);
    }

    // The confirmation said before the driver is installed or removed: the network pauses (D18, measured).
    public static string DriverInstallText =>
        L.T("Windows will ask for permission. While the driver is put in, this computer's network pauses for a few seconds (about 2 s measured; other computers may differ).");

    public static string DriverRemoveText =>
        L.T("Windows will ask for permission. While the driver is taken out, this computer's network pauses for a few seconds (about 6 s measured; other computers may differ). Apps can no longer be routed by their packets until it is installed again.");

    // Five rounds is what the field offers when the person turns "keep trying" off: a number to change, not an empty box.
    public static PolicyValues PolicyValuesOf(ConnectionPolicy p) => new(
        Seconds(p.RetryWait), Seconds(p.RetryWaitMax), p.RoundsMax?.ToString(English) ?? "5", p.RandomOrder, Seconds(p.AttemptTimeout),
        p.RetryAfterAuthFailure, p.PingRestart?.ToString(English) ?? "", p.Protocol ?? "", p.RoundsMax is null);

    // How the tunnel reaches the server, never what it carries: an app's TCP and UDP go through it on either (the owner:
    // "its mostly for all traffic").
    private static EditorOption Udp => new("udp", L.T("UDP"), L.T("Usually faster, and what games prefer. Some networks block it."));
    private static EditorOption Tcp => new("tcp", L.T("TCP"), L.T("Gets through more networks. Slower when packets are lost."));

    /// <summary>Settings' "VPN protocol", OpenVPN Connect's "Protocol": each profile's servers as it lists them, or only
    /// those of one transport.</summary>
    public static IReadOnlyList<EditorOption> ProtocolChoices() =>
        [new("", L.T("Automatic"), L.T("Each profile's servers in the order it lists them.")), Udp, Tcp];

    /// <summary>One profile's: only between transports it has servers for; nothing to choose when it has one.</summary>
    public static IReadOnlyList<EditorOption>? ProtocolChoices(IReadOnlyList<string> remotes)
    {
        var protocols = ClientSettings.ProtocolsOf(remotes);
        if (!protocols.Contains("udp") || !protocols.Contains("tcp")) return null;
        return [new("", L.T("Automatic"), L.T("Its servers in the order it lists them.")), Udp, Tcp];
    }

    /// <summary>Settings' "If it cannot connect": rounds before a connection stops trying, 0 never. A count set
    /// otherwise (a profile's own view takes any) is offered too, so the choice shows what is in force.</summary>
    public static IReadOnlyList<EditorOption> RetryChoices(int setting) =>
    [
        new("0", L.T("Keep trying")),
        .. new[] { 3, 5, 10 }.Union(setting > 0 ? [setting] : []).Order()
            .Select(n => new EditorOption(n.ToString(English), L.Count(n, "Stop after 1 failed round", "Stop after {0} failed rounds"))),
    ];

    public static string? ProtocolText(string? protocol) => protocol switch
    {
        "udp" => L.T("UDP"),
        "tcp" => L.T("TCP"),
        _ => null,
    };

    /// <summary>What applies without changes of the profile's own — its directives with Settings' choices over them —
    /// under its connection settings, so a change is made knowing what it changes.</summary>
    public static string PolicyHint(ConnectionPolicy p, bool overridden) =>
        L.T("Without changes here, from Settings and the profile itself: {0}, wait {1} s growing to {2} s, {3}, {4}, {5} s to answer{6}",
            ProtocolText(p.Protocol) ?? L.T("either protocol"), Seconds(p.RetryWait), Seconds(p.RetryWaitMax),
            p.RoundsMax is { } rounds ? L.Count(rounds, "give up after 1 round", "give up after {0} rounds") : L.T("never give up"),
            p.RandomOrder ? L.T("servers in random order") : L.T("servers in the order listed"), Seconds(p.AttemptTimeout),
            p.PingRestart is { } r ? L.T(", dead after {0} s of silence when the server sets none.", r) : L.T("."))
        + (overridden ? L.Join("", L.T("Your changes are shown above.")) : "");

    private static string Seconds(TimeSpan t) => ((int)Math.Round(t.TotalSeconds)).ToString(English);

    // The reason itself is the protocol library's (English, or Windows' own words for a socket error).
    private static string? Attempts(SessionStatus? tunnel) => tunnel is null ? null
        : tunnel.LastError is { } why ? L.T("Last attempt: {0}", why) : null;

    private static string? DirectAfter(RoutedItem app, LaneFacts lane) =>
        app.DirectAfterSeconds is { } after && lane.PhaseSince is { } since
            ? L.T("Your own connection in {0} s if it is not back.", Math.Max(0, (int)Math.Ceiling((since + TimeSpan.FromSeconds(after) - DateTimeOffset.Now).TotalSeconds)))
            : null;

    private static string? Join(string? first, string? second) => first is null ? second : L.Join(first, second);
}
