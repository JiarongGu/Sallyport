using System.Text.Json;
using Sallyport.Client;
using Sallyport.Runtime;

namespace Sallyport.Client.Tests;

/// <summary>The window decides nothing (docs/design/client-ui.md): these pin what the host decides for it.</summary>
public class ClientViewTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

    private static StoredProfile Stored(string? unsupported = null) =>
        new("ad50a53b0da826ad", "jp", ["udp/1194", "tcp/443"], At, unsupported);

    private static TunnelServiceStatus Status(string state, int attempts = 1, string? error = null) =>
        new(state, At, state == TunnelServiceStatus.Up ? "udp/1194" : null, attempts, 0, error,
            state == TunnelServiceStatus.Up ? "10.8.0.2" : null, 3, 2048, 5 * 1024 * 1024, 1088);

    // A connected profile's proxy listens on the free port it was given; one not connected has none yet.
    private static ProfileView View(TunnelServiceStatus? status = null, StoredProfile? stored = null, Location? location = null,
        bool locating = false, bool locationFailed = false, int? attemptsSinceUp = null, bool disconnecting = false) =>
        ClientView.Profile(new ProfileFacts(stored ?? Stored(), status is null ? null : 52114, status, location, locating, locationFailed,
            attemptsSinceUp, disconnecting, Removing: false));

    [Fact]
    public void An_idle_profile_offers_connect_and_no_port_until_it_connects()
    {
        var view = View();

        Assert.Equal(ProfilePhase.Idle, view.Phase);
        Assert.Equal("Not connected", view.StatusText);
        Assert.True(view.Actions.Connect is { Visible: true, Enabled: true, Label: "Connect" });
        Assert.False(view.Actions.Disconnect.Visible);
        Assert.False(view.Actions.Copy.Visible);
        Assert.True(view.Actions.Remove.Enabled);
        Assert.False(view.Actions.Locate.Visible);
        Assert.Equal(LocationPhase.Disconnected, view.Location.Phase);
        Assert.Equal("a free port when it connects", view.EndpointText);
        Assert.Null(view.Port);
        Assert.DoesNotContain(view.Facts, f => f.Label == "SOCKS5 port");
        Assert.Null(view.ConnectedSince);
    }

    [Fact]
    public void An_up_profile_shows_the_port_it_was_given_and_locks_removal()
    {
        var view = View(Status(TunnelServiceStatus.Up));

        Assert.Equal(ProfilePhase.Up, view.Phase);
        Assert.Equal(Tone.Up, view.Tone);
        Assert.Equal(At, view.ConnectedSince);
        Assert.False(view.Actions.Connect.Visible);
        Assert.True(view.Actions.Disconnect is { Visible: true, Enabled: true, Label: "Disconnect" });
        Assert.Equal("127.0.0.1:52114", view.EndpointText);
        Assert.True(view.Actions.Copy is { Visible: true, Enabled: true });
        Assert.Contains(new Fact("SOCKS5 port", "52114"), view.Facts);
        Assert.True(view.Actions.Remove is { Enabled: false, Reason: "Disconnect it on Overview first." });
        Assert.True(view.Actions.Locate is { Visible: true, Label: "Check location" });
        Assert.Contains(new Fact("Downloaded", "5.0 MB"), view.Facts);
        Assert.Contains(new Fact("Tunnel address", "10.8.0.2"), view.Facts);
    }

    [Fact]
    public void Retries_before_the_first_connection_count_attempts_and_name_the_last_reason()
    {
        var view = View(Status(TunnelServiceStatus.Connecting, attempts: 3, error: "no answer within 30 s"));

        Assert.Equal("Retrying connection…", view.StatusText);
        Assert.Equal("3 attempts so far. Last attempt: no answer within 30 s", view.DetailText);
        Assert.Equal("Cancel", view.Actions.Disconnect.Label);
    }

    [Fact]
    public void Reconnecting_counts_only_the_attempts_since_the_tunnel_was_last_up()
    {
        // The session's count runs across the whole run: 7 in all, 5 of them before it was last up.
        var view = View(Status(TunnelServiceStatus.Reconnecting, attempts: 7, error: "the server reset"), attemptsSinceUp: 2);

        Assert.Equal(ProfilePhase.Reconnecting, view.Phase);
        Assert.Equal("2 attempts since the connection was lost. Last attempt: the server reset", view.DetailText);
        Assert.Equal(Tone.Busy, view.Tone);
    }

    [Fact]
    public void A_refusal_offers_try_again_with_its_reason()
    {
        var view = View(Status(TunnelServiceStatus.Refused, error: "the server refused the certificate"));

        Assert.Equal(ProfilePhase.Refused, view.Phase);
        Assert.Equal("Could not connect", view.StatusText);
        Assert.Equal("The server refused the certificate", view.DetailText);
        Assert.Equal("Try again", view.Actions.Connect.Label);
        Assert.Equal(Tone.Trouble, view.Tone);
    }

    [Fact]
    public void An_unsupported_profile_cannot_be_connected_and_says_why()
    {
        var view = View(stored: Stored(unsupported: "tls-auth is not supported yet"));

        Assert.Equal(ProfilePhase.Unsupported, view.Phase);
        Assert.True(view.Actions.Connect is { Visible: true, Enabled: false });
        Assert.NotNull(view.Actions.Connect.Reason);
        // In the person's words (D29), not the protocol's: "tls-auth" tells them nothing they can act on.
        Assert.Equal(("Unavailable in this version", "Its security setup is not supported yet."), (view.StatusText, view.DetailText));
        Assert.Equal("It is missing a part it needs to connect. Ask whoever gave it to you for a new copy.", ClientView.CannotConnect("the profile has no <ca>"));
    }

    [Fact]
    public void A_profile_that_signs_in_names_its_username_or_says_none_is_set_and_one_that_does_not_says_nothing_of_it()
    {
        var signs = new StoredProfile("ad50a53b0da826ad", "work", ["udp/1194"], At, null, NeedsLogin: true);

        var named = ClientView.Profile(new ProfileFacts(signs, null, null, null, false, false, null, false, false, Login: new LoginState("alex", true)));
        var unnamed = ClientView.Profile(new ProfileFacts(signs, null, null, null, false, false, null, false, false));
        var plain = View();

        Assert.Contains(new Fact("Username", "alex"), named.Details!);
        Assert.Contains(new Fact("Username", "Not set"), unnamed.Details!);
        Assert.DoesNotContain(plain.Details!, f => f.Label == "Username");
        Assert.True(named.Actions.Edit is { Visible: true, Enabled: true, Label: "Edit…" });
    }

    [Fact]
    public void Disconnecting_is_busy_and_offers_nothing_else()
    {
        var view = View(Status(TunnelServiceStatus.Up), disconnecting: true);

        Assert.Equal(ProfilePhase.Disconnecting, view.Phase);
        Assert.True(view.Actions.Disconnect is { Visible: true, Busy: true, Enabled: false });
        Assert.False(view.Actions.Connect.Visible);
        Assert.False(view.Actions.Copy.Enabled);
    }

    [Fact]
    public void A_location_checked_again_keeps_the_previous_result_marked_as_previous()
    {
        var where = new Location("203.0.113.7", "Tokyo", "Tokyo", "JP", "AS64500 Example", At);

        var checkedOnce = View(Status(TunnelServiceStatus.Up), location: where);
        var again = View(Status(TunnelServiceStatus.Up), location: where, locating: true);

        Assert.Equal(LocationPhase.Checked, checkedOnce.Location.Phase);
        Assert.Equal("Exit location: Tokyo, Tokyo, JP", checkedOnce.Location.Text);
        Assert.Equal("Check again", checkedOnce.Actions.Locate.Label);
        Assert.Equal(LocationPhase.Checking, again.Location.Phase);
        Assert.True(again.Location.PreviousResult);
        Assert.True(again.Actions.Locate.Busy);
    }

    [Fact]
    public void A_lookup_that_did_not_answer_is_unavailable_not_a_failed_tunnel()
    {
        var view = View(Status(TunnelServiceStatus.Up), locationFailed: true);

        Assert.Equal(LocationPhase.Unavailable, view.Location.Phase);
        Assert.Equal(ProfilePhase.Up, view.Phase);
        Assert.Equal("Try again", view.Actions.Locate.Label);
    }

    [Fact]
    public void One_healthy_tunnel_does_not_hide_another_ones_failure()
    {
        var up = View(Status(TunnelServiceStatus.Up));
        var refused = View(Status(TunnelServiceStatus.Refused, error: "refused"));

        var summary = ClientView.Summary([up, refused]);

        Assert.Equal(Tone.Trouble, summary.Tone);
        Assert.Equal("1 connected · 1 could not connect", summary.Text);
        Assert.Equal(new ClientState.SummaryView("Not connected", Tone.Off), ClientView.Summary([View()]));
    }

    [Fact]
    public void The_state_crosses_as_the_contract_spells_it_nulls_included()
    {
        var json = JsonSerializer.Serialize(View(), ClientView.Json);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // web/src/contract.ts: camelCase, enums by camelCase name, and a missing value as null rather than left out.
        Assert.Equal("idle", root.GetProperty("phase").GetString());
        Assert.Equal("off", root.GetProperty("tone").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("detailText").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("connectedSince").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("actions").GetProperty("connect").GetProperty("reason").ValueKind);
        Assert.Equal("disconnected", root.GetProperty("location").GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("port").ValueKind);
        Assert.True(root.GetProperty("actions").TryGetProperty("copy", out _));
    }
}

public class AppLauncherTests
{
    [Theory]
    [InlineData("--no-first-run", "--no-first-run")]
    [InlineData("--host-resolver-rules=MAP * ~NOTFOUND , EXCLUDE 127.0.0.1", "\"--host-resolver-rules=MAP * ~NOTFOUND , EXCLUDE 127.0.0.1\"")]
    [InlineData(@"--user-data-dir=C:\Users\a b\apps\1", "\"--user-data-dir=C:\\Users\\a b\\apps\\1\"")]
    [InlineData(@"C:\dir with space\", "\"C:\\dir with space\\\\\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("", "\"\"")]
    public void An_argument_is_quoted_so_that_Windows_reads_it_back_whole(string argument, string quoted) =>
        Assert.Equal(quoted, AppLauncher.Quote(argument));

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", "Chromium")]
    [InlineData(@"C:\Program Files\Mozilla Firefox\firefox.exe", "Firefox")]
    [InlineData(@"C:\Windows\System32\curl.exe", "Environment")]
    public void The_kind_is_guessed_from_the_program(string path, string kind) =>
        Assert.Equal(Enum.Parse<AppKind>(kind), AppLauncher.Guess(path));
}
