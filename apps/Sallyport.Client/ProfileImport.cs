using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Sallyport.Client;

/// <summary>"Import a VPN profile" (D29), as OpenVPN Connect's Import Profile has it: from a file or from a link, then
/// the profile looked at before it is kept. <see cref="Source"/>: "file" or "link". <see cref="SignIn"/>: the link's
/// server asked for a username and password, so the page asks for them. <see cref="Connect"/>: connect it once imported.</summary>
internal sealed record NewProfileValues(string Source, string Link, string Username, string Password, string Name, bool Connect, bool SignIn = false);

/// <summary>A profile fetched from a link: its text, or that the server wants the person signed in, or why not.</summary>
internal sealed record Downloaded(string? Text, bool SignIn, string? Why);

/// <summary>
/// Getting a profile from a link (the owner: OpenVPN Connect "has different way to setup profile"). HTTPS only — a
/// profile holds its keys — at most <see cref="Largest"/> bytes in <see cref="Timeout"/>, never following a redirect
/// down to HTTP. A server's address alone is asked the way OpenVPN Access Server serves a person their profile: its
/// published REST call <c>/rest/GetUserlogin</c>, signed in with the person's username and password (HTTP Basic). A
/// username and password are sent only once the server has asked for them, and never kept.
/// </summary>
internal static class ProfileDownload
{
    public const int Largest = 1 << 20;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private const string AccessServerProfile = "/rest/GetUserlogin";

    /// <summary>The address asked for what the person typed: https:// before a bare server, Access Server's call for one
    /// with no path. Null, with why, for anything else.</summary>
    public static Uri? Address(string typed, out string? why)
    {
        why = null;
        var text = typed.Trim();
        if (text.Length == 0) { why = L.T("Type your VPN provider's address or the link to your profile."); return null; }
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) { why = L.T("That is not a link Windows can open."); return null; }
        if (uri.Scheme != Uri.UriSchemeHttps) { why = L.T("Use an https:// link: a profile holds your keys, and anything else would send them in the clear."); return null; }
        return uri.AbsolutePath is "" or "/" ? new UriBuilder(uri) { Path = AccessServerProfile }.Uri : uri;
    }

    public static async Task<Downloaded> GetAsync(HttpClient http, Uri address, string? username, string? password, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        if (!string.IsNullOrEmpty(username))
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return new(null, true, string.IsNullOrEmpty(username) ? null : L.T("{0} did not accept that username and password.", address.Host));
        if (!response.IsSuccessStatusCode) return new(null, false, L.T("{0} answered {1} ({2}).", address.Host, (int)response.StatusCode, response.ReasonPhrase ?? ""));
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        for (int read; (read = await body.ReadAsync(chunk, ct)) > 0;)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > Largest) return new(null, false, L.T("{0} sent far more than any profile.", address.Host));
        }
        return new(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length), false, null);
    }

    /// <summary>A name for what was fetched: the file's own name when the link names one, else the server's.</summary>
    public static string NameFor(Uri address)
    {
        var file = Path.GetFileNameWithoutExtension(address.AbsolutePath);
        return address.AbsolutePath.EndsWith(".ovpn", StringComparison.OrdinalIgnoreCase) || address.AbsolutePath.EndsWith(".conf", StringComparison.OrdinalIgnoreCase)
            ? file : address.Host;
    }
}
