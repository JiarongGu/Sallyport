using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Sallyport.Setup;

/// <summary>Setup's colours: the client's own (its styles.css), light or dark as Windows' apps are, so the window that
/// installs Sallyport looks like the one it installs.</summary>
sealed record SetupTheme(bool Dark, Color Background, Color Surface, Color Hover, Color Pressed, Color Text, Color Secondary,
    Color Border, Color Divider, Color Accent, Color OnAccent, Color Up, Color Busy, Color Trouble)
{
    public static readonly SetupTheme DarkTheme = new(true,
        Background: Rgb(0x14181E), Surface: Rgb(0x1C2129), Hover: Rgb(0x262B33), Pressed: Rgb(0x2D333D), Text: Rgb(0xE8ECF2),
        Secondary: Rgb(0x9AA5B4), Border: Rgb(0x6B7684), Divider: Rgb(0x2A313B), Accent: Rgb(0x5B9DFF), OnAccent: Rgb(0x0B1220),
        Up: Rgb(0x3CCF8E), Busy: Rgb(0xF2B84B), Trouble: Rgb(0xF06A6A));

    public static readonly SetupTheme LightTheme = new(false,
        Background: Rgb(0xF5F7FA), Surface: Rgb(0xFFFFFF), Hover: Rgb(0xE9ECF0), Pressed: Rgb(0xDFE3E8), Text: Rgb(0x18202C),
        Secondary: Rgb(0x5A6575), Border: Rgb(0x8A94A3), Divider: Rgb(0xE6EAF0), Accent: Rgb(0x185FC7), OnAccent: Rgb(0xFFFFFF),
        Up: Rgb(0x137347), Busy: Rgb(0x865900), Trouble: Rgb(0xB42332));

    private static Color Rgb(int rgb) => Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

    /// <summary>Dark only when Windows says apps are dark: the value is absent where the choice was never made, and
    /// Windows' own default is light. Setup runs as an administrator, often the person: their setting is the one read.</summary>
    public static SetupTheme Current()
    {
        using var personalize = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return personalize?.GetValue("AppsUseLightTheme") is 0 ? DarkTheme : LightTheme;
    }

    public static Color Mix(Color a, Color b, double t) =>
        Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    /// <summary>The title bar in the window's colours: dark where the theme is (Windows 10 2004 and later), and on
    /// Windows 11 the caption itself the colour of the header under it.</summary>
    public void Apply(Form form, Color caption)
    {
        var dark = Dark ? 1 : 0;
        DwmSetWindowAttribute(form.Handle, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
        var colour = caption.R | caption.G << 8 | caption.B << 16;
        DwmSetWindowAttribute(form.Handle, DwmCaptionColor, ref colour, sizeof(int));
    }

    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmCaptionColor = 35;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}

/// <summary>Drawing shared by setup's controls.</summary>
static class Draw
{
    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Sallyport's mark, as the client draws it (apps/Sallyport.Client/BrandMark.cs, the same 32-unit grid): a
    /// gateway with a way out rising through it, white on a rounded tile in the brand's gradient.</summary>
    public static void Gate(Graphics g, RectangleF r)
    {
        var saved = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TranslateTransform(r.X, r.Y);
        g.ScaleTransform(r.Width / 32f, r.Height / 32f);
        using (var tile = Rounded(new RectangleF(1, 1, 30, 30), 8))
        using (var fill = new LinearGradientBrush(new PointF(1, 1), new PointF(31, 31), Color.FromArgb(0x3D, 0x8B, 0xFF), Color.FromArgb(0x5B, 0x4B, 0xE0)))
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

    public static Icon GateIcon(int size)
    {
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap)) Gate(g, new RectangleF(0, 0, size, size));
        var handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}

/// <summary>A button in the client's style: the one that goes on filled with the accent, the others outlined. Still a
/// <see cref="Button"/> underneath, so keyboard, Enter/Escape and screen readers see an ordinary button.</summary>
sealed class ActionButton : Button
{
    private readonly SetupTheme _theme;
    private bool _hot, _down;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Primary { get; set; }

    public ActionButton(SetupTheme theme, string text, bool primary = false)
    {
        _theme = theme;
        Text = text;
        Primary = primary;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = new Size(96, 34);
        Padding = new Padding(14, 0, 14, 0);
        Margin = new Padding(8, 0, 0, 0);
        Cursor = Cursors.Hand;
        BackColor = theme.Background;
    }

    protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hot = _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var s = TextSize.Scale(DeviceDpi);
        g.Clear(Parent?.BackColor ?? _theme.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var box = new RectangleF(1.5f * s, 1.5f * s, Width - 3 * s, Height - 3 * s);
        using var shape = Draw.Rounded(box, 6 * s);
        Color fill, text, edge;
        if (!Enabled)
        {
            fill = Primary ? SetupTheme.Mix(_theme.Background, _theme.Border, 0.35) : Parent?.BackColor ?? _theme.Background;
            edge = SetupTheme.Mix(_theme.Background, _theme.Border, 0.5);
            text = SetupTheme.Mix(_theme.Background, _theme.Secondary, 0.7);
        }
        else if (Primary)
        {
            fill = _down ? SetupTheme.Mix(_theme.Accent, _theme.Text, 0.25) : _hot ? SetupTheme.Mix(_theme.Accent, _theme.Text, 0.12) : _theme.Accent;
            edge = fill;
            text = _theme.OnAccent;
        }
        else
        {
            fill = _down ? _theme.Pressed : _hot ? _theme.Hover : Parent?.BackColor ?? _theme.Background;
            edge = _theme.Border;
            text = _theme.Text;
        }
        using (var brush = new SolidBrush(fill)) g.FillPath(brush, shape);
        using (var pen = new Pen(edge, Math.Max(1, s))) g.DrawPath(pen, shape);
        if (Focused && ShowFocusCues)
        {
            using var ring = Draw.Rounded(new RectangleF(0.5f * s, 0.5f * s, Width - 1.5f * s, Height - 1.5f * s), 7 * s);
            using var pen = new Pen(Primary ? _theme.Text : _theme.Accent, 1.5f * s);
            g.DrawPath(pen, ring);
        }
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }
}

/// <summary>A choice with its reason under it: a check box in the client's style, title and description in one, the
/// whole of it clickable. A <see cref="CheckBox"/> underneath, so Space, tabbing and screen readers work as ever.</summary>
sealed class OptionBox : CheckBox
{
    private readonly SetupTheme _theme;
    private string _description = "";
    private readonly Font _titleFont;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Description
    {
        get => _description;
        set { _description = value; AccessibleDescription = value; FitHeight(); Invalidate(); }
    }

    public OptionBox(SetupTheme theme, Font font)
    {
        _theme = theme;
        Font = font;
        _titleFont = new Font(font.FontFamily, font.Size, FontStyle.Bold);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoSize = false;
        Cursor = Cursors.Hand;
        BackColor = theme.Background;
        Margin = new Padding(0, 0, 0, 12);
    }

    private int TextLeft => (int)(30 * TextSize.Scale(DeviceDpi));
    private const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;

    protected override void OnTextChanged(EventArgs e) { FitHeight(); base.OnTextChanged(e); }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); FitHeight(); }
    protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; base.OnEnabledChanged(e); }

    // As tall as its title and description wrapped to its width: nothing cut, nothing spare.
    private void FitHeight()
    {
        var width = Math.Max(1, Width - TextLeft);
        var title = TextRenderer.MeasureText(Text, _titleFont, new Size(width, 0), Wrap).Height;
        var description = _description.Length == 0 ? 0 : TextRenderer.MeasureText(_description, Font, new Size(width, 0), Wrap).Height + (int)(2 * TextSize.Scale(DeviceDpi));
        var height = Math.Max(title + description, (int)(22 * TextSize.Scale(DeviceDpi)));
        if (Height != height) Height = height;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var s = TextSize.Scale(DeviceDpi);
        g.Clear(Parent?.BackColor ?? _theme.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var faded = !Enabled;
        Color Fade(Color c) => faded ? SetupTheme.Mix(_theme.Background, c, 0.55) : c;

        var box = new RectangleF(1.5f * s, 1.5f * s, 18 * s, 18 * s);
        using (var shape = Draw.Rounded(box, 4 * s))
        {
            if (Checked)
            {
                using var fill = new SolidBrush(Fade(_theme.Accent));
                g.FillPath(fill, shape);
                using var tick = new Pen(Fade(_theme.OnAccent), 2 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                g.DrawLines(tick, [new PointF(box.X + 4.5f * s, box.Y + 9.5f * s), new PointF(box.X + 7.8f * s, box.Y + 12.8f * s), new PointF(box.X + 13.5f * s, box.Y + 5.5f * s)]);
            }
            else
            {
                using var fill = new SolidBrush(_theme.Surface);
                g.FillPath(fill, shape);
                using var pen = new Pen(Fade(_theme.Border), 1.2f * s);
                g.DrawPath(pen, shape);
            }
        }
        if (Focused && ShowFocusCues)
        {
            using var ring = Draw.Rounded(RectangleF.Inflate(box, 1.5f * s, 1.5f * s), 5.5f * s);
            using var pen = new Pen(_theme.Accent, 1.5f * s);
            g.DrawPath(pen, ring);
        }

        var width = Math.Max(1, Width - TextLeft);
        var title = TextRenderer.MeasureText(Text, _titleFont, new Size(width, 0), Wrap);
        TextRenderer.DrawText(g, Text, _titleFont, new Rectangle(TextLeft, 0, width, title.Height), Fade(_theme.Text), Wrap);
        if (_description.Length > 0)
            TextRenderer.DrawText(g, _description, Font, new Rectangle(TextLeft, title.Height + (int)(2 * s), width, Height), Fade(_theme.Secondary), Wrap);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _titleFont.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>A text box in a rounded field, its edge the accent while it has the keyboard.</summary>
sealed class FieldBox : Panel
{
    private readonly SetupTheme _theme;
    public TextBox Box { get; }

    public FieldBox(SetupTheme theme, Font font)
    {
        _theme = theme;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = theme.Background;
        Box = new TextBox { BorderStyle = BorderStyle.None, BackColor = theme.Surface, ForeColor = theme.Text, Font = font };
        Controls.Add(Box);
        Box.GotFocus += (_, _) => Invalidate();
        Box.LostFocus += (_, _) => Invalidate();
        Box.ReadOnlyChanged += (_, _) => { Box.BackColor = Box.ReadOnly ? theme.Background : theme.Surface; Invalidate(); };
        Margin = new Padding(0);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var s = TextSize.Scale(DeviceDpi);
        var inset = (int)(10 * s);
        Box.SetBounds(inset, (Height - Box.Height) / 2, Math.Max(1, Width - 2 * inset), Box.Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var s = TextSize.Scale(DeviceDpi);
        g.Clear(_theme.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var focused = Box.Focused && !Box.ReadOnly;
        using var shape = Draw.Rounded(new RectangleF(1, 1, Width - 2.5f, Height - 2.5f), 6 * s);
        using (var fill = new SolidBrush(Box.BackColor)) g.FillPath(fill, shape);
        using var pen = new Pen(focused ? _theme.Accent : SetupTheme.Mix(_theme.Background, _theme.Border, Box.ReadOnly ? 0.5 : 1), focused ? 2 * s : Math.Max(1, s));
        g.DrawPath(pen, shape);
    }
}

/// <summary>A drop-down in the field's own look: the choice, and a chevron 12 from its edge, in a rounded field that opens
/// a menu of the choices; the arrow keys move the choice as a combo box's do. A <see cref="Button"/> underneath, as
/// <see cref="ActionButton"/> is: a plain custom control reaches no screen reader at all (WinForms gives it no UI
/// Automation provider), a button does — named for what it chooses, the choice as its description.</summary>
sealed class ChoiceBox : Button
{
    private readonly SetupTheme _theme;
    private readonly string[] _choices;
    private readonly ContextMenuStrip _menu = new() { ShowImageMargin = false, ShowCheckMargin = true };
    private bool _hot;

    public event EventHandler? Chosen;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Selected { get; private set; }

    public ChoiceBox(SetupTheme theme, Font font, string[] choices, int selected)
    {
        _theme = theme;
        _choices = choices;
        Selected = Math.Max(0, selected);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        Font = font;
        BackColor = theme.Surface;
        Cursor = Cursors.Hand;
        AccessibleDescription = choices[Selected];
        _menu.BackColor = theme.Surface;
        _menu.ForeColor = theme.Text;
        _menu.Font = font;
        for (var i = 0; i < choices.Length; i++)
        {
            var index = i;
            _menu.Items.Add(new ToolStripMenuItem(choices[i], null, (_, _) => Choose(index)) { Checked = i == Selected });
        }
    }

    private void Choose(int index)
    {
        if (index == Selected || index < 0 || index >= _choices.Length) return;
        Selected = index;
        for (var i = 0; i < _menu.Items.Count; i++) ((ToolStripMenuItem)_menu.Items[i]).Checked = i == index;
        AccessibleDescription = _choices[index];
        AccessibilityNotifyClients(System.Windows.Forms.AccessibleEvents.ValueChange, -1);
        Invalidate();
        Chosen?.Invoke(this, EventArgs.Empty);
    }

    public void Open()
    {
        Focus();
        _menu.MinimumSize = new Size(Width, 0);
        _menu.Show(this, new Point(0, Height));
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Open();
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e is { Alt: true, KeyCode: Keys.Down }) Open();
        else if (e.KeyCode == Keys.Down) Choose(Selected + 1);
        else if (e.KeyCode == Keys.Up) Choose(Selected - 1);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hot = true; Invalidate(); }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hot = false; Invalidate(); }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }

    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var s = TextSize.Scale(DeviceDpi);
        g.Clear(Parent?.BackColor ?? _theme.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = Draw.Rounded(new RectangleF(1, 1, Width - 2.5f, Height - 2.5f), 6 * s);
        using (var fill = new SolidBrush(_hot ? _theme.Hover : _theme.Surface)) g.FillPath(fill, shape);
        using (var pen = new Pen(Focused ? _theme.Accent : _theme.Border, Focused ? 2 * s : Math.Max(1, s))) g.DrawPath(pen, shape);
        var chevron = 12 * s;
        TextRenderer.DrawText(g, _choices[Selected], Font, new Rectangle((int)(10 * s), 0, Width - (int)(10 * s + chevron * 2 + 4 * s), Height), _theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        // The chevron's 12-pixel box, 12 from the edge: the same room the client's drop-down leaves.
        float x = Width - 12 * s - chevron, y = (Height - chevron) / 2;
        using var stroke = new Pen(_theme.Secondary, 1.5f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(stroke, [new PointF(x + 2.5f * s, y + 4.5f * s), new PointF(x + 6 * s, y + 8 * s), new PointF(x + 9.5f * s, y + 4.5f * s)]);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _menu.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>How far setup is: a bar in the accent on a quiet track, read by screen readers as a progress bar.</summary>
sealed class Meter : Control
{
    private readonly SetupTheme _theme;
    private double _value;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double Value
    {
        get => _value;
        set { _value = Math.Clamp(value, 0, 1); AccessibilityNotifyClients(System.Windows.Forms.AccessibleEvents.ValueChange, -1); Invalidate(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Fill { get; set; }

    public Meter(SetupTheme theme)
    {
        _theme = theme;
        Fill = theme.Accent;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = theme.Background;
        AccessibleRole = AccessibleRole.ProgressBar;
        AccessibleName = "Progress";
        Height = 8;
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new MeterAccessible(this);

    private sealed class MeterAccessible(Meter owner) : ControlAccessibleObject(owner)
    {
        public override string? Value => $"{owner._value * 100:F0}%";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? _theme.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var track = new RectangleF(0, 0, Width - 1, Height - 1);
        using (var shape = Draw.Rounded(track, Height / 2f))
        using (var brush = new SolidBrush(_theme.Hover))
            g.FillPath(brush, shape);
        if (_value <= 0) return;
        var done = new RectangleF(0, 0, Math.Max(Height, (float)(track.Width * _value)), Height - 1);
        using (var shape = Draw.Rounded(done, Height / 2f))
        using (var brush = new SolidBrush(Fill))
            g.FillPath(brush, shape);
    }
}

/// <summary>How setup ended, drawn: a tick, a warning or a cross on a disc of its colour.</summary>
sealed class Mark : Control
{
    public enum Kind { Done, Partly, Failed }

    private readonly SetupTheme _theme;
    private Kind _kind;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Kind Shown
    {
        get => _kind;
        set { _kind = value; Invalidate(); }
    }

    public Mark(SetupTheme theme)
    {
        _theme = theme;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = theme.Background;
        Size = new Size(44, 44);
        Margin = new Padding(0, 0, 0, 12);
        AccessibleRole = AccessibleRole.Graphic;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? _theme.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var colour = _kind switch { Kind.Done => _theme.Up, Kind.Partly => _theme.Busy, _ => _theme.Trouble };
        var r = new RectangleF(1, 1, Width - 3, Height - 3);
        using (var disc = new SolidBrush(colour)) g.FillEllipse(disc, r);
        float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, u = r.Width / 44f;
        using var pen = new Pen(_theme.Dark ? _theme.Background : Color.White, 3.6f * u) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        switch (_kind)
        {
            case Kind.Done:
                g.DrawLines(pen, [new PointF(cx - 9 * u, cy + 0.5f * u), new PointF(cx - 2.5f * u, cy + 7 * u), new PointF(cx + 10 * u, cy - 6.5f * u)]);
                break;
            case Kind.Partly:
                g.DrawLine(pen, cx, cy - 10 * u, cx, cy + 3 * u);
                g.DrawLine(pen, cx, cy + 9.5f * u, cx, cy + 9.6f * u);
                break;
            default:
                g.DrawLine(pen, cx - 8 * u, cy - 8 * u, cx + 8 * u, cy + 8 * u);
                g.DrawLine(pen, cx + 8 * u, cy - 8 * u, cx - 8 * u, cy + 8 * u);
                break;
        }
    }
}
