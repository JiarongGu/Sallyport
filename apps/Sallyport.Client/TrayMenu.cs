namespace Sallyport.Client;

/// <summary>What choosing a tray entry does; <see cref="TrayEntry.Target"/> names the profile or app it acts on.</summary>
internal enum TrayVerb { None, OpenWindow, OpenSettings, Connect, Disconnect, Exit }

internal enum TrayEntryKind { Command, Note, Submenu, Separator }

/// <summary>One entry of the tray's menu: a command, a line that only says something (a note, not pressable), a
/// submenu of <see cref="Items"/>, or a separator. <see cref="Tip"/> is why a command cannot be chosen, or what it does.</summary>
internal sealed record TrayEntry(
    TrayEntryKind Kind, string Text, TrayVerb Verb = TrayVerb.None, string? Target = null, bool Enabled = true, string? Tip = null,
    bool Default = false, IReadOnlyList<TrayEntry>? Items = null)
{
    public static readonly TrayEntry Separator = new(TrayEntryKind.Separator, "");

    public static TrayEntry Note(string text) => new(TrayEntryKind.Note, text, Enabled: false);

    public static TrayEntry Submenu(string text, IReadOnlyList<TrayEntry> items) => new(TrayEntryKind.Submenu, text, Items: items);
}

/// <summary>
/// The tray's menu from the window's own views (docs/design/client-ui.md §7), so the two never disagree: each profile
/// to connect or disconnect — what goes through it is set in the window (the owner: no app selection here) — and the
/// way out. An action the window hides is left out; one it disables is shown disabled, with the window's reason as its tip.
/// </summary>
internal static class TrayMenu
{
    public static IReadOnlyList<TrayEntry> Build(IReadOnlyList<ProfileView> profiles, string networkStatus)
    {
        var menu = new List<TrayEntry>
        {
            // The window on its Overview, where the switch is; what is connected, its tip.
            new(TrayEntryKind.Command, L.T("Open Sallyport"), TrayVerb.OpenWindow, Tip: networkStatus, Default: true),
            TrayEntry.Separator,
        };

        if (profiles.Count == 0) menu.Add(TrayEntry.Note(L.T("No profiles yet — open Sallyport to import one")));
        foreach (var profile in profiles)
        {
            var items = new List<TrayEntry>();
            if (profile.DetailText is { } detail) items.Add(TrayEntry.Note(detail));
            Add(items, profile.Actions.Connect, TrayVerb.Connect, profile.Id);
            Add(items, profile.Actions.Disconnect, TrayVerb.Disconnect, profile.Id);
            if (items.Count == 0) items.Add(TrayEntry.Note(profile.StatusText));
            menu.Add(TrayEntry.Submenu($"{profile.Name} — {profile.StatusText}", items));
        }

        menu.Add(TrayEntry.Separator);
        menu.Add(new(TrayEntryKind.Command, L.T("Settings"), TrayVerb.OpenSettings));
        menu.Add(new(TrayEntryKind.Command, L.T("Exit Sallyport"), TrayVerb.Exit, Tip: L.T("Disconnects, then quits. Your apps use your own connection until Sallyport connects again.")));
        return menu;
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

    private static void Add(List<TrayEntry> items, UiAction action, TrayVerb verb, string target)
    {
        if (!action.Visible) return;
        items.Add(new(TrayEntryKind.Command, action.Label, verb, target, action.Enabled, action.Reason));
    }
}
