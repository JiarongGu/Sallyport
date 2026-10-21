using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Sallyport.Profile;
using Sallyport.Runtime;
using Sallyport.Windows;
using Sallyport.Windows.PacketFilter;
using Sallyport.Windows.Routing;
using Sallyport.Windows.Service;
using Shenora;
using Shenora.Core.Events;
using Shenora.Core.Ipc;
using Shenora.Modules.FileDialog;

namespace Sallyport.Client;

/// <summary>
/// What the window and the tray both drive, and the only author of what the window shows: the page is sent
/// <c>CLIENT.STATE</c> (<see cref="ClientState"/>) whenever anything changes and <c>CLIENT.NOTICE</c> for each outcome,
/// and sends back intents that answer nothing. An edit and a confirmation are the host's too, so the page never decides
/// what may be saved or removed. It never sees a profile's text (D14). Games are run through the router service
/// (<see cref="GameRunner"/>, docs/design/product.md).
/// </summary>
internal sealed partial class ClientCore
{
    public const string Module = "CLIENT";

    /// <summary>A profile with its certificates inline is a few kilobytes; a file far larger is not one.</summary>
    private const long LargestProfile = 1 << 20;
    private const int LongestName = 64;
    private const int LongestArguments = 2048;
    private const int NoticesKept = 20;
    private static readonly TimeSpan InfoNoticeLasts = TimeSpan.FromSeconds(8);

    private readonly ProfileStore _store;
    private readonly ClientSettings _settings;
    private readonly Connections _connections;
    private readonly RoutedItemStore _items;
    private readonly GameRunner _runner;
    private readonly PacketFilterSetup _driverSetup;
    private readonly ShenoraPaths _paths;
    private readonly IEventBus _events;
    private readonly IFileDialogs _dialogs;

    private readonly object _publishing = new();
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, byte> _locating = new();
    private readonly ConcurrentDictionary<string, byte> _locationFailed = new();
    private readonly ConcurrentDictionary<string, byte> _disconnecting = new();
    private readonly ConcurrentDictionary<string, byte> _removing = new();
    private readonly ConcurrentDictionary<string, AppPhase> _launching = new();
    private readonly List<Notice> _notices = [];
    private OpenEditor? _editor;
    private OpenConfirmation? _confirmation;
    private string? _dialog;
    // What the driver and the service are, read at most every few seconds: the window's state is taken far more often.
    private (DriverFacts Facts, long At)? _driverFacts;
    private string? _driverWork;
    private const long DriverFactsLast = 3000;
    private long _sequence;
    private long _noticeIds;
    private NavigationView? _navigation;
    private long _navigations;
    // The app whose page is open, whose programs are looked at while it is (ProgramWatch).
    private volatile string? _watchedApp;
    private readonly ProgramWatch _programs;
    private readonly AppIcons _icons = new();

    private sealed record OpenEditor(string Token, string Kind, string SubjectId, object Values, IReadOnlyDictionary<string, string> Errors);

    private sealed record OpenConfirmation(string Token, string Kind, string SubjectId, string Title, string Text, string ConfirmLabel, string CancelLabel);

    /// <summary>Raised after every published state, on whichever thread published it.</summary>
    public event Action? Changed;

    /// <summary>Raised with every notice, so the tray can say it while the window is hidden.</summary>
    public event Action<Notice>? Noticed;

    public ClientCore(ProfileStore store, ClientSettings settings, Connections connections, RoutedItemStore items, GameRunner runner,
        ShenoraPaths paths, IEventBus events, IFileDialogs dialogs)
    {
        _store = store;
        _settings = settings;
        L.Current = L.Resolve(settings.Language);
        _connections = connections;
        _items = items;
        _runner = runner;
        _paths = paths;
        _events = events;
        _dialogs = dialogs;
        // The pinned installer, fetched again when absent: a cache (D25), held unchangeable while it runs.
        _driverSetup = new PacketFilterSetup(paths.DataArea(Path.Combine("cache", "drivers")));
        _connections.Changed += Publish;
        // What is routed follows what the person connected and which apps are on (D23): looked at again whenever either
        // changes.
        _connections.Changed += Replan;
        // A profile that signs in connects with its remembered login or one typed for this connection (D29).
        _connections.LoginFor = LoginFor;
        _connections.SignInRefused += SignInRefusedBy;
        _runner.Changed += Publish;
        _runner.Lost += TurnOffOnLoss;
        _runner.Failed += SayFailed;
        _programs = new ProgramWatch(() => _watchedApp is { } watched && _items.Find(watched) is { } item ? [.. item.Programs.Select(p => p.Path)] : [], Publish);
        // Said once, where the person will see it: the window shows warnings until they are dismissed.
        if (items.Unreadable is { } why)
            Notify(NoticeKind.Warning, L.T("Your apps could not be read: {0}. Sallyport starts without them and keeps the file as it is until you change an app.", why));
        else if (items.Migrated is { Unreadable: > 0 and var lost })
            Notify(NoticeKind.Warning, L.Count(lost, "Some apps could not be migrated: 1 earlier game file could not be read. It is kept as it was.",
                "Some apps could not be migrated: {0} earlier game files could not be read. They are kept as they were."));
        StartUpdates();
    }

    public ClientState Snapshot()
    {
        lock (_publishing) return Take();
    }

    public void Publish()
    {
        lock (_publishing)
        {
            // Taken and emitted under one lock, so states leave in the order they were numbered.
            _events.Emit(Module, "STATE", JsonSerializer.SerializeToElement(Take(), ClientView.Json));
        }
        Changed?.Invoke();
    }

    /// <summary>A notice; <paramref name="stays"/>, one that waits to be dismissed whatever its kind (an update's outcome,
    /// said as the window opens: it would otherwise be gone before the page is drawn).</summary>
    public void Notify(NoticeKind kind, string text, NoticeSubject? subject = null, bool stays = false)
    {
        var notice = new Notice(Interlocked.Increment(ref _noticeIds).ToString(CultureInfo.InvariantCulture), kind, text, subject, DateTimeOffset.Now,
            UiAction.Shown(L.T("Dismiss")));
        lock (_gate)
        {
            _notices.Insert(0, notice);
            if (_notices.Count > NoticesKept) _notices.RemoveRange(NoticesKept, _notices.Count - NoticesKept);
        }
        // An outcome worth a moment, not a record: it leaves on its own. A warning or an error waits to be dismissed.
        if (kind == NoticeKind.Info && !stays) _ = Task.Delay(InfoNoticeLasts).ContinueWith(_ => Dismiss(notice.Id), TaskScheduler.Default);
        _events.Emit(Module, "NOTICE", JsonSerializer.SerializeToElement(notice, ClientView.Json));
        Publish();
        Noticed?.Invoke(notice);
    }

    /// <summary>Asks the window to show a page (the tray's Open Network): once, by a new request number, so a later state
    /// does not pull the person back from where they went.</summary>
    public void Navigate(Page page, string? app = null)
    {
        lock (_gate) _navigation = new NavigationView(page, Interlocked.Increment(ref _navigations), app);
        Publish();
    }

    /// <summary>The window's, the tray's and new notices' language: "en", "zh-Hans", or "system" for Windows' own.</summary>
    public void SetLanguage(string language)
    {
        if (language is not ("system" or "en" or "zh-Hans")) throw new ShenoraException("LANGUAGE", "language", language, L.T("That language is not one Sallyport speaks."));
        _settings.Language = language == "system" ? null : language;
        L.Current = L.Resolve(_settings.Language);
        Publish();
    }

    /// <summary>Settings' VPN protocol: "" for each profile's servers as listed, "udp" or "tcp". From the next connection.</summary>
    public void SetProtocol(string protocol)
    {
        if (protocol is not ("" or "udp" or "tcp")) throw new ShenoraException("PROTOCOL", "protocol", protocol, L.T("That is not a VPN protocol."));
        _settings.Connection = _settings.Connection with { Protocol = protocol == "" ? null : protocol };
        Publish();
    }

    /// <summary>Settings' "If it cannot connect": the failed rounds before a connection stops, 0 never.</summary>
    public void SetRetry(int rounds)
    {
        if (rounds is < 0 or > 1000) throw new ShenoraException("RETRY", "rounds", rounds.ToString(CultureInfo.InvariantCulture), L.T("Use 0 (keep trying) to 1000 rounds."));
        _settings.Connection = _settings.Connection with { RoundsMax = rounds };
        Publish();
    }

    /// <summary>The app whose page the window shows, or none: its programs are looked at while it is open.</summary>
    public void WatchApp(string? id)
    {
        if (id is not null && !RoutedItemStore.IsId(id)) throw new ShenoraException("APP_GONE", "id", id, L.T("That app is no longer listed."));
        _watchedApp = id;
        _programs.Poke();
        Publish();
    }

    /// <summary>The removal, as Windows' installed apps start it: the small uninstaller beside this program (D32), which
    /// asks to be administrator, then asks the person, and closes this window when they go ahead; an install without
    /// one, this program with /uninstall (D30).</summary>
    public void Uninstall()
    {
        if (InstallLayout.InstalledHere() is not { } folder || Environment.ProcessPath is not { } self) return;
        var uninstaller = Path.Combine(folder, "uninstall.exe");
        // Through the shell: its manifest asks for administrator, which a start of our own would refuse.
        var start = File.Exists(uninstaller) ? new ProcessStartInfo(uninstaller) { UseShellExecute = true }
            : new ProcessStartInfo(self, "/uninstall") { UseShellExecute = false };
        try { Process.Start(start)?.Dispose(); }
        catch (System.ComponentModel.Win32Exception e) { Notify(NoticeKind.Error, L.T("The removal did not start ({0}).", e.Message)); }
    }

    public void SetStartWithWindows(bool on)
    {
        try { StartupRegistration.Set(on); }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        { throw new ShenoraException("STARTUP", "startup", "", L.T("Windows did not let Sallyport change how it starts ({0}).", e.GetType().Name)); }
        Notify(NoticeKind.Info, on ? L.T("Sallyport starts in the notification area when you sign in.") : L.T("Sallyport no longer starts when you sign in."));
    }

    public void Dismiss(string noticeId)
    {
        bool removed;
        lock (_gate) removed = _notices.RemoveAll(n => n.Id == noticeId) > 0;
        if (removed) Publish();
    }

    private void Confirm(string kind, string subject, string title, string text, string confirm, string cancel)
    {
        lock (_gate) _confirmation = new OpenConfirmation(NewToken(), kind, subject, title, text, confirm, cancel);
        Publish();
    }

    /// <summary>What a confirmation asked, done: removing a profile or a game, installing or removing the driver.</summary>
    public async Task ConfirmAsync(string token)
    {
        OpenConfirmation? confirmation;
        lock (_gate)
        {
            confirmation = _confirmation?.Token == token ? _confirmation : null;
            _confirmation = null;
        }
        if (confirmation is null) return;
        switch (confirmation.Kind)
        {
            case "remove-profile": await RemoveProfileAsync(confirmation.SubjectId); break;
            case "disconnect": await TakeDownAsync(confirmation.SubjectId); break;
            case "remove-app": RemoveApp(confirmation.SubjectId); break;
            case "driver-install": await ManageDriverAsync(install: true); break;
            case "driver-remove": await ManageDriverAsync(install: false); break;
            case "update": await ApplyUpdateAsync(); break;
        }
    }

    public void CancelConfirmation(string token)
    {
        lock (_gate)
        {
            if (_confirmation?.Token != token) return;
            _confirmation = null;
        }
        Publish();
    }

    // ---- helpers

    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

    private static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private OpenEditor? EditorFor(string token, string kind)
    {
        lock (_gate) return _editor is { } e && e.Token == token && e.Kind == kind ? e : null;
    }

    private StoredProfile Profile(string id) =>
        _store.Find(id) ?? throw new ShenoraException("PROFILE_GONE", "id", id, L.T("That profile is no longer stored."));

    private RoutedItem Item(string id) =>
        _items.Find(id) ?? throw new ShenoraException("APP_GONE", "id", id, L.T("That app is no longer listed."));

    private async Task<FileDialogResult?> WithDialogAsync(string which, Func<Task<FileDialogResult>> show)
    {
        if (Interlocked.CompareExchange(ref _dialog, which, null) is not null) return null;
        Publish();
        try { return await show(); }
        finally
        {
            Volatile.Write(ref _dialog, null);
            Publish();
        }
    }

    /// <summary>The clipboard wants a single-threaded apartment; an intent may arrive on any thread. False, and said, when
    /// another program held the clipboard throughout: thrown on a thread of its own, that would end Sallyport.</summary>
    private bool Clipboard(string text)
    {
        var copied = false;
        var copying = new Thread(() =>
        {
            // Windows' clipboard is one program's at a time: asked again a few times, as Windows Forms can.
            try
            {
                System.Windows.Forms.Clipboard.SetDataObject(text, copy: true, retryTimes: 10, retryDelay: 50);
                copied = true;
            }
            catch (System.Runtime.InteropServices.ExternalException) { }
        });
        copying.SetApartmentState(ApartmentState.STA);
        copying.Start();
        copying.Join();
        if (!copied) Notify(NoticeKind.Warning, L.T("Nothing was copied: another program is using the clipboard. Try again."));
        return copied;
    }

}
