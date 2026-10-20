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
    public static readonly Color Brand = Color.FromArgb(0x5B, 0x9D, 0xFF);

    private static readonly Dictionary<Tone, Color> Colours = new()
    {
        [Tone.Off] = Color.FromArgb(0x6B, 0x72, 0x80),
        [Tone.Busy] = Color.FromArgb(0xF2, 0xB8, 0x4B),
        [Tone.Up] = Color.FromArgb(0x3C, 0xCF, 0x8E),
        [Tone.Trouble] = Color.FromArgb(0xF0, 0x6A, 0x6A),
        [Tone.Info] = Brand,
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

    public static Icon Draw(Color fill, int size)
    {
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var disc = new SolidBrush(fill);
            g.FillEllipse(disc, 0.5f, 0.5f, size - 1.5f, size - 1.5f);
            // A sallyport is a gate: an arched door, white on the state's colour.
            float w = size * 0.38f, x = (size - w) / 2, top = size * 0.27f, bottom = size * 0.78f;
            using var door = new GraphicsPath();
            door.AddArc(x, top, w, w, 180, 180);
            door.AddLine(x + w, top + w / 2, x + w, bottom);
            door.AddLine(x + w, bottom, x, bottom);
            door.CloseFigure();
            g.FillPath(Brushes.White, door);
        }
        var handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    private void OnChanged()
    {
        if (_window.IsDisposed || !_window.IsHandleCreated) return;
        try { _window.BeginInvoke(Refresh); }
        catch (InvalidOperationException) { /* the window is going away */ }
    }

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
            _icons[summary.Tone] = icon = Draw(Colours[summary.Tone], SystemInformation.SmallIconSize.Width);
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
        foreach (var entry in TrayMenu.Build(state.Profiles, state.Apps, state.Network.StatusText)) _menu.Items.Add(Item(entry, (MenuRenderer)_menu.Renderer));
    }

    private ToolStripItem Item(TrayEntry entry, MenuRenderer renderer)
    {
        if (entry.Kind == TrayEntryKind.Separator) return new ToolStripSeparator();
        var item = new ToolStripMenuItem(entry.Text) { Enabled = entry.Enabled, ToolTipText = entry.Tip ?? "", Padding = renderer.ItemPadding };
        if (entry.Default) item.Font = _bold ??= new Font(item.Font, FontStyle.Bold);
        if (entry.Kind == TrayEntryKind.Command) item.Click += (_, _) => Do(entry);
        if (entry.Items is { } items)
        {
            Style((ToolStripDropDownMenu)item.DropDown, renderer.Theme);
            foreach (var child in items) item.DropDownItems.Add(Item(child, renderer));
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
        menu.Padding = new Padding(0, 4, 0, 4);
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

    /// <summary>What an entry does. A question it raises (disconnecting a profile an app is routed through) is asked in
    /// the window, so the window comes up for it.</summary>
    private void Do(TrayEntry entry)
    {
        var id = entry.Target ?? "";
        switch (entry.Verb)
        {
            case TrayVerb.OpenWindow: ShowWindow(); return;
            case TrayVerb.OpenNetwork: ShowPage(Page.Network); return;
            case TrayVerb.OpenSettings: ShowPage(Page.Settings); return;
            case TrayVerb.Exit: Exit(); return;
            case TrayVerb.Connect: Run(() => { _core.Connect(id); return Task.CompletedTask; }); return;
            case TrayVerb.Disconnect: Run(() => _core.DisconnectAsync(id)); return;
            case TrayVerb.StopRun: Run(_core.StopRunAsync); return;
            case TrayVerb.Launch: Run(() => _core.LaunchAsync(id, null, CancellationToken.None)); return;
            case TrayVerb.Route: Run(() => { _core.RouteApp(id); return Task.CompletedTask; }); return;
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
        Tell(L.T("Sallyport is still running in the notification area. Connections and routing stay on."));
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

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUNDSMALL = 3;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    /// <summary>The theme's surface and text; a selected entry as a rounded fill inset from the edges, as Windows 11's
    /// own menus show it; thin separators. Sizes in device pixels at the menu's DPI.</summary>
    private sealed class MenuRenderer(Theme theme, float scale) : ToolStripRenderer
    {
        private readonly Dictionary<ToolStripItem, float> _textMiddle = [];

        public Theme Theme { get; } = theme;

        /// <summary>Windows draws the rounded border; drawn here too, its square corners would show outside it.</summary>
        public bool SystemDrawsBorder { get; set; }

        public Padding ItemPadding { get; } = new((int)(8 * scale), (int)(5 * scale), (int)(8 * scale), (int)(5 * scale));

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var surface = new SolidBrush(Theme.Caption.Surface);
            e.Graphics.FillRectangle(surface, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (SystemDrawsBorder) return;
            using var border = new Pen(Theme.Caption.Pressed);
            e.Graphics.DrawRectangle(border, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var inset = (int)(4 * scale);
            var bounds = new Rectangle(inset, 1, e.Item.Width - 2 * inset, e.Item.Height - 2);
            using var path = Rounded(bounds, 4 * scale);
            using var hover = new SolidBrush(Theme.Caption.Hover);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillPath(hover, path);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Secondary;
            _textMiddle[e.Item] = e.TextRectangle.Top + e.TextRectangle.Height / 2f;
            base.OnRenderItemText(e);
        }

        /// <summary>A chevron, as Windows 11's menus draw it: on the line of the entry's text (drawn before it), which
        /// sits above the middle of the entry and of the arrow's own box, and clear of the edge.</summary>
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            var half = 3.5f * scale;
            var x = e.ArrowRectangle.Left + e.ArrowRectangle.Width / 2f - 4 * scale;
            var y = e.Item is { } item && _textMiddle.TryGetValue(item, out var middle) ? middle : e.ArrowRectangle.Top + e.ArrowRectangle.Height / 2f;
            using var pen = new Pen(e.Item?.Enabled == false ? Theme.Secondary : Theme.Text, Math.Max(1, scale));
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.DrawLines(pen, [new PointF(x - half / 2, y - half), new PointF(x + half / 2, y), new PointF(x - half / 2, y + half)]);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var inset = (int)(12 * scale);
            var y = e.Item.Height / 2;
            using var line = new Pen(Theme.Caption.Pressed);
            e.Graphics.DrawLine(line, inset, y, e.Item.Width - inset, y);
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
