using System.Net;
using System.Text;
using Sallyport.Runtime;

namespace Sallyport.Client.Tests;

/// <summary>Import a VPN profile from a link (D29): which links are asked, how a server's sign-in is answered, and what a
/// profile's row says of its servers.</summary>
public sealed class ProfileImportTests
{
    [Theory]
    [InlineData("vpn.example.com", "https://vpn.example.com/rest/GetUserlogin")]
    [InlineData("https://vpn.example.com/", "https://vpn.example.com/rest/GetUserlogin")]
    [InlineData("  https://example.com/profiles/jp.ovpn ", "https://example.com/profiles/jp.ovpn")]
    [InlineData("vpn.example.com:943", "https://vpn.example.com:943/rest/GetUserlogin")]
    public void A_server_alone_is_asked_as_Access_Server_serves_a_profile_and_a_link_as_it_is(string typed, string asked)
    {
        Assert.Equal(asked, ProfileDownload.Address(typed, out var why)!.AbsoluteUri);
        Assert.Null(why);
    }

    [Theory]
    [InlineData("http://vpn.example.com/jp.ovpn", "Use an https:// link")]
    [InlineData("", "Type your VPN provider")]
    [InlineData("https://", "That is not a link")]
    public void A_link_that_could_send_the_keys_in_the_clear_or_is_no_link_is_refused(string typed, string reason)
    {
        Assert.Null(ProfileDownload.Address(typed, out var why));
        Assert.StartsWith(reason, why);
    }

    [Fact]
    public async Task A_server_that_asks_who_the_person_is_gets_their_sign_in_only_once_it_has_asked()
    {
        var server = new Server(request => request.Headers.Authorization is { Scheme: "Basic" } auth
            && Encoding.UTF8.GetString(Convert.FromBase64String(auth.Parameter!)) == "alex:correct horse"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("client\nremote vpn.example.com 1194 udp\n") }
                : new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var http = new HttpClient(server);
        var address = new Uri("https://vpn.example.com/rest/GetUserlogin");

        var first = await ProfileDownload.GetAsync(http, address, null, null, CancellationToken.None);
        var wrong = await ProfileDownload.GetAsync(http, address, "alex", "wrong horse", CancellationToken.None);
        var right = await ProfileDownload.GetAsync(http, address, "alex", "correct horse", CancellationToken.None);

        Assert.Equal((null, true, null), (first.Text, first.SignIn, first.Why));
        Assert.Null(server.Seen[0].Headers.Authorization);
        Assert.True(wrong.SignIn);
        Assert.Equal("vpn.example.com did not accept that username and password.", wrong.Why);
        Assert.StartsWith("client\nremote vpn.example.com", right.Text);
        Assert.Null(right.Why);
    }

    [Fact]
    public async Task What_is_far_larger_than_a_profile_is_not_read_to_its_end()
    {
        using var http = new HttpClient(new Server(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[ProfileDownload.Largest + 1]) }));

        var got = await ProfileDownload.GetAsync(http, new Uri("https://example.com/big.ovpn"), null, null, CancellationToken.None);

        Assert.Null(got.Text);
        Assert.Equal("example.com sent far more than any profile.", got.Why);
    }

    [Theory]
    [InlineData("https://example.com/profiles/jp.ovpn", "jp")]
    [InlineData("https://vpn.example.com/rest/GetUserlogin", "vpn.example.com")]
    public void What_is_fetched_is_named_for_its_file_else_its_server(string link, string name) =>
        Assert.Equal(name, ProfileDownload.NameFor(new Uri(link)));

    [Fact]
    public void A_profiles_row_names_its_server_first_and_says_how_many_more_it_has()
    {
        var jp = new StoredProfile("ad50a53b0da826ad", "jp", ["udp/1194", "tcp/443"], DateTimeOffset.UnixEpoch, null);

        var one = ClientView.Profile(new ProfileFacts(jp, null, null, null, false, false, null, false, false, Servers: ["vpn.example.com"]));
        var three = ClientView.Profile(new ProfileFacts(jp, null, null, null, false, false, null, false, false, Servers: ["a.example.com", "b.example.com", "c.example.com"]));
        var none = ClientView.Profile(new ProfileFacts(jp, null, null, null, false, false, null, false, false));

        Assert.Equal(new Fact("Server", "vpn.example.com"), one.Details![0]);
        Assert.Equal(new Fact("Server", "a.example.com and 2 more"), three.Details![0]);
        Assert.DoesNotContain(none.Details!, f => f.Label == "Server");
    }

    // A server that answers as the test says, and keeps what it was asked.
    private sealed class Server(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request);
            return Task.FromResult(answer(request));
        }
    }
}
