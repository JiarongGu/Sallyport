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
        _menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false, ShowCheckMargin = false };
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
        _icon.Text = Tooltip($"Sallyport — {summary.Text}");
    }

    private void Fill()
    {
        // Cleared items are not disposed by the strip; each holds a handle-backed image and font until it is.
        foreach (var old in _menu.Items.Cast<ToolStripItem>().ToList()) old.Dispose();
        var state = _core.Snapshot();

        var open = new ToolStripMenuItem("Open Sallyport", null, (_, _) => ShowWindow());
        open.Font = _bold ??= new Font(open.Font, FontStyle.Bold);
        _menu.Items.Add(open);
        _menu.Items.Add(new ToolStripMenuItem("Open Network", null, (_, _) => ShowPage(Page.Network)) { ToolTipText = state.Network.StatusText });
        _menu.Items.Add(new ToolStripSeparator());

        if (state.Profiles.Count == 0)
            _menu.Items.Add(new ToolStripMenuItem("No profiles yet — open Sallyport to import one") { Enabled = false });
        foreach (var profile in state.Profiles)
        {
            var id = profile.Id;
            var item = new ToolStripMenuItem($"{profile.Name} — {profile.StatusText}");
            if (profile.DetailText is { } detail) item.DropDownItems.Add(new ToolStripMenuItem(detail) { Enabled = false });
            Add(item, profile.Actions.Connect, () => { _core.Connect(id); return Task.CompletedTask; });
            Add(item, profile.Actions.Disconnect, () => _core.DisconnectAsync(id));
            if (item.DropDownItems.Count == 0) item.DropDownItems.Add(new ToolStripMenuItem(profile.StatusText) { Enabled = false });
            _menu.Items.Add(item);
        }

        // The app being routed, to stop; otherwise every app, to route or launch — as its card in the window offers.
        if (state.Apps.FirstOrDefault(a => a.Run is { Phase: not RunPhase.Ended }) is { } routed)
        {
            _menu.Items.Add(new ToolStripSeparator());
            var item = new ToolStripMenuItem($"{routed.Name} — {routed.Run!.StatusText}");
            if (routed.Run.DetailText is { } detail) item.DropDownItems.Add(new ToolStripMenuItem(detail) { Enabled = false });
            Add(item, routed.Actions.Stop, _core.StopRunAsync);
            Add(item, routed.Actions.Launch, () => _core.LaunchAsync(routed.Id, null, CancellationToken.None));
            _menu.Items.Add(item);
        }
        if (state.Apps.Where(a => a.Run is not { Phase: not RunPhase.Ended }).ToList() is { Count: > 0 } apps)
        {
            _menu.Items.Add(new ToolStripSeparator());
            var all = new ToolStripMenuItem("Apps");
            foreach (var app in apps)
            {
                var id = app.Id;
                var item = new ToolStripMenuItem($"{app.Name}  ·  {app.ThroughText}");
                Add(item, app.Actions.Launch, () => _core.LaunchAsync(id, null, CancellationToken.None));
                Add(item, app.Actions.Route, () => { _core.RouteApp(id); return Task.CompletedTask; });
                if (item.DropDownItems.Count == 0) item.DropDownItems.Add(new ToolStripMenuItem(app.StatusText) { Enabled = false });
                all.DropDownItems.Add(item);
            }
            _menu.Items.Add(all);
        }

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Settings", null, (_, _) => ShowPage(Page.Settings)));
        _menu.Items.Add(new ToolStripMenuItem("Exit Sallyport", null, (_, _) => Exit()) { ToolTipText = "Stops game routing, disconnects every profile, then quits." });
    }

    /// <summary>A profile's action as the window decides it: shown or not, pressable or not, and why.</summary>
    private void Add(ToolStripMenuItem parent, UiAction action, Func<Task> run)
    {
        if (!action.Visible) return;
        parent.DropDownItems.Add(new ToolStripMenuItem(action.Label, null, (_, _) => Run(run))
        {
            Enabled = action.Enabled,
            ToolTipText = action.Reason ?? "",
        });
    }

    private async void Run(Func<Task> action)
    {
        try { await action(); }
        catch (ShenoraException e) { _core.Notify(NoticeKind.Error, e.Message); }
        catch (Exception e) { _core.Notify(NoticeKind.Error, $"That did not work ({e.GetType().Name})."); }
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_exiting || e.CloseReason != CloseReason.UserClosing) return;
        // Closing the window is not leaving: the connections are what the person came for.
        e.Cancel = true;
        _window.Hide();
        if (_toldHidden) return;
        _toldHidden = true;
        Tell("Sallyport is still running in the tray. Connections and game routing stay on.");
    }

    /// <summary>The notification area refuses a tip longer than 127 characters: cut at the last whole phrase that fits.</summary>
    private static string Tooltip(string text)
    {
        if (text.Length <= 127) return text;
        var cut = text.LastIndexOf(" · ", 124, StringComparison.Ordinal);
        return (cut > 0 ? text[..cut] : text[..124]) + " …";
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

    private sealed class DarkMenuRenderer() : ToolStripProfessionalRenderer(new Palette())
    {
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Color.FromArgb(230, 232, 238) : Color.FromArgb(150, 156, 168);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Color.FromArgb(230, 232, 238);
            base.OnRenderArrow(e);
        }
    }

    private sealed class Palette : ProfessionalColorTable
    {
        private static readonly Color Surface = Color.FromArgb(29, 32, 39);
        private static readonly Color Hover = Color.FromArgb(42, 48, 59);
        private static readonly Color Border = Color.FromArgb(53, 61, 75);

        public override Color ToolStripDropDownBackground => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Surface;
    }
}
