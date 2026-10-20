using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Shenora.Core.Ipc;

namespace Sallyport.Client;

/// <summary>
/// The notification-area icon, drawn in the colour of the window's summary, and its menu: each profile to connect or
/// disconnect, the apps to launch (docs/design/client-ui.md §7). It renders the same view the window does, so the two
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
        Style(_menu, Theme.Current());
        foreach (var entry in TrayMenu.Build(state.Profiles, state.Network.StatusText)) _menu.Items.Add(Item(entry, (MenuRenderer)_menu.Renderer));
        Fit(_menu);
    }

    /// <summary>Every entry as wide as the longest one where the renderer puts the text, with room for a chevron: WinForms
    /// would size them from where it puts the text itself, and draws nothing of an entry outside its own width.</summary>
    private static void Fit(ToolStripDropDownMenu menu, bool submenu = false)
    {
        var renderer = (MenuRenderer)menu.Renderer;
        var widest = submenu ? renderer.MinimumSubmenuWidth : renderer.MinimumWidth;
        foreach (ToolStripItem item in menu.Items)
            if (item is ToolStripMenuItem entry)
                widest = Math.Max(widest, renderer.TextLeft + TextRenderer.MeasureText(entry.Text, entry.Font).Width
                    + (entry.HasDropDownItems ? renderer.ChevronRoom : renderer.EndRoom));
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
    }

    /// <summary>A submenu beside its menu, its first entry level with the row it opens from — WinForms puts it over the
    /// menu's edge and a few pixels low. To the left where the screen has no room on the right.</summary>
    private static void Place(ToolStripMenuItem owner)
    {
        if (owner.Owner is not ToolStripDropDown menu || owner.DropDown is not { } submenu || submenu.Items.Count == 0) return;
        var row = menu.PointToScreen(owner.Bounds.Location);
        var area = Screen.FromPoint(row).WorkingArea;
        var x = menu.Bounds.Right + submenu.Width <= area.Right ? menu.Bounds.Right : menu.Bounds.Left - submenu.Width;
        var y = Math.Clamp(row.Y - submenu.Items[0].Bounds.Top, area.Top, Math.Max(area.Top, area.Bottom - submenu.Height));
        // WinForms keeps an open drop-down where it put it (setting Location changed nothing, measured); Windows moves it.
        if (submenu.Location != new Point(x, y))
            SetWindowPos(submenu.Handle, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private ToolStripItem Item(TrayEntry entry, MenuRenderer renderer)
    {
        if (entry.Kind == TrayEntryKind.Separator) return new ToolStripSeparator { AutoSize = false, Height = renderer.SeparatorHeight };
        var item = new ToolStripMenuItem(entry.Text) { Enabled = entry.Enabled, ToolTipText = entry.Tip ?? "", Padding = renderer.ItemPadding };
        if (entry.Default) item.Font = _bold ??= new Font(item.Font, FontStyle.Bold);
        if (entry.Kind == TrayEntryKind.Command) item.Click += (_, _) => Do(entry);
        if (entry.Items is { } items)
        {
            Style((ToolStripDropDownMenu)item.DropDown, renderer.Theme);
            foreach (var child in items) item.DropDownItems.Add(Item(child, renderer));
            Fit((ToolStripDropDownMenu)item.DropDown, submenu: true);
            item.DropDown.Opened += (_, _) => Place(item);
        }
        return item;
    }

    /// <summary>A menu in Windows 11's manner: the theme's colours, room around each entry, rounded corners.</summary>
    private static void Style(ToolStripDropDownMenu menu, Theme theme)
    {
        menu.Renderer = new MenuRenderer(theme, menu.DeviceDpi / 96f);
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
        foreach (var icon in _icons.Values) icon.Dispose();
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUNDSMALL = 3;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    /// <summary>The theme's surface and text; a selected entry as a rounded fill inset from the edges, as Windows 11's
    /// own menus show it; thin separators. Sizes in device pixels at the menu's DPI.</summary>
    private sealed class MenuRenderer(Theme theme, float scale) : ToolStripRenderer
    {
        public Theme Theme { get; } = theme;

        /// <summary>Windows draws the rounded border; drawn here too, its square corners would show outside it.</summary>
        public bool SystemDrawsBorder { get; set; }

        // Windows 11's own menus: an entry 32 high, its text 16 from the menu's edge, the submenu chevron 16 from the other
        // edge with 24 before it; a separator 9 high. The text is placed here (TextLeft), not where WinForms would put it,
        // so the menu's width is set here too (Fit).
        public Padding ItemPadding { get; } = new((int)(4 * scale), (int)(8 * scale), (int)(8 * scale), (int)(8 * scale));

        public int TextLeft { get; } = (int)Math.Round(16 * scale);

        /// <summary>After an entry's text: 24 and the chevron's 16 when it opens a submenu, else 24.</summary>
        public int ChevronRoom { get; } = (int)Math.Round(40 * scale);

        public int EndRoom { get; } = (int)Math.Round(24 * scale);

        public int MinimumWidth { get; } = (int)Math.Round(180 * scale);

        public int MinimumSubmenuWidth { get; } = (int)Math.Round(120 * scale);

        public int SeparatorHeight { get; } = (int)Math.Round(9 * scale);

        /// <summary>From the window's edge to a highlight or a separator, on every side: the border, then 4.</summary>
        public int Edge { get; } = (int)Math.Round(4 * scale) + 1;

        /// <summary>The page's --hover on a card: the text's colour at 6% over the surface.</summary>
        private Color Hover => Color.FromArgb(
            Theme.Surface.R + (Theme.Text.R - Theme.Surface.R) * 6 / 100,
            Theme.Surface.G + (Theme.Text.G - Theme.Surface.G) * 6 / 100,
            Theme.Surface.B + (Theme.Text.B - Theme.Surface.B) * 6 / 100);

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

        /// <summary>The text centred on the entry's whole height, not on WinForms' own box for it (which sits high), and cut
        /// with an ellipsis rather than under the chevron.</summary>
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Secondary;
            // From the menu's edges: the entry may start in from the window's edge, and run past its right one.
            var left = TextLeft - e.Item.Bounds.Left;
            e.TextRectangle = new Rectangle(left, 0, Math.Max(0, Row(e.Item).Right - left - (int)(32 * scale)), e.Item.Height);
            e.TextFormat = (e.TextFormat & ~(TextFormatFlags.Top | TextFormatFlags.Bottom)) | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
            base.OnRenderItemText(e);
        }

        /// <summary>A chevron, as Windows 11's menus draw it: on the entry's middle, as its text now is, 14 in from the
        /// highlight's right.</summary>
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            var half = 3.5f * scale;
            var x = (e.Item is { } item ? Row(item).Right : e.ArrowRectangle.Right) - 14 * scale;
            var y = (e.Item?.Height ?? e.ArrowRectangle.Bottom) / 2f;
            using var pen = new Pen(e.Item?.Enabled == false ? Theme.Secondary : Theme.Text, Math.Max(1, scale));
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.DrawLines(pen, [new PointF(x - half / 2, y - half), new PointF(x + half / 2, y), new PointF(x - half / 2, y + half)]);
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
