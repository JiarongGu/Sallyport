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
internal sealed class ClientCore
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
        _driverSetup = new PacketFilterSetup(paths.DataArea("drivers"));
        _connections.Changed += Publish;
        _runner.Changed += Publish;
        _programs = new ProgramWatch(() => _watchedApp is { } watched && _items.Find(watched) is { } item ? [.. item.Programs.Select(p => p.Path)] : [], Publish);
        // Said once, where the person will see it: the window shows warnings until they are dismissed.
        if (items.Unreadable is { } why)
            Notify(NoticeKind.Warning, L.T("Your apps could not be read: {0}. Sallyport starts without them and keeps the file as it is until you change an app.", why));
        else if (items.Migrated is { Unreadable: > 0 and var lost })
            Notify(NoticeKind.Warning, L.Count(lost, "Some apps could not be migrated: 1 earlier game file could not be read. It is kept as it was.",
                "Some apps could not be migrated: {0} earlier game files could not be read. They are kept as they were."));
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

    public void Notify(NoticeKind kind, string text, NoticeSubject? subject = null)
    {
        var notice = new Notice(Interlocked.Increment(ref _noticeIds).ToString(CultureInfo.InvariantCulture), kind, text, subject, DateTimeOffset.Now,
            UiAction.Shown(L.T("Dismiss")));
        lock (_gate)
        {
            _notices.Insert(0, notice);
            if (_notices.Count > NoticesKept) _notices.RemoveRange(NoticesKept, _notices.Count - NoticesKept);
        }
        // An outcome worth a moment, not a record: it leaves on its own. A warning or an error waits to be dismissed.
        if (kind == NoticeKind.Info) _ = Task.Delay(InfoNoticeLasts).ContinueWith(_ => Dismiss(notice.Id), TaskScheduler.Default);
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

    // ---- profiles

    /// <summary>Asks for a profile file and imports it. One dialog at a time: a second ask while one is open is dropped,
    /// as a second press of a button already pressed would be.</summary>
    public async Task ImportFromDialogAsync()
    {
        var picked = await WithDialogAsync("import", () => _dialogs.OpenFileAsync(new OpenFileOptions
        {
            Title = L.T("Import an OpenVPN profile"),
            Filters = [new FileDialogFilter { Name = L.T("OpenVPN profiles"), Extensions = ["ovpn", "conf"] }],
            RememberPathKey = "import-profile",
        }));
        if (picked is { Success: true, FilePath: { } path }) Import(path);
    }

    public StoredProfile Import(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new ShenoraException("FILE_GONE", "file", file.Name, L.T("{0} is no longer there.", file.Name));
        if (file.Length > LargestProfile) throw new ShenoraException("PROFILE_INVALID", "file", file.Name, L.T("{0} is far larger than any profile.", file.Name));
        var name = Path.GetFileNameWithoutExtension(file.Name).Trim();
        if (name.Length == 0) name = "profile";
        if (name.Length > LongestName) name = name[..LongestName];

        StoredProfile stored;
        // The store's own reasons never quote the text, so they may cross to the page.
        try { stored = _store.Import(name, File.ReadAllText(file.FullName)); }
        catch (InvalidDataException e) { throw new ShenoraException("PROFILE_INVALID", "file", file.Name, L.T("{0} was not imported: {1}.", file.Name, e.Message)); }
        // The next step is the app: with none yet, the window goes where one is added.
        if (_items.List().Count == 0)
        {
            Notify(NoticeKind.Info, L.T("Imported {0}. Now add the game or program that should use it.", stored.Name), new("profile", stored.Id));
            Navigate(Page.Apps);
        }
        else Notify(NoticeKind.Info, L.T("Imported {0}. Its copy is encrypted for your Windows account.", stored.Name), new("profile", stored.Id));
        return stored;
    }

    public void Connect(string id)
    {
        var profile = Profile(id);
        if (profile.Unsupported is { } why) throw new ShenoraException("PROFILE_UNSUPPORTED", "id", id, L.T("This version cannot connect {0}: {1}.", profile.Name, why));
        _locationFailed.TryRemove(id, out _);
        _connections.Connect(id);
    }

    /// <summary>Takes the profile's channel down — its proxy, and what is routed through it, asked about first.</summary>
    public async Task DisconnectAsync(string id)
    {
        if (!_connections.IsRunning(id)) return;
        if (_runner.ProfileInUse == id && _runner.Facts() is { Ended: null } run && _items.Find(run.GameId) is { } routed)
        {
            Confirm("disconnect", id, L.T("Disconnect {0}?", Profile(id).Name),
                L.T("{0} uses it. Its VPN turns off too, and it carries on over your own connection.", routed.Name), L.T("Disconnect"), L.T("Keep connected"));
            return;
        }
        await TakeDownAsync(id);
    }

    private async Task TakeDownAsync(string id)
    {
        if (!_connections.IsRunning(id) || !_disconnecting.TryAdd(id, 0)) return;
        Publish();
        try { await _connections.DisconnectAsync(id); }
        finally
        {
            _disconnecting.TryRemove(id, out _);
            _locationFailed.TryRemove(id, out _);
            Publish();
        }
    }

    public async Task LocateAsync(string id, CancellationToken ct)
    {
        if (!_locating.TryAdd(id, 0)) return;
        _locationFailed.TryRemove(id, out _);
        Publish();
        try { await _connections.LocateAsync(id, ct); }
        // Could not be checked is not "failed" (rule 6): the row says so, and the tunnel is left as it is.
        catch (ShenoraException e) when (e.Code == "CHECK_UNAVAILABLE") { _locationFailed.TryAdd(id, 0); }
        finally
        {
            _locating.TryRemove(id, out _);
            Publish();
        }
    }

    public async Task CheckLatencyAsync(string id, CancellationToken ct)
    {
        Profile(id);
        try { await _connections.CheckLatencyAsync(id, ct); }
        // A check that could not be made is said as that, and changes nothing about the channel (rule 6).
        catch (Exception e) when (e is System.Net.NetworkInformation.PingException or TunnelEnded or ObjectDisposedException)
        { Notify(NoticeKind.Warning, L.T("Ping could not be checked: the connection changed while it was being measured."), new("profile", id)); }
    }

    public void CopyEndpoint(string id)
    {
        var profile = Profile(id);
        var endpoint = ClientView.Endpoint(_connections.ProxyPortOf(id)
            ?? throw new ShenoraException("NOT_CONNECTED", "id", id, L.T("Connect {0} first: its proxy takes a free port when it connects.", profile.Name)));
        Clipboard(endpoint);
        Notify(NoticeKind.Info, L.T("Copied {0}.", endpoint), new("profile", id));
    }

    public void RequestRemoveProfile(string id)
    {
        var profile = Profile(id);
        if (_connections.IsRunning(id)) throw new ShenoraException("CONNECTED", "id", id, L.T("Disconnect {0} before removing it.", profile.Name));
        if (_runner.ProfileInUse == id) throw new ShenoraException("PROFILE_IN_USE", "id", id, L.T("Turn off the VPN of the app that uses it first."));
        var inUse = _items.List().Count(i => i.ProfileId == id || i.Programs.Any(p => p.ProfileId == id));
        var text = L.Join(L.T("Deletes Sallyport's encrypted copy. The original file stays unchanged."),
            inUse == 0 ? null : L.Count(inUse, "1 app will need another profile.", "{0} apps will need another profile."));
        Confirm("remove-profile", id, L.T("Remove {0}?", profile.Name), text, L.T("Remove profile"), L.T("Keep profile"));
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
        }
    }

    private async Task RemoveProfileAsync(string id)
    {
        var name = _store.Find(id)?.Name;
        if (!_removing.TryAdd(id, 0)) return;
        Publish();
        try
        {
            await _connections.DisconnectAsync(id);
            if (!_store.Remove(id)) return;
            _settings.Forget(id);
        }
        finally
        {
            _removing.TryRemove(id, out _);
            Publish();
        }
        Notify(NoticeKind.Info, L.T("Removed {0}. The original file is unchanged.", name));
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

    // ---- apps (docs/design/window.md): a program or a folder, each program with its methods

    /// <summary>An app from a folder (a start's <c>--add-game</c>).</summary>
    public void AddFolder(string folder)
    {
        if (!Directory.Exists(folder)) throw new ShenoraException("APP_FOLDER", "folder", folder, L.T("That folder is not there."));
        RoutedItem item;
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        if (name.Length == 0) name = "app";
        if (name.Length > LongestName) name = name[..LongestName];
        try { item = WithDefaults(_items.CreateFromFolder(name, folder, DefaultProfile()?.Id)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new ShenoraException("APP_FOLDER", "folder", name, L.T("{0} could not be read: {1}.", name, e.GetType().Name)); }
        var included = item.Programs.Count(p => p.Included);
        Notify(NoticeKind.Info, item.Programs.Count == 0
            ? L.T("Added {0}, but its folder holds no program. Scan it again once it is installed.", item.Name)
            : L.T("Added {0}: {1} of its {2} programs will send all their traffic through the VPN.", item.Name, included, item.Programs.Count), new("app", item.Id));
    }

    /// <summary>Asks for the app's program and makes an app of it — one way in for a game and a program alike: a browser
    /// is launched with proxy settings, its own browser profile kept apart; anything else is an app of its folder, every
    /// program there sending all its traffic through the packet driver, the utilities left out, the one picked what
    /// Launch starts — unless its folder is one many programs share (Windows', Program Files itself, Downloads), where it
    /// is the program alone.</summary>
    // ---- "Add an app": a page of its own, the place first, then what is found there

    // What the open "Add an app" found, for its token: the page shows it, the save makes the app from it.
    private (string Token, SetupPreview Preview)? _setup;

    /// <summary>Opens "Add an app" with nothing chosen yet: the person's defaults for a new app.</summary>
    public void BeginAddApp()
    {
        var defaults = _settings.Defaults;
        lock (_gate)
        {
            _setup = null;
            _editor = new OpenEditor(NewToken(), "new-app", "", new NewAppValues("", "", DefaultProfile()?.Id, defaults.Loss,
                defaults.DirectAfterSeconds?.ToString(CultureInfo.InvariantCulture) ?? "", []), Empty);
        }
        Publish();
    }

    /// <summary>Browse…: one dialog for a folder or a program (Windows' own offers one or the other; this one, both).</summary>
    public async Task BrowseNewAppAsync(string token, NewAppValues values)
    {
        lock (_gate) if (_editor is not { Kind: "new-app" }) return;
        var picked = await WithDialogAsync("add-app", () => _dialogs.OpenFolderAsync(new OpenFolderOptions
        {
            // The dialog's own words say a file; the folder half has to be said, or no one finds it.
            Title = L.T("Choose the program, or open the game's folder and press Open"),
            AllowFileSelection = true,
            Filters = [new FileDialogFilter { Name = L.T("Programs"), Extensions = ["exe"] }],
            RememberPathKey = "add-app",
        }));
        if (picked is not { Success: true, FilePath: { } path }) return;
        // Browse pressed as the path field was left: looking at the place typed renewed the form meanwhile, and this is
        // still its Browse — what the person typed since stands, but the place is the one picked.
        OpenEditor? open;
        lock (_gate) open = _editor;
        if (open is not { Kind: "new-app" }) return;
        SetNewAppLocation(open.Token, (open.Token == token ? values : (NewAppValues)open.Values) with { Path = path });
    }

    /// <summary>The place typed, pasted or browsed to: what is there, said before anything is kept. A new token, so the
    /// page takes the found name and programs; a name the person typed stays.</summary>
    public void SetNewAppLocation(string token, NewAppValues values)
    {
        if (EditorFor(token, "new-app") is not { } editor) return;
        var preview = AppSetup.Preview(values.Path ?? "", OwnFolder, out var why);
        lock (_gate)
        {
            if (_editor?.Token != token) return;
            if (preview is null)
            {
                _setup = null;
                _editor = _editor with { Values = values with { Included = [] }, Errors = new Dictionary<string, string> { ["path"] = why! } };
            }
            else
            {
                var previous = _setup?.Preview.SuggestedName;
                var name = string.IsNullOrWhiteSpace(values.Name) || values.Name.Trim() == previous ? preview.SuggestedName : values.Name;
                var next = new OpenEditor(NewToken(), "new-app", "", values with
                {
                    Path = preview.Path, Name = name, Included = [.. preview.Programs.Where(p => p.Included).Select(p => p.Path)],
                }, Empty);
                _editor = next;
                _setup = (next.Token, preview);
            }
        }
        Publish();
    }

    /// <summary>Add app: made from what was found at the place (looked at again — the page's word is not taken for it),
    /// with the person's name, profile, loss policy and choice of programs. Its page opens.</summary>
    public void SaveNewApp(string token, NewAppValues values)
    {
        if (EditorFor(token, "new-app") is null) return;
        var errors = new Dictionary<string, string>();
        var preview = AppSetup.Preview(values.Path ?? "", OwnFolder, out var why);
        if (preview is null) errors["path"] = why!;
        var name = values.Name?.Trim() ?? "";
        if (name.Length is 0 or > LongestName) errors["name"] = L.T("Use 1 to {0} characters.", LongestName);
        if (values.ProfileId is not null && _store.Find(values.ProfileId) is null) errors["profileId"] = L.T("Choose a VPN profile.");
        if (!Enum.IsDefined(values.Loss)) errors["loss"] = L.T("Choose what happens when the VPN drops.");
        int? directAfter = null;
        var directAfterText = values.DirectAfterText?.Trim() ?? "";
        if (values.Loss == LossMode.Hold && directAfterText.Length > 0)
        {
            if (int.TryParse(directAfterText, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds is >= 5 and <= 3600) directAfter = seconds;
            else errors["directAfterText"] = L.T("Use 5 to 3600 seconds, or leave it empty to hold until the VPN is back.");
        }
        var included = new HashSet<string>(values.Included ?? [], StringComparer.OrdinalIgnoreCase);
        if (preview is { Kind: SetupKind.Folder } && !preview.Programs.Any(p => included.Contains(p.Path)))
            errors["form"] = L.T("Turn on at least one program to go through the VPN.");

        RoutedItem? made = null;
        lock (_gate)
        {
            if (_editor is not { } open || open.Token != token) return;
            if (errors.Count > 0) _editor = open with { Values = values, Errors = errors };
            else
            {
                try
                {
                    made = preview!.Kind switch
                    {
                        SetupKind.Browser => _items.CreateFromProgram(name, preview.Path, preview.ProgramKind, values.ProfileId, packets: false, proxy: true),
                        SetupKind.Program => _items.CreateFromProgram(name, preview.Path, preview.ProgramKind, values.ProfileId, packets: true, proxy: false),
                        _ when Directory.Exists(preview.Path) => _items.CreateFromFolder(name, preview.Path, values.ProfileId),
                        _ => _items.CreateFromProgramFolder(name, preview.Path, values.ProfileId),
                    };
                    // The person's choice of programs over the scan's, each marked as theirs where it differs.
                    foreach (var program in made.Programs.Where(p => p.Included != included.Contains(p.Path)).ToList())
                        if (preview.Kind == SetupKind.Folder) made = _items.Choose(made.Id, program.Path, included.Contains(program.Path));
                    made = made with { Loss = values.Loss, DirectAfterSeconds = directAfter };
                    _items.Put(made);
                    _editor = null;
                    _setup = null;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    _editor = open with { Values = values, Errors = new Dictionary<string, string> { ["form"] = L.T("{0} could not be read: {1}.", name, e.GetType().Name) } };
                }
            }
        }
        if (made is not null)
        {
            Notify(NoticeKind.Info, L.T("Added {0}. Turn it on to send its traffic through your VPN.", made.Name), new("app", made.Id));
            Navigate(Page.Apps, made.Id);
        }
        else Publish();
    }

    /// <summary>Whether a program's folder is its own (a game's, an installed program's) rather than one many share, whose
    /// other programs are none of the app's business. A folder with very many programs is taken as shared too.</summary>
    private static bool OwnFolder(string folder)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (Path.GetPathRoot(full) is { } root && string.Equals(Path.TrimEndingDirectorySeparator(root), full, StringComparison.OrdinalIgnoreCase)) return false;
        Environment.SpecialFolder[] shared =
        [
            Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86,
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolder.Desktop, Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.CommonDesktopDirectory, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData,
        ];
        var places = shared.Select(Environment.GetFolderPath).Where(p => p.Length > 0)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"))
            .Select(p => Path.TrimEndingDirectorySeparator(p));
        if (places.Any(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase))) return false;
        try { return Directory.EnumerateFiles(full, "*.exe", SearchOption.AllDirectories).Take(101).Count() <= 100; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Adds one more program to an app — a launcher elsewhere, say — sending all its traffic.</summary>
    public async Task AddProgramToAppAsync(string id)
    {
        var item = Item(id);
        if (IsLive(id)) throw new ShenoraException("APP_ROUTING", "id", id, L.T("Turn its VPN off before changing its programs."));
        var path = await PickProgramAsync("add-program", L.T("Choose another program for {0}", item.Name));
        if (path is null) return;
        try { _items.AddProgram(id, path); }
        catch (ArgumentException) { throw new ShenoraException("APP_PROGRAM", "id", id, L.T("That is not a program.")); }
        Notify(NoticeKind.Info, L.T("Added {0} to {1}.", Path.GetFileName(path), item.Name), new("app", id));
    }

    // A new app's profile: the default the person chose, while it can connect; else the first that can.
    private StoredProfile? DefaultProfile() =>
        _settings.Defaults.ProfileId is { } chosen && _store.Find(chosen) is { Unsupported: null } profile ? profile
        : _store.List().FirstOrDefault(p => p.Unsupported is null);

    private RoutedItem WithDefaults(RoutedItem item)
    {
        var defaults = _settings.Defaults;
        if (defaults.Loss == item.Loss && defaults.DirectAfterSeconds == item.DirectAfterSeconds) return item;
        var updated = item with { Loss = defaults.Loss, DirectAfterSeconds = defaults.DirectAfterSeconds };
        _items.Put(updated);
        return updated;
    }

    public void BeginDefaultsEdit()
    {
        var defaults = _settings.Defaults;
        lock (_gate)
            _editor = new OpenEditor(NewToken(), "defaults", "defaults", new DefaultsValues(_store.Find(defaults.ProfileId ?? "") is null ? null : defaults.ProfileId,
                defaults.Loss, defaults.DirectAfterSeconds?.ToString(CultureInfo.InvariantCulture) ?? ""), Empty);
        Publish();
    }

    public void SaveDefaults(string token, DefaultsValues values)
    {
        if (EditorFor(token, "defaults") is null) return;
        var directAfterText = values.DirectAfterText?.Trim() ?? "";
        var errors = new Dictionary<string, string>();
        if (values.ProfileId is { } chosen && _store.Find(chosen) is null) errors["profileId"] = L.T("Choose one of your profiles.");
        if (!Enum.IsDefined(values.Loss)) errors["loss"] = L.T("Choose what happens when the VPN drops.");
        int? directAfter = null;
        if (values.Loss == LossMode.Hold && directAfterText.Length > 0)
        {
            if (int.TryParse(directAfterText, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds is >= 5 and <= 3600) directAfter = seconds;
            else errors["directAfterText"] = L.T("Use 5 to 3600 seconds, or leave it empty to hold until the VPN is back.");
        }
        lock (_gate)
        {
            if (_editor?.Token != token) return;
            if (errors.Count > 0) { _editor = _editor with { Values = values, Errors = errors }; }
            else
            {
                _settings.Defaults = new AppDefaults(values.ProfileId, values.Loss, directAfter);
                _editor = null;
            }
        }
        Publish();
    }

    private async Task<string?> PickProgramAsync(string which, string title)
    {
        var picked = await WithDialogAsync(which, () => _dialogs.OpenFileAsync(new OpenFileOptions
        {
            Title = title,
            Filters = [new FileDialogFilter { Name = L.T("Programs"), Extensions = ["exe"] }],
            RememberPathKey = "add-app",
        }));
        return picked is { Success: true, FilePath: { } path } ? path : null;
    }

    public void RescanApp(string id)
    {
        var before = Item(id);
        if (IsLive(id)) throw new ShenoraException("APP_ROUTING", "id", id, L.T("Turn its VPN off before scanning again."));
        RoutedItem after;
        try { after = _items.Rescan(id); }
        catch (DirectoryNotFoundException) { throw new ShenoraException("APP_FOLDER", "id", id, L.T("{0}'s folder is not there any more.", before.Name)); }
        var added = after.Programs.Count(e => !before.Programs.Any(b => string.Equals(b.Path, e.Path, StringComparison.OrdinalIgnoreCase)));
        var gone = before.Programs.Count(b => !after.Programs.Any(e => string.Equals(b.Path, e.Path, StringComparison.OrdinalIgnoreCase)));
        Notify(NoticeKind.Info, added + gone == 0 ? L.T("{0}'s folder holds the same programs.", after.Name) :
            L.T("{0}: {1}, {2} gone. Your choices for the rest stand.", after.Name, L.Count(added, "1 new program", "{0} new programs"), gone), new("app", id));
    }

    /// <summary>Includes or leaves out one of an app's programs, by its id: a path from the page is never trusted.</summary>
    public void SetAppProgram(string id, string programId, bool included)
    {
        var item = Item(id);
        if (IsLive(id)) throw new ShenoraException("APP_ROUTING", "id", id, L.T("Turn its VPN off before changing its programs."));
        var program = item.Programs.FirstOrDefault(p => p.Id == programId)
            ?? throw new ShenoraException("APP_PROGRAM", "id", id, L.T("That program is not one of this app's."));
        _items.Choose(id, program.Path, included);
        Publish();
    }

    public void BeginAppEdit(string id)
    {
        var item = Item(id);
        if (IsLive(id)) throw new ShenoraException("APP_ROUTING", "id", id, L.T("Turn its VPN off before changing it."));
        if (_launching.ContainsKey(id)) throw new ShenoraException("LAUNCHING", "id", id, L.T("Wait for the launch to finish."));
        lock (_gate)
            _editor = new OpenEditor(NewToken(), "app", id, new AppEditValues(item.Name, _store.Find(item.ProfileId ?? "") is null ? null : item.ProfileId,
                item.Loss, item.DirectAfterSeconds?.ToString(CultureInfo.InvariantCulture) ?? "", AppViews.LaunchTarget(item)?.Id), Empty);
        Publish();
    }

    public void SaveApp(string token, AppEditValues values)
    {
        if (EditorFor(token, "app") is not { } editor) return;
        var item = Item(editor.SubjectId);
        var name = values.Name?.Trim() ?? "";
        var directAfterText = values.DirectAfterText?.Trim() ?? "";
        var errors = new Dictionary<string, string>();
        if (name.Length is 0 or > LongestName) errors["name"] = L.T("Use 1 to {0} characters.", LongestName);
        if (values.ProfileId is null || _store.Find(values.ProfileId) is null) errors["profileId"] = L.T("Choose a VPN profile.");
        if (!Enum.IsDefined(values.Loss)) errors["loss"] = L.T("Choose what happens when the VPN drops.");
        int? directAfter = null;
        if (values.Loss == LossMode.Hold && directAfterText.Length > 0)
        {
            if (int.TryParse(directAfterText, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds is >= 5 and <= 3600) directAfter = seconds;
            else errors["directAfterText"] = L.T("Use 5 to 3600 seconds, or leave it empty to hold until the VPN is back.");
        }
        if (values.LaunchProgramId is { } launch && !item.Programs.Any(p => p.Id == launch && p.Included))
            errors["launchProgramId"] = L.T("Choose one of its included programs.");
        if (IsLive(item.Id)) errors["form"] = L.T("Turn its VPN off before changing it.");

        lock (_gate)
        {
            if (_editor?.Token != token) return;
            if (errors.Count > 0) { _editor = _editor with { Values = values, Errors = errors }; }
            else
            {
                _items.Put(item with { Name = name, ProfileId = values.ProfileId, Loss = values.Loss, DirectAfterSeconds = directAfter, LaunchProgramId = values.LaunchProgramId });
                _editor = null;
            }
        }
        Publish();
    }

    public void BeginProgramEdit(string id, string programId)
    {
        var item = Item(id);
        if (IsLive(id)) throw new ShenoraException("APP_ROUTING", "id", id, L.T("Turn its VPN off before changing its methods."));
        var program = item.Programs.FirstOrDefault(p => p.Id == programId)
            ?? throw new ShenoraException("APP_PROGRAM", "id", id, L.T("That program is not one of this app's."));
        lock (_gate)
            _editor = new OpenEditor(NewToken(), "program", $"{id}/{programId}", new ProgramEditValues(program.Packets, program.Proxy is not null,
                program.Proxy?.Kind ?? AppLauncher.Guess(program.Path), program.LaunchArguments), Empty);
        Publish();
    }

    public void SaveProgram(string token, ProgramEditValues values)
    {
        if (EditorFor(token, "program") is not { } editor) return;
        var (id, programId) = (editor.SubjectId[..editor.SubjectId.IndexOf('/')], editor.SubjectId[(editor.SubjectId.IndexOf('/') + 1)..]);
        var item = Item(id);
        var arguments = values.Arguments?.Trim() ?? "";
        var errors = new Dictionary<string, string>();
        if (!values.Packets && !values.Proxy) errors["form"] = L.T("Choose at least one way for its traffic to reach the VPN, or leave the program out instead.");
        if (values.Proxy && !Enum.IsDefined(values.Kind)) errors["kind"] = L.T("Choose how the program is told about the proxy.");
        if (arguments.Length > LongestArguments) errors["arguments"] = L.T("Use at most {0:N0} characters.", LongestArguments);
        if (IsLive(id)) errors["form"] = L.T("Turn its VPN off before changing its methods.");

        lock (_gate)
        {
            if (_editor?.Token != token) return;
            if (errors.Count > 0) { _editor = _editor with { Values = values, Errors = errors }; }
            else
            {
                _items.Put(item with
                {
                    Programs = [.. item.Programs.Select(p => p.Id != programId ? p : p with
                    {
                        Packets = values.Packets,
                        // A program keeps its own browser folder once it has one, so its sign-ins survive a change of method.
                        Proxy = values.Proxy ? new ProxyLaunch(values.Kind, arguments, p.Proxy?.DataKey ?? p.Id) : null,
                        Arguments = arguments,
                    })],
                });
                _editor = null;
            }
        }
        Publish();
    }

    public void CancelEdit(string token)
    {
        lock (_gate)
        {
            if (_editor?.Token != token) return;
            _editor = null;
        }
        Publish();
    }

    public void RequestRemoveApp(string id)
    {
        var item = Item(id);
        if (IsLive(id)) throw new ShenoraException("APP_ROUTING", "id", id, L.T("Turn its VPN off before removing it."));
        if (_launching.ContainsKey(id)) throw new ShenoraException("LAUNCHING", "id", id, L.T("Wait for the launch to finish."));
        Confirm("remove-app", id, L.T("Remove {0}?", item.Name), L.T("Removes it from Sallyport. Its programs and files are not touched; a running copy keeps running."),
            L.T("Remove app"), L.T("Keep app"));
    }

    private void RemoveApp(string id)
    {
        var name = _items.Find(id)?.Name;
        if (!_items.Remove(id)) return;
        lock (_gate)
        {
            if (_editor?.SubjectId.StartsWith(id, StringComparison.Ordinal) == true) _editor = null;
        }
        Notify(NoticeKind.Info, L.T("Removed {0} from Sallyport. Its programs are unchanged.", name));
    }

    /// <summary>Starts routing an app's programs through its VPN profile's channel. Routed before they start, every
    /// packet of them goes through it from the first; routed while they run, their connections move to the VPN and a game
    /// may need to rejoin.</summary>
    public void RouteApp(string id)
    {
        var item = Item(id);
        var game = item.AsGame();
        var choice = item.Choice;
        if (choice.ProfileId is null || _store.Find(choice.ProfileId) is not { } stored)
            throw new ShenoraException("APP_NO_PROFILE", "id", id, L.T("Choose a VPN profile for {0} first (Edit).", item.Name));
        if (stored.Unsupported is { } why) throw new ShenoraException("PROFILE_UNSUPPORTED", "id", id, L.T("This version cannot connect {0}: {1}.", stored.Name, why));
        if (_runner.IsRunning) throw new ShenoraException("RUN_BUSY", "id", id, L.T("Another app is using the VPN. Turn its VPN off first."));
        if (item.PacketPrograms.Count == 0) throw new ShenoraException("APP_EMPTY", "id", id, L.T("Include at least one of {0}'s programs sending all its traffic.", item.Name));

        // Through the profile's channel: the same login as its proxy, if that is connected (docs/design/window.md).
        _runner.Start(game, stored.Id, choice.Policy);
        Notify(NoticeKind.Info, L.T("{0} now uses {1}. If it is running already, it may need to reconnect.", item.Name, stored.Name), new("app", id));
    }

    public Task StopRunAsync() => _runner.StopAsync();

    /// <summary>Starts an app as its card says: its packet programs routed first (and ready before it starts, so its
    /// first packet goes through the VPN), then the program Launch starts — with proxy settings, its profile connected,
    /// if that is its method. A wait that ends without the route or the tunnel up never launches later.</summary>
    public async Task LaunchAsync(string id, string? programId, CancellationToken ct)
    {
        var item = Item(id);
        var target = programId is null ? AppViews.LaunchTarget(item) : item.Programs.FirstOrDefault(p => p.Id == programId && p.Included);
        if (target is null) throw new ShenoraException("APP_NO_TARGET", "id", id, L.T("Choose what Launch starts for {0} (Edit).", item.Name));
        if (!File.Exists(target.Path)) throw new ShenoraException("APP_MISSING", "id", id, L.T("{0} is not where it was added from.", Path.GetFileName(target.Path)));
        var profileId = item.ProfileId ?? throw new ShenoraException("APP_NO_PROFILE", "id", id, L.T("Choose a VPN profile for {0} first (Edit).", item.Name));
        var profile = Profile(profileId);
        if (!_launching.TryAdd(id, AppPhase.Connecting)) return;
        Publish();
        try
        {
            try
            {
                if (item.PacketPrograms.Count > 0)
                {
                    if (!IsLive(id)) RouteApp(id);
                    await WaitRoutedAsync(id, TimeSpan.FromSeconds(45), ct);
                }
                if (target.Proxy is not null)
                {
                    Connect(profileId);
                    await _connections.WaitUpAsync(profileId, TimeSpan.FromSeconds(45), ct);
                }
            }
            catch (ShenoraException e) { throw new ShenoraException(e.Code, "id", id, L.T("{0} was not started. {1}", item.Name, Capital(e.Message))); }
            _launching[id] = AppPhase.Starting;
            Publish();
            try
            {
                if (target.Proxy is { } proxy)
                    // The port its profile's proxy took when the channel opened, just now or before.
                    AppLauncher.Launch(new RoutedApp(item.Id, item.Name, target.Path, proxy.Kind, profileId, target.LaunchArguments),
                        _connections.ProxyPortOf(profileId) ?? throw new ShenoraException("PORT_IN_USE", "id", id, L.T("{0}'s proxy is not listening.", profile.Name)),
                        Path.Combine(_paths.DataDir, "apps", proxy.DataKey));
                else
                    // Started as the person, from its own folder, as Explorer would: its packets are routed by its path.
                    Process.Start(new ProcessStartInfo(target.Path, target.LaunchArguments) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(target.Path)! })?.Dispose();
            }
            catch (FileNotFoundException) { throw new ShenoraException("APP_MISSING", "id", id, L.T("{0} was not found where it was added from.", Path.GetFileName(target.Path))); }
            catch (System.ComponentModel.Win32Exception e) { throw new ShenoraException("APP_FAILED", "id", id, L.T("Windows did not start {0}: {1}", Path.GetFileName(target.Path), e.Message)); }
        }
        finally
        {
            _launching.TryRemove(id, out _);
            Publish();
        }
        Notify(NoticeKind.Info, target.Proxy is not null
            ? L.T("{0} started with proxy settings for {1}.", Path.GetFileName(target.Path), profile.Name)
            : L.T("{0} started; its traffic goes through {1}.", Path.GetFileName(target.Path), profile.Name), new("app", id));
    }

    // Until the service routes the app's programs through a generation that is up: what a program then sends is the
    // VPN's from its first packet.
    private async Task WaitRoutedAsync(string id, TimeSpan within, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + within;
        while (true)
        {
            var run = _runner.Facts();
            if (run is null || run.GameId != id) throw new ShenoraException("NOT_ROUTED", "id", id, L.T("its VPN did not turn on"));
            if (run.Ended is { } why) throw new ShenoraException("NOT_ROUTED", "id", id, L.T("its VPN turned off: {0}", why));
            if (run.Route?.Phase == RoutePhase.Routing) return;
            if (DateTime.UtcNow > deadline) throw new ShenoraException("ROUTE_TIMEOUT", "id", id, L.T("its VPN was not ready within {0:0} s", within.TotalSeconds));
            await Task.Delay(200, ct);
        }
    }

    private bool IsLive(string id) => _runner.Facts() is { Ended: null } run && run.GameId == id;

    // ---- a profile's connection settings

    public void BeginPolicyEdit(string id)
    {
        var stored = Profile(id);
        if (stored.Unsupported is not null) throw new ShenoraException("PROFILE_UNSUPPORTED", "id", id, L.T("This version cannot connect this profile."));
        var effective = _settings.PolicyFor(id, OwnPolicy(id), ClientSettings.ProtocolsOf(stored.Remotes));
        lock (_gate) _editor = new OpenEditor(NewToken(), "policy", id, GameViews.PolicyValuesOf(effective), Empty);
        Publish();
    }

    public void SavePolicy(string token, PolicyValues values)
    {
        if (EditorFor(token, "policy") is not { } editor) return;
        // What it connects with without changes of its own: Settings' choices over its directives.
        var basis = _settings.BaseFor(OwnPolicy(editor.SubjectId), ClientSettings.ProtocolsOf(Profile(editor.SubjectId).Remotes));
        var errors = new Dictionary<string, string>();
        int? Read(string? text, string field, int least, int most, string message)
        {
            var trimmed = text?.Trim() ?? "";
            if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= least && n <= most) return n;
            errors[field] = message;
            return null;
        }
        var wait = Read(values.RetryWaitText, "retryWaitText", 1, 300, L.T("Use 1 to 300 seconds."));
        var waitMax = Read(values.RetryWaitMaxText, "retryWaitMaxText", 1, 3600, L.T("Use 1 to 3600 seconds."));
        var rounds = values.KeepTrying ? 0 : Read(values.RoundsMaxText, "roundsMaxText", 1, 1000, L.T("Use 1 to 1000 rounds."));
        // Only a transport the profile has servers of: the choices offered are those.
        var protocol = values.Protocol is "" ? null : values.Protocol;
        if (protocol is not null && GameViews.ProtocolChoices(Profile(editor.SubjectId).Remotes)?.Any(c => c.Id == protocol) != true)
            errors["protocol"] = L.T("This profile has no servers of that type.");
        var timeout = Read(values.AttemptTimeoutText, "attemptTimeoutText", 2, 300, L.T("Use 2 to 300 seconds."));
        int? pingRestart = null;
        if (!string.IsNullOrWhiteSpace(values.PingRestartText)) pingRestart = Read(values.PingRestartText, "pingRestartText", 5, 600, L.T("Use 5 to 600 seconds, or leave it empty for the server's own."));
        if (wait is { } w && waitMax is { } m && m < w) errors["retryWaitMaxText"] = L.T("The longest wait cannot be shorter than the first.");

        lock (_gate)
        {
            if (_editor?.Token != token) return;
            if (errors.Count > 0) { _editor = _editor with { Values = values, Errors = errors }; Publish(); return; }
            // Only what differs from Settings and the profile's own is kept as a change: a later change in Settings, or a
            // later import of a profile that says otherwise, is then followed where the person did not choose.
            int? Differs(int value, TimeSpan theirs) => value == (int)Math.Round(theirs.TotalSeconds) ? null : value;
            _settings.PutPolicy(editor.SubjectId, new PolicyOverride(
                Differs(wait!.Value, basis.RetryWait), Differs(waitMax!.Value, basis.RetryWaitMax),
                rounds == (basis.RoundsMax ?? 0) ? null : rounds, values.RandomOrder == basis.RandomOrder ? null : values.RandomOrder,
                Differs(timeout!.Value, basis.AttemptTimeout), values.RetryAfterAuthFailure == basis.RetryAfterAuthFailure ? null : values.RetryAfterAuthFailure,
                pingRestart is { } r && (r != basis.PingRestart || !basis.KeepaliveOverridesServer) ? r : null,
                protocol == basis.Protocol ? null : protocol ?? PolicyOverride.Any));
            _editor = null;
        }
        Publish();
        Notify(NoticeKind.Info, L.T("Saved. The next connection uses these settings."), new("profile", editor.SubjectId));
    }

    public void ResetPolicy(string token)
    {
        if (EditorFor(token, "policy") is not { } editor) return;
        lock (_gate)
        {
            if (_editor?.Token != token) return;
            _settings.PutPolicy(editor.SubjectId, new PolicyOverride());
            _editor = null;
        }
        Publish();
        Notify(NoticeKind.Info, L.T("{0} follows Settings again from the next connection.", Profile(editor.SubjectId).Name), new("profile", editor.SubjectId));
    }

    // The policy the profile itself carries (its OpenVPN directives): read from the stored copy, which is opened for it.
    private ConnectionPolicy OwnPolicy(string id)
    {
        try { return _store.Load(id).Policy; }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or FormatException or FileNotFoundException)
        { throw new ShenoraException("PROFILE_UNREADABLE", "id", id, L.T("The stored profile could not be opened.")); }
    }

    // ---- the packet driver

    public void RequestDriverInstall() =>
        Confirm("driver-install", "", L.T("Install the packet driver?"), GameViews.DriverInstallText, L.T("Install"), L.T("Not now"));

    public void RequestDriverRemove()
    {
        if (_runner.IsRunning) throw new ShenoraException("RUN_BUSY", "driver", "", L.T("Turn its VPN off first."));
        Confirm("driver-remove", "", L.T("Remove the packet driver?"), GameViews.DriverRemoveText, L.T("Remove"), L.T("Keep it"));
    }

    private async Task ManageDriverAsync(bool install)
    {
        if (Interlocked.CompareExchange(ref _driverWork, install ? L.T("Installing the packet driver…") : L.T("Removing the packet driver…"), null) is not null) return;
        _driverFacts = null;
        Publish();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            await _driverSetup.FetchAsync(http);
            // An installed client puts the driver's own files beside its folder (D19), not in the system drive's Program Files.
            var outcome = install
                ? await _driverSetup.InstallAsync(InstallLayout.InstalledHere() is { } here ? InstallLayout.DriverFolder(here) : null)
                : await _driverSetup.UninstallAsync();
            Notify(outcome.Succeeded ? NoticeKind.Info : NoticeKind.Error, outcome.Text);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidDataException)
        {
            Notify(NoticeKind.Error, L.T("The driver's installer could not be fetched from NT Kernel ({0}). Check the connection and try again.", e.GetType().Name));
        }
        finally
        {
            Volatile.Write(ref _driverWork, null);
            _driverFacts = null;
            Publish();
        }
    }

    private DriverFacts Driver()
    {
        var now = Environment.TickCount64;
        if (_driverFacts is { } cached && now - cached.At < DriverFactsLast) return cached.Facts with { Working = _driverWork is not null, WorkingText = _driverWork };
        var driver = _driverSetup.Status();
        var (service, _) = ServiceControl.Status(RouterProtocol.ServiceName);
        var facts = new DriverFacts(service != Windows.PacketFilter.ServiceState.NotInstalled, service == Windows.PacketFilter.ServiceState.Running,
            driver.Service is not (Windows.PacketFilter.ServiceState.NotInstalled or Windows.PacketFilter.ServiceState.Unknown),
            driver.Service == Windows.PacketFilter.ServiceState.Running, driver.VersionText, _driverWork is not null, _driverWork);
        _driverFacts = (facts, now);
        return facts;
    }

    public void CopyGuideExample()
    {
        Clipboard(ClientView.CurlExample(GuideEndpoint(Snapshot().Profiles)));
        Notify(NoticeKind.Info, L.T("Copied the example."));
    }

    // ---- the view

    private ClientState Take()
    {
        var stored = _store.List();
        var items = _items.List();
        var run = _runner.Facts();
        var routedThrough = run is { Ended: null } ? run.ProfileId : null;
        var routedGame = routedThrough is null ? null : items.FirstOrDefault(g => g.Id == run!.GameId)?.Name ?? L.T("an app");
        var profiles = stored.Select(p => ClientView.Profile(new ProfileFacts(
                p, _connections.ProxyPortOf(p.Id), _connections.StatusOf(p.Id), _connections.LocationOf(p.Id), _locating.ContainsKey(p.Id),
                _locationFailed.ContainsKey(p.Id), _connections.AttemptsSinceUp(p.Id), _disconnecting.ContainsKey(p.Id), _removing.ContainsKey(p.Id),
                routedThrough == p.Id ? routedGame : null, !_settings.PolicyOf(p.Id).IsEmpty, _connections.NextAttemptOf(p.Id),
                _settings.ProtocolFor(p.Id, ClientSettings.ProtocolsOf(p.Remotes)))))
            .ToList();
        var driver = GameViews.Driver(Driver());
        var dialog = Volatile.Read(ref _dialog);
        var apps = items.Select(i =>
            {
                var profile = stored.FirstOrDefault(p => p.Id == i.ProfileId);
                return AppViews.Item(new ItemFacts(i, profile, run, run is { Ended: null } && run.GameId != i.Id, driver.Phase == DriverPhase.Ready,
                    dialog is not null, _launching.TryGetValue(i.Id, out var phase) ? phase : null,
                    p => profile is null || p.Proxy is not { } proxy ? []
                        : AppLauncher.Describe(new RoutedApp(i.Id, i.Name, p.Path, proxy.Kind, profile.Id, p.LaunchArguments), _connections.ProxyPortOf(profile.Id),
                            Path.Combine(_paths.DataDir, "apps", proxy.DataKey)),
                    i.Id == _watchedApp ? _programs.Of : null));
            })
            .ToList();

        var guideEndpoint = GuideEndpoint(profiles);
        var guide = new ClientState.GuideView(guideEndpoint, ClientView.CurlExample(guideEndpoint), UiAction.Shown(L.T("Copy")));
        OpenEditor? editor;
        OpenConfirmation? confirmation;
        List<Notice> notices;
        NavigationView? navigation;
        (string Token, SetupPreview Preview)? setup;
        lock (_gate)
        {
            editor = _editor;
            // What "Add an app" found, only for the token it was found for.
            setup = _setup is { } open && open.Token == editor?.Token ? open : null;
            confirmation = _confirmation;
            notices = [.. _notices];
            navigation = _navigation;
        }
        bool startsWithWindows;
        try { startsWithWindows = StartupRegistration.IsOn(); }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { startsWithWindows = false; }

        return new ClientState(
            ++_sequence,
            ClientView.Summary(profiles),
            new ClientState.TopActions(
                UiAction.Shown(L.T("Import profile…"), dialog is null, L.T("A file dialog is open."), busy: dialog == "import"),
                UiAction.Shown(L.T("Add an app…"), dialog is null, L.T("A file dialog is open."), busy: dialog == "add-app")),
            profiles,
            apps,
            ClientView.ProfilesEmpty(stored.Count),
            AppViews.Empty(stored.Count, apps.Count),
            WindowViews.Network(profiles, apps, run, stored, driver, stored.ToDictionary(p => p.Id, p => _connections.ProxyProblemOf(p.Id)),
                stored.ToDictionary(p => p.Id, p => _connections.LatencyOf(p.Id)), stored.ToDictionary(p => p.Id, p => _connections.TrafficOf(p.Id))),
            WindowViews.Settings(driver, startsWithWindows, guide, _paths.DataDir, InstallLayout.InstalledHere(), _settings.Defaults,
                stored.FirstOrDefault(p => p.Id == _settings.Defaults.ProfileId), editor is not null, _settings.Language, _settings.Connection),
            // Where a first look is useful: with nothing to route through yet, the page that imports one; else the Overview,
            // where the switch is.
            stored.Count == 0 ? Page.Profiles : Page.Network,
            navigation,
            [.. stored.Select(p => new ClientState.ProfileChoice(p.Id, p.Name, p.Unsupported is null))],
            [.. Enum.GetValues<AppKind>().Select(k => new ClientState.KindChoice(k, ClientView.KindLabel(k), ClientView.KindHelp(k)))],
            GameViews.LossChoices,
            editor is null ? null : new EditorView(editor.Token, editor.SubjectId, "editing", editor.Errors,
                // The guidance until a save is refused; then the refusal, which says it, alone. "Add an app" says what it
                // found at the place chosen, and adds nothing until it has found something.
                editor.Kind == "policy" ? PolicyHint(editor.SubjectId) : editor.Kind == "new-app" ? setup?.Preview.Summary : null,
                editor.Kind == "new-app" ? UiAction.Shown(L.T("Add app"), setup is not null, L.T("Choose where it is first.")) : UiAction.Shown(L.T("Save")),
                UiAction.Shown(L.T("Cancel")),
                editor.Kind == "policy" && !_settings.PolicyOf(editor.SubjectId).IsEmpty ? UiAction.Shown(L.T("Follow Settings")) : UiAction.Hidden,
                editor.Kind, editor.Values,
                // What Launch may start: the app's included programs. The connection types the profile has servers for.
                // Only an app Sallyport starts has a choice of what it starts: one of its programs told of the proxy.
                editor.Kind == "app" && items.FirstOrDefault(i => i.Id == editor.SubjectId) is { } edited && edited.PacketPrograms.Count == 0
                    && edited.Programs.Count(p => p.Included && p.Proxy is not null) > 1
                    ? [.. edited.Programs.Where(p => p.Included && p.Proxy is not null).Select(p => new EditorOption(p.Id, Path.GetFileName(p.Path)))]
                    : editor.Kind == "policy" && stored.FirstOrDefault(p => p.Id == editor.SubjectId) is { } profile
                        ? GameViews.ProtocolChoices(profile.Remotes)
                        // A folder's programs, each to turn on or off before the app is made; where it sits, if not at the top.
                        : setup is { Preview: { Kind: SetupKind.Folder, Programs.Count: > 1 } found }
                            ? [.. found.Programs.Select(p => new EditorOption(p.Path, Path.GetFileName(p.Path), p.Reason is { } reason ? L.T(reason)
                                : Path.GetRelativePath(Directory.Exists(found.Path) ? found.Path : Path.GetDirectoryName(found.Path)!, Path.GetDirectoryName(p.Path)!)
                                    is var where && where != "." ? where : null))]
                            : null),
            confirmation is null ? null : new ConfirmationView(confirmation.Token, confirmation.Title, confirmation.Text,
                UiAction.Shown(confirmation.ConfirmLabel), UiAction.Shown(confirmation.CancelLabel)),
            notices,
            L.Code);
    }

    private string? PolicyHint(string id)
    {
        try { return GameViews.PolicyHint(_settings.BaseFor(OwnPolicy(id), ClientSettings.ProtocolsOf(Profile(id).Remotes)), !_settings.PolicyOf(id).IsEmpty); }
        catch (ShenoraException) { return null; }
    }

    /// <summary>The examples use a connected profile's port when there is one, so they can be copied as they are; else PORT,
    /// where the port its proxy takes when it connects goes.</summary>
    private static string GuideEndpoint(IReadOnlyList<ProfileView> profiles) =>
        profiles.FirstOrDefault(p => p.Port is not null) is { Port: { } port } ? ClientView.Endpoint(port) : "127.0.0.1:PORT";

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

    /// <summary>The clipboard wants a single-threaded apartment; an intent may arrive on any thread.</summary>
    private static void Clipboard(string text)
    {
        var copying = new Thread(() => System.Windows.Forms.Clipboard.SetText(text));
        copying.SetApartmentState(ApartmentState.STA);
        copying.Start();
        copying.Join();
    }

}
