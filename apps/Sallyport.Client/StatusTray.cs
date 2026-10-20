using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Shenora.Core.Ipc;

namespace Sallyport.Client;

/// <summary>
/// The notification-area icon, drawn in the colour of the window's summary, and its menu: each profile a row that
/// connects or disconnects it (docs/design/client-ui.md §7). It renders the same view the window does, so the two
/// never disagree. Its own rather than Shenora's <c>TrayIcon</c>, which sets its icon and text once, when it is made —
/// and a VPN's tray is read for whether it is connected.
/// </summary>
internal sealed class StatusTray : IDisposable
{
    private static readonly Dictionary<Tone, Color> Colours = new()
    {
        [Tone.Off] = Color.FromArgb(0x6B, 0x72, 0x80),
        [Tone.Busy] = Color.FromArgb(0xF2, 0xB8, 0x4B),
        [Tone.Up] = Color.FromArgb(0x3C, 0xCF, 0x8E),
        [Tone.Trouble] = Color.FromArgb(0xF0, 0x6A, 0x6A),
        [Tone.Info] = BrandMark.Top,
    };

    private readonly Form _window;
    private readonly ClientCore _core;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly Dictionary<Tone, Icon> _icons = [];
    private Font? _bold;
    // Windows' own glyphs (Settings, Exit), at the size the menu's DPI wants: kept, as each rebuild would make a new one.
    private Font? _glyphs;
    private bool _exiting;
    private bool _toldHidden;

    public StatusTray(Form window, ClientCore core)
    {
        _window = window;
        _core = core;
        _menu = new ContextMenuStrip();
        // Rebuilt as it opens, so it is never a state behind.
        _menu.Opening += (_, e) => { Fill(); e.Cancel = false; };
        Fill();
        _icon = new NotifyIcon { ContextMenuStrip = _menu, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
        _window.FormClosing += OnClosing;
        _core.Changed += OnChanged;
        _core.Noticed += OnNoticed;
        Refresh();
    }

    public void ShowWindow()
    {
        if (_window.IsDisposed) return;
        _window.Show();
        if (_window.WindowState == FormWindowState.Minimized) _window.WindowState = FormWindowState.Normal;
        _window.Activate();
    }

    private void ShowPage(Page page)
    {
        _core.Navigate(page);
        ShowWindow();
    }

    public void Exit()
    {
        _exiting = true;
        _window.Close();
    }

    public void Tell(string text, ToolTipIcon kind = ToolTipIcon.Info) => _icon.ShowBalloonTip(5000, "Sallyport", text, kind);

    private void OnChanged()
    {
        if (_window.IsDisposed || !_window.IsHandleCreated) return;
        try
        {
            _window.BeginInvoke(() =>
            {
                Refresh();
                // A sign-in a connect waits for (D29) — from the tray's Connect, or a server that did not accept the last
                // one while the window was away — is answered in the window: brought up, once for each.
                var asking = _core.Snapshot().SignIn?.Token;
                if (asking is not null && asking != _signInShown) ShowWindow();
                _signInShown = asking;
            });
        }
        catch (InvalidOperationException) { /* the window is going away */ }
    }

    private string? _signInShown;

    /// <summary>The window shows every notice; the tray repeats one only when the window is out of sight.</summary>
    private void OnNoticed(Notice notice)
    {
        if (_window.IsDisposed || !_window.IsHandleCreated) return;
        try
        {
            _window.BeginInvoke(() =>
            {
                if (_window.Visible && _window.WindowState != FormWindowState.Minimized) return;
                Tell(notice.Text, notice.Kind switch { NoticeKind.Error => ToolTipIcon.Error, NoticeKind.Warning => ToolTipIcon.Warning, _ => ToolTipIcon.Info });
            });
        }
        catch (InvalidOperationException) { /* the window is going away */ }
    }

    private void Refresh()
    {
        var summary = _core.Snapshot().Summary;
        if (!_icons.TryGetValue(summary.Tone, out var icon))
            _icons[summary.Tone] = icon = BrandMark.Icon(SystemInformation.SmallIconSize.Width, Colours[summary.Tone]);
        _icon.Icon = icon;
        _icon.Text = TrayMenu.Tooltip(summary.Text);
    }

    /// <summary>The menu as <see cref="TrayMenu"/> builds it from the window's views, in the theme Windows is in now.</summary>
    private void Fill()
    {
        // Cleared items are not disposed by the strip; each holds a handle-backed image and font until it is.
        foreach (var old in _menu.Items.Cast<ToolStripItem>().ToList()) old.Dispose();
        var state = _core.Snapshot();
        var scale = _menu.DeviceDpi / 96f;
        if (_glyphs is null || Math.Abs(_glyphs.Size - 16 * scale) > 0.1f)
        {
            _glyphs?.Dispose();
            // Windows 11's icon font, else Windows 10's: the same glyphs at the same code points.
            var family = FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
            _glyphs = new Font(family, 16 * scale, GraphicsUnit.Pixel);
        }
        _menu.Renderer = new MenuRenderer(Theme.Current(), scale, _glyphs);
        Style(_menu);
        var renderer = (MenuRenderer)_menu.Renderer;
        foreach (var entry in TrayMenu.Build(state.Profiles, state.Network.StatusText)) _menu.Items.Add(Item(entry, renderer));
        Fit(_menu);
    }

    /// <summary>Every entry as wide as the widest one wants (<see cref="MenuRenderer.Wants"/>): WinForms would size them
    /// from where it puts the text itself, and draws nothing of an entry outside its own width.</summary>
    private static void Fit(ToolStripDropDownMenu menu)
    {
        var renderer = (MenuRenderer)menu.Renderer;
        var widest = menu.Items.OfType<ToolStripMenuItem>().Select(renderer.Wants).Append(renderer.MinimumWidth).Max();
        // The layout gives every entry the widest one's preferred width, which the right padding raises to the width wanted;
        // the window is held to it, as WinForms would size the window by its own measure.
        var preferred = menu.Items.OfType<ToolStripMenuItem>().Select(i => i.GetPreferredSize(Size.Empty).Width).DefaultIfEmpty(0).Max();
        var extra = Math.Max(0, widest - preferred);
        foreach (var entry in menu.Items.OfType<ToolStripMenuItem>())
            entry.Padding = new Padding(entry.Padding.Left, entry.Padding.Top, entry.Padding.Right + extra, entry.Padding.Bottom);
        var window = widest + menu.Padding.Horizontal + 2;
        menu.MinimumSize = new Size(window, 0);
        menu.MaximumSize = new Size(window, 0);

        // Room above the first entry's highlight and below the last's as at its sides (MenuRenderer.Edge): the menu's own
        // top and bottom padding is not laid out (the first entry 2 px from the top at 200%), an entry's margin is. The
        // highlight is drawn 1 px inside its entry.
        var entries = menu.Items.Cast<ToolStripItem>().ToList();
        if (entries.Count > 0)
        {
            var display = menu.DisplayRectangle;
            entries[0].Margin = entries[0].Margin with { Top = Math.Max(0, renderer.Edge - display.Top - 1) };
            entries[^1].Margin = entries[^1].Margin with { Bottom = Math.Max(0, renderer.Edge - (menu.ClientSize.Height - display.Bottom) - 1) };
        }
        // An entry is laid out to a width of the layout's own measure (to 360 of 371 at 200%, from x 0, a separator from
        // 2 to 369); its highlight is drawn within it, so it must reach as far as the highlight does.
        menu.PerformLayout();
        foreach (var entry in menu.Items.OfType<ToolStripMenuItem>())
        {
            var shortBy = menu.ClientSize.Width - renderer.Edge - entry.Bounds.Right;
            if (shortBy > 0) entry.Padding = entry.Padding with { Right = entry.Padding.Right + shortBy };
        }
        menu.PerformLayout();
        // The bottom as measured, not as the padding says: the window ends 2 px under the last entry at 200% whatever its
        // bottom padding (the last highlight 3 px from the edge, the first 9), and it grows by what the margin adds.
        if (entries.Count > 0)
        {
            var under = menu.ClientSize.Height - entries[^1].Bounds.Bottom + 1;
            if (under != renderer.Edge)
            {
                entries[^1].Margin = entries[^1].Margin with { Bottom = Math.Max(0, entries[^1].Margin.Bottom + renderer.Edge - under) };
                menu.PerformLayout();
            }
        }
    }

    private ToolStripItem Item(TrayEntry entry, MenuRenderer renderer)
    {
        if (entry.Kind == TrayEntryKind.Separator) return new ToolStripSeparator { AutoSize = false, Height = renderer.SeparatorHeight };
        var item = new ToolStripMenuItem(entry.Text)
        {
            Enabled = entry.Enabled, ToolTipText = entry.Tip ?? "", Tag = entry,
            // A profile's row is taller: its state and what pressing it does are on it too.
            Padding = entry.Tone is null ? renderer.ItemPadding : renderer.RowPadding,
            // What a screen reader says of a row is what it shows: the name, where it is, what pressing it does.
            AccessibleName = string.Join(" · ", new[] { entry.Text, entry.Status, entry.Action }.OfType<string>()),
        };
        if (entry.Default) item.Font = _bold ??= new Font(item.Font, FontStyle.Bold);
        if (entry.Kind == TrayEntryKind.Command) item.Click += (_, _) => Do(entry);
        return item;
    }

    /// <summary>A menu in Windows 11's manner: room around each entry, rounded corners; its colours are the renderer's.</summary>
    private static void Style(ToolStripDropDownMenu menu)
    {
        menu.ShowImageMargin = false;
        menu.ShowCheckMargin = false;
        menu.ShowItemToolTips = true;
        var scale = menu.DeviceDpi / 96f;
        menu.Padding = new Padding(0, (int)(4 * scale), 0, (int)(4 * scale));
        if (menu.IsHandleCreated) Round(menu);
        else menu.HandleCreated += (_, _) => Round(menu);
    }

    private static void Round(ToolStrip menu)
    {
        var preference = DWMWCP_ROUNDSMALL;
        // Refused before Windows 11, where the menu keeps its square corners and draws its own border.
        if (DwmSetWindowAttribute(menu.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int)) == 0 && menu.Renderer is MenuRenderer renderer)
            renderer.SystemDrawsBorder = true;
    }

    /// <summary>What an entry does. A question it raises (disconnecting a profile apps go through) is asked in the window,
    /// so the window comes up for it.</summary>
    private void Do(TrayEntry entry)
    {
        var id = entry.Target ?? "";
        switch (entry.Verb)
        {
            case TrayVerb.OpenWindow: ShowPage(Page.Network); return;
            case TrayVerb.OpenSettings: ShowPage(Page.Settings); return;
            case TrayVerb.Exit: Exit(); return;
            case TrayVerb.Connect: Run(() => { _core.Connect(id); return Task.CompletedTask; }); return;
            case TrayVerb.Disconnect: Run(() => _core.DisconnectAsync(id)); return;
        }
    }

    private async void Run(Func<Task> action)
    {
        var asked = _core.Snapshot().Confirmation?.Token;
        try { await action(); }
        catch (ShenoraException e) { _core.Notify(NoticeKind.Error, e.Message); }
        catch (Exception e) { _core.Notify(NoticeKind.Error, L.T("That did not work ({0}).", e.GetType().Name)); }
        if (_core.Snapshot().Confirmation is { } confirmation && confirmation.Token != asked) ShowWindow();
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_exiting || e.CloseReason != CloseReason.UserClosing) return;
        // Closing the window is not leaving: the connections are what the person came for.
        e.Cancel = true;
        _window.Hide();
        if (_toldHidden) return;
        _toldHidden = true;
        Tell(L.T("Sallyport is still running, here beside the clock. What is connected stays connected; right-click here to exit."));
    }

    public void Dispose()
    {
        _core.Changed -= OnChanged;
        _core.Noticed -= OnNoticed;
        _window.FormClosing -= OnClosing;
        // A disposed but visible icon stays in the notification area until the pointer passes over it.
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _bold?.Dispose();
        _glyphs?.Dispose();
        foreach (var icon in _icons.Values) icon.Dispose();
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUNDSMALL = 3;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    /// <summary>The theme's surface and text; a selected entry as a rounded fill inset from the edges, as Windows 11's
    /// own menus show it; thin separators. Before each entry's text, its glyph — the mark, Windows' own, or a profile's
    /// state as a dot — and at a profile's end what pressing it does, as the window's buttons say it. Sizes in device
    /// pixels at the menu's DPI.</summary>
    private sealed class MenuRenderer(Theme theme, float scale, Font glyphs) : ToolStripRenderer
    {
        public Theme Theme { get; } = theme;

        /// <summary>Windows draws the rounded border; drawn here too, its square corners would show outside it.</summary>
        public bool SystemDrawsBorder { get; set; }

        // Windows 11's own menus: an entry 32 high, its glyph 16 square 12 in from the highlight, the text 12 after it; a
        // separator 9 high. A profile's row is 40 high. The text is placed here (TextLeft), not where WinForms would put
        // it, so the menu's width is set here too (Wants).
        public Padding ItemPadding { get; } = new((int)(4 * scale), (int)(8 * scale), (int)(8 * scale), (int)(8 * scale));

        public Padding RowPadding { get; } = new((int)(4 * scale), (int)(12 * scale), (int)(8 * scale), (int)(12 * scale));

        /// <summary>From the window's edge to a highlight or a separator, on every side: the border, then 4.</summary>
        public int Edge { get; } = (int)Math.Round(4 * scale) + 1;

        private int GlyphLeft => Edge + S(12);

        private int GlyphSize => S(16);

        private int TextLeft => GlyphLeft + GlyphSize + S(12);

        public int MinimumWidth { get; } = (int)Math.Round(200 * scale);

        public int SeparatorHeight { get; } = (int)Math.Round(9 * scale);

        private int S(float logical) => (int)Math.Round(logical * scale);

        private const TextFormatFlags Line = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

        /// <summary>The window's width this entry wants, from the window's left edge: its glyph and text, a profile's state
        /// after its name and its action's pill after that, then the room at the end.</summary>
        public int Wants(ToolStripMenuItem item)
        {
            var width = TextLeft + TextRenderer.MeasureText(item.Text, item.Font, Size.Empty, Line).Width;
            if (item.Tag is TrayEntry { Status: { } status } entry)
            {
                width += S(8) + TextRenderer.MeasureText(status, item.Font, Size.Empty, Line).Width;
                if (entry.Action is { } action) width += S(24) + Pill(action, item.Font).Width;
            }
            return width + S(12) + Edge;
        }

        private Size Pill(string action, Font font)
        {
            var text = TextRenderer.MeasureText(action, font, Size.Empty, Line);
            return new(text.Width + S(20), S(24));
        }

        /// <summary>The page's --hover on a card: the text's colour at 6% over the surface.</summary>
        private Color Hover => Mix(Theme.Text, 6);

        private Color Mix(Color over, int percent) => Color.FromArgb(
            Theme.Surface.R + (over.R - Theme.Surface.R) * percent / 100,
            Theme.Surface.G + (over.G - Theme.Surface.G) * percent / 100,
            Theme.Surface.B + (over.B - Theme.Surface.B) * percent / 100);

        /// <summary>A state's dot, as the page's status pills colour it (styles.css' --up-fill, --busy, --trouble, --off).</summary>
        private Color Dot(Tone tone) => (tone, Theme.Dark) switch
        {
            (Tone.Up, false) => Color.FromArgb(0x1F, 0x9D, 0x55),
            (Tone.Up, true) => Color.FromArgb(0x3C, 0xCF, 0x8E),
            (Tone.Busy, false) => Color.FromArgb(0xD9, 0x8E, 0x04),
            (Tone.Busy, true) => Color.FromArgb(0xF2, 0xB8, 0x4B),
            (Tone.Trouble, false) => Color.FromArgb(0xC8, 0x30, 0x3E),
            (Tone.Trouble, true) => Color.FromArgb(0xF0, 0x6A, 0x6A),
            (_, false) => Color.FromArgb(0x8A, 0x94, 0xA3),
            _ => Color.FromArgb(0x7D, 0x87, 0x94),
        };

        /// <summary>A state's words, in the page's --tone-text colours: what it is, read beside the name.</summary>
        private Color Said(Tone tone) => (tone, Theme.Dark) switch
        {
            (Tone.Up, false) => Color.FromArgb(0x13, 0x73, 0x47),
            (Tone.Busy, false) => Color.FromArgb(0x86, 0x59, 0x00),
            (Tone.Trouble, false) => Color.FromArgb(0xB4, 0x23, 0x32),
            (Tone.Up or Tone.Busy or Tone.Trouble, true) => Dot(tone),
            _ => Theme.Secondary,
        };

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var surface = new SolidBrush(Theme.Surface);
            e.Graphics.FillRectangle(surface, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (SystemDrawsBorder) return;
            using var border = new Pen(Theme.Rule);
            e.Graphics.DrawRectangle(border, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var (left, right) = Row(e.Item);
            var bounds = new Rectangle(left, 1, right - left, e.Item.Height - 2);
            using var path = Rounded(bounds, 4 * scale);
            using var hover = new SolidBrush(Hover);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillPath(hover, path);
        }

        /// <summary>The entry drawn here, not where WinForms would put it: centred on its whole height (WinForms' own box
        /// sits high), its glyph first, a profile's state and action after its name, each cut with an ellipsis rather than
        /// run into the next.</summary>
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var g = e.Graphics;
            var item = e.Item;
            var entry = item.Tag as TrayEntry;
            var x = -item.Bounds.Left;
            var height = item.Height;
            var text = item.Enabled ? Theme.Text : Theme.Secondary;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var glyph = new Rectangle(x + GlyphLeft, (height - GlyphSize) / 2, GlyphSize, GlyphSize);
            switch (entry)
            {
                case { Tone: { } tone }:
                    var d = S(10);
                    var dot = new RectangleF(glyph.X + (GlyphSize - d) / 2f, glyph.Y + (GlyphSize - d) / 2f, d, d);
                    // Not connected is a ring, as the page's pill says it: nothing is on.
                    if (tone is Tone.Off or Tone.Info)
                    {
                        using var ring = new Pen(Dot(tone), Math.Max(1.5f, 1.5f * scale));
                        dot.Inflate(-ring.Width / 2, -ring.Width / 2);
                        g.DrawEllipse(ring, dot);
                    }
                    else
                    {
                        using var fill = new SolidBrush(Dot(tone));
                        g.FillEllipse(fill, dot);
                    }
                    break;
                case { Glyph: TrayGlyph.Mark }:
                    BrandMark.Paint(g, glyph);
                    break;
                case { Glyph: TrayGlyph.Settings or TrayGlyph.Exit }:
                    // Settings (E713) and Power (E7E8), in Windows' own icon font.
                    TextRenderer.DrawText(g, entry.Glyph == TrayGlyph.Settings ? "\uE713" : "\uE7E8", glyphs, glyph, text,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                    break;
            }

            var (_, right) = Row(item);
            var end = right - S(12);
            if (entry is { Action: { } action })
            {
                // What pressing the row does, as a quiet button at its end: the window's accent, on a wash of it.
                var size = Pill(action, item.Font);
                var pill = new Rectangle(end - size.Width, (height - size.Height) / 2, size.Width, size.Height);
                var accent = item.Enabled ? Theme.Accent : Theme.Secondary;
                using (var path = Rounded(pill, size.Height / 2f))
                using (var wash = new SolidBrush(Mix(accent, item.Selected ? 18 : 11)))
                    g.FillPath(wash, path);
                TextRenderer.DrawText(g, action, item.Font, pill, accent, Line | TextFormatFlags.HorizontalCenter);
                end = pill.Left - S(16);
            }

            var left = x + TextLeft;
            var name = TextRenderer.MeasureText(g, item.Text, item.Font, Size.Empty, Line).Width;
            TextRenderer.DrawText(g, item.Text, item.Font, new Rectangle(left, 0, Math.Max(0, Math.Min(name, end - left)), height), text, Line | TextFormatFlags.EndEllipsis);
            if (entry is { Status: { } status, Tone: { } said })
            {
                var from = left + name + S(8);
                TextRenderer.DrawText(g, status, item.Font, new Rectangle(from, 0, Math.Max(0, end - from), height),
                    item.Enabled ? Said(said) : Theme.Secondary, Line | TextFormatFlags.EndEllipsis);
            }
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var (left, right) = Row(e.Item);
            var y = e.Item.Height / 2;
            using var line = new Pen(Theme.Rule);
            e.Graphics.DrawLine(line, left, y, right - 1, y);
        }

        /// <summary>Where a highlight or a separator runs in an entry's own coordinates: <see cref="Edge"/> in from the
        /// window's edges, so every row lines up whatever x and width the layout gave the entry (an entry from 0, a
        /// separator from 2, at 200%).</summary>
        private (int Left, int Right) Row(ToolStripItem item)
        {
            var window = item.Owner?.ClientSize.Width ?? item.Width;
            return (Edge - item.Bounds.Left, window - Edge - item.Bounds.Left);
        }

        private static GraphicsPath Rounded(Rectangle r, float radius)
        {
            var d = 2 * radius;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
