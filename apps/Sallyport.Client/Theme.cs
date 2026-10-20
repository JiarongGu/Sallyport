using Microsoft.Win32;
using Shenora.Windows;

namespace Sallyport.Client;

/// <summary>
/// The window's colours outside the page — the form behind it, the caption buttons the form paints in the page's title
/// bar, the splash's title bar, the tray's menu — for the OS's light or dark setting (docs/design/client-ui.md §5). They are the page's
/// own tokens (styles.css), which it follows through <c>prefers-color-scheme</c>, which WebView2 takes from the OS;
/// Shenora.Windows does not say which one is in force, so this reads it where Windows keeps it.
/// </summary>
internal sealed record Theme(bool Dark, Color Background, Color Sidebar, Color Surface, Color Accent, Color Text, Color Secondary,
    Color Rule, CaptionButtonColors Caption)
{
    // --background (the page, under its header, which the caption buttons sit on), --sidebar, --surface (a card, the
    // tray's menu), --accent, --text, --secondary, --divider; --caption-hover and --caption-pressed.
    public static readonly Theme DarkTheme = new(true, Color.FromArgb(0x14, 0x18, 0x1E), Color.FromArgb(0x0F, 0x12, 0x16),
        Color.FromArgb(0x1C, 0x21, 0x29), Color.FromArgb(0x5B, 0x9D, 0xFF), Color.FromArgb(0xE8, 0xEC, 0xF2), Color.FromArgb(0x9A, 0xA5, 0xB4),
        Color.FromArgb(0x2A, 0x31, 0x3B), new CaptionButtonColors
    {
        Surface = Color.FromArgb(0x14, 0x18, 0x1E),
        Hover = Color.FromArgb(0x26, 0x2B, 0x33),
        Pressed = Color.FromArgb(0x1D, 0x21, 0x28),
        Glyph = Color.FromArgb(0xE8, 0xEC, 0xF2),
        CloseHover = Color.FromArgb(0xC4, 0x2B, 0x1C),
        ClosePressed = Color.FromArgb(0xA3, 0x24, 0x17),
        CloseGlyphHot = Color.White,
    });

    public static readonly Theme LightTheme = new(false, Color.FromArgb(0xF5, 0xF7, 0xFA), Color.FromArgb(0xE9, 0xED, 0xF3),
        Color.White, Color.FromArgb(0x18, 0x5F, 0xC7), Color.FromArgb(0x18, 0x20, 0x2C), Color.FromArgb(0x5A, 0x65, 0x75),
        Color.FromArgb(0xE6, 0xEA, 0xF0), new CaptionButtonColors
    {
        Surface = Color.FromArgb(0xF5, 0xF7, 0xFA),
        Hover = Color.FromArgb(0xE9, 0xEC, 0xF0),
        Pressed = Color.FromArgb(0xDF, 0xE3, 0xE8),
        Glyph = Color.FromArgb(0x18, 0x20, 0x2C),
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
