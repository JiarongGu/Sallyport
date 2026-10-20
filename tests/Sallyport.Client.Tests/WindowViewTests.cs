using System.Text.Json;
using Sallyport.Profile;
using Sallyport.Runtime;
using Sallyport.Windows.Routing;
using Sallyport.Windows.Service;

namespace Sallyport.Client.Tests;

/// <summary>What Network and Settings show, as the host decides it (docs/design/window.md).</summary>
public class WindowViewTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    private static readonly StoredProfile Jp = new("ad50a53b0da826ad", "jp", ["udp/1194", "tcp/443"], At, null);
    private static readonly StoredProfile Us = new("0b1c2d3e4f5a6b7c", "us", ["udp/1194"], At, null);

    private static readonly DriverView Ready = GameViews.Driver(new DriverFacts(true, true, true, true, "3.6.2.1", false, null));

    private static ProfileView Profile(StoredProfile stored, string? state, string? error = null) =>
        ClientView.Profile(new ProfileFacts(stored, 1088, state is null ? null : new TunnelServiceStatus(state, At,
            state == TunnelServiceStatus.Up ? "udp/1194" : null, 1, 0, error, state == TunnelServiceStatus.Up ? "10.8.0.2" : null, 2, 2048, 4096, 1088),
            null, false, false, null, false, false));

    private static RoutedItem Zenless() => new("0123456789abcdef", "Zenless Zone Zero", @"E:\Games\ZZZ", At, null, LossMode.Hold, null,
        [new("000000000001", @"E:\Games\ZZZ\ZenlessZoneZero.exe", true, null, false, true, null)], Uses: [new AppUse(Us.Id)]);

    // us's lane carrying Zenless, its tunnel as given.
    private static RunFacts Running(SessionState tunnel) =>
        new([new LaneFacts(Us.Id, 0, new Dictionary<string, DateTimeOffset> { [Zenless().Id] = At },
                new SessionStatus(tunnel, At, "udp/1194", 1, 0, null, "10.8.0.6", 1024, 2048), tunnel == SessionState.Up ? RoutePhase.Routing : RoutePhase.Held, At)],
            At, false, new RouteState(tunnel == SessionState.Up ? RoutePhase.Routing : RoutePhase.Held, 1, 10, 12, 500, 2, 0, 0, 0, 0, 0, 3, 1, 0), null, null);

    private static ItemFacts Facts(RunFacts? run) => new(Zenless(), [Jp, Us], run, id => run is not null && id == Us.Id, _ => null, true, false, null, _ => []);

    private static IReadOnlyList<AppItemView> Games(RunFacts? run) => [AppViews.Item(Facts(run))];

    // Each profile's section as the core builds it: the apps in it, and the others to put in.
    private static Dictionary<string, (IReadOnlyList<AppSectionView>, IReadOnlyList<EditorOption>)> Sections(RunFacts? run) => new()
    {
        [Jp.Id] = ([], [new EditorOption(Zenless().Id, Zenless().Name)]),
        [Us.Id] = ([AppViews.InSection(Facts(run), Us, forApp: false)], []),
    };

    private static readonly IReadOnlyDictionary<string, string?> NoProblems = new Dictionary<string, string?>();

    [Fact]
    public void Nothing_connected_says_so_and_offers_where_to_begin()
    {
        var view = WindowViews.Network([Profile(Jp, null)], Games(null), null, [Jp, Us], Ready, NoProblems);

        Assert.Equal("Nothing is connected", view.StatusText);
        Assert.Equal(Tone.Off, view.Tone);
        Assert.Equal(new NetworkEmpty("Nothing is connected", "Connect a profile to begin: the apps whose Use VPN is on go through it."), view.Empty);
        Assert.Empty(view.Channels);
        Assert.Equal(Ready, view.Routing);
    }

    [Fact]
    public void A_connected_proxy_is_a_channel_with_its_endpoint_and_disconnect()
    {
        var channel = Assert.Single(WindowViews.Network([Profile(Jp, TunnelServiceStatus.Up)], Games(null), null, [Jp, Us], Ready, NoProblems).Channels);

        Assert.Equal("jp", channel.Name);
        Assert.Equal("Connected", channel.StatusText);
        Assert.True(channel.Actions.Disconnect is { Visible: true, Enabled: true, Label: "Disconnect" });
        // One line: the type it is connected over, never its port; its proxy; what is open through it. No tunnel address.
        Assert.Equal(["UDP", "SOCKS5 proxy on 127.0.0.1:1088", "2 open connections"], channel.Line);
    }

    [Fact]
    public void A_channel_that_reconnected_says_how_often_on_its_line()
    {
        var again = ClientView.Profile(new ProfileFacts(Jp, 1088, new TunnelServiceStatus(TunnelServiceStatus.Up, At, "tcp/443", 1, 2, null, "10.8.0.2", 1, 0, 0, 1088),
            null, false, false, null, false, false));

        var channel = Assert.Single(WindowViews.Network([again], Games(null), null, [Jp, Us], Ready, NoProblems).Channels);

        Assert.Equal(["TCP", "SOCKS5 proxy on 127.0.0.1:1088", "1 open connection", "Reconnected 2 times"], channel.Line);
    }

    [Fact]
    public void An_open_channel_shows_its_traffic_as_figures_and_a_minute_of_rates()
    {
        var traffic = new Dictionary<string, Traffic?> { [Jp.Id] = new Traffic(1_258_291, 348_160, [0, 2048, 4096], [0, 512, 1024]) };

        var channel = Assert.Single(WindowViews.Network([Profile(Jp, TunnelServiceStatus.Up)], Games(null), null, [Jp, Us], Ready, NoProblems,
            traffic: traffic).Channels);

        Assert.NotNull(channel.Traffic);
        Assert.Equal(new Fact("Received", "4.0 KB/s", "1.2 MB"), channel.Traffic.Received);
        Assert.Equal(new Fact("Sent", "1.0 KB/s", "340 KB"), channel.Traffic.Sent);
        Assert.Equal([0, 2048, 4096], channel.Traffic.InPerSecond);
    }

    [Fact]
    public void A_failure_comes_first_and_is_not_hidden_by_a_healthy_channel()
    {
        var view = WindowViews.Network([Profile(Jp, TunnelServiceStatus.Up), Profile(Us, TunnelServiceStatus.Refused, "authentication was refused")],
            Games(null), null, [Jp, Us], Ready, NoProblems);

        Assert.Equal(["us", "jp"], view.Channels.Select(c => c.Name));
        Assert.Equal(Tone.Trouble, view.Tone);
        Assert.Equal("2 connections", view.StatusText);
        // A refused profile serves no proxy, and is not connected over anything.
        Assert.Empty(view.Channels[0].Line);
    }

    [Fact]
    public void Every_vpn_is_a_section_with_its_apps_and_the_others_to_put_in()
    {
        var run = Running(SessionState.Up);
        var view = WindowViews.Network([Profile(Jp, null), Profile(Us, TunnelServiceStatus.Up)], Games(run), run, [Jp, Us], Ready, NoProblems, sections: Sections(run));

        var channel = Assert.Single(view.Channels);
        Assert.Equal("us", channel.Name);
        Assert.Equal("Connected", channel.StatusText);
        // The person's own connection: theirs to end, the apps' switches staying on (D23).
        Assert.True(channel.Actions.Disconnect is { Visible: true, Label: "Disconnect" });
        // Every profile a section in import order, connected or not; jp's empty, with Zenless to put in (D24).
        Assert.Equal(["jp", "us"], view.Sections!.Select(s => s.Profile.Name));
        Assert.True(view.Sections![0] is { Channel: null, Apps.Count: 0, Add: { Visible: true, Label: "Add an app to jp" } });
        Assert.Equal("Put an app here and it goes through jp whenever it is connected.", view.Sections[0].HintText);
        var us = view.Sections[1];
        Assert.Same(channel, us.Channel);
        // Every app in it already, but a new one can always be added from here (Add an app with us ticked).
        Assert.True(us.Add.Visible);
        Assert.Empty(us.Addable);
        var selection = Assert.Single(us.Apps);
        Assert.Equal("Zenless Zone Zero", selection.Name);
        Assert.Equal("VPN on · us", selection.Run!.StatusText);
        Assert.True(selection.Vpn is { Visible: true, Enabled: true, Label: "Use VPN" });
        Assert.DoesNotContain(selection.Run.Facts, f => f.Label is "Connected over" or "Tunnel address");
        Assert.Equal("1 connection · 1 app using the VPN", view.StatusText);
    }

    [Fact]
    public void A_reconnecting_route_says_so_on_its_channel()
    {
        var run = Running(SessionState.Reconnecting);
        var view = WindowViews.Network([Profile(Us, TunnelServiceStatus.Reconnecting)], Games(run), run, [Jp, Us], Ready, NoProblems, sections: Sections(run));
        var channel = Assert.Single(view.Channels);

        Assert.Equal(Tone.Busy, channel.Tone);
        Assert.Equal("Reconnecting…", channel.StatusText);
        Assert.Equal("Reconnecting — its internet is blocked meanwhile", Assert.Single(view.Sections!).Apps[0].Run!.StatusText);
    }

    [Fact]
    public void A_proxy_and_a_route_on_one_profile_are_one_channel()
    {
        var run = Running(SessionState.Up);
        var view = WindowViews.Network([Profile(Us, TunnelServiceStatus.Up)], Games(run), run, [Jp, Us], Ready, NoProblems, sections: Sections(run));

        var channel = Assert.Single(view.Channels);
        Assert.Contains("SOCKS5 proxy on 127.0.0.1:1088", channel.Line);
        Assert.Equal("Zenless Zone Zero", Assert.Single(Assert.Single(view.Sections!).Apps).Name);
        Assert.True(channel.Actions.Disconnect is { Visible: true, Label: "Disconnect" });
        Assert.Equal("1 connection · 1 app using the VPN", view.StatusText);
    }

    [Fact]
    public void A_proxy_that_could_not_listen_is_said_on_its_channel_without_failing_it()
    {
        var view = WindowViews.Network([Profile(Us, TunnelServiceStatus.Up)], Games(null), null, [Jp, Us], Ready,
            new Dictionary<string, string?> { [Us.Id] = "port 1088 is in use by something else" });

        var channel = Assert.Single(view.Channels);
        Assert.Contains("SOCKS5 proxy not listening: port 1088 is in use by something else", channel.Line);
        Assert.Equal(Tone.Up, channel.Tone);
    }

    [Fact]
    public void The_graphs_round_trip_is_the_vpns_and_a_hops_answer_is_not_shown()
    {
        var through = new PathSample(4, 3, 48, "1.1.1.1", true);

        // The server answered only from a hop on the way: the way through it is the figure.
        var view = WindowViews.Latency(new Latency("1.1.1.1", through, "10.8.0.1", new PathSample(4, 2, 35, "10.8.0.5", false), At), checking: false);
        // The server's own tunnel side answered: that is the figure.
        var server = WindowViews.Latency(new Latency("1.1.1.1", through, "10.8.0.1", new PathSample(4, 4, 31, "10.8.0.1", true), At), checking: true);

        Assert.Equal((ProbePhase.Available, "48 ms", "Through the VPN to 1.1.1.1"), (view.Phase, view.Figure, view.Text));
        // Checked again, the last figure stays on the graph meanwhile.
        Assert.Equal((ProbePhase.Checking, "31 ms", "To the VPN server"), (server.Phase, server.Figure, server.Text));
    }

    [Fact]
    public void No_echo_answered_is_unavailable_not_a_failed_tunnel()
    {
        var none = new PathSample(4, 0, null, null, false);

        var view = WindowViews.Latency(new Latency("1.1.1.1", none, null, null, At), checking: false);

        Assert.Equal(ProbePhase.Unavailable, view.Phase);
        // No number is not a number shown; why there is none, said (rule 6).
        Assert.Null(view.Figure);
        Assert.Equal("Nothing answered, so ping could not be checked. The VPN itself may still work.", view.Text);
        var first = WindowViews.Latency(null, checking: false);
        Assert.Equal((ProbePhase.Unchecked, "Not measured yet"), (first.Phase, first.Text));
    }

    [Fact]
    public void A_retry_countdown_shows_only_while_the_connection_waits_for_one()
    {
        var at = DateTimeOffset.UtcNow.AddSeconds(20);
        ProfileView With(string state, DateTimeOffset? next) => ClientView.Profile(new ProfileFacts(Jp, 1088,
            new TunnelServiceStatus(state, At, null, 3, 1, "no answer", null, 0, 0, 0, 1088), null, false, false, null, false, false, NextAttemptAt: next));

        Assert.Equal(at, With(TunnelServiceStatus.Reconnecting, at).NextAttemptAt);
        Assert.Null(With(TunnelServiceStatus.Reconnecting, DateTimeOffset.UtcNow.AddSeconds(-1)).NextAttemptAt);
        Assert.Null(With(TunnelServiceStatus.Refused, at).NextAttemptAt);
    }

    [Theory]
    [InlineData(false, "Turn on")]
    [InlineData(true, "Turn off")]
    public void Settings_says_how_Sallyport_starts_and_where_it_is(bool on, string toggle)
    {
        var guide = new ClientState.GuideView("127.0.0.1:1088", ClientView.CurlExample("127.0.0.1:1088"), UiAction.Shown("Copy"));
        var view = WindowViews.Settings(Ready, on, guide, @"D:\Program Files\Sallyport\data\users\S-1-5-21-1", installedIn: null, editing: false);

        Assert.Equal(on, view.Startup.On);
        Assert.Equal("Start Sallyport with Windows", view.Startup.Label);
        Assert.Equal(toggle, view.Startup.Toggle.Label);
        // The version plainly; where it lives folded away (Places), for those who look.
        Assert.Equal(["Version"], view.About.Select(f => f.Label));
        Assert.StartsWith("Not installed (a development build in ", view.Places!.Single(f => f.Label == "Installed in").Value);
        Assert.Equal(@"D:\Program Files\Sallyport\data\users\S-1-5-21-1", view.Places!.Single(f => f.Label == "Your data").Value);
    }

    [Fact]
    public void Pages_cross_by_their_camelCase_names()
    {
        var json = JsonSerializer.Serialize(new NavigationView(Page.Network, 3), ClientView.Json);

        // No app's page asked for: said as null, not left out (contract.ts).
        Assert.Equal("""{"page":"network","request":3,"app":null}""", json);
    }
}
