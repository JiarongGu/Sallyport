using System.Globalization;
using System.Security.Principal;
using System.Text.Json;
using Sallyport.Windows;

namespace Sallyport.Setup;

/// <summary>
/// Setup's words in the person's language, as the client's are (apps/Sallyport.Client/Language.cs): written in English
/// where they are used and looked up by it in <see cref="Chinese"/>. Which language: <c>--language</c>, else the one the
/// person chose in an installed Sallyport (its settings, in their own folder of the install), else Windows' own — and
/// whichever the person picks in setup's window after that.
/// </summary>
internal static class L
{
    public static bool Chinese { get; private set; }

    /// <summary>The language's own name for itself in the client's settings: "en" or "zh-Hans".</summary>
    public static string Code => Chinese ? "zh-Hans" : "en";

    /// <summary>The languages setup offers, each named in itself, as the client's Settings names them.</summary>
    public static readonly (string Code, string Name)[] Choices = [("en", "English"), ("zh-Hans", "中文简体")];

    /// <summary>The language the person chose in setup's window.</summary>
    public static void Use(string code) => Chinese = code == "zh-Hans";

    /// <summary>A family that has the Chinese glyphs itself: Segoe UI leans on font linking for them, which is not
    /// everywhere what a person reading Chinese expects.</summary>
    public static string Font => Chinese ? "Microsoft YaHei UI" : "Segoe UI";

    public static string FontSemibold => Chinese ? "Microsoft YaHei UI" : "Segoe UI Semibold";

    public static string T(string english) =>
        Chinese && Setup.Chinese.Words.TryGetValue(english, out var translated) ? translated : english;

    public static string T(string template, params object?[] values) => string.Format(CultureInfo.InvariantCulture, T(template), values);

    /// <summary>Two sentences as one line: a space between them in English, none in Chinese.</summary>
    public static string Join(string first, string second) => Chinese ? first + second : $"{first} {second}";

    public static void Choose(string? asked, string? installedIn)
    {
        var language = asked ?? Chosen(installedIn);
        Chinese = language switch
        {
            "zh-Hans" or "zh" => true,
            "en" => false,
            _ => CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase),
        };
    }

    // What the person running setup chose in the client: the "language" their settings keep, read and nothing else.
    private static string? Chosen(string? installedIn)
    {
        if (installedIn is null || WindowsIdentity.GetCurrent().User is not { } me) return null;
        // The shared settings (D25), else an earlier install's own folder for this person.
        var settings = Path.Combine(InstallLayout.Area(installedIn, "config"), "client.json");
        if (!File.Exists(settings)) settings = Path.Combine(InstallLayout.Users(installedIn), me.Value, "config", "client.json");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(settings));
            return document.RootElement.TryGetProperty("language", out var language) && language.ValueKind == JsonValueKind.String ? language.GetString() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}
