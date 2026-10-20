using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Sallyport.Client;

/// <summary>
/// The window while its page loads: the page's rail with the mark where the page puts it, so the window looks like
/// itself rather than a bare strip of caption buttons, and a thin line in the accent where the
/// page's content begins, running while it waits. Its top moves the window, as the page's header and sidebar do.
/// </summary>
internal sealed class Splash : Control
{
    // The page's geometry in CSS pixels (styles.css): the rail 76 wide, always; its head 48 high with the mark 28 across
    // at 24, centred; the page's header 48 high. Drawn where the page draws them, the page replaces this without anything
    // moving.
    private const float HeaderHeight = 48, Rail = 76, MarkLeft = 24, MarkSize = 28;

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

    private int SidebarWidth => Scaled(Rail);

    private Rectangle LineBounds() => new(SidebarWidth, Scaled(HeaderHeight), Width - SidebarWidth, Math.Max(2, Scaled(LineHeight)));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var scale = CssPixel;
        using (var background = new SolidBrush(_theme.Background)) g.FillRectangle(background, ClientRectangle);
        using (var sidebar = new SolidBrush(_theme.Sidebar)) g.FillRectangle(sidebar, 0, 0, SidebarWidth, Height);

        BrandMark.Paint(g, new RectangleF(MarkLeft * scale, (HeaderHeight - MarkSize) / 2 * scale, MarkSize * scale, MarkSize * scale));

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
        var x = (float)(bounds.X - length + (bounds.Width + length) * eased);
        using var accent = new SolidBrush(_theme.Accent);
        // Within the content's top edge: it never runs over the sidebar.
        var clip = g.Clip;
        g.SetClip(bounds);
        g.FillRectangle(accent, x, bounds.Y, length, bounds.Height);
        g.Clip = clip;
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
