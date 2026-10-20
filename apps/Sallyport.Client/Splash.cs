using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Sallyport.Client;

/// <summary>
/// The window while its page loads: the page's title bar and tab strip with the brand where the page puts it, so the
/// window looks like itself rather than a bare strip of caption buttons, and a thin line in the accent under the strip
/// running while it waits. Its title bar moves the window and maximizes it on a double click, as the page's does.
/// </summary>
internal sealed class Splash : Control
{
    // The page's geometry in CSS pixels (styles.css): .title-bar 40 high, .brand 12 from the left with a gap of 8, the
    // mark 22 across, the name 14 at weight 600, .navigation 44 high ending in a 1px rule. Drawn where the page draws
    // them, the page replaces this without anything moving.
    private const float TitleBarHeight = 40, NavigationHeight = 44, BrandLeft = 12, MarkSize = 22, BrandGap = 8, NameSize = 14;

    /// <summary>The loading line: 2 CSS pixels under the rule, a third of the width crossing in this long.</summary>
    private const float LineHeight = 2;
    private static readonly TimeSpan Crossing = TimeSpan.FromMilliseconds(1400);

    private const int WM_NCLBUTTONDOWN = 0x00A1, HTCAPTION = 2, SPI_GETCLIENTAREAANIMATION = 0x1042;

    private readonly Action _toggleMaximize;
    private readonly System.Windows.Forms.Timer _frames = new() { Interval = 16 };
    private readonly Stopwatch _since = Stopwatch.StartNew();
    private Theme _theme;

    public Splash(Theme theme, Action toggleMaximize)
    {
        _theme = theme;
        _toggleMaximize = toggleMaximize;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Fill;
        _frames.Tick += (_, _) => Invalidate(LineBounds());
        // Windows' "Show animations" off is the page's prefers-reduced-motion: the line then stays away.
        if (Animates()) _frames.Start();
    }

    public void Apply(Theme theme)
    {
        _theme = theme;
        Invalidate();
    }

    /// <summary>Device pixels per CSS pixel, as WebView2 scales the page on this monitor.</summary>
    private float CssPixel => DeviceDpi / 96f;

    private int Scaled(float css) => (int)Math.Round(css * CssPixel);

    private Rectangle LineBounds() => new(0, Scaled(TitleBarHeight + NavigationHeight), Width, Math.Max(2, Scaled(LineHeight)));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var scale = CssPixel;
        using (var background = new SolidBrush(_theme.Background)) g.FillRectangle(background, ClientRectangle);
        var chrome = Scaled(TitleBarHeight + NavigationHeight);
        using (var surface = new SolidBrush(_theme.Caption.Surface)) g.FillRectangle(surface, 0, 0, Width, chrome);
        var rule = Math.Max(1, (int)scale);
        using (var line = new SolidBrush(_theme.Rule)) g.FillRectangle(line, 0, chrome - rule, Width, rule);

        float size = MarkSize * scale, left = BrandLeft * scale, top = (TitleBarHeight * scale - size) / 2;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        DrawMark(g, _theme.Accent, left, top, size);
        g.SmoothingMode = SmoothingMode.None;

        using var font = NameFont(NameSize * scale);
        var name = new Rectangle((int)(left + size + BrandGap * scale), 0, Width, Scaled(TitleBarHeight));
        TextRenderer.DrawText(g, "Sallyport", font, name, _theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

        if (_frames.Enabled) DrawLine(g);
    }

    /// <summary>A third of the width crossing from beyond the left edge to beyond the right, slow at either end and quick
    /// across the middle (a sine ease), then again.</summary>
    private void DrawLine(Graphics g)
    {
        var bounds = LineBounds();
        var t = _since.Elapsed.TotalMilliseconds % Crossing.TotalMilliseconds / Crossing.TotalMilliseconds;
        var eased = (1 - Math.Cos(Math.PI * t)) / 2;
        var length = bounds.Width / 3f;
        var x = (float)(-length + (bounds.Width + length) * eased);
        using var accent = new SolidBrush(_theme.Accent);
        g.FillRectangle(accent, x, bounds.Y, length, bounds.Height);
    }

    /// <summary>The page's first choice of font where Windows has it (Segoe UI Variable, Windows 11), at its weight 600.</summary>
    private static Font NameFont(float pixels)
    {
        var font = new Font("Segoe UI Variable Text Semibold", pixels, GraphicsUnit.Pixel);
        // A family Windows does not have comes back as another one, not as an error.
        if (font.Name == "Segoe UI Variable Text Semibold") return font;
        font.Dispose();
        return new Font("Segoe UI Semibold", pixels, GraphicsUnit.Pixel);
    }

    /// <summary>The page's GateMark (a 32-unit view box): a disc in the accent, an arched door in white.</summary>
    private static void DrawMark(Graphics g, Color accent, float left, float top, float size)
    {
        var unit = size / 32;
        using var disc = new SolidBrush(accent);
        g.FillEllipse(disc, left + 0.5f * unit, top + 0.5f * unit, 31 * unit, 31 * unit);
        using var door = new GraphicsPath();
        door.AddArc(left + 10 * unit, top + 9 * unit, 12 * unit, 12 * unit, 180, 180);
        door.AddLine(left + 22 * unit, top + 15 * unit, left + 22 * unit, top + 25 * unit);
        door.AddLine(left + 22 * unit, top + 25 * unit, left + 10 * unit, top + 25 * unit);
        door.CloseFigure();
        g.FillPath(Brushes.White, door);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || e.Y >= Scaled(TitleBarHeight) || FindForm() is not { } window) return;
        if (e.Clicks >= 2)
        {
            _toggleMaximize();
            return;
        }
        // The OS's own move loop, as the page's START_DRAG begins it: the button is still down, so it follows the pointer.
        ReleaseCapture();
        SendMessage(window.Handle, WM_NCLBUTTONDOWN, HTCAPTION, IntPtr.Zero);
    }

    private static bool Animates() => !SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, out var on, 0) || on;

    protected override void Dispose(bool disposing)
    {
        if (disposing) _frames.Dispose();
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, int wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(int action, int param, out bool value, int winIni);
}
