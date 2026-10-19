using Sallyport.Profile;
using Sallyport.Runtime;
using Sallyport.Windows.Routing;
using Sallyport.Windows.Service;

namespace Sallyport.Client.Tests;

/// <summary>What the Games page shows, as the host decides it (docs/design/product.md §5).</summary>
public class GameViewTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    private static readonly StoredProfile Jp = new("ad50a53b0da826ad", "jp", ["udp/1194", "tcp/443"], At, null);

    private static GameProfile Zenless(bool included = true) => new("0123456789abcdef", "Zenless Zone Zero", @"E:\Games\ZZZ",
        [new(@"E:\Games\ZZZ\ZenlessZoneZero.exe", included), new(@"E:\Games\ZZZ\unins000.exe", false, "installs or removes software")], At);

    private static RouteState Route(RoutePhase phase, int selected = 2, long late = 0, int unreadable = 0) =>
        new(phase, phase == RoutePhase.Routing ? 1 : 0, 100, 120, 5000, 3, 0, 0, late, 0, 0, 7, selected, unreadable);

    private static RunFacts Facts(RouteState? route, string? ended = null, LossPolicy? loss = null, DateTimeOffset? phaseSince = null) =>
        new("0123456789abcdef", Jp.Id, loss ?? new LossPolicy(), At, route is null && ended is null, null, route, phaseSince, ended, null);

    private static GameView View(RunFacts? run = null, StoredProfile? profile = null, bool another = false, bool proxied = false, bool driverReady = true,
        GameProfile? game = null) =>
        GameViews.Game(game ?? Zenless(), new GameChoice(Jp.Id, LossMode.Hold, null), profile ?? Jp, run, another, proxied, driverReady, busy: false);

    [Fact]
    public void A_game_ready_to_run_says_what_it_routes()
    {
        var view = View();

        Assert.Equal("Not running", view.StatusText);
        Assert.Equal("Through jp", view.ThroughText);
        Assert.Equal("1 program routed of 2.", view.ProgramsText);
        Assert.True(view.Actions.Run is { Visible: true, Enabled: true, Label: "Run" });
        Assert.False(view.Actions.Stop.Visible);
        Assert.Equal("Left out — installs or removes software", $"Left out — {view.Programs[1].Reason}");
    }

    [Theory]
    [InlineData(true, false, true, "Another game is being routed. Stop it first.")]
    [InlineData(false, true, true, "jp is connected as a proxy. Disconnect it first: one profile serves a game or a proxy at a time.")]
    [InlineData(false, false, false, "Game routing is not ready on this computer (see above).")]
    public void A_game_that_cannot_run_says_why(bool another, bool proxied, bool driverReady, string reason)
    {
        var run = View(another: another, proxied: proxied, driverReady: driverReady).Actions.Run;

        Assert.False(run.Enabled);
        Assert.Equal(reason, run.Reason);
    }

    [Fact]
    public void A_game_with_nothing_included_cannot_run()
    {
        Assert.Equal("Include at least one of its programs.", View(game: Zenless(included: false)).Actions.Run.Reason);
    }

    [Fact]
    public void A_routing_game_says_so_and_holds_its_settings_still()
    {
        var view = View(Facts(Route(RoutePhase.Routing), phaseSince: At));

        Assert.Equal("Routed through jp", view.StatusText);
        Assert.Equal(Tone.Up, view.Tone);
        Assert.Equal(RunPhase.Routing, view.Run!.Phase);
        Assert.Equal(At, view.Run.Since);
        Assert.True(view.Actions.Stop.Visible);
        Assert.False(view.Actions.Run.Visible);
        Assert.False(view.Actions.Edit.Enabled);
        Assert.False(view.Programs[0].Toggle.Enabled);
        Assert.Contains(view.Run.Facts, f => f.Label == "Connections routed" && f.Value == "7");
    }

    [Fact]
    public void A_lost_tunnel_reads_by_the_loss_policy()
    {
        var held = GameViews.Run(Zenless(), Jp, Facts(Route(RoutePhase.Held), loss: new LossPolicy(LossMode.Hold, TimeSpan.FromSeconds(60)),
            phaseSince: DateTimeOffset.Now));
        var direct = GameViews.Run(Zenless(), Jp, Facts(Route(RoutePhase.Direct)));

        Assert.Equal("Reconnecting — the game's traffic is held", held.StatusText);
        Assert.Matches(@"Your own connection in (59|60) s if it is not back\.", held.DetailText);
        Assert.Equal("Reconnecting — the game uses your own connection", direct.StatusText);
    }

    [Fact]
    public void An_ended_run_keeps_its_reason_and_offers_to_run_again()
    {
        var view = View(Facts(Route(RoutePhase.Stopped), ended: "The network stopped answering, so routing stopped to let it recover."));

        Assert.Equal("Routing stopped", view.StatusText);
        Assert.Equal(Tone.Trouble, view.Tone);
        Assert.Equal("The network stopped answering, so routing stopped to let it recover.", view.Run!.DetailText);
        Assert.True(view.Actions.Run is { Visible: true, Label: "Run again" });
    }

    [Fact]
    public void A_run_the_person_stopped_is_not_trouble()
    {
        var view = View(Facts(Route(RoutePhase.Stopped), ended: GameRunner.StoppedByPerson));

        Assert.Equal(("Stopped", Tone.Off), (view.StatusText, view.Tone));
        Assert.Null(view.Run!.DetailText);
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
    public void A_profile_in_use_by_a_game_offers_no_proxy_and_cannot_be_removed()
    {
        var view = ClientView.Profile(new ProfileFacts(Jp, 1088, null, null, false, false, null, false, false, UsedBy: "Zenless Zone Zero"));

        Assert.False(view.Actions.Connect.Enabled);
        Assert.Equal("Zenless Zone Zero is being routed through it. One profile serves a game or a proxy at a time.", view.Actions.Connect.Reason);
        Assert.False(view.Actions.Remove.Enabled);
        Assert.Contains(view.Facts, f => f.Label == "In use by");
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
