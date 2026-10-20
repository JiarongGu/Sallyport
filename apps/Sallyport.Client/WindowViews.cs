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
/// <summary>A page to show, once per request; <see cref="App"/>, an app's own page on Apps.</summary>
internal sealed record NavigationView(Page Page, long Request, string? App = null);

internal sealed record ChannelActions(UiAction Disconnect);

internal enum ProbePhase { Unchecked, Checking, Available, Unavailable }

/// <summary>A channel's round trip as its traffic graph carries it: <see cref="Figure"/>, "48 ms", or none yet or none
/// answered; <see cref="Text"/>, what it is, or why there is none.</summary>
internal sealed record LatencyView(ProbePhase Phase, string? Figure, string Text, DateTimeOffset? CheckedAt);

/// <summary>One profile's connection as Network shows it: what it is, its traffic and round trip, one line for the rest
/// (<see cref="Line"/>: "UDP", its proxy, what is open through it, its reconnects), and how to end it.</summary>
internal sealed record ChannelView(
    string Id, string ProfileId, string Name, Tone Tone, string StatusText, string? DetailText, DateTimeOffset? ConnectedSince,
    IReadOnlyList<string> Line, ChannelActions Actions,
    DateTimeOffset? NextAttemptAt = null, LatencyView? Latency = null, TrafficView? Traffic = null);

/// <summary>A profile's section on the Overview (D24): the profile and its switch, its connection while it has one, the
/// apps in it, each with its own switch, and the apps that could be put in it (<see cref="Add"/> posts
/// ADD_APP_TO_PROFILE with one of <see cref="Addable"/>).</summary>
internal sealed record SectionView(ProfileView Profile, ChannelView? Channel, IReadOnlyList<AppSectionView> Apps, IReadOnlyList<EditorOption> Addable,
    UiAction Add, string? HintText);

/// <summary>A channel's traffic for its card: what it has received and sent, as figures with the rate now, and the rates
/// a second apart over the last minute (bytes per second, oldest first) for its graph.</summary>
internal sealed record TrafficView(Fact Received, Fact Sent, IReadOnlyList<double> InPerSecond, IReadOnlyList<double> OutPerSecond);

internal sealed record NetworkEmpty(string Title, string Text);

internal sealed record NetworkView(string StatusText, Tone Tone, NetworkEmpty? Empty, IReadOnlyList<ChannelView> Channels, DriverView Routing,
    IReadOnlyList<SectionView>? Sections = null);

internal sealed record StartupView(bool On, string Label, string Text, UiAction Toggle);

/// <summary>The language the window speaks, "en" or "zh-Hans", and the choices.</summary>
internal sealed record LanguageView(string Setting, IReadOnlyList<EditorOption> Choices);

internal sealed record SettingsView(DriverView Routing, StartupView Startup, ClientState.GuideView Guide, IReadOnlyList<Fact> About,
    LanguageView Language, ConnectionSettingsView Connection, IReadOnlyList<Fact>? Places = null, MoveView? Move = null,
    UiAction? Uninstall = null);

/// <summary>Settings' export and import of the profiles, the apps and the connection settings (D26): BEGIN_EXPORT, BEGIN_IMPORT.</summary>
internal sealed record MoveView(UiAction Export, UiAction Import);

/// <summary>Settings' connection choices, each applied the moment it is chosen: SET_PROTOCOL, SET_RETRY.</summary>
internal sealed record ConnectionSettingsView(LanguageView Protocol, LanguageView Retry);

internal static class WindowViews
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    /// <summary>What is connected and what goes through it now: a channel for each profile in use, failure first.</summary>
    /// <summary>What is connected and what goes through it now: one channel per profile in use — its proxy and the apps
    /// routed through it on one card, as they share one login — failure first. <paramref name="proxyProblems"/>: why a
    /// channel's proxy is not listening, by profile.</summary>
    /// <param name="sections">Each profile's apps and the apps that could join it, by profile (D24).</param>
    public static NetworkView Network(IReadOnlyList<ProfileView> profiles, IReadOnlyList<AppItemView> games, RunFacts? run,
        IReadOnlyList<StoredProfile> stored, DriverView routing, IReadOnlyDictionary<string, string?> proxyProblems,
        IReadOnlyDictionary<string, (Latency? Last, bool Checking)>? latencies = null, IReadOnlyDictionary<string, Traffic?>? traffic = null,
        IReadOnlyDictionary<string, (IReadOnlyList<AppSectionView> Apps, IReadOnlyList<EditorOption> Addable)>? sections = null)
    {
        var channels = new List<ChannelView>();
        foreach (var p in profiles)
        {
            if (p.Phase is ProfilePhase.Idle or ProfilePhase.Unsupported or ProfilePhase.Removing) continue;
            var flow = traffic?.GetValueOrDefault(p.Id) is { } t ? Traffic(t) : null;
            var proxy = p.Phase is ProfilePhase.Refused or ProfilePhase.Stopped ? null
                : proxyProblems.GetValueOrDefault(p.Id) is { } problem ? L.T("SOCKS5 proxy not listening: {0}", problem)
                : L.T("SOCKS5 proxy on {0}", p.EndpointText);
            // One line for what the section shows nowhere else (the owner: "just display a line for them like connection
            // number"): how it is connected, its proxy and what is open through it, and its reconnects, if any. The
            // tunnel's address and its port are not for the person to read; the counts are the graph's.
            List<string> line =
            [
                .. p.Tunnel?.Transport is { } transport ? [transport] : Array.Empty<string>(),
                .. proxy is null ? Array.Empty<string>() : [proxy],
                .. p.Tunnel is { } figures ? [L.Count(figures.OpenConnections, "1 open connection", "{0} open connections")] : Array.Empty<string>(),
                .. p.Tunnel is { Reconnects: > 0 and var again } ? [L.Count(again, "Reconnected once", "Reconnected {0} times")] : Array.Empty<string>(),
            ];
            var (last, checking) = latencies?.GetValueOrDefault(p.Id) ?? (null, false);
            channels.Add(new ChannelView($"channel:{p.Id}", p.Id, p.Name, p.Tone, p.StatusText, p.DetailText, p.ConnectedSince, line,
                new ChannelActions(p.Actions.Disconnect),
                p.NextAttemptAt, p.Phase == ProfilePhase.Up || last is not null ? Latency(last, checking) : null, flow));
        }

        // Trouble first, then work under way: one healthy channel must not hide another's failure.
        channels = [.. channels.OrderBy(c => c.Tone switch { Tone.Trouble => 0, Tone.Busy => 1, Tone.Up => 2, _ => 3 })];
        var routedApps = run is { Ended: null } ? run.Lanes.Sum(l => l.Apps.Count) : 0;
        var worst = channels.Select(c => c.Tone).DefaultIfEmpty(Tone.Off)
            .MinBy(t => t switch { Tone.Trouble => 0, Tone.Busy => 1, Tone.Up => 2, _ => 3 });
        var connections = L.Count(channels.Count, "1 connection", "{0} connections");
        var status = channels.Count == 0 ? L.T("Nothing is connected")
            : routedApps > 0 ? L.T("{0} · {1} using the VPN", connections, L.Count(routedApps, "1 app", "{0} apps")) : connections;
        // Every profile that can connect, in the order they were imported: its section stays where it is whether connected or not.
        var views = profiles.Where(p => p.Phase != ProfilePhase.Unsupported).Select(p =>
        {
            var (apps, addable) = sections?.GetValueOrDefault(p.Id) ?? ([], []);
            // Always there: an app already added, or a new one (Add an app with this profile ticked).
            return new SectionView(p, channels.FirstOrDefault(c => c.ProfileId == p.Id), apps, addable,
                UiAction.Shown(L.T("Add an app to {0}", p.Name)),
                apps.Count == 0 ? L.T("Put an app here and it goes through {0} whenever it is connected.", p.Name) : null);
        }).ToList();
        return new NetworkView(status, worst, channels.Count == 0 ? new(L.T("Nothing is connected"), L.T("Connect a profile to begin: the apps whose Use VPN is on go through it.")) : null,
            channels, routing, views);
    }

    public static TrafficView Traffic(Traffic t) => new(
        new(L.T("Received"), L.T("{0}/s", ClientView.Bytes((long)t.InPerSecond.LastOrDefault())), ClientView.Bytes(t.BytesIn)),
        new(L.T("Sent"), L.T("{0}/s", ClientView.Bytes((long)t.OutPerSecond.LastOrDefault())), ClientView.Bytes(t.BytesOut)),
        t.InPerSecond, t.OutPerSecond);

    /// <summary>A channel's round trip for its graph: the VPN server's own when it answers an echo to its tunnel side
    /// (many do not), else the way through it to the public address — a hop's answer is only an estimate, so not shown. A
    /// figure kept while it is checked again; none answered said as that, which says nothing of the tunnel (rule 6).</summary>
    public static LatencyView Latency(Latency? last, bool checking)
    {
        var phase = checking ? ProbePhase.Checking : ProbePhase.Unchecked;
        if (last is null) return new(phase, null, checking ? L.T("Checking ping…") : L.T("Not measured yet"), null);
        var (figure, text) = last.ToGateway is { MedianMs: { } server, FromDestination: true } ? (server, L.T("To the VPN server"))
            : last.Vpn is { MedianMs: { } through, FromDestination: true } ? (through, L.T("Through the VPN to {0}", last.Target))
            : ((double?)null, L.T("Nothing answered, so ping could not be checked. The VPN itself may still work."));
        return new(checking ? phase : figure is null ? ProbePhase.Unavailable : ProbePhase.Available,
            figure is { } ms ? L.T("{0} ms", ms.ToString("F0", English)) : null, text, last.Checked);
    }

    /// <summary>An app's part of the route as its section shows it: the tunnel's own facts are the section's, said once
    /// above the apps it carries.</summary>
    public static RunView InSection(RunView run)
    {
        string[] channels = [L.T("Connected over"), L.T("Tunnel address"), L.T("Reconnects")];
        return run with { Facts = [.. run.Facts.Where(f => !channels.Contains(f.Label))] };
    }

    public static SettingsView Settings(DriverView routing, bool startsWithWindows, ClientState.GuideView guide, string dataFolder, string? installedIn,
        bool editing, string? languageSetting = null, PolicyOverride? connection = null,
        string? dialogOpen = null, bool moving = false)
    {
        // The driver and the service are said once, under Packet routing.
        var about = new List<Fact> { new(L.T("Version"), Version) };
        // Where it lives: long paths only some look for, folded on the page.
        List<Fact> places =
        [
            new(L.T("Installed in"), installedIn ?? L.T("Not installed (a development build in {0})", AppContext.BaseDirectory.TrimEnd('\\'))),
            new(L.T("Your data"), dataFolder),
        ];
        return new SettingsView(routing,
            new StartupView(startsWithWindows, L.T("Start Sallyport with Windows"),
                L.T("Opens Sallyport beside the clock when you sign in. Your apps use the VPN only once you connect."),
                UiAction.Shown(startsWithWindows ? L.T("Turn off") : L.T("Turn on"))),
            guide, about,
            Language(languageSetting),
            // Never given "keep trying" stands until something else is chosen.
            new ConnectionSettingsView(new(connection?.Protocol ?? "", GameViews.ProtocolChoices()),
                new((connection?.RoundsMax ?? 0).ToString(CultureInfo.InvariantCulture), GameViews.RetryChoices(connection?.RoundsMax ?? 0))),
            places,
            // While Export or Import is open beneath them, its own buttons are what to press.
            moving ? new MoveView(UiAction.Hidden, UiAction.Hidden) : new MoveView(
                UiAction.Shown(L.T("Export…"), !editing && dialogOpen is null, dialogOpen ?? L.T("Finish the open edit first.")),
                UiAction.Shown(L.T("Import…"), !editing && dialogOpen is null, dialogOpen ?? L.T("Finish the open edit first."))),
            // Only an installed copy has setup's removal to start; a development build has nothing installed.
            installedIn is null ? UiAction.Hidden : UiAction.Shown(L.T("Uninstall Sallyport…")));
    }

    /// <summary>The language choice, each named in itself; until the person picks one, the one setup ran in, or Windows'.</summary>
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
