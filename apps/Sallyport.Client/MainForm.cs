using Microsoft.Win32;
using Shenora;
using Shenora.Core.Events;
using Shenora.Core.Ipc;
using Shenora.Core.Shell;
using Shenora.Windows;
using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Sallyport.Client;

/// <summary>This start's arguments, and its claim to be the only client — which later starts hand theirs to.</summary>
internal sealed record Start(string[] Arguments, SingleInstance Claim);

/// <summary>
/// A frameless window: the page draws the title bar and reports where its three caption buttons are; the form paints
/// those natively in that hole, so Windows still offers Snap Layouts on maximize.
/// </summary>
internal sealed class MainForm : OptimizedForm
{
    /// <summary>The page's header height and caption button width in CSS pixels, for the buttons the form paints before
    /// the page has said where its own are.</summary>
    private const double TitleBarHeight = 48, CaptionButtonWidth = 46;

    private readonly WebViewHost _host;
    private readonly Splash _splash;
    private readonly WebView2Control _webView;
    private readonly WebViewIpcBridge _bridge;
    private readonly StatusTray _tray;
    private readonly ClientCore _core;
    private readonly Icon _windowIcon;
    private readonly Start _start;
    private bool _pageOwnsCaptionButtons;
    private Theme _theme;

    public MainForm(WebViewHostOptions hostOptions, IMessageDispatcher dispatcher, IEventBus eventBus, ClientCore core, Start start)
        : this(hostOptions, dispatcher, eventBus, core, start, Theme.Current())
    {
    }

    private MainForm(WebViewHostOptions hostOptions, IMessageDispatcher dispatcher, IEventBus eventBus, ClientCore core, Start start, Theme theme)
        : base(new OptimizedFormOptions
        {
            FramelessChrome = true,
            NativeCaptionButtons = true,
            // Form, WebView2 and splash in the page's own background, so nothing flashes on the way in.
            BackColor = theme.Background,
            DwmBorderColor = theme.Background,
            ImmersiveDarkMode = theme.Dark,
        })
    {
        _core = core;
        _start = start;
        _theme = theme;
        Text = "Sallyport";
        // Minimize and close only: the window keeps the size it is given (the owner: "we probably don't need the
        // window max button"), and without the style Windows offers no maximize either — Win+Up, a double click, Snap.
        MaximizeBox = false;
        MinimumSize = new Size(760, 520);
        Size = new Size(1000, 700);
        _windowIcon = BrandMark.Icon(32);
        Icon = _windowIcon;
        CaptionButtonColors = theme.Caption;

        _webView = new WebView2Control { Dock = DockStyle.Fill };
        Controls.Add(_webView);
        _splash = new Splash(theme);
        Controls.Add(_splash);
        _splash.BringToFront();
        Resize += (_, _) => PaintCaptionButtonsUntilThePageDoes();

        dispatcher.MapModule(new WindowCommandModule(new WindowCommandOptions
        {
            Window = this,
            // A frameless window maximizes to the work area by hand, and WindowState never shows it.
            ToggleMaximize = ToggleMaximize,
            IsMaximized = () => AppPlacement == WindowPlacement.Maximized,
            CoordinateSpace = _webView,
            SetCaptionButtons = SetCaptionButtons,
            // The page says when its first screen has its state: lifted at navigation, the splash would leave a frame of
            // bare background (the page's script not yet run) — the title bar empty but for the caption buttons.
            CloseSplash = () => BeginInvoke(LiftSplash),
        }));

        // Made before the WebView2 initialises: events raised before the page is ready are held for it, not lost.
        _bridge = new WebViewIpcBridge(_webView, new WebViewIpcBridgeOptions
        {
            Dispatcher = dispatcher,
            EventBus = eventBus,
            Shell = new ShellInfo
            {
                Name = "winforms",
                Capabilities = [ShellCapability.WindowChrome, ShellCapability.FilePicker, ShellCapability.Tray],
            },
            OnClientReady = _ =>
            {
                _pageOwnsCaptionButtons = true;
                // The page asks nothing on load: it is sent what to show, and sent it again after a reload.
                _core.Publish();
            },
        });
        _tray = new StatusTray(this, core);
        _host = new WebViewHost(_webView, hostOptions);
        Load += OnLoadAsync;
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
    }

    /// <summary>Windows says a setting changed, not which value: the theme is read again and applied if it moved.</summary>
    private void OnPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        var theme = Theme.Current();
        if (theme == _theme || IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() =>
        {
            _theme = theme;
            ApplyChromeTheme(theme.Background, theme.Background, theme.Dark);
            CaptionButtonColors = theme.Caption;
            if (!_splash.IsDisposed) _splash.Apply(theme);
        });
    }

    /// <summary>What a start was given: profiles to import (a path ending .ovpn or .conf), <c>--add-game</c> and a game's
    /// folder, <c>--tray</c> to stay out of sight, <c>--exit</c> to leave as the tray's Exit does. Called for this start
    /// and for each later one handed over by <see cref="SingleInstance"/>.</summary>
    private void Receive(string[] args, bool atStart)
    {
        if (args.Contains("--exit"))
        {
            _tray.Exit();
            return;
        }
        foreach (var path in args.Where(a => !a.StartsWith('-')))
        {
            if (!path.EndsWith(".ovpn", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".conf", StringComparison.OrdinalIgnoreCase)) continue;
            // Onto the import page, as one chosen there is: the person sees its name and server before it is kept.
            _core.OpenProfileFile(path);
        }
        var addGame = Array.IndexOf(args, "--add-game");
        if (addGame >= 0 && addGame + 1 < args.Length)
            try { _core.AddFolder(args[addGame + 1]); }
            catch (ShenoraException e) { _core.Notify(NoticeKind.Error, e.Message); }
        if (!atStart) _tray.ShowWindow();
        // Hidden only once shown: the window has to exist for the page to load behind it.
        else if (args.Contains("--tray")) BeginInvoke(Hide);
    }

    private async void OnLoadAsync(object? sender, EventArgs e)
    {
        PaintCaptionButtonsUntilThePageDoes();
        if (!WebViewEnvironment.IsRuntimeAvailable())
        {
            MessageBox.Show(L.T("The WebView2 Runtime is not installed.\n\nInstall the Evergreen WebView2 Runtime from Microsoft and start Sallyport again."),
                Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            _tray.Exit();
            return;
        }

        try
        {
            await _host.InitializeAsync();
            _bridge.Attach();
            _webView.CoreWebView2.NavigationCompleted += (_, args) =>
            {
                if (!args.IsSuccess)
                {
                    LiftSplash();
                    MessageBox.Show(L.T("The window's page did not load ({0}).", args.WebErrorStatus), Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                // A page that loaded but never says it is ready (a script error) is still shown, rather than the splash for good.
                var fallback = new System.Windows.Forms.Timer { Interval = 5000 };
                fallback.Tick += (_, _) =>
                {
                    fallback.Dispose();
                    LiftSplash();
                };
                fallback.Start();
            };
            _host.Navigate();
            Receive(_start.Arguments, atStart: true);
            // Listened for only now: what a later start hands over is acted on through this window's handle.
            _start.Claim.Listen(handed => BeginInvoke(() => Receive(handed, atStart: false)));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            _tray.Exit();
        }
    }

    private void LiftSplash()
    {
        if (_splash.IsDisposed) return;
        Controls.Remove(_splash);
        _splash.Dispose();
    }

    /// <summary>Until the page reports its buttons, the splash still needs a way to close the window: the same two,
    /// where the page will put them.</summary>
    private void PaintCaptionButtonsUntilThePageDoes()
    {
        if (_pageOwnsCaptionButtons || !IsHandleCreated || IsDisposed) return;
        var scale = DpiHelper.ScaleFromDeviceDpi(DeviceDpi);
        var w = (int)Math.Round(CaptionButtonWidth * scale);
        var h = (int)Math.Round(TitleBarHeight * scale);
        var right = ClientSize.Width;
        if (w <= 0 || h <= 0 || right <= 0) return;
        SetCaptionButtons(
        [
            new CaptionButtonRegion(CaptionButtonKind.Minimize, new Rectangle(right - 2 * w, 0, w, h)),
            new CaptionButtonRegion(CaptionButtonKind.Close, new Rectangle(right - w, 0, w, h)),
        ]);
    }

    private const int WS_MAXIMIZEBOX = 0x00010000, WM_SYSCOMMAND = 0x0112, SC_MAXIMIZE = 0xF030;

    /// <summary>The frameless frame keeps the maximize style whatever <see cref="Form.MaximizeBox"/> says; without it,
    /// Windows offers no maximize — not Win+Up, not dragging to the top, not Snap's maximize.</summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var created = base.CreateParams;
            created.Style &= ~WS_MAXIMIZEBOX;
            return created;
        }
    }

    protected override void WndProc(ref Message m)
    {
        // What still asks to maximize (the system menu, another program) is refused: the window keeps its size.
        if (m.Msg == WM_SYSCOMMAND && ((int)m.WParam & 0xFFF0) == SC_MAXIMIZE) return;
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // A static event: left subscribed, it would keep this form alive and call into it after disposal.
            SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
            _bridge.Dispose();
            _tray.Dispose();
            _windowIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}
