namespace Sallyport.Client;

/// <summary>What choosing a tray entry does; <see cref="TrayEntry.Target"/> names the profile it acts on.</summary>
internal enum TrayVerb { None, OpenWindow, OpenSettings, Connect, Disconnect, Exit }

internal enum TrayEntryKind { Command, Note, Separator }

/// <summary>What a command shows before its text: the app's mark, or one of Windows' own glyphs.</summary>
internal enum TrayGlyph { None, Mark, Settings, Exit }

/// <summary>One entry of the tray's menu: a command, a line that only says something (a note, not pressable), or a
/// separator. <see cref="Tip"/> is why a command cannot be chosen, or what it does. A profile's row (D24's one switch, in
/// the tray) carries its <see cref="Tone"/> as a dot, its <see cref="Status"/> beside its name, and what pressing it does,
/// <see cref="Action"/>, at its end.</summary>
internal sealed record TrayEntry(
    TrayEntryKind Kind, string Text, TrayVerb Verb = TrayVerb.None, string? Target = null, bool Enabled = true, string? Tip = null,
    bool Default = false, TrayGlyph Glyph = TrayGlyph.None, Tone? Tone = null, string? Status = null, string? Action = null)
{
    public static readonly TrayEntry Separator = new(TrayEntryKind.Separator, "");

    public static TrayEntry Note(string text) => new(TrayEntryKind.Note, text, Enabled: false);
}

/// <summary>
/// The tray's menu from the window's own views (docs/design/client-ui.md §7), so the two never disagree: each profile a
/// row that connects or disconnects it as pressed — what goes through it is set in the window (the owner: no app
/// selection here) — and the way out. An action the window hides is left out; one it disables is shown disabled, with
/// the window's reason as its tip.
/// </summary>
internal static class TrayMenu
{
    public static IReadOnlyList<TrayEntry> Build(IReadOnlyList<ProfileView> profiles, string networkStatus)
    {
        var menu = new List<TrayEntry>
        {
            // The window on its Overview, where the switch is; what is connected, its tip.
            new(TrayEntryKind.Command, L.T("Open Sallyport"), TrayVerb.OpenWindow, Tip: networkStatus, Default: true, Glyph: TrayGlyph.Mark),
            TrayEntry.Separator,
        };

        if (profiles.Count == 0) menu.Add(TrayEntry.Note(L.T("No profiles yet — open Sallyport to import one")));
        foreach (var profile in profiles) menu.Add(Row(profile));

        menu.Add(TrayEntry.Separator);
        menu.Add(new(TrayEntryKind.Command, L.T("Settings"), TrayVerb.OpenSettings, Glyph: TrayGlyph.Settings));
        menu.Add(new(TrayEntryKind.Command, L.T("Exit Sallyport"), TrayVerb.Exit, Tip: L.T("Disconnects, then quits. Your apps use your own connection until Sallyport connects again."),
            Glyph: TrayGlyph.Exit));
        return menu;
    }

    /// <summary>A profile as one row, pressed to do the one thing the window would offer now: disconnect (or cancel) while
    /// it runs, connect (or try again) while it does not. Nothing to do — it is being removed, say — is a row that only
    /// says where it is.</summary>
    private static TrayEntry Row(ProfileView profile)
    {
        var (action, verb) = profile.Actions.Disconnect.Visible ? (profile.Actions.Disconnect, TrayVerb.Disconnect)
            : profile.Actions.Connect.Visible ? (profile.Actions.Connect, TrayVerb.Connect) : (UiAction.Hidden, TrayVerb.None);
        // Why it cannot be pressed, else what the window would say under it (the attempts since it was lost, say).
        var tip = !action.Enabled && action.Reason is { } why ? why : profile.DetailText;
        return verb == TrayVerb.None
            ? new(TrayEntryKind.Note, profile.Name, Enabled: false, Tip: tip, Tone: profile.Tone, Status: profile.StatusText)
            : new(TrayEntryKind.Command, profile.Name, verb, profile.Id, action.Enabled, tip, Tone: profile.Tone,
                Status: profile.StatusText, Action: action.Label);
    }

    /// <summary>The icon's tip: the window's summary, cut at the last whole phrase that fits the notification area's
    /// 127 characters (it refuses a longer one).</summary>
    public static string Tooltip(string summary)
    {
        var text = L.T("Sallyport — {0}", summary);
        if (text.Length <= 127) return text;
        var cut = text.LastIndexOf(" · ", 124, StringComparison.Ordinal);
        return (cut > 0 ? text[..cut] : text[..124]) + " …";
    }
}
