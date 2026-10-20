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

    private static RoutedItem Zenless() => new("0123456789abcdef", "Zenless Zone Zero", @"E:\Games\ZZZ", At, Us.Id, LossMode.Hold, null,
        [new("000000000001", @"E:\Games\ZZZ\ZenlessZoneZero.exe", true, null, false, true, null)]);

    private static RunFacts Running(SessionState tunnel) =>
        new(Zenless().Id, Us.Id, new LossPolicy(), At, false,
            new SessionStatus(tunnel, At, "udp/1194", 1, 0, null, "10.8.0.6", 1024, 2048),
            new RouteState(tunnel == SessionState.Up ? RoutePhase.Routing : RoutePhase.Held, 1, 10, 12, 500, 2, 0, 0, 0, 0, 0, 3, 1, 0),
            At, null, null);

    private static IReadOnlyList<AppItemView> Games(RunFacts? run) =>
        [AppViews.Item(new ItemFacts(Zenless(), Us, run, false, true, false, null, _ => []))];

    private static readonly IReadOnlyDictionary<string, string?> NoProblems = new Dictionary<string, string?>();

    [Fact]
    public void Nothing_connected_says_so_and_offers_where_to_begin()
    {
        var view = WindowViews.Network([Profile(Jp, null)], Games(null), null, [Jp, Us], Ready, NoProblems);

        Assert.Equal("Nothing is connected", view.StatusText);
        Assert.Equal(Tone.Off, view.Tone);
        Assert.Equal(new NetworkEmpty("Nothing is connected", "Turn on an app's VPN, or connect a profile, to begin."), view.Empty);
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
    public void An_open_channel_shows_its_traffic_as_figures_and_a_minute_of_rates()
    {
        var traffic = new Dictionary<string, Traffic?> { [Jp.Id] = new Traffic(1_258_291, 348_160, [0, 2048, 4096], [0, 512, 1024]) };

        var channel = Assert.Single(WindowViews.Network([Profile(Jp, TunnelServiceStatus.Up)], Games(null), null, [Jp, Us], Ready, NoProblems,
            traffic: traffic).Channels);

        Assert.NotNull(channel.Traffic);
        Assert.Equal(new Fact("Received", "4.0 KB/s", "1.2 MB"), channel.Traffic.Received);
        Assert.Equal(new Fact("Sent", "1.0 KB/s", "340 KB"), channel.Traffic.Sent);
        Assert.Equal([0, 2048, 4096], channel.Traffic.InPerSecond);
        // The counts are the figures now; not said twice.
        Assert.DoesNotContain(channel.Facts, f => f.Label is "Downloaded" or "Uploaded");
    }

    [Fact]
    public void A_channel_without_traffic_yet_keeps_its_counts_as_facts()
    {
        var channel = Assert.Single(WindowViews.Network([Profile(Jp, TunnelServiceStatus.Up)], Games(null), null, [Jp, Us], Ready, NoProblems).Channels);

        Assert.Null(channel.Traffic);
        Assert.Contains(channel.Facts, f => f.Label == "Downloaded");
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
        Assert.Equal("VPN on · us", selection.Run.StatusText);
        Assert.True(selection.Stop is { Visible: true, Label: "Turn off VPN" });
        Assert.DoesNotContain(selection.Run.Facts, f => f.Label is "Connected over" or "Tunnel address");
        Assert.Contains(channel.Facts, f => f.Label == "Tunnel address" && f.Value == "10.8.0.6");
        Assert.Equal("1 connection · 1 app using the VPN", view.StatusText);
    }

    [Fact]
    public void A_reconnecting_route_says_so_on_its_channel()
    {
        var run = Running(SessionState.Reconnecting);
        var channel = Assert.Single(WindowViews.Network([], Games(run), run, [Jp, Us], Ready, NoProblems).Channels);

        Assert.Equal(Tone.Busy, channel.Tone);
        Assert.Equal("Reconnecting…", channel.StatusText);
        Assert.Null(channel.ConnectedSince);
        Assert.Equal("Reconnecting — its internet is blocked meanwhile", channel.Selections[0].Run.StatusText);
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
        Assert.Equal("1 connection · 1 app using the VPN", view.StatusText);
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

    [Fact]
    public void Latency_says_each_paths_round_trip_and_marks_a_hops_answer_as_an_estimate()
    {
        var last = new Latency("1.1.1.1", new PathSample(4, 4, 14, "1.1.1.1", true), new PathSample(4, 3, 48, "1.1.1.1", true), "10.8.0.1",
            new PathSample(4, 2, 35, "10.8.0.5", false), At);

        var view = WindowViews.Latency(last, checking: false);

        Assert.Equal(ProbePhase.Available, view.Phase);
        // The round trip is the figure the window shows large; the rest qualifies it.
        Assert.Equal(["14 ms", "48 ms", "35 ms"], view.Paths.Select(p => p.Figure));
        Assert.Equal(["4 of 4 answered", "3 of 4 answered", "2 of 4 answered; farthest responding hop 10.8.0.5 — estimate — tunnel gateway 10.8.0.1"],
            view.Paths.Select(p => p.Value));
    }

    [Fact]
    public void No_echo_answered_is_unavailable_not_a_failed_tunnel()
    {
        var none = new PathSample(4, 0, null, null, false);

        var view = WindowViews.Latency(new Latency("1.1.1.1", none, none, null, null, At), checking: false);

        Assert.Equal(ProbePhase.Unavailable, view.Phase);
        Assert.Equal("No echo was answered, so latency could not be checked. The tunnel itself may still work.", view.StatusText);
        Assert.Equal("No reply — latency unavailable", view.Paths[0].Value);
        // No number is not a number shown large.
        Assert.All(view.Paths, p => Assert.Null(p.Figure));
        Assert.Equal("The server named no tunnel gateway.", view.Paths[2].Value);
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
        var view = WindowViews.Settings(Ready, on, guide, @"D:\Program Files\Sallyport\data\users\S-1-5-21-1", installedIn: null,
            new AppDefaults(Jp.Id, LossMode.Direct, null), Jp, editing: false);

        Assert.Equal(on, view.Startup.On);
        Assert.Equal("Start Sallyport with Windows", view.Startup.Label);
        Assert.Equal(toggle, view.Startup.Toggle.Label);
        // The version plainly; where it lives folded away (Places), for those who look.
        Assert.Equal(["Version"], view.About.Select(f => f.Label));
        Assert.StartsWith("Not installed (a development build in ", view.Places!.Single(f => f.Label == "Installed in").Value);
        Assert.Equal(@"D:\Program Files\Sallyport\data\users\S-1-5-21-1", view.Places!.Single(f => f.Label == "Your data").Value);
        Assert.Equal(["jp", "your own connection until it is back"], view.Defaults.Facts.Select(f => f.Value));
        Assert.True(view.Defaults.Edit is { Visible: true, Enabled: true, Label: "Change…" });
    }

    [Fact]
    public void Pages_cross_by_their_camelCase_names()
    {
        var json = JsonSerializer.Serialize(new NavigationView(Page.Network, 3), ClientView.Json);

        // No app's page asked for: said as null, not left out (contract.ts).
        Assert.Equal("""{"page":"network","request":3,"app":null}""", json);
    }
}
