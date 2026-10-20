using Sallyport.Profile;
using Sallyport.Runtime;
using Sallyport.Windows.Routing;
using Sallyport.Windows.Service;

namespace Sallyport.Client.Tests;

/// <summary>What the Apps page shows, as the host decides it (docs/design/window.md, step 6).</summary>
public class AppViewTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    private static readonly StoredProfile Jp = new("ad50a53b0da826ad", "jp", ["udp/1194", "tcp/443"], At, null);

    private static RoutedItem Zenless(bool included = true, bool on = false) => new("0123456789abcdef", "Zenless Zone Zero", @"E:\Games\ZZZ", At, Jp.Id, LossMode.Hold, null,
        [new("000000000001", @"E:\Games\ZZZ\ZenlessZoneZero.exe", included, null, false, true, null),
         new("000000000002", @"E:\Games\ZZZ\unins000.exe", false, "installs or removes software", false, true, null)], UseVpn: on);

    private static RouteState Route(RoutePhase phase, int selected = 2, long late = 0, int unreadable = 0, IReadOnlyList<ProgramRouted>? programs = null) =>
        new(phase, phase == RoutePhase.Routing ? 1 : 0, 100, 120, 5000, 3, 0, 0, late, 0, 0, 7, selected, unreadable, programs);

    // The route through jp, carrying the apps named (Zenless when none is), each since At.
    private static RunFacts Facts(RouteState? route, string? ended = null, DateTimeOffset? phaseSince = null, params string[] apps) =>
        new(Jp.Id, (apps.Length == 0 ? ["0123456789abcdef"] : apps).ToDictionary(a => a, _ => At), At, route is null && ended is null, null, route, phaseSince, ended, null);

    private static AppItemView View(RunFacts? run = null, StoredProfile? profile = null, bool connected = false, bool driverReady = true,
        RoutedItem? item = null, AppPhase? launching = null, string? carriedBy = null, string? failure = null) =>
        AppViews.Item(new ItemFacts(item ?? Zenless(), profile ?? Jp, run, connected, driverReady, false, launching, _ => ["--proxy-server=socks5://127.0.0.1:1088"],
            CarriedBy: carriedBy, Failure: failure));

    [Fact]
    public void Its_page_says_its_setup_label_by_label()
    {
        // No Launch for what is routed by packets, so nothing said of what Launch starts; its folder is the page's own line.
        Assert.Equal(
            [new Fact("VPN profile", "jp"), new Fact("Uses the VPN", "All traffic of 1 program"), new Fact("If the VPN drops", GameViews.LossShort(new GameChoice(null, LossMode.Hold, null)))],
            View().Setup!);
        var none = AppViews.Item(new ItemFacts(Zenless() with { ProfileId = null }, null, null, false, true, false, null, _ => []));
        Assert.Equal(new Fact("VPN profile", "None chosen"), none.Setup![0]);
    }

    [Fact]
    public void An_app_whose_switch_is_off_says_so_and_can_be_turned_on_connected_or_not()
    {
        var view = View();

        Assert.Equal(("VPN off", Tone.Off, false), (view.StatusText, view.Tone, view.VpnOn));
        Assert.Equal("Through jp", view.ThroughText);
        Assert.Equal("All traffic of 1 program", view.MethodText);
        Assert.Equal("Using the VPN: 1 program of 2.", view.ProgramsText);
        // A choice, not a connection (D23): it may be turned on whether jp is connected or not.
        Assert.True(view.Actions.Vpn is { Visible: true, Enabled: true, Label: "Use VPN" });
        Assert.True(View(connected: true).Actions.Vpn is { Visible: true, Enabled: true });
        Assert.Equal("Left out — installs or removes software", view.Programs[1].StatusText);
        Assert.Equal("All traffic", view.Programs[0].StatusText);
        // A game is started as the person always starts it: Sallyport offers no Launch for what it routes by packets.
        Assert.False(view.Actions.Launch.Visible);
        Assert.False(view.Programs[0].LaunchTarget);
        Assert.Null(view.LaunchText);
        Assert.DoesNotContain(view.Setup!, f => f.Label == "Launch starts");
    }

    [Theory]
    [InlineData(false, true, null, null, "Waiting for jp", "Info", "Connect jp and it goes through the VPN.")]
    [InlineData(true, false, null, null, "Cannot use the VPN", "Trouble",
        "Turning on an app's VPN needs the driver and the Sallyport service. Open Settings to see what is missing.")]
    [InlineData(true, true, "us", null, "Waiting for jp", "Info",
        "Apps go through us now: one profile carries apps at a time. Disconnect it and this app goes through jp.")]
    [InlineData(true, true, null, "The packet driver is not installed.", "Not using the VPN", "Trouble",
        "The packet driver is not installed. Turn Use VPN off and on to try again.")]
    [InlineData(true, true, null, null, "Connecting jp…", "Busy", null)]
    public void An_app_turned_on_says_what_its_traffic_waits_for(bool connected, bool driverReady, string? carriedBy, string? failure, string status, string tone, string? detail)
    {
        var view = View(item: Zenless(on: true), connected: connected, driverReady: driverReady, carriedBy: carriedBy, failure: failure);

        Assert.Equal((status, tone, detail), (view.StatusText, view.Tone.ToString(), view.DetailText));
        // On stays on, and can always be turned off.
        Assert.True(view.VpnOn);
        Assert.True(view.Actions.Vpn is { Visible: true, Enabled: true, Reason: null });
    }

    [Fact]
    public void An_app_with_nothing_included_cannot_be_turned_on_but_one_on_can_be_turned_off()
    {
        Assert.True(View(item: Zenless(included: false)).Actions.Vpn is { Enabled: false, Reason: "Include at least one program sending all its traffic." });
        var on = View(item: Zenless(included: false, on: true));
        Assert.True(on.Actions.Vpn is { Enabled: true, Reason: null });
        Assert.Equal("No program included", on.StatusText);
    }

    [Fact]
    public void An_app_its_profile_carries_says_so_and_can_still_be_changed()
    {
        var view = View(Facts(Route(RoutePhase.Routing), phaseSince: At), item: Zenless(on: true), connected: true);

        Assert.Equal("VPN on · jp", view.StatusText);
        Assert.Equal(Tone.Up, view.Tone);
        Assert.Equal(RunPhase.Routing, view.Run!.Phase);
        Assert.Equal(At, view.Run.Since);
        Assert.True(view.VpnOn);
        // Its programs and setup change while it goes through the VPN: the route's selection follows at once (D23).
        Assert.True(view.Actions.Edit.Enabled);
        Assert.True(view.Programs[0].Actions.Toggle.Enabled);
        Assert.True(view.Programs[0].Actions.Edit.Enabled);
        Assert.Contains(view.Run.Facts, f => f.Label == "Connections through the VPN" && f.Value == "7");
    }

    [Fact]
    public void A_route_that_does_not_carry_the_app_is_not_its_own()
    {
        var view = View(Facts(Route(RoutePhase.Routing), phaseSince: At, apps: "a1b2c3d4e5f6"), item: Zenless(on: true), connected: true);

        Assert.Null(view.Run);
        Assert.Equal("Connecting jp…", view.StatusText);
    }

    [Fact]
    public void Of_several_apps_in_one_route_each_counts_its_own_programs()
    {
        var route = Route(RoutePhase.Routing, programs:
            [new(@"E:\Games\ZZZ\ZenlessZoneZero.exe", 1, 4, 300), new(@"C:\Other\other.exe", 2, 9, 900)]);
        var run = GameViews.Run(Zenless(on: true), Jp, Facts(route, phaseSince: At, apps: ["0123456789abcdef", "a1b2c3d4e5f6"]));

        Assert.Contains(new Fact("Its processes", "1"), run.Facts);
        Assert.Contains(new Fact("Connections through the VPN", "4"), run.Facts);
        Assert.Contains(new Fact("Packets sent through the VPN", "300"), run.Facts);
        // The route's own figures are every app's: not said as this one's.
        Assert.DoesNotContain(run.Facts, f => f.Label == "Packets out / in");
    }

    [Fact]
    public void A_lost_tunnel_reads_by_the_apps_own_policy()
    {
        var lost = Facts(Route(RoutePhase.Held), phaseSince: DateTimeOffset.Now);
        var held = GameViews.Run(Zenless() with { DirectAfterSeconds = 60 }, Jp, lost);
        var direct = GameViews.Run(Zenless() with { Loss = LossMode.Direct }, Jp, lost);
        var waitedOut = GameViews.Run(Zenless() with { DirectAfterSeconds = 60 }, Jp, Facts(Route(RoutePhase.Held), phaseSince: DateTimeOffset.Now.AddSeconds(-90)));

        Assert.Equal("Reconnecting — its internet is blocked meanwhile", held.StatusText);
        Assert.Matches(@"Your own connection in (59|60) s if it is not back\.", held.DetailText);
        Assert.Equal("Reconnecting — on your own connection meanwhile", direct.StatusText);
        Assert.Equal("Reconnecting — on your own connection meanwhile", waitedOut.StatusText);
    }

    [Fact]
    public void A_browser_launched_with_proxy_settings_offers_launch_and_no_switch()
    {
        var edge = new RoutedItem("a1b2c3d4e5f6", "Edge", null, At, Jp.Id, LossMode.Hold, null,
            [new("000000000003", @"C:\nowhere\msedge.exe", true, null, true, false, new ProxyLaunch(AppKind.Chromium, "", "a1b2c3d4e5f6"))]);

        var view = View(item: edge);

        Assert.Equal("Launched with proxy settings", view.MethodText);
        Assert.Equal("Ready to launch", view.StatusText);
        Assert.False(view.Actions.Vpn.Visible);
        Assert.Null(view.LossText);
        Assert.Equal("Launched with proxy settings (Chromium browser)", view.Programs[0].StatusText);
        Assert.Equal(["--proxy-server=socks5://127.0.0.1:1088"], view.Programs[0].StartupSettings);
        // Not on this machine: Launch says why, and the program says it is missing.
        Assert.True(view.Actions.Launch is { Visible: true, Enabled: false, Reason: "msedge.exe is not where it was added from." });
        Assert.True(view.Programs[0].Missing);
        Assert.Equal("", view.ProgramsText);
    }

    [Fact]
    public void A_program_with_both_methods_says_both_and_its_launch_connects_first()
    {
        var both = Zenless() with
        {
            Programs = [new("000000000001", @"E:\Games\ZZZ\ZenlessZoneZero.exe", true, null, false, true, new ProxyLaunch(AppKind.Environment, "", "x"))],
        };

        var view = View(item: both, launching: AppPhase.Connecting);

        Assert.Equal("All traffic of 1 program · 1 launched with proxy settings", view.MethodText);
        Assert.Equal("All traffic · launched with proxy settings (Proxy environment)", view.Programs[0].StatusText);
        Assert.Equal("Connecting jp before launch…", view.StatusText);
        // Its own Launch, on its row: the app routes its packets, so the app has none of its own.
        Assert.False(view.Actions.Launch.Visible);
        Assert.True(view.Programs[0].Actions.Launch is { Visible: true, Enabled: false });
        Assert.False(view.Actions.Edit.Enabled);
    }

    [Fact]
    public void An_app_without_a_profile_says_so()
    {
        var view = AppViews.Item(new ItemFacts(Zenless() with { ProfileId = null }, null, null, false, true, false, null, _ => []));

        Assert.Equal("Choose a VPN profile", view.StatusText);
        Assert.Equal("No VPN profile chosen", view.ThroughText);
        Assert.Equal("Choose a VPN profile for it first (Edit).", view.Actions.Vpn.Reason);
    }

    [Fact]
    public void Its_icon_is_the_program_chosen_else_the_one_it_starts_else_its_first()
    {
        var game = Zenless();
        Assert.Equal("000000000001", AppViews.IconProgram(game)!.Id);
        Assert.Equal("000000000002", AppViews.IconProgram(game with { IconProgramId = "000000000002" })!.Id);
        // A choice whose program is gone falls back rather than showing nothing.
        Assert.Equal("000000000001", AppViews.IconProgram(game with { IconProgramId = "ffffffffffff" })!.Id);
        // The program already its icon is not offered again; the others are, while they are there to be read.
        Assert.False(View().Programs[0].Actions.Icon.Visible);
    }

    [Fact]
    public void Coverage_is_said_in_three_outcomes()
    {
        Assert.Equal(CheckResult.Unavailable, GameViews.Coverage(null).Result);
        Assert.Equal(CheckResult.Unavailable, GameViews.Coverage(Route(RoutePhase.Routing, selected: 0)).Result);
        Assert.Equal((CheckResult.Failed, "2 connections of the app left before it was routed."),
            (GameViews.Coverage(Route(RoutePhase.Routing, late: 2)).Result, GameViews.Coverage(Route(RoutePhase.Routing, late: 2)).Text));
        // With several apps on one route, whose it was cannot be said: it is said as the route's.
        Assert.Equal("2 connections of the apps through this VPN left before they were routed.", GameViews.Coverage(Route(RoutePhase.Routing, late: 2), apps: 2).Text);
        Assert.Equal(CheckResult.Unavailable, GameViews.Coverage(Route(RoutePhase.Routing, unreadable: 1)).Result);
        Assert.Equal(CheckResult.Holds, GameViews.Coverage(Route(RoutePhase.Routing)).Result);
    }

    [Fact]
    public void A_plan_routes_every_apps_programs_and_the_strictest_rule_wins_a_shared_one()
    {
        var plan = new RoutePlan(Jp.Id,
        [
            new PlannedApp("a1b2c3d4e5f6", [new(@"C:\Launcher\launcher.exe", LossMode.Direct), new(@"E:\Games\ZZZ\ZenlessZoneZero.exe", LossMode.Direct)]),
            new PlannedApp("0123456789abcdef", [new(@"E:\Games\ZZZ\ZenlessZoneZero.exe", LossMode.Hold)]),
        ]);

        Assert.Equal(2, plan.Programs.Count);
        Assert.Equal(LossMode.Hold, plan.Programs.Single(p => p.Path.EndsWith("ZenlessZoneZero.exe", StringComparison.Ordinal)).Loss);
        Assert.True(plan.Same(plan with { Apps = [.. plan.Apps] }));
        Assert.False(plan.Same(plan with { Apps = [plan.Apps[0]] }));
        Assert.False(plan.Same(null));
    }

    [Fact]
    public void An_apps_rules_carry_its_loss_policy_for_each_program_it_sends_whole()
    {
        var game = Zenless() with { Loss = LossMode.Hold, DirectAfterSeconds = 30 };

        Assert.Equal([new ProgramRule(@"E:\Games\ZZZ\ZenlessZoneZero.exe", LossMode.Hold, 30)], game.Rules);
        // A wait means something only while held.
        Assert.Null(Assert.Single((game with { Loss = LossMode.Direct }).Rules).DirectAfterSeconds);
    }

    [Theory]
    [InlineData(LossMode.Hold, null, "If the VPN drops: its internet is blocked until it is back")]
    [InlineData(LossMode.Hold, 30, "If the VPN drops: blocked, then your own connection after 30 s")]
    [InlineData(LossMode.Direct, null, "If the VPN drops: your own connection until it is back")]
    [InlineData(LossMode.Stop, null, "If the VPN drops: its VPN turns off")]
    public void The_loss_policy_reads_as_a_sentence(LossMode mode, int? after, string text) =>
        Assert.Equal(text, GameViews.LossText(new GameChoice(Jp.Id, mode, after)));

    [Fact]
    public void A_profile_an_app_is_routed_through_still_offers_its_proxy_and_cannot_be_removed()
    {
        // One channel per profile (docs/design/window.md): its proxy and a route share it.
        var view = ClientView.Profile(new ProfileFacts(Jp, 1088, null, null, false, false, null, false, false, UsedBy: "Zenless Zone Zero"));

        Assert.True(view.Actions.Connect is { Visible: true, Enabled: true, Reason: null });
        Assert.False(view.Actions.Remove.Enabled);
        Assert.Equal("Turn off the VPN of the app that uses it first.", view.Actions.Remove.Reason);
        Assert.Contains(view.Facts, f => f.Label == "Used by" && f.Value == "Zenless Zone Zero");
    }

    [Fact]
    public void A_changed_setting_is_laid_over_the_profiles_own_and_the_rest_is_kept()
    {
        var own = new ConnectionPolicy { RetryWait = TimeSpan.FromSeconds(5), RoundsMax = 8, RandomOrder = true };

        var mine = new PolicyOverride(RoundsMax: 0, PingRestartSeconds: 20).Over(own);

        Assert.Equal(TimeSpan.FromSeconds(5), mine.RetryWait);
        Assert.Null(mine.RoundsMax);
        Assert.True(mine.RandomOrder);
        Assert.Equal((5, 20, true), (mine.Ping, mine.PingRestart, mine.KeepaliveOverridesServer));
        Assert.True(new PolicyOverride().IsEmpty);
        Assert.Equal(own, new PolicyOverride().Over(own));
        Assert.Equal("tcp", new PolicyOverride(Protocol: "tcp").Over(own).Protocol);
    }

    [Fact]
    public void A_profiles_own_protocol_is_offered_only_between_transports_it_has_and_never_by_port()
    {
        var both = GameViews.ProtocolChoices(["udp/1194", "tcp/443"])!;

        Assert.Equal(["", "udp", "tcp"], both.Select(c => c.Id));
        Assert.Equal(["Automatic", "UDP", "TCP"], both.Select(c => c.Label));
        // How the tunnel reaches the server, not what goes through it: no port in the words (the owner).
        Assert.DoesNotContain(both, c => c.Help!.Contains("1194") || c.Help.Contains("443"));
        Assert.Null(GameViews.ProtocolChoices(["udp/1194", "udp/1195"]));
    }

    [Fact]
    public void A_profile_held_to_one_protocol_says_so_among_its_details()
    {
        var held = ClientView.Profile(new ProfileFacts(Jp, null, null, null, false, false, null, false, false, Protocol: "tcp"));
        var free = ClientView.Profile(new ProfileFacts(Jp, null, null, null, false, false, null, false, false));

        Assert.Contains(new Fact("VPN protocol", "TCP"), held.Facts);
        Assert.DoesNotContain(free.Facts, f => f.Label == "VPN protocol");
    }

    [Fact]
    public void Settings_choices_go_over_every_profiles_own_and_a_profiles_changes_over_them()
    {
        // Its own folder: the shared one is removed by another class when it is empty, and refuses new ones meanwhile.
        var file = Path.Combine(Path.GetTempPath(), "sallyport-settings-" + Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            var settings = new ClientSettings(file) { Connection = new PolicyOverride(RoundsMax: 5, Protocol: "tcp") };
            var own = new ConnectionPolicy { RetryWait = TimeSpan.FromSeconds(7) };

            var plain = settings.PolicyFor("a", own, ["udp", "tcp"]);
            Assert.Equal(("tcp", 5, TimeSpan.FromSeconds(7)), (plain.Protocol, plain.RoundsMax, plain.RetryWait));
            // A profile without TCP servers keeps all of its own rather than none.
            Assert.Null(settings.PolicyFor("b", own, ["udp"]).Protocol);
            Assert.Null(settings.ProtocolFor("b", ["udp"]));

            // One profile let back to all of its servers, and to keep trying, while Settings hold the rest to TCP.
            settings.PutPolicy("a", new PolicyOverride(RoundsMax: 0, Protocol: PolicyOverride.Any));
            var mine = settings.PolicyFor("a", own, ["udp", "tcp"]);
            Assert.Equal((null, null), (mine.Protocol, mine.RoundsMax));
            Assert.Null(settings.ProtocolFor("a", ["udp", "tcp"]));
            Assert.Equal("tcp", settings.ProtocolFor("c", ["udp", "tcp"]));

            // Kept across a restart.
            Assert.Equal(new PolicyOverride(RoundsMax: 5, Protocol: "tcp"), new ClientSettings(file).Connection);
        }
        finally { Directory.Delete(Path.GetDirectoryName(file)!, recursive: true); }
    }

    [Fact]
    public void Settings_offer_keep_trying_and_a_few_counts_and_show_one_set_otherwise()
    {
        Assert.Equal(["0", "3", "5", "10"], GameViews.RetryChoices(0).Select(c => c.Id));
        Assert.Equal("Keep trying", GameViews.RetryChoices(0)[0].Label);
        Assert.Equal(["0", "3", "5", "7", "10"], GameViews.RetryChoices(7).Select(c => c.Id));
        Assert.Equal("Stop after 7 failed rounds", GameViews.RetryChoices(7)[3].Label);
    }

    [Fact]
    public void The_editor_starts_from_keep_trying_with_a_number_ready_and_the_type_as_saved()
    {
        Assert.True(GameViews.PolicyValuesOf(new ConnectionPolicy()) is { KeepTrying: true, RoundsMaxText: "5", Protocol: "" });
        Assert.True(GameViews.PolicyValuesOf(new ConnectionPolicy { RoundsMax = 3, Protocol = "udp" }) is { KeepTrying: false, RoundsMaxText: "3", Protocol: "udp" });
    }
}
