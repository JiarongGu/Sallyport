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

    private static RoutedItem Zenless(bool included = true) => new("0123456789abcdef", "Zenless Zone Zero", @"E:\Games\ZZZ", At, Jp.Id, LossMode.Hold, null,
        [new("000000000001", @"E:\Games\ZZZ\ZenlessZoneZero.exe", included, null, false, true, null),
         new("000000000002", @"E:\Games\ZZZ\unins000.exe", false, "installs or removes software", false, true, null)]);

    private static RouteState Route(RoutePhase phase, int selected = 2, long late = 0, int unreadable = 0) =>
        new(phase, phase == RoutePhase.Routing ? 1 : 0, 100, 120, 5000, 3, 0, 0, late, 0, 0, 7, selected, unreadable);

    private static RunFacts Facts(RouteState? route, string? ended = null, LossPolicy? loss = null, DateTimeOffset? phaseSince = null) =>
        new("0123456789abcdef", Jp.Id, loss ?? new LossPolicy(), At, route is null && ended is null, null, route, phaseSince, ended, null);

    private static AppItemView View(RunFacts? run = null, StoredProfile? profile = null, bool another = false, bool driverReady = true,
        RoutedItem? item = null, AppPhase? launching = null) =>
        AppViews.Item(new ItemFacts(item ?? Zenless(), profile ?? Jp, run, another, driverReady, false, launching, _ => ["--proxy-server=socks5://127.0.0.1:1088"]));

    [Fact]
    public void A_folder_ready_to_route_says_what_it_routes()
    {
        var view = View();

        Assert.Equal("Not routing", view.StatusText);
        Assert.Equal("Through jp", view.ThroughText);
        Assert.Equal("All traffic of 1 program", view.MethodText);
        Assert.Equal("1 program included of 2.", view.ProgramsText);
        Assert.True(view.Actions.Route is { Visible: true, Enabled: true, Label: "Route" });
        Assert.False(view.Actions.Stop.Visible);
        Assert.Equal("Left out — installs or removes software", view.Programs[1].StatusText);
        Assert.Equal("All traffic", view.Programs[0].StatusText);
        // The folder's program named most like the folder is what Launch starts, until the person chooses.
        Assert.True(view.Programs[0].LaunchTarget);
        Assert.Equal("Launch starts ZenlessZoneZero.exe", view.LaunchText);
    }

    [Theory]
    [InlineData(true, true, "Another app is being routed. Stop it first.")]
    [InlineData(false, false, "Packet routing needs the driver and the Sallyport service. Open Settings to see what is missing.")]
    public void An_app_that_cannot_be_routed_says_why(bool another, bool driverReady, string reason)
    {
        var route = View(another: another, driverReady: driverReady).Actions.Route;

        Assert.False(route.Enabled);
        Assert.Equal(reason, route.Reason);
    }

    [Fact]
    public void An_app_with_nothing_included_cannot_be_routed() =>
        Assert.Equal("Include at least one program sending all its traffic.", View(item: Zenless(included: false)).Actions.Route.Reason);

    [Fact]
    public void A_routed_app_says_so_and_holds_its_settings_still()
    {
        var view = View(Facts(Route(RoutePhase.Routing), phaseSince: At));

        Assert.Equal("Routed through jp", view.StatusText);
        Assert.Equal(Tone.Up, view.Tone);
        Assert.Equal(RunPhase.Routing, view.Run!.Phase);
        Assert.Equal(At, view.Run.Since);
        Assert.True(view.Actions.Stop.Visible);
        Assert.False(view.Actions.Route.Visible);
        Assert.False(view.Actions.Edit.Enabled);
        Assert.False(view.Programs[0].Actions.Toggle.Enabled);
        Assert.False(view.Programs[0].Actions.Edit.Enabled);
        Assert.Contains(view.Run.Facts, f => f.Label == "Connections routed" && f.Value == "7");
    }

    [Fact]
    public void A_lost_tunnel_reads_by_the_loss_policy()
    {
        var held = GameViews.Run(Zenless().AsGame(), Jp, Facts(Route(RoutePhase.Held), loss: new LossPolicy(LossMode.Hold, TimeSpan.FromSeconds(60)),
            phaseSince: DateTimeOffset.Now));
        var direct = GameViews.Run(Zenless().AsGame(), Jp, Facts(Route(RoutePhase.Direct)));

        Assert.Equal("Reconnecting — the game's traffic is held", held.StatusText);
        Assert.Matches(@"Your own connection in (59|60) s if it is not back\.", held.DetailText);
        Assert.Equal("Reconnecting — the game uses your own connection", direct.StatusText);
    }

    [Fact]
    public void An_ended_route_keeps_its_reason_and_offers_to_route_again()
    {
        var view = View(Facts(Route(RoutePhase.Stopped), ended: "The network stopped answering, so routing stopped to let it recover."));

        Assert.Equal("Routing stopped", view.StatusText);
        Assert.Equal(Tone.Trouble, view.Tone);
        Assert.Equal("The network stopped answering, so routing stopped to let it recover.", view.Run!.DetailText);
        Assert.True(view.Actions.Route is { Visible: true, Label: "Route again" });
    }

    [Fact]
    public void A_route_the_person_stopped_is_not_trouble()
    {
        var view = View(Facts(Route(RoutePhase.Stopped), ended: GameRunner.StoppedByPerson));

        Assert.Equal(("Stopped", Tone.Off), (view.StatusText, view.Tone));
        Assert.Null(view.Run!.DetailText);
    }

    [Fact]
    public void A_browser_launched_with_proxy_settings_offers_launch_and_no_route()
    {
        var edge = new RoutedItem("a1b2c3d4e5f6", "Edge", null, At, Jp.Id, LossMode.Hold, null,
            [new("000000000003", @"C:\nowhere\msedge.exe", true, null, true, false, new ProxyLaunch(AppKind.Chromium, "", "a1b2c3d4e5f6"))]);

        var view = View(item: edge);

        Assert.Equal("Launched with proxy settings", view.MethodText);
        Assert.Equal("Ready to launch", view.StatusText);
        Assert.False(view.Actions.Route.Visible);
        Assert.Null(view.LossText);
        Assert.Equal("Launched with proxy settings (chromium browser)", view.Programs[0].StatusText);
        Assert.Equal(["--proxy-server=socks5://127.0.0.1:1088"], view.Programs[0].StartupSettings);
        // Not on this machine: Launch says why, and the program says it is missing.
        Assert.True(view.Actions.Launch is { Visible: true, Enabled: false, Reason: "msedge.exe is not where it was added from." });
        Assert.True(view.Programs[0].Missing);
        Assert.Equal("", view.ProgramsText);
    }

    [Fact]
    public void A_program_with_both_methods_says_both_and_a_launch_routes_first()
    {
        var both = Zenless() with
        {
            Programs = [new("000000000001", @"E:\Games\ZZZ\ZenlessZoneZero.exe", true, null, false, true, new ProxyLaunch(AppKind.Environment, "", "x"))],
        };

        var view = View(item: both, launching: AppPhase.Connecting);

        Assert.Equal("All traffic of 1 program · 1 launched with proxy settings", view.MethodText);
        Assert.Equal("All traffic · launched with proxy settings (proxy environment)", view.Programs[0].StatusText);
        Assert.Equal("Routing it before launch…", view.StatusText);
        Assert.True(view.Actions.Launch.Busy);
        Assert.False(view.Actions.Edit.Enabled);
    }

    [Fact]
    public void An_app_without_a_profile_says_so()
    {
        var view = AppViews.Item(new ItemFacts(Zenless() with { ProfileId = null }, null, null, false, true, false, null, _ => []));

        Assert.Equal("Choose a VPN profile", view.StatusText);
        Assert.Equal("No VPN profile chosen", view.ThroughText);
        Assert.Equal("Choose a VPN profile for it first (Edit).", view.Actions.Route.Reason);
    }

    [Fact]
    public void Coverage_is_said_in_three_outcomes()
    {
        Assert.Equal(CheckResult.Unavailable, GameViews.Coverage(null).Result);
        Assert.Equal(CheckResult.Unavailable, GameViews.Coverage(Route(RoutePhase.Routing, selected: 0)).Result);
        Assert.Equal((CheckResult.Failed, "2 connections of the game left before it was routed."),
            (GameViews.Coverage(Route(RoutePhase.Routing, late: 2)).Result, GameViews.Coverage(Route(RoutePhase.Routing, late: 2)).Text));
        Assert.Equal(CheckResult.Unavailable, GameViews.Coverage(Route(RoutePhase.Routing, unreadable: 1)).Result);
        Assert.Equal(CheckResult.Holds, GameViews.Coverage(Route(RoutePhase.Routing)).Result);
    }

    [Theory]
    [InlineData(LossMode.Hold, null, "If the VPN drops: the game's traffic is held")]
    [InlineData(LossMode.Hold, 30, "If the VPN drops: held, then your own connection after 30 s")]
    [InlineData(LossMode.Direct, null, "If the VPN drops: your own connection until it is back")]
    [InlineData(LossMode.Stop, null, "If the VPN drops: routing stops")]
    public void The_loss_policy_reads_as_a_sentence(LossMode mode, int? after, string text) =>
        Assert.Equal(text, GameViews.LossText(new GameChoice(Jp.Id, mode, after)));

    [Fact]
    public void A_profile_an_app_is_routed_through_still_offers_its_proxy_and_cannot_be_removed()
    {
        // One channel per profile (docs/design/window.md): its proxy and a route share it.
        var view = ClientView.Profile(new ProfileFacts(Jp, 1088, null, null, false, false, null, false, false, UsedBy: "Zenless Zone Zero"));

        Assert.True(view.Actions.Connect is { Visible: true, Enabled: true, Reason: null });
        Assert.False(view.Actions.Remove.Enabled);
        Assert.Equal("Stop routing what goes through it first.", view.Actions.Remove.Reason);
        Assert.Contains(view.Facts, f => f.Label == "Routing through it" && f.Value == "Zenless Zone Zero");
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
    }
}
