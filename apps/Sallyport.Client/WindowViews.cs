using System.Globalization;
using System.Reflection;
using Microsoft.Win32;
using Sallyport.Profile;
using Sallyport.Runtime;
using Sallyport.Windows.Routing;

namespace Sallyport.Client;

// The Network and Settings pages' view model and their words (docs/design/window.md), as ClientView.cs and GameView.cs
// are the other pages': the page renders this and decides nothing (D15).

internal enum Page { Apps, Network, Profiles, Settings }

/// <summary>The page the host asks the window to show: a new <see cref="Request"/> moves it there once (the tray's Open
/// Network); otherwise the person's own choice of page stands.</summary>
internal sealed record NavigationView(Page Page, long Request);

/// <summary>An app's routing as the channel it uses shows it.</summary>
internal sealed record SelectionView(string Id, string Name, RunView Run, UiAction Stop);

internal sealed record ChannelActions(UiAction Disconnect, UiAction CheckLatency);

internal enum ProbePhase { Unchecked, Checking, Available, Unavailable }

/// <summary>A channel's latency as Network shows it: each path's round trip, or why there is none.</summary>
internal sealed record LatencyView(ProbePhase Phase, string StatusText, IReadOnlyList<Fact> Paths, DateTimeOffset? CheckedAt);

/// <summary>One profile's connection as Network shows it: what it is, what it carries — its proxy, the apps routed
/// through it — and how to end it.</summary>
internal sealed record ChannelView(
    string Id, string ProfileId, string Name, Tone Tone, string StatusText, string? DetailText, DateTimeOffset? ConnectedSince,
    IReadOnlyList<Fact> Facts, string? ProxyText, IReadOnlyList<SelectionView> Selections, ChannelActions Actions,
    DateTimeOffset? NextAttemptAt = null, LatencyView? Latency = null);

internal sealed record NetworkEmpty(string Title, string Text);

internal sealed record NetworkView(string StatusText, Tone Tone, NetworkEmpty? Empty, IReadOnlyList<ChannelView> Channels, DriverView Routing);

internal sealed record StartupView(bool On, string Label, string Text, UiAction Toggle);

internal sealed record DefaultsView(IReadOnlyList<Fact> Facts, UiAction Edit);

/// <summary>The language the window speaks, "en" or "zh-Hans", and the choices.</summary>
internal sealed record LanguageView(string Setting, IReadOnlyList<EditorOption> Choices);

internal sealed record SettingsView(DriverView Routing, StartupView Startup, ClientState.GuideView Guide, IReadOnlyList<Fact> About, DefaultsView Defaults,
    LanguageView Language);

internal sealed record DefaultsValues(string? ProfileId, LossMode Loss, string DirectAfterText);

internal static class WindowViews
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    /// <summary>What is connected and what goes through it now: a channel for each profile in use, failure first.</summary>
    /// <summary>What is connected and what goes through it now: one channel per profile in use — its proxy and the apps
    /// routed through it on one card, as they share one login — failure first. <paramref name="proxyProblems"/>: why a
    /// channel's proxy is not listening, by profile.</summary>
    public static NetworkView Network(IReadOnlyList<ProfileView> profiles, IReadOnlyList<AppItemView> games, RunFacts? run,
        IReadOnlyList<StoredProfile> stored, DriverView routing, IReadOnlyDictionary<string, string?> proxyProblems,
        IReadOnlyDictionary<string, (Latency? Last, bool Checking)>? latencies = null)
    {
        var channels = new List<ChannelView>();
        var routedGame = run is { Ended: null } ? games.FirstOrDefault(g => g.Id == run.GameId) : null;
        // What never changes is on the Profiles page; who routes through it is said by the apps the channel carries.
        string[] notHere = [L.T("Transport"), L.T("Imported"), L.T("SOCKS5 port"), L.T("Connection settings"), L.T("Routing through it")];
        foreach (var p in profiles)
        {
            if (p.Phase is ProfilePhase.Idle or ProfilePhase.Unsupported or ProfilePhase.Removing) continue;
            var facts = p.Facts.Where(f => !notHere.Contains(f.Label)).ToList();
            var selections = routedGame is { Run: { } routedRun } && run!.ProfileId == p.Id
                ? new List<SelectionView> { Selection(routedGame, routedRun) }
                : [];
            var proxy = p.Phase is ProfilePhase.Refused or ProfilePhase.Stopped ? null
                : proxyProblems.GetValueOrDefault(p.Id) is { } problem ? L.T("SOCKS5 proxy not listening: {0}", problem)
                : L.T("SOCKS5 proxy on {0}", p.EndpointText);
            var (last, checking) = latencies?.GetValueOrDefault(p.Id) ?? (null, false);
            channels.Add(new ChannelView($"channel:{p.Id}", p.Id, p.Name, p.Tone, p.StatusText, p.DetailText, p.ConnectedSince, facts, proxy, selections,
                new ChannelActions(p.Actions.Disconnect,
                    p.Phase == ProfilePhase.Up ? UiAction.Shown(last is null ? L.T("Check latency") : L.T("Check again"), busy: checking) : UiAction.Hidden),
                p.NextAttemptAt, p.Phase == ProfilePhase.Up || last is not null ? Latency(last, checking) : null));
        }
        // A run whose channel is not up yet (the service still preparing it) is shown on its own until it is.
        if (routedGame is { Run: { } routed } game && run is not null && !channels.Any(c => c.ProfileId == run.ProfileId))
        {
            var name = stored.FirstOrDefault(s => s.Id == run.ProfileId)?.Name ?? L.T("its profile");
            var tunnel = run.Tunnel;
            var (tone, text, detail) = tunnel?.State switch
            {
                SessionState.Up => (Tone.Up, L.T("Connected"), (string?)null),
                SessionState.Reconnecting => (Tone.Busy, L.T("Reconnecting…"), tunnel.LastError is { } why ? L.T("Last attempt: {0}", why) : null),
                SessionState.Connecting => (Tone.Busy, tunnel.Attempts > 1 ? L.T("Retrying connection…") : L.T("Connecting…"),
                    tunnel.LastError is { } why2 ? L.T("Last attempt: {0}", why2) : null),
                _ when run.Preparing => (Tone.Busy, L.T("Preparing…"), null),
                _ => (Tone.Busy, L.T("Connecting…"), null),
            };
            var facts = new List<Fact>();
            if (tunnel is { State: SessionState.Up })
            {
                if (tunnel.Remote is { } remote) facts.Add(new(L.T("Connected over"), remote));
                if (tunnel.Address is { } address) facts.Add(new(L.T("Tunnel address"), address));
            }
            if (tunnel is not null)
            {
                facts.Add(new(L.T("Downloaded"), ClientView.Bytes(tunnel.BytesIn)));
                facts.Add(new(L.T("Uploaded"), ClientView.Bytes(tunnel.BytesOut)));
                if (tunnel.Reconnects > 0) facts.Add(new(L.T("Reconnects"), tunnel.Reconnects.ToString("N0", English)));
            }
            channels.Add(new ChannelView($"route:{run.ProfileId}", run.ProfileId, name, tone, text, detail,
                tunnel is { State: SessionState.Up } ? tunnel.Since : null, facts, null,
                [Selection(game, routed)], new ChannelActions(UiAction.Hidden, UiAction.Hidden)));
        }

        // Trouble first, then work under way: one healthy channel must not hide another's failure.
        channels = [.. channels.OrderBy(c => c.Tone switch { Tone.Trouble => 0, Tone.Busy => 1, Tone.Up => 2, _ => 3 })];
        var routedApps = channels.Sum(c => c.Selections.Count);
        var worst = channels.Select(c => c.Tone).DefaultIfEmpty(Tone.Off)
            .MinBy(t => t switch { Tone.Trouble => 0, Tone.Busy => 1, Tone.Up => 2, _ => 3 });
        var connections = L.Count(channels.Count, "1 connection", "{0} connections");
        var status = channels.Count == 0 ? L.T("Nothing is connected")
            : routedApps > 0 ? L.T("{0} · {1} routed", connections, L.Count(routedApps, "1 app", "{0} apps")) : connections;
        return new NetworkView(status, worst, channels.Count == 0 ? new(L.T("Nothing is connected"), L.T("Route an app or connect a profile to begin.")) : null,
            channels, routing);
    }

    /// <summary>A channel's latency, in words: each path's median round trip and how many of its echoes were answered,
    /// or that none was — which says nothing of the tunnel (rule 6) — and only a hop's answer marked as the estimate it is.</summary>
    public static LatencyView Latency(Latency? last, bool checking)
    {
        if (last is null) return new(checking ? ProbePhase.Checking : ProbePhase.Unchecked,
            checking ? L.T("Checking latency…") : L.T("Round trips to {0}: from this computer, through the VPN, and to the VPN server.", Connections.LatencyTarget), [], null);
        string Line(PathSample s) => s.MedianMs is { } ms
            ? s.FromDestination
                ? L.T("{0} ms ({1} of {2} answered)", ms.ToString("F0", English), s.Answered, s.Sent)
                : L.T("{0} ms ({1} of {2} answered; farthest responding hop {3} — estimate)", ms.ToString("F0", English), s.Answered, s.Sent, s.Responder)
            : L.T("No reply — latency unavailable");
        var paths = new List<Fact>
        {
            new(L.T("Direct to {0}", last.Target), Line(last.Direct)),
            new(L.T("Through the VPN to {0}", last.Target), Line(last.Vpn)),
            new(L.T("To the VPN server"), last.ToGateway is { } g && last.Gateway is { } gateway
                ? L.T("{0} — tunnel gateway {1}", Line(g), gateway) : L.T("The server named no tunnel gateway.")),
        };
        var any = last.Direct.Answered + last.Vpn.Answered + (last.ToGateway?.Answered ?? 0) > 0;
        return new(checking ? ProbePhase.Checking : any ? ProbePhase.Available : ProbePhase.Unavailable,
            checking ? L.T("Checking latency…") : any ? L.T("Latency")
                : L.T("No echo was answered, so latency could not be checked. The tunnel itself may still work."), paths, last.Checked);
    }

    // The tunnel's own facts are the channel's, said once above the apps it carries.
    private static SelectionView Selection(AppItemView game, RunView run)
    {
        string[] channels = [L.T("Connected over"), L.T("Tunnel address"), L.T("Reconnects")];
        return new(game.Id, game.Name, run with { Facts = [.. run.Facts.Where(f => !channels.Contains(f.Label))] }, game.Actions.Stop);
    }

    public static SettingsView Settings(DriverView routing, bool startsWithWindows, ClientState.GuideView guide, string dataFolder, string? installedIn,
        AppDefaults defaults, StoredProfile? defaultProfile, bool editing, string? languageSetting = null)
    {
        // The driver and the service are said once, under Packet routing.
        var about = new List<Fact>
        {
            new(L.T("Version"), Version),
            new(L.T("Installed in"), installedIn ?? L.T("Not installed (a development build in {0})", AppContext.BaseDirectory.TrimEnd('\\'))),
            new(L.T("Your data"), dataFolder),
        };
        return new SettingsView(routing,
            new StartupView(startsWithWindows, L.T("Start Sallyport with Windows"),
                L.T("Opens Sallyport in the notification area when you sign in. Connections and routing start only when you start them."),
                UiAction.Shown(startsWithWindows ? L.T("Turn off") : L.T("Turn on"))),
            guide, about,
            new DefaultsView(
            [
                new(L.T("VPN profile"), defaultProfile?.Name ?? (defaults.ProfileId is null ? L.T("The first that can connect") : L.T("Not chosen (the one chosen was removed)"))),
                new(L.T("If the VPN drops"), GameViews.LossShort(new GameChoice(null, defaults.Loss, defaults.DirectAfterSeconds))),
            ], UiAction.Shown(L.T("Change…"), !editing, L.T("Finish the open edit first."))),
            Language(languageSetting));
    }

    /// <summary>The language choice, each named in itself; until the person picks one, the one Windows' language gives.</summary>
    public static LanguageView Language(string? setting) => new(
        setting is "en" or "zh-Hans" ? setting : L.Resolve(null) == Client.Language.Chinese ? "zh-Hans" : "en",
        [
            new("en", "English"),
            new("zh-Hans", "中文简体"),
        ]);

    public static string Version =>
        typeof(WindowViews).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? L.T("unknown");
}

/// <summary>Whether Sallyport starts when the person signs in: Windows' own per-user Run entry, pointing at this client
/// with <c>--tray</c>. One that points elsewhere (an earlier install) is not this one's and reads as off.</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "Sallyport";

    private static string Command => $"\"{Environment.ProcessPath}\" --tray";

    public static bool IsOn()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(Name) is string value && string.Equals(value, Command, StringComparison.OrdinalIgnoreCase);
    }

    public static void Set(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue(Name, Command);
        else key.DeleteValue(Name, throwOnMissingValue: false);
    }
}
