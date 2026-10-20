using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Sallyport.Client;

/// <summary>
/// The window while its page loads: the page's sidebar with its menu button and the brand where the page puts them, so
/// the window looks like itself rather than a bare strip of caption buttons, and a thin line in the accent where the
/// page's content begins, running while it waits. Its top moves the window, as the page's header and sidebar do.
/// </summary>
internal sealed class Splash : Control
{
    // The page's geometry in CSS pixels (styles.css): the sidebar 232 wide (60 when the window is under 700), its head
    // 48 high with ☰ in a 40 button at 8 and the mark 20 across at 56, the name at 86 in 15 at weight 600; the page's
    // header 48 high. Drawn where the page draws them, the page replaces this without anything moving.
    private const float HeaderHeight = 48, Sidebar = 232, NarrowSidebar = 60, NarrowBelow = 700, MenuLeft = 8, MenuSize = 40,
        MarkLeft = 56, MarkSize = 20, NameLeft = 86, NameSize = 15;

    /// <summary>The loading line: 2 CSS pixels where the content begins, a third of its width crossing in this long.</summary>
    private const float LineHeight = 2;
    private static readonly TimeSpan Crossing = TimeSpan.FromMilliseconds(1400);

    private const int WM_NCLBUTTONDOWN = 0x00A1, HTCAPTION = 2, SPI_GETCLIENTAREAANIMATION = 0x1042;

    private readonly System.Windows.Forms.Timer _frames = new() { Interval = 16 };
    private readonly Stopwatch _since = Stopwatch.StartNew();
    private Theme _theme;

    public Splash(Theme theme)
    {
        _theme = theme;
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

    private bool Narrow => Width / CssPixel < NarrowBelow;

    private int SidebarWidth => Scaled(Narrow ? NarrowSidebar : Sidebar);

    private Rectangle LineBounds() => new(SidebarWidth, Scaled(HeaderHeight), Width - SidebarWidth, Math.Max(2, Scaled(LineHeight)));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var scale = CssPixel;
        using (var background = new SolidBrush(_theme.Background)) g.FillRectangle(background, ClientRectangle);
        using (var sidebar = new SolidBrush(_theme.Sidebar)) g.FillRectangle(sidebar, 0, 0, SidebarWidth, Height);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        // ☰: three strokes of the page's 16-unit icon, centred in its button.
        var icon = (MenuLeft + (MenuSize - 16) / 2) * scale;
        var iconTop = (HeaderHeight - 16) / 2 * scale;
        using (var stroke = new Pen(_theme.Text, 1.5f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            foreach (var y in new[] { 4f, 8f, 12f })
                g.DrawLine(stroke, icon + 2.5f * scale, iconTop + y * scale, icon + 13.5f * scale, iconTop + y * scale);
        if (!Narrow)
        {
            DrawMark(g, StatusTray.Brand, MarkLeft * scale, (HeaderHeight - MarkSize) / 2 * scale, MarkSize * scale);
            g.SmoothingMode = SmoothingMode.None;
            using var font = NameFont(NameSize * scale);
            var name = new Rectangle(Scaled(NameLeft), 0, SidebarWidth - Scaled(NameLeft), Scaled(HeaderHeight));
            TextRenderer.DrawText(g, "Sallyport", font, name, _theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
        g.SmoothingMode = SmoothingMode.None;

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

    /// <summary>The page's GateMark (a 32-unit view box): a disc in the brand's colour, an arched door in white.</summary>
    private static void DrawMark(Graphics g, Color brand, float left, float top, float size)
    {
        var unit = size / 32;
        using var disc = new SolidBrush(brand);
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
        if (e.Button != MouseButtons.Left || e.Y >= Scaled(HeaderHeight) || FindForm() is not { } window) return;
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
