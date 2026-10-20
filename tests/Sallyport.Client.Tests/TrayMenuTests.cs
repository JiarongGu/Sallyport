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

    [Fact]
    public void With_nothing_yet_it_opens_the_window_says_how_to_begin_and_leaves()
    {
        var menu = TrayMenu.Build([], "Nothing is connected");

        Assert.Equal(
            [TrayEntryKind.Command, TrayEntryKind.Separator, TrayEntryKind.Note, TrayEntryKind.Separator, TrayEntryKind.Command, TrayEntryKind.Command],
            menu.Select(e => e.Kind));
        // One way into the window, on its Overview, with the app's mark; what is connected, its tip.
        Assert.True(menu[0] is { Text: "Open Sallyport", Verb: TrayVerb.OpenWindow, Default: true, Tip: "Nothing is connected", Glyph: TrayGlyph.Mark });
        Assert.True(menu[2] is { Text: "No profiles yet — open Sallyport to import one", Enabled: false });
        Assert.True(menu[4] is { Text: "Settings", Verb: TrayVerb.OpenSettings, Glyph: TrayGlyph.Settings });
        Assert.True(menu[5] is { Text: "Exit Sallyport", Verb: TrayVerb.Exit, Glyph: TrayGlyph.Exit,
            Tip: "Disconnects, then quits. Your apps use your own connection until Sallyport connects again." });
        // Only one entry is the default (what a double click would do), and it is opening the window.
        Assert.Single(menu, e => e.Default);
    }

    [Fact]
    public void A_profile_not_connected_is_one_row_that_connects_it()
    {
        var profile = Profile(null);

        var row = Assert.Single(TrayMenu.Build([profile], ""), e => e.Tone is not null);

        // Its name, where it is, what pressing it does: no submenu to open first.
        Assert.True(row is { Kind: TrayEntryKind.Command, Text: "jp", Verb: TrayVerb.Connect, Action: "Connect", Enabled: true, Tone: Tone.Off });
        Assert.Equal(profile.StatusText, row.Status);
        Assert.Equal(Jp.Id, row.Target);
    }

    [Fact]
    public void A_connected_profiles_row_disconnects_it()
    {
        var row = Assert.Single(TrayMenu.Build([Profile(TunnelServiceStatus.Up)], ""), e => e.Tone is not null);

        Assert.True(row is { Text: "jp", Status: "Connected", Tone: Tone.Up, Verb: TrayVerb.Disconnect, Action: "Disconnect", Enabled: true, Target: "ad50a53b0da826ad" });
    }

    [Fact]
    public void A_profile_still_connecting_can_be_cancelled_from_the_tray()
    {
        var row = Assert.Single(TrayMenu.Build([Profile(TunnelServiceStatus.Connecting)], ""), e => e.Tone is not null);

        Assert.True(row is { Verb: TrayVerb.Disconnect, Action: "Cancel", Tone: Tone.Busy });
    }

    [Fact]
    public void Every_command_says_what_it_acts_on_and_no_app_is_offered_here()
    {
        var menu = TrayMenu.Build([Profile(TunnelServiceStatus.Up)], "1 connection");

        var commands = menu.Where(e => e.Kind == TrayEntryKind.Command).ToList();
        Assert.All(commands, c => Assert.NotEqual(TrayVerb.None, c.Verb));
        Assert.All(commands.Where(c => c.Verb is TrayVerb.Connect or TrayVerb.Disconnect), c => Assert.False(string.IsNullOrEmpty(c.Target)));
        Assert.All(menu.Where(e => e.Kind == TrayEntryKind.Note), n => Assert.False(n.Enabled));
        // What goes through a profile is set in the window, profile by profile (the owner): the tray connects it as set.
        Assert.Equal(["Open Sallyport", "jp", "Settings", "Exit Sallyport"], menu.Where(e => e.Kind != TrayEntryKind.Separator).Select(e => e.Text));
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
