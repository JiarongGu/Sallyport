using System.Text.RegularExpressions;

namespace Sallyport.Client;

/// <summary>
/// The router service's reasons, and the router client's: given in English, by programs that have no window and no
/// language of their own, and shown in the window's. Each is listed here once with its Chinese in <see cref="Chinese"/>
/// (LanguageTests holds the two lists together); one that is not listed is shown as it came.
/// </summary>
internal static class RouterWords
{
    /// <summary>As the service and <c>RouterClient</c> say them, made sentences; <c>{0}</c> a value (an error's number,
    /// an exit code).</summary>
    internal static readonly string[] Reasons =
    [
        "The service is stopping.",
        "Stopped by the client.",
        "Not understood.",
        "The client went away.",
        "Its process could not be read.",
        "It is not in a person's session.",
        "It runs as the system.",
        "It is not Sallyport's own client.",
        "Game routing is in use already — by another window or another person on this computer.",
        "There is no program to route.",
        "The packet driver is not ready.",
        "The packet driver is not installed.",
        "The packet driver did not open (Windows error {0}).",
        "No network adapter carries the route to the internet.",
        "The router did not start.",
        "The router was started without its programs.",
        "The router stopped (exit {0}).",
        "The router stopped answering.",
        "The router stopped moving packets.",
        "The client's lease ran out.",
        "The network stopped answering, so routing stopped to let it recover.",
        "This service speaks version {0}; update Sallyport.",
        "This run was prepared for another client.",
        "Not this run.",
        "The service answered with something this client does not understand.",
        "The run answered with something this client does not understand.",
        "What answered on the router's pipe is not the Sallyport service.",
        "What answered on the router's pipe is not running as the system.",
        "What answered on the router's pipe does not run from Sallyport's folder.",
        "What answered on the run's pipe is not the router the service started.",
        "The Sallyport service is not set to run as the system.",
        "The Sallyport service does not run from Sallyport's folder.",
        "Windows did not say what runs the Sallyport service (error {0}).",
        "The router stopped before it could be reached.",
    ];

    private static readonly (Regex Pattern, string Template)[] Templates =
        [.. Reasons.Where(r => r.Contains("{0}")).Select(r => (new Regex("^" + Regex.Escape(r).Replace(@"\{0}", "(.+?)") + "$"), r))];

    /// <summary>A reason in the window's language: made a sentence, and translated where it is one of <see cref="Reasons"/>.</summary>
    public static string Say(string? reason)
    {
        var sentence = Sentence(reason);
        if (Array.IndexOf(Reasons, sentence) >= 0) return L.T(sentence);
        foreach (var (pattern, template) in Templates)
            if (pattern.Match(sentence) is { Success: true } match) return L.T(template, match.Groups[1].Value);
        return sentence;
    }

    public static string Sentence(string? text) =>
        string.IsNullOrEmpty(text) ? "" : char.ToUpperInvariant(text[0]) + text[1..] + (text.EndsWith('.') ? "" : ".");
}
