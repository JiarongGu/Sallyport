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

    private static GameProfile Zenless() => new("0123456789abcdef", "Zenless Zone Zero", @"E:\Games\ZZZ", [new(@"E:\Games\ZZZ\ZenlessZoneZero.exe", true)], At);

    private static RunFacts Running(SessionState tunnel) =>
        new(Zenless().Id, Us.Id, new LossPolicy(), At, false,
            new SessionStatus(tunnel, At, "udp/1194", 1, 0, null, "10.8.0.6", 1024, 2048),
            new RouteState(tunnel == SessionState.Up ? RoutePhase.Routing : RoutePhase.Held, 1, 10, 12, 500, 2, 0, 0, 0, 0, 0, 3, 1, 0),
            At, null, null);

    private static IReadOnlyList<GameView> Games(RunFacts? run) =>
        [GameViews.Game(Zenless(), new GameChoice(Us.Id, LossMode.Hold, null), Us, run, false, true, false)];

    private static readonly IReadOnlyDictionary<string, string?> NoProblems = new Dictionary<string, string?>();

    [Fact]
    public void Nothing_connected_says_so_and_offers_where_to_begin()
    {
        var view = WindowViews.Network([Profile(Jp, null)], Games(null), null, [Jp, Us], Ready, NoProblems);

        Assert.Equal("Nothing is connected", view.StatusText);
        Assert.Equal(Tone.Off, view.Tone);
        Assert.Equal(new NetworkEmpty("Nothing is connected", "Route an app or connect a profile to begin."), view.Empty);
        Assert.Empty(view.Channels);
        Assert.Equal(Ready, view.Routing);
    }

    [Fact]
    public void A_connected_proxy_is_a_channel_with_its_endpoint_and_disconnect()
    {
        var channel = Assert.Single(WindowViews.Network([Profile(Jp, TunnelServiceStatus.Up)], Games(null), null, [Jp, Us], Ready, NoProblems).Channels);

        Assert.Equal("jp", channel.Name);
        Assert.Equal("Connected", channel.StatusText);
        Assert.Equal("SOCKS5 proxy on 127.0.0.1:1088", channel.ProxyText);
        Assert.True(channel.Actions.Disconnect is { Visible: true, Enabled: true, Label: "Disconnect" });
        // What never changes stays on the Profiles page.
        Assert.DoesNotContain(channel.Facts, f => f.Label is "Transport" or "Imported" or "SOCKS5 port");
        Assert.Contains(channel.Facts, f => f.Label == "Tunnel address" && f.Value == "10.8.0.2");
    }

    [Fact]
    public void A_failure_comes_first_and_is_not_hidden_by_a_healthy_channel()
    {
        var view = WindowViews.Network([Profile(Jp, TunnelServiceStatus.Up), Profile(Us, TunnelServiceStatus.Refused, "authentication was refused")],
            Games(null), null, [Jp, Us], Ready, NoProblems);

        Assert.Equal(["us", "jp"], view.Channels.Select(c => c.Name));
        Assert.Equal(Tone.Trouble, view.Tone);
        Assert.Equal("2 connections", view.StatusText);
        // A refused profile serves no proxy.
        Assert.Null(view.Channels[0].ProxyText);
    }

    [Fact]
    public void A_routed_game_is_a_selection_on_its_profiles_channel()
    {
        var run = Running(SessionState.Up);
        var view = WindowViews.Network([Profile(Jp, null), Profile(Us, null)], Games(run), run, [Jp, Us], Ready, NoProblems);

        var channel = Assert.Single(view.Channels);
        Assert.Equal("us", channel.Name);
        Assert.Equal("Connected", channel.StatusText);
        Assert.Equal(At, channel.ConnectedSince);
        Assert.False(channel.Actions.Disconnect.Visible);
        var selection = Assert.Single(channel.Selections);
        Assert.Equal("Zenless Zone Zero", selection.Name);
        Assert.Equal("Routed through us", selection.Run.StatusText);
        Assert.True(selection.Stop is { Visible: true, Label: "Stop routing" });
        Assert.DoesNotContain(selection.Run.Facts, f => f.Label is "Connected over" or "Tunnel address");
        Assert.Contains(channel.Facts, f => f.Label == "Tunnel address" && f.Value == "10.8.0.6");
        Assert.Equal("1 connection · 1 app routed", view.StatusText);
    }

    [Fact]
    public void A_reconnecting_route_says_so_on_its_channel()
    {
        var run = Running(SessionState.Reconnecting);
        var channel = Assert.Single(WindowViews.Network([], Games(run), run, [Jp, Us], Ready, NoProblems).Channels);

        Assert.Equal(Tone.Busy, channel.Tone);
        Assert.Equal("Reconnecting…", channel.StatusText);
        Assert.Null(channel.ConnectedSince);
        Assert.Equal("Reconnecting — the game's traffic is held", channel.Selections[0].Run.StatusText);
    }

    [Fact]
    public void A_proxy_and_a_route_on_one_profile_are_one_channel()
    {
        var run = Running(SessionState.Up);
        var view = WindowViews.Network([Profile(Us, TunnelServiceStatus.Up)], Games(run), run, [Jp, Us], Ready, NoProblems);

        var channel = Assert.Single(view.Channels);
        Assert.Equal("SOCKS5 proxy on 127.0.0.1:1088", channel.ProxyText);
        Assert.Equal("Zenless Zone Zero", Assert.Single(channel.Selections).Name);
        Assert.True(channel.Actions.Disconnect is { Visible: true, Label: "Disconnect" });
        Assert.Equal("1 connection · 1 app routed", view.StatusText);
    }

    [Fact]
    public void A_proxy_that_could_not_listen_is_said_on_its_channel_without_failing_it()
    {
        var view = WindowViews.Network([Profile(Us, TunnelServiceStatus.Up)], Games(null), null, [Jp, Us], Ready,
            new Dictionary<string, string?> { [Us.Id] = "port 1088 is in use by something else" });

        var channel = Assert.Single(view.Channels);
        Assert.Equal("SOCKS5 proxy not listening: port 1088 is in use by something else", channel.ProxyText);
        Assert.Equal(Tone.Up, channel.Tone);
    }

    [Theory]
    [InlineData(false, "Turn on")]
    [InlineData(true, "Turn off")]
    public void Settings_says_how_Sallyport_starts_and_where_it_is(bool on, string toggle)
    {
        var guide = new ClientState.GuideView("127.0.0.1:1088", ClientView.CurlExample("127.0.0.1:1088"), UiAction.Shown("Copy"));
        var view = WindowViews.Settings(Ready, on, guide, @"D:\Program Files\Sallyport\data\users\S-1-5-21-1", installedIn: null);

        Assert.Equal(on, view.Startup.On);
        Assert.Equal("Start Sallyport with Windows", view.Startup.Label);
        Assert.Equal(toggle, view.Startup.Toggle.Label);
        Assert.Contains(view.About, f => f.Label == "Version");
        Assert.StartsWith("Not installed (a development build in ", view.About.Single(f => f.Label == "Installed in").Value);
        Assert.Equal(@"D:\Program Files\Sallyport\data\users\S-1-5-21-1", view.About.Single(f => f.Label == "Your data").Value);
        Assert.Equal(["Version", "Installed in", "Your data"], view.About.Select(f => f.Label));
    }

    [Fact]
    public void Pages_cross_by_their_camelCase_names()
    {
        var json = JsonSerializer.Serialize(new NavigationView(Page.Network, 3), ClientView.Json);

        Assert.Equal("""{"page":"network","request":3}""", json);
    }
}
