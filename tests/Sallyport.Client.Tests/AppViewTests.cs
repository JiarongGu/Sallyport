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

    private static readonly StoredProfile Us = new("0b1c2d3e4f5a6b7c", "us", ["udp/1194"], At, null);

    // Zenless in jp's section, switched on or off there; or in the sections given.
    private static RoutedItem Zenless(bool included = true, bool on = false, params AppUse[] uses) => new("0123456789abcdef", "Zenless Zone Zero", @"E:\Games\ZZZ", At, null, LossMode.Hold, null,
        [new("000000000001", @"E:\Games\ZZZ\ZenlessZoneZero.exe", included, null, false, true, null),
         new("000000000002", @"E:\Games\ZZZ\unins000.exe", false, "installs or removes software", false, true, null)], Uses: uses.Length > 0 ? uses : [new AppUse(Jp.Id, on)]);

    private static RouteState Route(RoutePhase phase, int selected = 2, long late = 0, int unreadable = 0, IReadOnlyList<ProgramRouted>? programs = null) =>
        new(phase, phase == RoutePhase.Routing ? 1 : 0, 100, 120, 5000, 3, 0, 0, late, 0, 0, 7, selected, unreadable, programs);

    // A profile's lane carrying the apps named (Zenless when none is), each since At.
    private static LaneFacts Lane(StoredProfile profile, RoutePhase phase, DateTimeOffset? since = null, params string[] apps) =>
        new(profile.Id, profile == Jp ? 0 : 1, (apps.Length == 0 ? ["0123456789abcdef"] : apps).ToDictionary(a => a, _ => At), null, phase, since);

    private static RunFacts Facts(RouteState? route, params LaneFacts[] lanes) => new(lanes, At, route is null, route, null, null);

    private static ItemFacts F(RoutedItem? item = null, RunFacts? run = null, string[]? connected = null, bool driverReady = true, AppPhase? launching = null,
        string? failure = null) =>
        new(item ?? Zenless(), [Jp, Us], run, id => connected?.Contains(id) == true, id => id == Jp.Id ? failure : null, driverReady, false, launching,
            _ => ["--proxy-server=socks5://127.0.0.1:1088"]);

    private static AppItemView View(RoutedItem? item = null, RunFacts? run = null, string[]? connected = null, bool driverReady = true, AppPhase? launching = null,
        string? failure = null) => AppViews.Item(F(item, run, connected, driverReady, launching, failure));

    [Fact]
    public void Its_page_says_its_setup_and_lists_every_vpn_with_a_switch_of_its_own()
    {
        var view = View();

        // No Launch for what is routed by packets, so nothing said of what Launch starts; its VPNs are a section of their own.
        Assert.Equal([new Fact("Uses the VPN", "All traffic of 1 program"), new Fact("If the VPN drops", GameViews.LossShort(new GameChoice(null, LossMode.Hold, null)))],
            view.Setup!);
        // The one it is in first, then every other profile it could be put in (D24).
        Assert.Equal(["jp", "us"], view.Vpns!.Select(v => v.Name));
        Assert.True(view.Vpns![0] is { Member: true, On: false, StatusText: "Off" });
        Assert.True(view.Vpns[1] is { Member: false, On: false, StatusText: "Not used" });
        Assert.All(view.Vpns, v => Assert.True(v.Vpn is { Visible: true, Enabled: true, Label: "Use VPN" }));
    }

    [Fact]
    public void An_app_switched_off_says_so_and_names_the_vpns_it_is_in()
    {
        var view = View();

        Assert.Equal(("VPN off", Tone.Off), (view.StatusText, view.Tone));
        Assert.Equal("Through jp", view.ThroughText);
        Assert.Equal("Through jp, us", View(Zenless(uses: [new AppUse(Jp.Id), new AppUse(Us.Id, false)])).ThroughText);
        Assert.Equal("All traffic of 1 program", view.MethodText);
        Assert.Equal("Using the VPN: 1 program of 2.", view.ProgramsText);
        Assert.Equal("Left out — installs or removes software", view.Programs[1].StatusText);
        Assert.Equal("All traffic", view.Programs[0].StatusText);
        // A game is started as the person always starts it: Sallyport offers no Launch for what it routes by packets.
        Assert.False(view.Actions.Launch.Visible);
        Assert.False(view.Programs[0].LaunchTarget);
        Assert.Null(view.LaunchText);
    }

    [Fact]
    public void An_app_not_where_it_was_added_from_says_where_it_was_and_offers_Locate()
    {
        var view = AppViews.Item(F(Zenless(on: true)) with { Here = false });
        var here = View(Zenless(on: true));

        Assert.Equal(("Not found on this computer", "Trouble"), (view.StatusText, view.Tone.ToString()));
        Assert.Equal(@"It was in E:\Games\ZZZ. Choose Locate… and show Sallyport where it is now.", view.DetailText);
        Assert.Equal("Locate…", view.Actions.Locate!.Label);
        Assert.Same(UiAction.Hidden, here.Actions.Locate);
    }

    [Fact]
    public void An_app_in_no_vpn_says_where_to_put_it()
    {
        var view = View(Zenless() with { Uses = [] });

        Assert.Equal(("In no VPN yet", "In no VPN yet"), (view.StatusText, view.ThroughText));
        Assert.Equal("Turn on Use VPN for a profile below, or add it to a VPN on Overview.", view.DetailText);
        Assert.All(view.Vpns!, v => Assert.False(v.Member));
    }

    [Theory]
    [InlineData(false, true, null, "Waiting for jp", "Off", "Connect jp and it goes through the VPN.")]
    [InlineData(true, false, null, "Cannot use the VPN", "Trouble",
        "Turning on an app's VPN needs the driver and the Sallyport service. Open Settings to see what is missing.")]
    [InlineData(true, true, "The packet driver is not installed.", "Not using the VPN", "Trouble",
        "The packet driver is not installed. Turn it off and on to try again.")]
    [InlineData(true, true, null, "Connecting jp…", "Busy", null)]
    public void An_app_switched_on_in_a_section_says_what_its_traffic_waits_for(bool connected, bool driverReady, string? failure, string status, string tone, string? detail)
    {
        var facts = F(Zenless(on: true), connected: connected ? [Jp.Id] : [], driverReady: driverReady, failure: failure);

        var row = AppViews.InSection(facts, Jp, forApp: true);

        Assert.Equal((status, tone, detail), (row.StatusText, row.Tone.ToString(), row.DetailText));
        // On stays on, and can always be turned off; the card says the same as the row that waits.
        Assert.True(row is { On: true, Vpn: { Visible: true, Enabled: true, Reason: null } });
        Assert.Equal(status, AppViews.Item(facts).StatusText);
    }

    [Fact]
    public void An_app_on_in_two_connected_vpns_goes_through_the_one_that_carries_it_and_the_other_says_so()
    {
        var both = Zenless(uses: [new AppUse(Jp.Id), new AppUse(Us.Id)]);
        var run = Facts(Route(RoutePhase.Routing), Lane(Us, RoutePhase.Routing, At));
        var facts = F(both, run, connected: [Jp.Id, Us.Id]);

        var jp = AppViews.InSection(facts, Jp, forApp: false);
        var us = AppViews.InSection(facts, Us, forApp: false);

        Assert.Equal(("Through us now", Tone.Info, "It goes through jp when us is disconnected."), (jp.StatusText, jp.Tone, jp.DetailText));
        Assert.Null(jp.Run);
        Assert.Equal("VPN on · us", us.StatusText);
        Assert.NotNull(us.Run);
        // On the Overview, a section's app is the app, by name and icon; the tunnel's facts are the section's.
        Assert.Equal("Zenless Zone Zero", us.Name);
        Assert.DoesNotContain(us.Run!.Facts, f => f.Label is "Connected over" or "Tunnel address");
        Assert.True(us.Remove is { Visible: true, Label: "Remove from us" });
        Assert.Equal("VPN on · us", AppViews.Item(facts).StatusText);
    }

    [Fact]
    public void An_app_with_nothing_included_cannot_be_turned_on_but_one_on_can_be_turned_off()
    {
        Assert.True(AppViews.InSection(F(Zenless(included: false)), Jp, true).Vpn is { Enabled: false, Reason: "Include at least one program sending all its traffic." });
        Assert.True(AppViews.InSection(F(Zenless(included: false, on: true)), Jp, true).Vpn is { Enabled: true, Reason: null });
        Assert.Equal("No program included", View(Zenless(included: false, on: true)).StatusText);
    }

    [Fact]
    public void An_app_its_vpn_carries_says_so_and_can_still_be_changed()
    {
        var view = View(Zenless(on: true), Facts(Route(RoutePhase.Routing), Lane(Jp, RoutePhase.Routing, At)), connected: [Jp.Id]);

        Assert.Equal("VPN on · jp", view.StatusText);
        Assert.Equal(Tone.Up, view.Tone);
        Assert.Equal(RunPhase.Routing, view.Run!.Phase);
        Assert.Equal(At, view.Run.Since);
        // Its programs and setup change while it goes through the VPN: the route's selection follows at once (D23).
        Assert.True(view.Actions.Edit.Enabled);
        Assert.True(view.Programs[0].Actions.Toggle.Enabled);
        Assert.True(view.Programs[0].Actions.Edit.Enabled);
        Assert.Contains(view.Run.Facts, f => f.Label == "Connections through the VPN" && f.Value == "7");
    }

    [Fact]
    public void A_lane_that_does_not_carry_the_app_is_not_its_own()
    {
        var view = View(Zenless(on: true), Facts(Route(RoutePhase.Routing), Lane(Jp, RoutePhase.Routing, At, "a1b2c3d4e5f6")), connected: [Jp.Id]);

        Assert.Null(view.Run);
        Assert.Equal("Connecting jp…", view.StatusText);
    }

    [Fact]
    public void Of_several_apps_in_one_route_each_counts_its_own_programs()
    {
        var route = Route(RoutePhase.Routing, programs:
            [new(@"E:\Games\ZZZ\ZenlessZoneZero.exe", 1, 4, 300), new(@"C:\Other\other.exe", 2, 9, 900)]);
        var lane = Lane(Jp, RoutePhase.Routing, At, "0123456789abcdef", "a1b2c3d4e5f6");
        var run = GameViews.Run(Zenless(on: true), Jp, Facts(route, lane), lane);

        Assert.Contains(new Fact("Its processes", "1"), run.Facts);
        Assert.Contains(new Fact("Connections through the VPN", "4"), run.Facts);
        Assert.Contains(new Fact("Packets sent through the VPN", "300"), run.Facts);
        // The route's own figures are every app's: not said as this one's.
        Assert.DoesNotContain(run.Facts, f => f.Label == "Packets out / in");
    }

    [Fact]
    public void A_lost_tunnel_reads_by_the_apps_own_policy_on_its_own_lane()
    {
        var lost = Lane(Jp, RoutePhase.Held, DateTimeOffset.Now);
        var held = GameViews.Run(Zenless() with { DirectAfterSeconds = 60 }, Jp, Facts(Route(RoutePhase.Routing), lost), lost);
        var direct = GameViews.Run(Zenless() with { Loss = LossMode.Direct }, Jp, Facts(Route(RoutePhase.Routing), lost), lost);
        var longAgo = Lane(Jp, RoutePhase.Held, DateTimeOffset.Now.AddSeconds(-90));
        var waitedOut = GameViews.Run(Zenless() with { DirectAfterSeconds = 60 }, Jp, Facts(Route(RoutePhase.Routing), longAgo), longAgo);

        // The route's first lane is up; this lane is the one lost, and its own phase is what the app reads.
        Assert.Equal("Reconnecting — its internet is blocked meanwhile", held.StatusText);
        Assert.Matches(@"Your own connection in (59|60) s if it is not back\.", held.DetailText);
        Assert.Equal("Reconnecting — on your own connection meanwhile", direct.StatusText);
        Assert.Equal("Reconnecting — on your own connection meanwhile", waitedOut.StatusText);
    }

    [Fact]
    public void A_browser_launched_with_proxy_settings_is_launched_through_its_section_and_has_no_switch()
    {
        var edge = new RoutedItem("a1b2c3d4e5f6", "Edge", null, At, null, LossMode.Hold, null,
            [new("000000000003", @"C:\nowhere\msedge.exe", true, null, true, false, new ProxyLaunch(AppKind.Chromium, "", "a1b2c3d4e5f6"))], Uses: [new AppUse(Jp.Id)]);

        var view = View(edge);
        var row = AppViews.InSection(F(edge), Jp, forApp: false);

        Assert.Equal("Launched with proxy settings", view.MethodText);
        Assert.Equal("Ready to launch", view.StatusText);
        Assert.False(row.Vpn.Visible);
        Assert.True(row.Launch is { Visible: true, Enabled: false, Reason: "msedge.exe is not where it was added from." });
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

        var view = View(both, launching: AppPhase.Connecting);

        Assert.Equal("All traffic of 1 program · 1 launched with proxy settings", view.MethodText);
        Assert.Equal("All traffic · launched with proxy settings (Proxy environment)", view.Programs[0].StatusText);
        Assert.Equal("Connecting before launch…", view.StatusText);
        // Its own Launch, on its row: the app routes its packets, so the app has none of its own.
        Assert.False(view.Actions.Launch.Visible);
        Assert.True(view.Programs[0].Actions.Launch is { Visible: true, Enabled: false });
        Assert.False(view.Actions.Edit.Enabled);
    }

    [Fact]
    public void An_apps_sections_are_kept_switched_and_taken_out_and_an_earlier_clients_one_profile_is_read_as_one()
    {
        var earlier = new RoutedItem("0123456789abcdef", "Zenless", null, At, Jp.Id, LossMode.Hold, null, [], UseVpn: true);
        Assert.Equal([new AppUse(Jp.Id, true)], earlier.Sections);

        var both = earlier.WithUse(Us.Id, null);
        Assert.Equal([new AppUse(Jp.Id, true), new AppUse(Us.Id, true)], both.Sections);
        // Written as sections from then on, never as the one profile.
        Assert.Null(both.ProfileId);
        Assert.Equal([new AppUse(Jp.Id, false), new AppUse(Us.Id, true)], both.WithUse(Jp.Id, false).Sections);
        Assert.Equal([new AppUse(Us.Id, true)], both.WithUse(Jp.Id, null, remove: true).Sections);
        // Launched through the first of its sections that is connected, else its first.
        Assert.Equal(Us.Id, both.LaunchProfile(id => id == Us.Id));
        Assert.Equal(Jp.Id, both.LaunchProfile(_ => false));
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
    public void A_plan_puts_each_vpns_programs_in_its_lane_and_the_strictest_rule_wins_a_shared_one()
    {
        var plan = new RoutePlan(
        [
            new PlannedLane(Us.Id, [new PlannedApp("a1b2c3d4e5f6", [new(@"C:\Launcher\launcher.exe", LossMode.Direct), new(@"E:\Games\ZZZ\ZenlessZoneZero.exe", LossMode.Direct)])]),
            new PlannedLane(Jp.Id, [new PlannedApp("0123456789abcdef", [new(@"E:\Games\ZZZ\ZenlessZoneZero.exe", LossMode.Hold)])]),
        ]);

        var programs = plan.Programs(id => id == Us.Id ? 0 : 3);

        Assert.Equal(2, programs.Count);
        Assert.True(programs.Single(p => p.Path.EndsWith("launcher.exe", StringComparison.Ordinal)) is { Lane: 0, Loss: LossMode.Direct });
        Assert.True(programs.Single(p => p.Path.EndsWith("ZenlessZoneZero.exe", StringComparison.Ordinal)) is { Lane: 3, Loss: LossMode.Hold });
        Assert.True(plan.Same(plan with { Lanes = [.. plan.Lanes] }));
        Assert.False(plan.Same(plan with { Lanes = [plan.Lanes[0]] }));
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

    [Fact]
    public void A_lanes_generation_crosses_the_pipe_with_its_lane_and_the_first_lanes_as_the_sessions_own()
    {
        Assert.Equal(7, GameRoute.Wire(0, 7));
        Assert.Equal((3, 7), GameRoute.FromWire(GameRoute.Wire(3, 7)));
        Assert.Equal((0, 7), GameRoute.FromWire(7));
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
        Assert.Equal("Disconnect it on Overview first.", view.Actions.Remove.Reason);
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
    public void A_profiles_row_says_how_it_connects_and_whether_that_is_Settings_or_its_own_never_its_servers_ports()
    {
        ProfileView With(ProfileChoices choices) => ClientView.Profile(new ProfileFacts(Jp, null, null, null, false, false, null, false, false, Choices: choices));
        var settings = With(new(["udp", "tcp"], null, false, 0, false, false));
        var own = With(new(["udp", "tcp"], "tcp", true, 5, true, true));
        var udpOnly = With(new(["udp"], null, false, 3, false, false));

        Assert.Equal([new Fact("VPN protocol", "Automatic (as in Settings)"), new Fact("If it cannot connect", "Keep trying (as in Settings)"),
            new Fact("Imported", L.Date(At.ToLocalTime()))], settings.Details);
        Assert.Equal([new Fact("VPN protocol", "TCP (this profile only)"), new Fact("If it cannot connect", "Stop after 5 failed rounds (this profile only)"),
            new Fact("More settings", "Changed for this profile")], own.Details!.SkipLast(1));
        Assert.Equal(new Fact("VPN protocol", "UDP only"), udpOnly.Details![0]);
        Assert.All(new[] { settings, own, udpOnly }, v => Assert.DoesNotContain(v.Facts.Concat(v.Details!), f => f.Value.Contains("1194") || f.Value.Contains("443")));
    }

    [Fact]
    public void A_profiles_choices_are_its_own_where_it_changed_them_and_Settings_elsewhere()
    {
        // Its own folder: the shared one is removed by another class when it is empty, and refuses new ones meanwhile.
        var file = Path.Combine(Path.GetTempPath(), "sallyport-choices-" + Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            var settings = new ClientSettings(file) { Connection = new PolicyOverride(RoundsMax: 5, Protocol: "tcp") };
            settings.PutPolicy("a", new PolicyOverride(Protocol: PolicyOverride.Any, PingRestartSeconds: 20));

            Assert.Equal((null, true, 5, false, true), Of(settings.ChoicesFor("a", ["udp", "tcp"])));
            Assert.Equal(("tcp", false, 5, false, false), Of(settings.ChoicesFor("b", ["udp", "tcp"])));
            // Settings' TCP is no choice for a profile with no TCP server: its servers as they are.
            Assert.Equal((null, false, 5, false, false), Of(settings.ChoicesFor("b", ["udp"])));
        }
        finally { if (Directory.Exists(Path.GetDirectoryName(file))) Directory.Delete(Path.GetDirectoryName(file)!, recursive: true); }

        static (string?, bool, int, bool, bool) Of(ProfileChoices c) => (c.Protocol, c.ProtocolOwn, c.RoundsMax, c.RoundsOwn, c.OtherOwn);
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

    [Fact]
    public void A_silence_limit_that_only_stands_in_for_the_servers_starts_empty()
    {
        // Saved as shown, a 60 that stood in for a server setting none would become one that outranks the server's.
        Assert.Equal("", GameViews.PolicyValuesOf(new ConnectionPolicy { PingRestart = 60 }).PingRestartText);
        Assert.Equal("90", GameViews.PolicyValuesOf(new ConnectionPolicy { PingRestart = 90, KeepaliveOverridesServer = true }).PingRestartText);
    }
}
