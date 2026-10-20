using Microsoft.Win32;

namespace Sallyport.Client;

/// <summary>
/// Windows' Text size (Settings → Accessibility → Text size): the factor the page's root size is multiplied by, so a
/// person who made text bigger for every app gets it here too. WebView2 follows the screen's scale by itself, not this.
/// Read where Windows keeps it (<c>HKCU\Software\Microsoft\Accessibility</c>, <c>TextScaleFactor</c>, 100 to 225; absent
/// at 100), again whenever Windows says a setting changed or the window comes back.
/// </summary>
internal static class TextSize
{
    /// <summary>Tries a size without changing Windows' own (measuring the page at another size).</summary>
    private const string Override = "SALLYPORT_TEXT_SCALE";

    public static double Factor { get; private set; } = Read();

    /// <summary>Reads it again; true when it changed.</summary>
    public static bool Refresh()
    {
        var now = Read();
        if (now == Factor) return false;
        Factor = now;
        return true;
    }

    private static double Read()
    {
        if (double.TryParse(Environment.GetEnvironmentVariable(Override), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var asked) && asked is >= 1 and <= 2.25)
            return asked;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility");
            return key?.GetValue("TextScaleFactor") is int percent and >= 100 and <= 225 ? percent / 100.0 : 1;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return 1; }
    }
}
