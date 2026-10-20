using Sallyport.Profile;
using Sallyport.Runtime;
using Sallyport.Windows.Routing;
using Sallyport.Windows.Service;

namespace Sallyport.Client.Tests;

/// <summary>The tray's menu as it is built from the window's views (TrayMenu): what each profile and app offers there,
/// and that the menu never offers what the window would not.</summary>
public class TrayMenuTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    private static readonly StoredProfile Jp = new("ad50a53b0da826ad", "jp", ["udp/1194", "tcp/443"], At, null);

    private static ProfileView Profile(string? state) =>
        ClientView.Profile(new ProfileFacts(Jp, 1088, state is null ? null : new TunnelServiceStatus(state, At,
            state == TunnelServiceStatus.Up ? "udp/1194" : null, 1, 0, null, state == TunnelServiceStatus.Up ? "10.8.0.2" : null, 2, 2048, 4096, 1088),
            null, false, false, null, false, false));

    private static RoutedItem App(string id, string name) => new(id, name, $@"E:\Games\{name}", At, Jp.Id, LossMode.Hold, null,
        [new($"{id}-1", $@"E:\Games\{name}\{name}.exe", true, null, false, true, null)]);

    private static readonly RoutedItem Zenless = App("0123456789abcdef", "Zenless");
    private static readonly RoutedItem Genshin = App("fedcba9876543210", "Genshin");

    private static RunFacts Running(RoutedItem item, string? ended = null) =>
        new(item.Id, Jp.Id, new LossPolicy(), At, false,
            new SessionStatus(SessionState.Up, At, "udp/1194", 1, 0, null, "10.8.0.6", 1024, 2048),
            new RouteState(RoutePhase.Routing, 1, 10, 12, 500, 2, 0, 0, 0, 0, 0, 3, 1, 0),
            At, ended, ended is null ? null : At.AddMinutes(5));

    private static AppItemView Item(RoutedItem item, RunFacts? run = null, bool anotherRunning = false) =>
        AppViews.Item(new ItemFacts(item, Jp, run, anotherRunning, true, false, null, _ => []));

    private static List<TrayEntry> Commands(IEnumerable<TrayEntry> entries) =>
        entries.SelectMany(e => e.Items is { } items ? Commands(items) : e.Kind == TrayEntryKind.Command ? [e] : []).ToList();

    [Fact]
    public void With_nothing_yet_it_opens_the_window_says_how_to_begin_and_leaves()
    {
        var menu = TrayMenu.Build([], [], "Nothing is connected");

        Assert.Equal(
            [TrayEntryKind.Command, TrayEntryKind.Separator, TrayEntryKind.Note, TrayEntryKind.Separator, TrayEntryKind.Command, TrayEntryKind.Command],
            menu.Select(e => e.Kind));
        // One way into the window, on its Overview; what is connected, its tip.
        Assert.True(menu[0] is { Text: "Open Sallyport", Verb: TrayVerb.OpenWindow, Default: true, Tip: "Nothing is connected" });
        Assert.True(menu[2] is { Text: "No profiles yet — open Sallyport to import one", Enabled: false });
        Assert.True(menu[4] is { Text: "Settings", Verb: TrayVerb.OpenSettings });
        Assert.True(menu[5] is { Text: "Exit Sallyport", Verb: TrayVerb.Exit, Tip: "Stops routing, disconnects every profile, then quits." });
        // Only one entry is the default (what a double click would do), and it is opening the window.
        Assert.Single(menu, e => e.Default);
    }

    [Fact]
    public void A_profile_not_connected_offers_connect_and_not_disconnect()
    {
        var profile = Profile(null);

        var entry = Assert.Single(TrayMenu.Build([profile], [], ""), e => e.Kind == TrayEntryKind.Submenu);

        Assert.Equal($"jp — {profile.StatusText}", entry.Text);
        var connect = Assert.Single(entry.Items!);
        Assert.True(connect is { Text: "Connect", Verb: TrayVerb.Connect, Enabled: true });
        Assert.Equal(Jp.Id, connect.Target);
    }

    [Fact]
    public void A_connected_profile_offers_disconnect_and_not_connect()
    {
        var entry = Assert.Single(TrayMenu.Build([Profile(TunnelServiceStatus.Up)], [], ""), e => e.Kind == TrayEntryKind.Submenu);

        Assert.Equal("jp — Connected", entry.Text);
        Assert.DoesNotContain(entry.Items!, e => e.Verb == TrayVerb.Connect);
        Assert.True(Assert.Single(entry.Items!, e => e.Verb == TrayVerb.Disconnect) is { Text: "Disconnect", Enabled: true, Target: "ad50a53b0da826ad" });
    }

    [Fact]
    public void A_profile_still_connecting_can_be_cancelled_from_the_tray()
    {
        var entry = Assert.Single(TrayMenu.Build([Profile(TunnelServiceStatus.Connecting)], [], ""), e => e.Kind == TrayEntryKind.Submenu);

        Assert.True(Assert.Single(entry.Items!, e => e.Kind == TrayEntryKind.Command) is { Text: "Cancel", Verb: TrayVerb.Disconnect });
    }

    [Fact]
    public void Apps_not_routed_are_offered_to_launch_or_route_with_the_windows_reasons()
    {
        var menu = TrayMenu.Build([Profile(null)], [Item(Zenless)], "");

        var apps = Assert.Single(menu, e => e is { Kind: TrayEntryKind.Submenu, Text: "Apps" });
        var zenless = Assert.Single(apps.Items!);
        Assert.Equal("Zenless  ·  Through jp", zenless.Text);
        Assert.True(Assert.Single(zenless.Items!, e => e.Verb == TrayVerb.Route) is { Text: "Route", Enabled: true, Target: "0123456789abcdef" });
        // Its program is not on this machine: the window disables Launch and says why, and so does the tray.
        var launch = Assert.Single(zenless.Items!, e => e.Verb == TrayVerb.Launch);
        Assert.False(launch.Enabled);
        Assert.Equal("Zenless.exe is not where it was added from.", launch.Tip);
    }

    [Fact]
    public void The_routed_app_stands_on_its_own_to_stop_and_the_others_cannot_route_meanwhile()
    {
        var run = Running(Zenless);
        var menu = TrayMenu.Build([Profile(null)], [Item(Zenless, run), Item(Genshin, anotherRunning: true)], "");

        var routed = Assert.Single(menu, e => e.Text.StartsWith("Zenless — ", StringComparison.Ordinal));
        Assert.Equal("Zenless — Routed through jp", routed.Text);
        Assert.True(Assert.Single(routed.Items!, e => e.Verb == TrayVerb.StopRun) is { Text: "Stop routing", Enabled: true });
        Assert.DoesNotContain(routed.Items!, e => e.Verb == TrayVerb.Route);

        var apps = Assert.Single(menu, e => e.Text == "Apps");
        var genshin = Assert.Single(apps.Items!);
        Assert.StartsWith("Genshin", genshin.Text);
        var route = Assert.Single(genshin.Items!, e => e.Verb == TrayVerb.Route);
        Assert.False(route.Enabled);
        Assert.Equal("Another app is being routed. Stop it first.", route.Tip);
    }

    [Fact]
    public void An_app_whose_routing_ended_is_back_among_the_apps_to_route_again()
    {
        var menu = TrayMenu.Build([], [Item(Zenless, Running(Zenless, ended: "stopped"))], "");

        Assert.DoesNotContain(menu, e => e.Text.StartsWith("Zenless — ", StringComparison.Ordinal));
        var zenless = Assert.Single(Assert.Single(menu, e => e.Text == "Apps").Items!);
        Assert.Contains(zenless.Items!, e => e is { Verb: TrayVerb.Route, Text: "Route again" });
    }

    [Fact]
    public void Every_command_says_what_it_acts_on_and_nothing_else_can_be_pressed()
    {
        var run = Running(Zenless);
        var menu = TrayMenu.Build([Profile(TunnelServiceStatus.Up)], [Item(Zenless, run), Item(Genshin, anotherRunning: true)], "1 connection");

        var commands = Commands(menu);
        Assert.All(commands, c => Assert.NotEqual(TrayVerb.None, c.Verb));
        Assert.All(commands.Where(c => c.Verb is TrayVerb.Connect or TrayVerb.Disconnect or TrayVerb.Launch or TrayVerb.Route or TrayVerb.StopRun),
            c => Assert.False(string.IsNullOrEmpty(c.Target)));
        Assert.All(menu.SelectMany(Flatten).Where(e => e.Kind == TrayEntryKind.Note), n => Assert.False(n.Enabled));

        static IEnumerable<TrayEntry> Flatten(TrayEntry e) => e.Items is { } items ? items.SelectMany(Flatten).Prepend(e) : [e];
    }

    [Fact]
    public void The_icons_tip_is_the_summary_cut_at_a_whole_phrase_to_what_windows_accepts()
    {
        Assert.Equal("Sallyport — Not connected", TrayMenu.Tooltip("Not connected"));

        var phrases = string.Join(" · ", Enumerable.Range(1, 20).Select(i => $"profile {i} connected"));
        var tip = TrayMenu.Tooltip(phrases);

        Assert.True(tip.Length <= 127, $"{tip.Length} characters");
        Assert.EndsWith("connected …", tip);
        Assert.StartsWith("Sallyport — profile 1 connected · ", tip);
    }
}
