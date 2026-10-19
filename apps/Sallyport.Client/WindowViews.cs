using System.Globalization;
using System.Reflection;
using Microsoft.Win32;
using Sallyport.Profile;
using Sallyport.Runtime;

namespace Sallyport.Client;

// The Network and Settings pages' view model and their words (docs/design/window.md), as ClientView.cs and GameView.cs
// are the other pages': the page renders this and decides nothing (D15).

internal enum Page { Apps, Network, Profiles, Settings }

/// <summary>The page the host asks the window to show: a new <see cref="Request"/> moves it there once (the tray's Open
/// Network); otherwise the person's own choice of page stands.</summary>
internal sealed record NavigationView(Page Page, long Request);

/// <summary>An app's routing as the channel it uses shows it.</summary>
internal sealed record SelectionView(string Id, string Name, RunView Run, UiAction Stop);

internal sealed record ChannelActions(UiAction Disconnect);

/// <summary>One profile's connection as Network shows it: what it is, what it carries — its proxy, the apps routed
/// through it — and how to end it.</summary>
internal sealed record ChannelView(
    string Id, string ProfileId, string Name, Tone Tone, string StatusText, string? DetailText, DateTimeOffset? ConnectedSince,
    IReadOnlyList<Fact> Facts, string? ProxyText, IReadOnlyList<SelectionView> Selections, ChannelActions Actions);

internal sealed record NetworkEmpty(string Title, string Text);

internal sealed record NetworkView(string StatusText, Tone Tone, NetworkEmpty? Empty, IReadOnlyList<ChannelView> Channels, DriverView Routing);

internal sealed record StartupView(bool On, string Label, string Text, UiAction Toggle);

internal sealed record SettingsView(DriverView Routing, StartupView Startup, ClientState.GuideView Guide, IReadOnlyList<Fact> About);

internal static class WindowViews
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    /// <summary>What is connected and what goes through it now: a channel for each profile in use, failure first.</summary>
    /// <summary>What is connected and what goes through it now: one channel per profile in use — its proxy and the apps
    /// routed through it on one card, as they share one login — failure first. <paramref name="proxyProblems"/>: why a
    /// channel's proxy is not listening, by profile.</summary>
    public static NetworkView Network(IReadOnlyList<ProfileView> profiles, IReadOnlyList<GameView> games, RunFacts? run,
        IReadOnlyList<StoredProfile> stored, DriverView routing, IReadOnlyDictionary<string, string?> proxyProblems)
    {
        var channels = new List<ChannelView>();
        var routedGame = run is { Ended: null } ? games.FirstOrDefault(g => g.Id == run.GameId) : null;
        foreach (var p in profiles)
        {
            if (p.Phase is ProfilePhase.Idle or ProfilePhase.Unsupported or ProfilePhase.Removing) continue;
            // The profile's own facts, without what never changes (it is on the Profiles page).
            var facts = p.Facts.Where(f => f.Label is not ("Transport" or "Imported" or "SOCKS5 port" or "Connection settings" or "Routing through it")).ToList();
            var selections = routedGame is { Run: { } routedRun } && run!.ProfileId == p.Id
                ? new List<SelectionView> { Selection(routedGame, routedRun) }
                : [];
            var proxy = p.Phase is ProfilePhase.Refused or ProfilePhase.Stopped ? null
                : proxyProblems.GetValueOrDefault(p.Id) is { } problem ? $"SOCKS5 proxy not listening: {problem}"
                : $"SOCKS5 proxy on {p.EndpointText}";
            channels.Add(new ChannelView($"channel:{p.Id}", p.Id, p.Name, p.Tone, p.StatusText, p.DetailText, p.ConnectedSince, facts, proxy, selections,
                new ChannelActions(p.Actions.Disconnect)));
        }
        // A run whose channel is not up yet (the service still preparing it) is shown on its own until it is.
        if (routedGame is { Run: { } routed } game && run is not null && !channels.Any(c => c.ProfileId == run.ProfileId))
        {
            var name = stored.FirstOrDefault(s => s.Id == run.ProfileId)?.Name ?? "its profile";
            var tunnel = run.Tunnel;
            var (tone, text, detail) = tunnel?.State switch
            {
                SessionState.Up => (Tone.Up, "Connected", (string?)null),
                SessionState.Reconnecting => (Tone.Busy, "Reconnecting…", tunnel.LastError is { } why ? $"Last attempt: {why}" : null),
                SessionState.Connecting => (Tone.Busy, tunnel.Attempts > 1 ? "Retrying connection…" : "Connecting…",
                    tunnel.LastError is { } why2 ? $"Last attempt: {why2}" : null),
                _ when run.Preparing => (Tone.Busy, "Preparing…", null),
                _ => (Tone.Busy, "Connecting…", null),
            };
            var facts = new List<Fact>();
            if (tunnel is { State: SessionState.Up })
            {
                if (tunnel.Remote is { } remote) facts.Add(new("Connected over", remote));
                if (tunnel.Address is { } address) facts.Add(new("Tunnel address", address));
            }
            if (tunnel is not null)
            {
                facts.Add(new("Downloaded", ClientView.Bytes(tunnel.BytesIn)));
                facts.Add(new("Uploaded", ClientView.Bytes(tunnel.BytesOut)));
                if (tunnel.Reconnects > 0) facts.Add(new("Reconnects", tunnel.Reconnects.ToString("N0", English)));
            }
            channels.Add(new ChannelView($"route:{run.ProfileId}", run.ProfileId, name, tone, text, detail,
                tunnel is { State: SessionState.Up } ? tunnel.Since : null, facts, null,
                [Selection(game, routed)], new ChannelActions(UiAction.Hidden)));
        }

        // Trouble first, then work under way: one healthy channel must not hide another's failure.
        channels = [.. channels.OrderBy(c => c.Tone switch { Tone.Trouble => 0, Tone.Busy => 1, Tone.Up => 2, _ => 3 })];
        var routedApps = channels.Sum(c => c.Selections.Count);
        var worst = channels.Select(c => c.Tone).DefaultIfEmpty(Tone.Off)
            .MinBy(t => t switch { Tone.Trouble => 0, Tone.Busy => 1, Tone.Up => 2, _ => 3 });
        var status = channels.Count == 0 ? "Nothing is connected"
            : Plural(channels.Count, "connection") + (routedApps > 0 ? $" · {Plural(routedApps, "app")} routed" : "");
        return new NetworkView(status, worst, channels.Count == 0 ? new("Nothing is connected", "Route an app or connect a profile to begin.") : null,
            channels, routing);
    }

    // The tunnel's own facts are the channel's, said once above the apps it carries.
    private static SelectionView Selection(GameView game, RunView run) =>
        new(game.Id, game.Name, run with { Facts = [.. run.Facts.Where(f => f.Label is not ("Connected over" or "Tunnel address" or "Reconnects"))] }, game.Actions.Stop);

    public static SettingsView Settings(DriverView routing, bool startsWithWindows, ClientState.GuideView guide, string dataFolder, string? installedIn)
    {
        // The driver and the service are said once, under Packet routing.
        var about = new List<Fact>
        {
            new("Version", Version),
            new("Installed in", installedIn ?? $"Not installed (a development build in {AppContext.BaseDirectory.TrimEnd('\\')})"),
            new("Your data", dataFolder),
        };
        return new SettingsView(routing,
            new StartupView(startsWithWindows, "Start Sallyport with Windows",
                "Opens Sallyport in the notification area when you sign in. Connections and routing start only when you start them.",
                UiAction.Shown(startsWithWindows ? "Turn off" : "Turn on")),
            guide, about);
    }

    public static string Version =>
        typeof(WindowViews).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";

    private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n:N0} {noun}s";
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
