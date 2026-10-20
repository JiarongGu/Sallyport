using Microsoft.Win32;

namespace Sallyport.Setup;

/// <summary>
/// Windows' Text size (Settings → Accessibility → Text size), which setup's window follows as the client's does: its
/// fonts, and every size it lays out in 96-DPI units, times it. Read once, where Windows keeps it
/// (<c>HKCU\Software\Microsoft\Accessibility</c>, <c>TextScaleFactor</c>, 100 to 225; absent at 100).
/// </summary>
internal static class TextSize
{
    /// <summary>Tries a size without changing Windows' own (measuring the window at another size).</summary>
    private const string Override = "SALLYPORT_TEXT_SCALE";

    public static float Factor { get; } = Read();

    /// <summary>A 96-DPI length's factor on a screen of <paramref name="dpi"/>: the screen's scale and the text size.</summary>
    public static float Scale(int dpi) => dpi / 96f * Factor;

    /// <summary>A font's size at this text size.</summary>
    public static float Points(float points) => points * Factor;

    private static float Read()
    {
        if (float.TryParse(Environment.GetEnvironmentVariable(Override), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var asked) && asked is >= 1 and <= 2.25f)
            return asked;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility");
            return key?.GetValue("TextScaleFactor") is int percent and >= 100 and <= 225 ? percent / 100f : 1;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return 1; }
    }
}
