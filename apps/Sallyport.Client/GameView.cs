using System.Globalization;
using Sallyport.Profile;
using Sallyport.Runtime;
using Sallyport.Windows.Routing;
using Sallyport.Windows.Service;

namespace Sallyport.Client;

// The Games page's view model (web/src/contract.ts) and its words, as ClientView.cs is the rest's: the page renders this
// and decides nothing (D15). docs/design/product.md §5.

internal enum RunPhase { Preparing, Connecting, Routing, Held, Direct, Ended }

/// <summary>Three outcomes, never two (rule 6).</summary>
internal enum CheckResult { Holds, Failed, Unavailable }

internal enum DriverPhase { Ready, NotInstalled, ServiceMissing, Working, Unknown }

internal sealed record GameProgramView(string Path, string Name, bool Included, string? Reason, UiAction Toggle);

internal sealed record CheckView(CheckResult Result, string Text);

internal sealed record RunView(RunPhase Phase, Tone Tone, string StatusText, string? DetailText, DateTimeOffset? Since, IReadOnlyList<Fact> Facts,
    CheckView Coverage);

internal sealed record GameActions(UiAction Run, UiAction Stop, UiAction Edit, UiAction Rescan, UiAction Remove);

internal sealed record GameView(
    string Id, string Name, string? Folder, string ThroughText, string LossText, Tone Tone, string StatusText, string ProgramsText,
    IReadOnlyList<GameProgramView> Programs, RunView? Run, GameActions Actions);

internal sealed record DriverView(DriverPhase Phase, Tone Tone, string StatusText, string? DetailText, IReadOnlyList<Fact> Facts,
    UiAction Install, UiAction Remove);

internal sealed record GameValues(string Name, string? ProfileId, LossMode Loss, string DirectAfterText);

internal sealed record PolicyValues(
    string RetryWaitText, string RetryWaitMaxText, string RoundsMaxText, bool RandomOrder, string AttemptTimeoutText,
    bool RetryAfterAuthFailure, string PingRestartText);

internal sealed record LossChoice(LossMode Value, string Label, string Help);

/// <summary>What the driver and the service are, as the client can see them unelevated.</summary>
internal sealed record DriverFacts(bool ServiceInstalled, bool ServiceRunning, bool DriverInstalled, bool DriverRunning, string? Version, bool Working, string? WorkingText);

internal static class GameViews
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    public static IReadOnlyList<LossChoice> LossChoices { get; } =
    [
        new(LossMode.Hold, "Hold the game's traffic", "Nothing of the game leaves outside the VPN while it reconnects. The game may lag or disconnect meanwhile."),
        new(LossMode.Direct, "Use my own connection", "The game keeps playing over your own connection while the VPN reconnects, and goes back through it once it is up."),
        new(LossMode.Stop, "Stop routing", "Routing ends when the connection is lost; the game carries on over your own connection."),
    ];

    public static string LossText(GameChoice choice) => choice.Loss switch
    {
        LossMode.Direct => "If the VPN drops: your own connection until it is back",
        LossMode.Stop => "If the VPN drops: routing stops",
        _ when choice.DirectAfterSeconds is > 0 and var s => $"If the VPN drops: held, then your own connection after {s} s",
        _ => "If the VPN drops: the game's traffic is held",
    };

    public static GameView Game(GameProfile game, GameChoice choice, StoredProfile? profile, RunFacts? run, bool anotherRunning, bool profileProxied,
        bool driverReady, bool busy)
    {
        var mine = run?.GameId == game.Id ? run : null;
        var live = mine is { Ended: null };
        var included = game.Executables.Count(e => e.Included);
        var runView = mine is null ? null : Run(game, profile, mine);
        var (tone, status) = runView is { } r ? (r.Tone, r.StatusText)
            : profile is null ? (Tone.Off, "Choose a VPN profile to run it")
            : included == 0 ? (Tone.Off, "No program of the game is included")
            : (Tone.Off, "Not running");

        string? cannotRun =
            anotherRunning ? "Another game is being routed. Stop it first." :
            profile is null ? "Choose a VPN profile for this game first (Edit)." :
            profile.Unsupported is not null ? "This version cannot connect its VPN profile." :
            profileProxied ? $"{profile.Name} is connected as a proxy. Disconnect it first: one profile serves a game or a proxy at a time." :
            included == 0 ? "Include at least one of its programs." :
            !driverReady ? "Game routing is not ready on this computer (see above)." :
            null;
        var actions = new GameActions(
            Run: live ? UiAction.Hidden : UiAction.Shown(mine is { Ended: not null } ? "Run again" : "Run", cannotRun is null && !busy, cannotRun),
            Stop: live ? UiAction.Shown("Stop routing") : UiAction.Hidden,
            Edit: UiAction.Shown("Edit", !live, "Stop routing before changing it."),
            Rescan: UiAction.Shown("Scan folder again", !live && game.Folder is not null, live ? "Stop routing before scanning again." : "This game has no folder."),
            Remove: UiAction.Shown("Remove game", !live, "Stop routing before removing it."));

        var programs = game.Executables.Select(e => new GameProgramView(e.Path, Path.GetFileName(e.Path), e.Included, e.Included ? null : e.Reason ?? "left out",
                UiAction.Shown(e.Included ? "Leave out" : "Include", !live, "Stop routing before changing its programs.")))
            .ToList();
        var programsText = $"{Plural(included, "program")} routed of {game.Executables.Count}" + (live ? "; changes wait until routing stops." : ".");
        return new GameView(game.Id, game.Name, game.Folder, profile is null ? "No VPN profile chosen" : $"Through {profile.Name}", LossText(choice),
            tone, status, programsText, programs, runView, actions);
    }

    public static RunView Run(GameProfile game, StoredProfile? profile, RunFacts run)
    {
        var name = profile?.Name ?? "its profile";
        var route = run.Route;
        var tunnel = run.Tunnel;
        var (phase, tone, text, detail) = run switch
        {
            { Ended: GameRunner.StoppedByPerson } => (RunPhase.Ended, Tone.Off, "Stopped", (string?)null),
            { Ended: { } why } => (RunPhase.Ended, Tone.Trouble, "Routing stopped", why),
            { Preparing: true } => (RunPhase.Preparing, Tone.Busy, "Preparing…", "Asking the Sallyport service to route the game's programs."),
            _ => route?.Phase switch
            {
                RoutePhase.Routing => (RunPhase.Routing, Tone.Up, $"Routed through {name}", "Every connection of the game's programs goes through the VPN."),
                RoutePhase.Held => (RunPhase.Held, Tone.Busy, "Reconnecting — the game's traffic is held",
                    Join(Attempts(tunnel), DirectAfter(run))),
                RoutePhase.Direct => (RunPhase.Direct, Tone.Busy, "Reconnecting — the game uses your own connection",
                    Join(Attempts(tunnel), "It goes back through the VPN once it is up.")),
                _ => (RunPhase.Connecting, Tone.Busy, $"Connecting {name}…", "The game's traffic is held until the VPN is up."),
            },
        };

        var facts = new List<Fact>();
        if (route is { } s)
        {
            facts.Add(new("Game processes", s.Selected.ToString("N0", English)));
            facts.Add(new("Connections routed", s.RoutedFlows.ToString("N0", English)));
            facts.Add(new("Packets out / in", $"{s.Routed:N0} / {s.HandedUp:N0}"));
            if (s.HeldWhileDown > 0) facts.Add(new("Held while reconnecting", s.HeldWhileDown.ToString("N0", English)));
            if (s.DirectWhileDown > 0) facts.Add(new("Sent directly while reconnecting", s.DirectWhileDown.ToString("N0", English)));
            if (s.Blocked > 0) facts.Add(new("Blocked (IPv6, local network)", s.Blocked.ToString("N0", English)));
        }
        if (tunnel is { State: SessionState.Up })
        {
            if (tunnel.Remote is { } remote) facts.Add(new("Connected over", remote));
            if (tunnel.Address is { } address) facts.Add(new("Tunnel address", address));
            if (tunnel.Reconnects > 0) facts.Add(new("Reconnects", tunnel.Reconnects.ToString("N0", English)));
        }
        return new RunView(phase, tone, text, detail, phase == RunPhase.Routing ? run.PhaseSince : null, facts, Coverage(route));
    }

    /// <summary>Whether everything of the game went through the VPN: holds, failed (with how many), or could not be
    /// checked (and why) — never collapsed (rule 6).</summary>
    public static CheckView Coverage(RouteState? route) => route switch
    {
        null => new(CheckResult.Unavailable, "Not checked yet."),
        { LateFlows: > 0 and var late } => new(CheckResult.Failed, $"{Plural((int)late, "connection")} of the game left before it was routed."),
        { Selected: 0 } => new(CheckResult.Unavailable, "No program of the game has run yet, so nothing could be checked."),
        { Unreadable: > 0 and var n } => new(CheckResult.Unavailable,
            $"Every connection seen from the game went through the VPN, but {Plural(n, "process")} could not be read, so whether they were the game could not be checked."),
        _ => new(CheckResult.Holds, "Every connection of the game's programs went through the VPN."),
    };

    public static DriverView Driver(DriverFacts f)
    {
        var (phase, tone, text, detail) = f switch
        {
            { Working: true } => (DriverPhase.Working, Tone.Busy, f.WorkingText ?? "Working…", "Windows may ask for permission. The network pauses for a few seconds."),
            { ServiceInstalled: false } => (DriverPhase.ServiceMissing, Tone.Trouble, "The Sallyport service is not installed",
                "Game routing needs it. Install Sallyport with its setup to put it in."),
            { DriverInstalled: false } => (DriverPhase.NotInstalled, Tone.Off, "The packet driver is not installed",
                "Game routing takes each game's packets through NT Kernel's free Windows Packet Filter driver. Sallyport fetches NT Kernel's own release and installs it."),
            { ServiceRunning: false } => (DriverPhase.Unknown, Tone.Trouble, "The Sallyport service is not running", "Windows starts it at boot; restart the computer if it stays stopped."),
            _ => (DriverPhase.Ready, Tone.Up, "Ready to route games", (string?)null),
        };
        var facts = new List<Fact>
        {
            new("Packet driver", f.DriverInstalled ? f.DriverRunning ? "Installed, running" : "Installed, not running" : "Not installed"),
            new("Sallyport service", f.ServiceInstalled ? f.ServiceRunning ? "Running" : "Stopped" : "Not installed"),
        };
        if (f.Version is { } version) facts.Add(new("Driver version", version));
        return new DriverView(phase, tone, text, detail, facts,
            Install: f.DriverInstalled ? UiAction.Hidden : UiAction.Shown("Install driver…", !f.Working, busy: f.Working),
            Remove: f.DriverInstalled ? UiAction.Shown("Remove driver…", !f.Working, busy: f.Working) : UiAction.Hidden);
    }

    public static EmptyView? GamesEmpty(int games) => games > 0 ? null : new(
        "No games yet",
        "Add a game by its folder. Sallyport lists its programs; when you run it with a VPN profile, everything those programs send goes through the VPN.",
        "ADD_GAME");

    // The confirmation said before the driver is installed or removed: the network pauses (D18, measured).
    public const string DriverInstallText =
        "Windows will ask for permission. While the driver is put in, this computer's network pauses for a few seconds (about 2 s measured; other computers may differ).";

    public const string DriverRemoveText =
        "Windows will ask for permission. While the driver is taken out, this computer's network pauses for a few seconds (about 6 s measured; other computers may differ). Games can no longer be routed until it is installed again.";

    public static PolicyValues PolicyValuesOf(ConnectionPolicy p) => new(
        Seconds(p.RetryWait), Seconds(p.RetryWaitMax), p.RoundsMax?.ToString(English) ?? "0", p.RandomOrder, Seconds(p.AttemptTimeout),
        p.RetryAfterAuthFailure, p.PingRestart?.ToString(English) ?? "");

    /// <summary>What the profile itself says, under the connection settings, so a change is made knowing what it changes.</summary>
    public static string PolicyHint(ConnectionPolicy p, bool overridden) =>
        $"The profile's own: wait {Seconds(p.RetryWait)} s growing to {Seconds(p.RetryWaitMax)} s, " +
        (p.RoundsMax is { } rounds ? $"give up after {Plural(rounds, "round")}" : "never give up") +
        $", {(p.RandomOrder ? "servers in random order" : "servers in the order listed")}, {Seconds(p.AttemptTimeout)} s to answer" +
        (p.PingRestart is { } r ? $", dead after {r} s of silence when the server sets none." : ".") +
        (overridden ? " Your changes are shown above." : "");

    private static string Seconds(TimeSpan t) => ((int)Math.Round(t.TotalSeconds)).ToString(English);

    private static string? Attempts(SessionStatus? tunnel) => tunnel is null ? null
        : tunnel.LastError is { } why ? $"Last attempt: {why}" : null;

    private static string? DirectAfter(RunFacts run) =>
        run.Loss.DirectAfter is { } after && run.PhaseSince is { } since
            ? $"Your own connection in {Math.Max(0, (int)Math.Ceiling((since + after - DateTimeOffset.Now).TotalSeconds))} s if it is not back."
            : null;

    private static string? Join(string? first, string? second) => first is null ? second : second is null ? first : $"{first} {second}";

    private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n:N0} {noun}{(noun.EndsWith('s') ? "es" : "s")}";
}
