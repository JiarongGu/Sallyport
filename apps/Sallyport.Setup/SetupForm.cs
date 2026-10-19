using System.Diagnostics;
using Sallyport.Windows.PacketFilter;

namespace Sallyport.Setup;

/// <summary>
/// Setup's window, in three pages: what it will do and where (the folder, the packet driver; on removal, whether the
/// person's own profiles go too), how far it is, and how it ended — with every step it took one click away. In the
/// client's colours, light or dark as Windows is.
/// </summary>
sealed class SetupForm : Form
{
    // The content's width in 96-dpi pixels; the form scales it to the screen it opens on.
    private const int Width96 = 512;

    private readonly SetupTheme _theme = SetupTheme.Current();
    private readonly bool _uninstall;
    private readonly bool _installed;
    private readonly Func<Stream?> _payload;
    private readonly string _version;
    private readonly string? _missing;
    private readonly Font _headingFont = new("Segoe UI Semibold", 14f);
    private readonly Font _titleFont = new("Segoe UI Semibold", 12.5f);
    private readonly Font _strongFont = new("Segoe UI Semibold", 9.75f);
    private long? _needed;
    private bool _busy;
    private Installer? _installer;

    private readonly Panel _header = new() { Dock = DockStyle.Top, Height = 76 };
    private readonly Panel _footer = new() { Dock = DockStyle.Bottom, Height = 64 };
    private readonly Panel _body = new() { Dock = DockStyle.Fill, Padding = new Padding(24, 20, 24, 8) };
    private readonly FlowLayoutPanel _choose = Page();
    private readonly FlowLayoutPanel _progress = Page();
    private readonly FlowLayoutPanel _finish = Page();

    private readonly Label _chooseHeading = new();
    private readonly Label _chooseIntro = new();
    private readonly Label _whereLabel = new() { Text = "Install folder" };
    private readonly FlowLayoutPanel _whereRow = new() { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 0, 0, 6) };
    private readonly FieldBox _where;
    private readonly ActionButton _browse;
    private readonly Label _whereHint = new();
    private readonly OptionBox _option;
    private readonly Label _missingNote = new();
    private readonly LinkLabel _missingLink = new() { Text = "Get the WebView2 Runtime from Microsoft" };

    private readonly Label _progressHeading = new();
    private readonly Label _stepText = new();
    private readonly Meter _meter;
    private readonly LinkLabel _detailsLink = new() { Text = "Show details" };
    private readonly TextBox _details;

    private readonly Mark _mark;
    private readonly Label _finishHeading = new();
    private readonly Label _finishText = new();
    private readonly OptionBox _openNow;

    private readonly ActionButton _primary;
    private readonly ActionButton _secondary;
    private readonly LinkLabel _logLink = new() { Text = "Setup log", Visible = false, AutoSize = true };

    public SetupForm(bool uninstall, SetupChoices choices, Func<Stream?> payload, string version, string folder, bool installed)
    {
        _uninstall = uninstall;
        _installed = installed;
        _payload = payload;
        _version = version;
        _missing = uninstall ? null : Installer.Missing();

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.75f);
        Text = uninstall ? "Remove Sallyport" : "Sallyport setup";
        Icon = Draw.GateIcon(32);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(Width96 + 48, 440);
        BackColor = _theme.Background;
        ForeColor = _theme.Text;

        _where = new FieldBox(_theme, Font) { Size = new Size(Width96 - 104, 34) };
        _where.Box.Text = folder;
        _where.Box.AccessibleName = "Install folder";
        _browse = new ActionButton(_theme, "Browse…") { MinimumSize = new Size(96, 34) };
        _option = new OptionBox(_theme, Font) { Width = Width96 };
        _meter = new Meter(_theme) { Width = Width96, Height = 8, Margin = new Padding(0, 4, 0, 12) };
        _details = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, Width = Width96, Height = 150,
            BackColor = _theme.Surface, ForeColor = _theme.Secondary, Visible = false, TabStop = false, Margin = new Padding(0, 4, 0, 0),
        };
        _mark = new Mark(_theme);
        _openNow = new OptionBox(_theme, Font) { Width = Width96, Text = "Open Sallyport now", Checked = true, Margin = new Padding(0, 8, 0, 0) };
        _primary = new ActionButton(_theme, "", primary: true);
        _secondary = new ActionButton(_theme, "Cancel");

        Style(_chooseHeading, _headingFont, _theme.Text, 0, 0, 0, 6);
        Style(_chooseIntro, Font, _theme.Secondary, 0, 0, 0, 18);
        Style(_whereLabel, _strongFont, _theme.Text, 0, 0, 0, 6);
        Style(_whereHint, Font, _theme.Secondary, 0, 0, 0, 18);
        Style(_missingNote, Font, _theme.Trouble, 0, 4, 0, 2);
        Style(_progressHeading, _headingFont, _theme.Text, 0, 0, 0, 14);
        Style(_stepText, Font, _theme.Secondary, 0, 0, 0, 6);
        Style(_finishHeading, _headingFont, _theme.Text, 0, 0, 0, 6);
        Style(_finishText, Font, _theme.Secondary, 0, 0, 0, 6);
        foreach (var link in new[] { _missingLink, _detailsLink, _logLink })
        {
            link.AutoSize = true;
            link.LinkColor = link.ActiveLinkColor = _theme.Accent;
            link.VisitedLinkColor = _theme.Accent;
            link.DisabledLinkColor = _theme.Secondary;
            link.LinkBehavior = LinkBehavior.HoverUnderline;
            link.BackColor = Color.Transparent;
        }
        _detailsLink.Margin = new Padding(0, 4, 0, 0);

        _whereRow.Controls.AddRange([_where, _browse]);
        _choose.Controls.AddRange([_chooseHeading, _chooseIntro, _whereLabel, _whereRow, _whereHint, _option, _missingNote, _missingLink]);
        _progress.Controls.AddRange([_progressHeading, _stepText, _meter, _detailsLink, _details]);
        _finish.Controls.AddRange([_mark, _finishHeading, _finishText, _openNow]);
        _body.Controls.AddRange([_choose, _progress, _finish]);
        _footer.Controls.AddRange([_logLink, _secondary, _primary]);
        Controls.AddRange([_body, _footer, _header]);

        _header.BackColor = _footer.BackColor = _theme.Surface;
        _header.Paint += PaintHeader;
        _footer.Paint += (_, e) =>
        {
            using var line = new Pen(_theme.Divider);
            e.Graphics.DrawLine(line, 0, 0, _footer.Width, 0);
        };
        _footer.Layout += (_, _) => LayOutFooter();

        if (uninstall) SetUpRemoval(choices);
        else SetUpInstall(choices);
        ShowPage(_choose);

        AcceptButton = _primary;
        // The keyboard starts on the button, not in the folder: a field that has it opens with its text selected.
        ActiveControl = _primary;
        _primary.Click += async (_, _) => await PrimaryAsync();
        _secondary.Click += (_, _) => Close();
        _browse.Click += (_, _) => Browse();
        _where.Box.TextChanged += (_, _) => CheckWhere();
        _detailsLink.LinkClicked += (_, _) =>
        {
            _details.Visible = !_details.Visible;
            _detailsLink.Text = _details.Visible ? "Hide details" : "Show details";
        };
        _logLink.LinkClicked += (_, _) => Open(Installer.LogPath);
        _missingLink.LinkClicked += (_, _) => Open(Installer.WebView2Download);
        FormClosing += (_, e) => e.Cancel = _busy;
        ResumeLayout(false);
        PerformLayout();
    }

    private static FlowLayoutPanel Page() => new()
    {
        Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Visible = false, AutoScroll = false, Margin = new Padding(0),
    };

    private static void Style(Label label, Font font, Color colour, int left, int top, int right, int bottom)
    {
        label.AutoSize = true;
        label.MaximumSize = new Size(Width96, 0);
        label.Font = font;
        label.ForeColor = colour;
        label.Margin = new Padding(left, top, right, bottom);
    }

    private void SetUpInstall(SetupChoices choices)
    {
        var old = Installer.ExistingVersion();
        var verb = !_installed ? "Install" : old is not null && old != _version ? "Update" : "Reinstall";
        _chooseHeading.Text = verb == "Update" ? $"Update Sallyport {old} → {_version}" : $"{verb} Sallyport {_version}";
        _chooseIntro.Text = _installed
            ? $"Sallyport is {(verb == "Update" ? "updated" : "reinstalled")} where it is. Your VPN profiles, apps and settings are kept."
            : "Sallyport routes the games and apps you choose through your OpenVPN server; everything else keeps your normal connection. " +
              "A small Windows service does the routing, idle until you use it.";
        _where.Box.ReadOnly = _installed;
        _browse.Visible = !_installed;
        if (_installed) _where.Width = Width96;
        _option.Text = "Install the packet driver";
        _option.Description = "Routes games without any setting in them: NT Kernel's free Windows Packet Filter, fetched from NT Kernel. " +
            "The network pauses for a few seconds while it goes in. Without it, apps can still use Sallyport's proxy.";
        _option.Checked = choices.Driver;
        _missingNote.Text = _missing ?? "";
        _missingNote.Visible = _missing is not null;
        _missingLink.Visible = _missing?.Contains(Installer.WebView2Download, StringComparison.Ordinal) == true;
        _primary.Text = verb;
        _whereHint.Text = "Working out the space it needs…";
        CheckWhere();

        Shown += async (_, _) =>
        {
            var needed = await Task.Run(() =>
            {
                using var stream = _payload();
                return stream is null ? (long?)null : Installer.UnpackedBytes(stream);
            });
            var driver = await Task.Run(Installer.DriverState);
            if (IsDisposed) return;
            _needed = needed ?? -1;
            if (driver is not (ServiceState.NotInstalled or ServiceState.Unknown))
            {
                // Already here (Sallyport's or another program's): nothing to choose, and nothing setup will fetch.
                _option.Text = "Packet driver";
                _option.Description = "Already installed on this computer.";
                _option.Checked = true;
                _option.Enabled = false;
            }
            CheckWhere();
        };
    }

    private void SetUpRemoval(SetupChoices choices)
    {
        var installer = new Installer(_ => { }, _where.Box.Text);
        _chooseHeading.Text = "Remove Sallyport";
        _chooseIntro.Text = $"Stops any routing, then removes the Sallyport service, its Start menu entry and its files from {installer.Folder}. " +
            (installer.RemovesDriver
                ? "The packet driver Sallyport installed goes too; the network pauses for a few seconds while it does."
                : "The packet driver stays: Sallyport did not install it.");
        _whereLabel.Visible = _whereRow.Visible = _whereHint.Visible = _missingNote.Visible = _missingLink.Visible = false;
        _option.Text = "Also remove my VPN profiles and settings";
        _option.Description = "Sallyport's encrypted copies, kept in your Windows account. The .ovpn files you imported are not touched.";
        _option.Checked = choices.RemoveData;
        _primary.Text = "Remove";
    }

    // The folder as typed or chosen, checked as it changes: why it will not do, or what it needs and what is free.
    private void CheckWhere()
    {
        if (_uninstall) return;
        var folder = _where.Box.Text.Trim();
        string? trouble;
        string hint;
        if (_installed)
        {
            trouble = null;
            hint = "Sallyport is installed here. To move it, remove it from Windows' installed apps, then install it again.";
        }
        else if (Installer.Unfit(folder) is { } why)
        {
            trouble = hint = why;
        }
        else if (_needed is null)
        {
            trouble = null;
            hint = "Working out the space it needs…";
        }
        else if (_needed < 0)
        {
            trouble = hint = "This setup carries nothing to install.";
        }
        else
        {
            var free = Installer.FreeBytes(folder);
            var drive = Path.GetPathRoot(Path.GetFullPath(folder))!.TrimEnd('\\');
            // Room for the files and for the update after them, which is staged beside these before it replaces them.
            if (free is { } bytes && bytes < _needed * 2) trouble = hint = $"Needs {Amount(_needed.Value * 2)} free; {drive} has {Amount(bytes)}.";
            else
            {
                trouble = null;
                hint = free is { } room ? $"Needs {Amount(_needed.Value)} · {Amount(room)} free on {drive}" : $"Needs {Amount(_needed.Value)}";
            }
        }
        _whereHint.Text = hint;
        _whereHint.ForeColor = trouble is null ? _theme.Secondary : _theme.Trouble;
        _primary.Enabled = trouble is null && _missing is null && _needed is not null;
    }

    private static string Amount(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):F1} GB" : $"{Math.Max(1, bytes / (1L << 20)):N0} MB";

    private void Browse()
    {
        var current = _where.Box.Text.Trim();
        using var pick = new FolderBrowserDialog
        {
            Description = "Choose where to install Sallyport. It goes in a Sallyport folder inside the one you choose.",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };
        try
        {
            if (Path.IsPathFullyQualified(current) && Path.GetDirectoryName(Path.GetFullPath(current)) is { } parent && Directory.Exists(parent))
                pick.InitialDirectory = parent;
        }
        catch (ArgumentException) { }
        if (pick.ShowDialog(this) != DialogResult.OK) return;
        // A folder is chosen to install INTO: Sallyport goes in a folder of its own, so choosing "D:\Games" can never put
        // it among, or replace, what is there. A folder already named Sallyport is taken as the one meant.
        var chosen = Path.TrimEndingDirectorySeparator(pick.SelectedPath);
        _where.Box.Text = Path.GetFileName(chosen).Equals(Installer.Name, StringComparison.OrdinalIgnoreCase) ? chosen : Path.Combine(chosen, Installer.Name);
    }

    private async Task PrimaryAsync()
    {
        if (_finish.Visible)
        {
            if (_primary.Text == "Try again")
            {
                ShowPage(_choose);
                CheckWhere();
                return;
            }
            if (!_uninstall && _openNow.Visible && _openNow.Checked && _installer is not null)
                // Through Explorer, so the window runs as the person, not as the administrator this setup is.
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_installer.Client}\"") { UseShellExecute = false });
            Close();
            return;
        }
        await RunAsync();
    }

    private async Task RunAsync()
    {
        if (!_uninstall && (Installer.Unfit(_where.Box.Text.Trim()) is not null || _missing is not null)) return;
        _busy = true;
        _details.Clear();
        _meter.Value = 0;
        _meter.Fill = _theme.Accent;
        _progressHeading.Text = _uninstall ? "Removing Sallyport" : _installed ? "Updating Sallyport" : "Installing Sallyport";
        _stepText.Text = "Starting…";
        ShowPage(_progress);
        _primary.Enabled = _secondary.Enabled = false;
        ControlBox = false;

        var installer = _installer = new Installer(Report, _where.Box.Text.Trim());
        _logLink.Visible = true;
        var choices = new SetupChoices(Driver: _option.Checked && _option.Enabled, RemoveData: _option.Checked);
        IReadOnlyList<string>? left = null;
        string? failure = null;
        try
        {
            if (_uninstall) left = await Task.Run(() => installer.UninstallAsync(choices, CancellationToken.None));
            else
            {
                await using var payload = _payload() ?? throw new SetupFailed("This setup carries nothing to install.");
                left = await Task.Run(() => installer.InstallAsync(payload, _version, choices, CancellationToken.None));
            }
        }
        catch (Exception e) when (e is SetupFailed or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidDataException or HttpRequestException)
        {
            failure = e.Message;
            Report(new SetupProgress($"Stopped: {failure}", _meter.Value));
        }
        finally
        {
            _busy = false;
            ControlBox = true;
        }
        Finish(left, failure);
    }

    private void Report(SetupProgress progress)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Report(progress));
            return;
        }
        _meter.Value = progress.Done;
        if (progress.Text is not { } text) return;
        _stepText.Text = text;
        _details.AppendText($"{DateTime.Now:HH:mm:ss}  {text}{Environment.NewLine}");
    }

    private void Finish(IReadOnlyList<string>? left, string? failure)
    {
        var what = _uninstall ? "removed" : _installed ? "updated" : "installed";
        if (failure is not null)
        {
            _mark.Shown = Mark.Kind.Failed;
            _finishHeading.Text = _uninstall ? "Sallyport was not removed" : "Setup did not finish";
            _finishText.Text = $"{failure}\n\nThe details below, and the setup log, say every step it took.";
            _primary.Text = "Try again";
            _secondary.Text = "Close";
            _secondary.Visible = true;
        }
        else
        {
            var partly = left is { Count: > 0 };
            _mark.Shown = partly ? Mark.Kind.Partly : Mark.Kind.Done;
            _finishHeading.Text = partly ? $"Sallyport is {what}, with something left" : $"Sallyport is {what}";
            var done = _uninstall
                ? "Its service, Start menu entry and files are gone."
                : $"It is in {_installer!.Folder}. Find it in the Start menu, or open a .ovpn file with it.";
            _finishText.Text = partly ? $"{done}\n\n{string.Join("\n", left!)}" : done;
            _primary.Text = "Finish";
            _secondary.Visible = false;
        }
        _openNow.Visible = !_uninstall && failure is null;
        // The steps stay one click away on the last page too, open where it failed.
        _finish.Controls.AddRange([_detailsLink, _details]);
        _details.Visible = failure is not null;
        _detailsLink.Text = _details.Visible ? "Hide details" : "Show details";
        _primary.Enabled = _secondary.Enabled = true;
        ShowPage(_finish);
        _primary.Focus();
    }

    private void ShowPage(Control page)
    {
        foreach (var p in new[] { _choose, _progress, _finish }) p.Visible = p == page;
        LayOutFooter();
    }

    private void LayOutFooter()
    {
        var s = DeviceDpi / 96f;
        var gap = (int)(8 * s);
        var right = _footer.Width - (int)(24 * s);
        foreach (var button in new[] { _primary, _secondary })
        {
            if (!button.Visible) continue;
            var size = button.GetPreferredSize(Size.Empty);
            button.SetBounds(right - size.Width, (_footer.Height - size.Height) / 2, size.Width, size.Height);
            right -= size.Width + gap;
        }
        _logLink.Location = new Point((int)(24 * s), (_footer.Height - _logLink.Height) / 2);
    }

    private void PaintHeader(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        var s = DeviceDpi / 96f;
        Draw.Gate(g, new RectangleF(24 * s, 18 * s, 40 * s, 40 * s), SetupTheme.Brand, Color.White);
        var x = (int)(78 * s);
        TextRenderer.DrawText(g, "Sallyport", _titleFont, new Point(x, (int)(15 * s)), _theme.Text, TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, $"Version {_version} · routes the games and apps you choose through your VPN", Font,
            new Point(x, (int)(41 * s)), _theme.Secondary, TextFormatFlags.NoPrefix);
        using var line = new Pen(_theme.Divider);
        g.DrawLine(line, 0, _header.Height - 1, _header.Width, _header.Height - 1);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _theme.Apply(this, _theme.Surface);
    }

    private static void Open(string target)
    {
        // Through Explorer: the page or the log opens as the person, in what they open such things with.
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = false }); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _headingFont.Dispose();
            _titleFont.Dispose();
            _strongFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
