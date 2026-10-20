using Microsoft.Win32;
using Shenora.Windows;

namespace Sallyport.Client;

/// <summary>
/// The window's colours outside the page — the form behind it, the caption buttons the form paints in the page's title
/// bar, the splash's title bar — for the OS's light or dark setting (docs/design/client-ui.md §5). They are the page's
/// own tokens (styles.css), which it follows through <c>prefers-color-scheme</c>, which WebView2 takes from the OS;
/// Shenora.Windows does not say which one is in force, so this reads it where Windows keeps it.
/// </summary>
internal sealed record Theme(bool Dark, Color Background, Color Accent, Color Text, Color Rule, CaptionButtonColors Caption)
{
    public static readonly Theme DarkTheme = new(true, Color.FromArgb(0x15, 0x17, 0x1C), Color.FromArgb(0x5B, 0x9D, 0xFF),
        Color.FromArgb(0xE6, 0xE8, 0xEE), Color.FromArgb(0x67, 0x74, 0x87), new CaptionButtonColors
    {
        Surface = Color.FromArgb(0x1D, 0x20, 0x27),
        Hover = Color.FromArgb(0x2A, 0x30, 0x3B),
        Pressed = Color.FromArgb(0x35, 0x3D, 0x4B),
        Glyph = Color.FromArgb(0xE6, 0xE8, 0xEE),
        CloseHover = Color.FromArgb(0xC4, 0x2B, 0x1C),
        ClosePressed = Color.FromArgb(0xA3, 0x24, 0x17),
        CloseGlyphHot = Color.White,
    });

    public static readonly Theme LightTheme = new(false, Color.FromArgb(0xF5, 0xF6, 0xF8), Color.FromArgb(0x18, 0x5F, 0xC7),
        Color.FromArgb(0x19, 0x22, 0x30), Color.FromArgb(0x7A, 0x87, 0x97), new CaptionButtonColors
    {
        Surface = Color.White,
        Hover = Color.FromArgb(0xE9, 0xED, 0xF3),
        Pressed = Color.FromArgb(0xDC, 0xE3, 0xEC),
        Glyph = Color.FromArgb(0x19, 0x22, 0x30),
        CloseHover = Color.FromArgb(0xC4, 0x2B, 0x1C),
        ClosePressed = Color.FromArgb(0xA3, 0x24, 0x17),
        CloseGlyphHot = Color.White,
    });

    /// <summary>Dark only when Windows says apps are dark: the value is absent where the choice was never made, and
    /// Windows' own default is light.</summary>
    public static Theme Current()
    {
        using var personalize = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return personalize?.GetValue("AppsUseLightTheme") is 0 ? DarkTheme : LightTheme;
    }
}
