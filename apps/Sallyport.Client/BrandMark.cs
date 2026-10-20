using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Sallyport.Client;

/// <summary>
/// Sallyport's mark: a gateway — a sallyport is a fortress's gate — with a way out rising through it, white on a rounded
/// tile. One drawing for every place the host shows it: the window's and the taskbar's icon, the tray's (its tile in the
/// state's colour), the splash. The page draws the same 32-unit geometry (Chrome.tsx, GateMark); icon.ps1 the setup's.
/// </summary>
internal static class BrandMark
{
    /// <summary>The tile's gradient, from its top left to its bottom right.</summary>
    public static readonly Color Top = Color.FromArgb(0x3D, 0x8B, 0xFF), Bottom = Color.FromArgb(0x5B, 0x4B, 0xE0);

    public static void Paint(Graphics g, RectangleF box) => Paint(g, box, Top, Bottom);

    public static void Paint(Graphics g, RectangleF box, Color top, Color bottom)
    {
        var saved = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TranslateTransform(box.X, box.Y);
        g.ScaleTransform(box.Width / 32f, box.Height / 32f);

        using (var tile = Rounded(1, 1, 30, 30, 8))
        using (var fill = new LinearGradientBrush(new PointF(1, 1), new PointF(31, 31), top, bottom))
            g.FillPath(fill, tile);

        using var stroke = new Pen(Color.White, 2.75f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using (var gate = new GraphicsPath())
        {
            gate.AddLine(8.5f, 24f, 8.5f, 15.5f);
            gate.AddArc(8.5f, 8f, 15f, 15f, 180, 180);
            gate.AddLine(23.5f, 15.5f, 23.5f, 24f);
            g.DrawPath(stroke, gate);
        }
        g.DrawLine(stroke, 16f, 24.5f, 16f, 15.5f);
        g.DrawLines(stroke, [new PointF(13f, 18.5f), new PointF(16f, 15.5f), new PointF(19f, 18.5f)]);
        g.Restore(saved);
    }

    /// <summary>The mark as an icon of <paramref name="size"/> pixels; <paramref name="tone"/> fills its tile (a shade
    /// deeper towards its corner) instead of the brand's gradient — the tray's state.</summary>
    public static Icon Icon(int size, Color? tone = null)
    {
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            if (tone is { } t) Paint(g, new RectangleF(0, 0, size, size), t, Deeper(t));
            else Paint(g, new RectangleF(0, 0, size, size));
        }
        var handle = bitmap.GetHicon();
        try { return (Icon)System.Drawing.Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    private static Color Deeper(Color c) => Color.FromArgb(c.A, c.R * 4 / 5, c.G * 4 / 5, c.B * 4 / 5);

    private static GraphicsPath Rounded(float x, float y, float w, float h, float r)
    {
        var d = 2 * r;
        var path = new GraphicsPath();
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
