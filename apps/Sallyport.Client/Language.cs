using System.Globalization;

namespace Sallyport.Client;

internal enum Language { English, Chinese }

/// <summary>
/// The words of the window, the tray and the notices in the person's language — chosen in Settings, or Windows' own —
/// each written in English where it is used and looked up by that English in <see cref="Chinese"/>. A sentence with
/// values in it is a template, "{0}" where each goes, so a translation puts them where its grammar wants them. What has
/// no translation yet shows in English rather than not at all (WindowViewTests checks that none is missing).
/// </summary>
internal static class L
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly CultureInfo EnglishDates = CultureInfo.GetCultureInfo("en-US");
    private static volatile int _current;

    public static Language Current
    {
        get => (Language)_current;
        set => _current = (int)value;
    }

    /// <summary>The page's own name for the language: what it picks its own words by, and the HTML lang.</summary>
    public static string Code => Current == Language.Chinese ? "zh-Hans" : "en";

    public static string T(string english) =>
        Current == Language.Chinese && Chinese.Words.TryGetValue(english, out var translated) ? translated : english;

    public static string T(string template, params object?[] values) => string.Format(Invariant, T(template), values);

    /// <summary>One or many: English says "1 attempt" and "3 attempts"; a language without plurals translates both.</summary>
    public static string Count(int n, string one, string many) => n == 1 ? T(one) : T(many, n.ToString("N0", Invariant));

    /// <summary>Two sentences as one line: a space between them in English, none in Chinese, whose own full stop parts them.</summary>
    public static string Join(string first, string? second) =>
        second is null ? first : Current == Language.Chinese ? first + second : $"{first} {second}";

    public static string Date(DateTimeOffset at) =>
        Current == Language.Chinese ? at.ToString("yyyy'年'M'月'd'日'", Invariant) : at.ToString("d MMM yyyy", EnglishDates);

    /// <summary>The setting's language: "en", "zh-Hans", or anything else for Windows' own display language.</summary>
    public static Language Resolve(string? setting) => setting switch
    {
        "en" => Language.English,
        "zh-Hans" => Language.Chinese,
        _ => CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? Language.Chinese : Language.English,
    };
}
