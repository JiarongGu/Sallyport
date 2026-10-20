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

    /// <summary>Setup's removal, as Windows' installed apps start it: this program with /uninstall (D30), which asks to be
    /// administrator, then asks the person, and closes this window when they go ahead.</summary>
    public void Uninstall()
    {
        if (InstallLayout.InstalledHere() is null || Environment.ProcessPath is not { } self) return;
        try { Process.Start(new ProcessStartInfo(self, "/uninstall") { UseShellExecute = false })?.Dispose(); }
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

    // ---- profiles

    // ---- "Import a VPN profile" (D29): a page of its own, as OpenVPN Connect's Import Profile is — a file or a link
    // first, then the profile looked at (its name, its server) before anything is kept.

    // What the open import read, for its token: the text stays here, in memory, until it is imported or let go.
    private (string Token, string Origin, string Text, ProfilePreview Preview)? _reading;
    private static readonly HttpClient Downloads = new() { Timeout = ProfileDownload.Timeout };

    /// <summary>Opens "Import a VPN profile" with nothing read yet: from a file unless the person chooses a link.</summary>
    public void BeginProfileImport()
    {
        lock (_gate)
        {
            _reading = null;
            _editor = new OpenEditor(NewToken(), "new-profile", "", new NewProfileValues("file", "", "", "", "", false), Empty);
        }
        Publish();
    }

    /// <summary>A profile file given to Sallyport — opened from Explorer, or a later start handed one — read onto the
    /// import page, as one chosen there would be.</summary>
    public void OpenProfileFile(string path)
    {
        BeginProfileImport();
        OpenEditor? open;
        lock (_gate) open = _editor;
        if (open is not null) ReadProfileFile(open.Token, (NewProfileValues)open.Values, path);
    }

    /// <summary>Choose a file…: one dialog at a time, as every dialog is.</summary>
    public async Task BrowseProfileAsync(string token, NewProfileValues values)
    {
        if (EditorFor(token, "new-profile") is null) return;
        var picked = await WithDialogAsync("import", () => _dialogs.OpenFileAsync(new OpenFileOptions
        {
            Title = L.T("Import an OpenVPN profile"),
            Filters = [new FileDialogFilter { Name = L.T("OpenVPN profiles"), Extensions = ["ovpn", "conf"] }],
            RememberPathKey = "import-profile",
        }));
        if (picked is { Success: true, FilePath: { } path }) ReadProfileFile(token, values, path);
    }

    private void ReadProfileFile(string token, NewProfileValues values, string path)
    {
        var file = new FileInfo(path);
        string? why = null, text = null;
        if (!file.Exists) why = L.T("{0} is no longer there.", file.Name);
        else if (file.Length > LargestProfile) why = L.T("{0} is far larger than any profile.", file.Name);
        else
            try { text = File.ReadAllText(file.FullName); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { why = L.T("Could not read this file. Choose it again or use another file."); }
        Read(token, values with { Source = "file" }, file.Name, Path.GetFileNameWithoutExtension(file.Name), text, why);
    }

    /// <summary>Get the profile: the link fetched (HTTPS only), its server's sign-in asked for only when it asks.</summary>
    public async Task FetchProfileAsync(string token, NewProfileValues values)
    {
        if (EditorFor(token, "new-profile") is null) return;
        var address = ProfileDownload.Address(values.Link ?? "", out var why);
        if (address is null)
        {
            Refuse(token, values with { Source = "link" }, "link", why!);
            return;
        }
        Downloaded got;
        lock (_gate) _fetching = token;
        Publish();
        try { got = await ProfileDownload.GetAsync(Downloads, address, values.SignIn ? values.Username : null, values.SignIn ? values.Password : null, CancellationToken.None); }
        // Never a way past it: a server that cannot prove who it is gets no password and sends no keys.
        catch (HttpRequestException e) when (e.InnerException is System.Security.Authentication.AuthenticationException)
        { got = new(null, false, L.T("Could not verify {0}'s identity. Check the address or contact your provider.", address.Host)); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        { got = new(null, false, L.T("Could not reach {0}. Try again.", address.Host)); }
        finally { lock (_gate) _fetching = null; }
        if (got.SignIn)
        {
            // Under a new token, so the page takes the sign-in it now shows; what the person typed in the link stays.
            lock (_gate)
            {
                if (_editor?.Token != token) return;
                _reading = null;
                _editor = new OpenEditor(NewToken(), "new-profile", "", values with { Source = "link", SignIn = true, Password = "" },
                    new Dictionary<string, string>
                    {
                        [got.Why is null ? "form" : "password"] = got.Why ?? L.T("{0} asks who you are: type the username and password your VPN provider gave you.", address.Host),
                    });
            }
            Publish();
            return;
        }
        Read(token, values with { Source = "link", Password = "" }, address.Host, ProfileDownload.NameFor(address), got.Text, got.Why);
    }

    // The import page's token while a link is being fetched: its button says so.
    private string? _fetching;

    // What was read, looked at: kept under a new token (the page takes the name suggested) or refused where it was asked.
    private void Read(string token, NewProfileValues values, string origin, string suggested, string? text, string? why)
    {
        ProfilePreview? preview = null;
        if (text is not null && why is null)
            // The store's own reasons never quote the text, so they may cross to the page.
            try { preview = ProfileStore.Preview(text); }
            catch (InvalidDataException) { why = null; }
        if (preview is null)
        {
            Refuse(token, values, values.Source == "link" ? "link" : "form", why ?? (values.Source == "link"
                ? L.T("This address did not provide a VPN profile. Check it with your provider.")
                : L.T("This file does not contain a usable VPN profile. Ask whoever supplied it for another copy.")));
            return;
        }
        lock (_gate)
        {
            if (_editor?.Token != token) return;
            // The username the profile carries, else the one the person signed in to its provider with, to start from; the
            // provider's password is never taken for the VPN's.
            var next = new OpenEditor(NewToken(), "new-profile", "", values with
            {
                Name = UniqueName(suggested), Connect = values.Connect && preview.Unsupported is null,
                NeedsLogin = preview.NeedsLogin && preview.Unsupported is null,
                Username = preview.Username ?? (values.SignIn ? values.Username : ""), Password = "", Remember = false, SignIn = false,
            }, Empty);
            _editor = next;
            _reading = (next.Token, origin, text!, preview);
        }
        Publish();
    }

    private void Refuse(string token, NewProfileValues values, string field, string why)
    {
        lock (_gate)
        {
            if (_editor?.Token != token) return;
            _reading = null;
            _editor = _editor with { Values = values, Errors = new Dictionary<string, string> { [field] = why } };
        }
        Publish();
    }

    // A name no profile has yet: the file's, then "(2)", "(3)"… — two sections of the same name would be told apart by
    // nothing on the Overview.
    private string UniqueName(string suggested)
    {
        var given = suggested.Trim() is { Length: > 0 } trimmed ? trimmed : "profile";
        var name = given.Length > LongestName ? given[..LongestName] : given;
        var taken = _store.List().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var n = 2; taken.Contains(name); n++) name = $"{given[..Math.Min(given.Length, LongestName - 6)]} ({n})";
        return name;
    }

    /// <summary>Import: the text read, kept sealed under the person's name for it; connected at once if they asked.</summary>
    public void SaveProfileImport(string token, NewProfileValues values)
    {
        if (EditorFor(token, "new-profile") is null) return;
        (string Token, string Origin, string Text, ProfilePreview Preview)? reading;
        lock (_gate) reading = _reading is { } open && open.Token == token ? open : null;
        if (reading is not { Text: var text, Preview: var preview })
        {
            Refuse(token, values, "form", L.T("Choose a file or get the profile from its link first."));
            return;
        }
        var errors = new Dictionary<string, string>();
        var name = values.Name?.Trim() ?? "";
        if (name.Length is 0 or > LongestName) errors["name"] = L.T("Use 1 to {0} characters.", LongestName);
        // Never over another: two sections of one name would be told apart by nothing on the Overview.
        else if (_store.List().Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            errors["name"] = L.T("A profile already uses this name. Choose another name.");
        // A username and password are needed now only to remember them or to connect: otherwise asked when it connects.
        var signsIn = preview.NeedsLogin && preview.Unsupported is null;
        var username = values.Username?.Trim() ?? "";
        var password = values.Password ?? "";
        if (signsIn && (values.Remember || values.Connect))
        {
            if (username.Length == 0) errors["username"] = L.T("Enter a username.");
            if (password.Length == 0) errors["password"] = L.T("Enter a password.");
        }
        if (errors.Count > 0)
        {
            lock (_gate) if (_editor?.Token == token) _editor = _editor with { Values = values with { Password = "" }, Errors = errors };
            Publish();
            return;
        }
        StoredProfile stored;
        try { stored = _store.Import(name, text); }
        catch (InvalidDataException e) { Refuse(token, values, "form", L.T("{0} was not imported: {1}.", reading.Value.Origin, e.Message)); return; }
        if (signsIn && username.Length > 0) KeepLogin(stored.Id, username, values.Remember ? password : null);
        if (signsIn && values.Connect && !values.Remember) _sessionLogins[stored.Id] = new Login(username, password);
        lock (_gate)
        {
            if (_editor?.Token == token) _editor = null;
            _reading = null;
        }
        _looks[stored.Id] = preview;
        // The next step is the app: with none yet, the window goes where one is added.
        if (_items.List().Count == 0)
        {
            Notify(NoticeKind.Info, L.T("Imported {0}. Now add the game or program that should use it.", stored.Name), new("profile", stored.Id));
            Navigate(Page.Apps);
        }
        else Notify(NoticeKind.Info, L.T("Imported {0}. Its copy is encrypted for your Windows account.", stored.Name), new("profile", stored.Id));
        if (values.Connect && stored.Unsupported is null) Connect(stored.Id);
        Publish();
    }

    // What each profile says of itself — its servers, whose it is — read from its sealed copy once and kept in memory
    // only: its row shows them, as OpenVPN Connect's list does, and the description beside the copy never names them.
    private readonly ConcurrentDictionary<string, ProfilePreview> _looks = new();

    private ProfilePreview LookOf(string id) => _looks.GetOrAdd(id, key =>
    {
        try { return ProfileStore.Preview(_store.Text(key)); }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or InvalidDataException or FileNotFoundException or IOException)
        { return new ProfilePreview([], [], null); }
    });

    private IReadOnlyList<string> ServersOf(string id) => LookOf(id).Servers;

    // ---- Signing in (D29): a profile that asks for a username and password connects with one the person remembered,
    // or one typed for this connection alone — kept in memory until they disconnect or Sallyport quits.

    private readonly ConcurrentDictionary<string, Login> _sessionLogins = new();
    // The sign-in open over the window, for one profile at a time, with why the last one did not do.
    private (string Token, string ProfileId, string? Error)? _signIn;
    // Each profile's username and whether its password is kept, read from its sealed login once: its row shows them.
    private readonly ConcurrentDictionary<string, LoginState> _logins = new();

    private Login? LoginFor(string id) => _sessionLogins.TryGetValue(id, out var typed) ? typed : _store.SavedLogin(id);

    private LoginState LoginStateOf(string id) => _logins.GetOrAdd(id, key =>
    {
        try { return _store.LoginOf(key); }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or IOException) { return new LoginState(null, false); }
    });

    private void KeepLogin(string id, string username, string? password)
    {
        _store.SetLogin(id, username, password);
        _logins.TryRemove(id, out _);
    }

    private void OpenSignIn(string id, string? error)
    {
        lock (_gate) _signIn = (NewToken(), id, error);
        Publish();
    }

    // The server did not accept them: what was typed for this connection is let go, and the person asked again — a
    // remembered one is not tried again on its own.
    private void SignInRefusedBy(string id)
    {
        _sessionLogins.TryRemove(id, out _);
        if (_store.Find(id) is null) return;
        OpenSignIn(id, L.T("The VPN server did not accept this username and password. Check them and try again."));
    }

    /// <summary>Connect, from the sign-in: the username kept, and the password too when the person chose to remember it.</summary>
    public void SignIn(string token, SignInValues values)
    {
        string id;
        lock (_gate)
        {
            if (_signIn is not { } open || open.Token != token) return;
            id = open.ProfileId;
        }
        var username = values.Username?.Trim() ?? "";
        var password = values.Password ?? "";
        var error = username.Length == 0 ? L.T("Enter a username.") : password.Length == 0 ? L.T("Enter a password.") : null;
        if (error is not null)
        {
            lock (_gate) if (_signIn?.Token == token) _signIn = (token, id, error);
            Publish();
            return;
        }
        KeepLogin(id, username, values.Remember ? password : null);
        if (values.Remember) _sessionLogins.TryRemove(id, out _);
        else _sessionLogins[id] = new Login(username, password);
        lock (_gate) _signIn = null;
        Connect(id);
        Publish();
    }

    /// <summary>Not now: the profile stays as it was, disconnected.</summary>
    public void CancelSignIn(string token)
    {
        lock (_gate)
        {
            if (_signIn?.Token != token) return;
            _signIn = null;
        }
        Publish();
    }

    // ---- Edit a profile (D29): its name, for one that signs in its username and kept password, and how it connects.

    public void BeginProfileEdit(string id)
    {
        var profile = Profile(id);
        var login = profile.NeedsLogin ? LoginStateOf(id) : new LoginState(null, false);
        // What it connects with now, Settings' and its own together, so a choice of its own starts from there.
        var policy = profile.Unsupported is null
            ? GameViews.PolicyValuesOf(_settings.PolicyFor(id, OwnPolicy(id), ClientSettings.ProtocolsOf(profile.Remotes)))
            : null;
        lock (_gate) _editor = new OpenEditor(NewToken(), "profile-edit", id,
            new ProfileEditValues(profile.Name, login.Username ?? "", "", false, false, profile.NeedsLogin, login.PasswordSaved,
                !_settings.PolicyOf(id).IsEmpty, policy), Empty);
        Publish();
    }

    public void SaveProfileEdit(string token, ProfileEditValues values)
    {
        if (EditorFor(token, "profile-edit") is not { } editor) return;
        var profile = Profile(editor.SubjectId);
        var errors = new Dictionary<string, string>();
        var name = values.Name?.Trim() ?? "";
        if (name.Length is 0 or > LongestName) errors["name"] = L.T("Use 1 to {0} characters.", LongestName);
        else if (_store.List().Any(p => p.Id != profile.Id && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            errors["name"] = L.T("A profile already uses this name. Choose another name.");
        var username = values.Username?.Trim() ?? "";
        if (profile.NeedsLogin && values.ChangePassword && string.IsNullOrEmpty(values.Password)) errors["password"] = L.T("Enter a password.");
        if (profile.NeedsLogin && values.ChangePassword && username.Length == 0) errors["username"] = L.T("Enter a username.");
        var connects = profile.Unsupported is null;
        var policy = connects && values.OwnSettings && values.Policy is { } own ? PolicyChange(profile.Id, own, errors) : new PolicyOverride();
        if (errors.Count > 0)
        {
            lock (_gate) if (_editor?.Token == token) _editor = _editor with { Values = values with { Password = "" }, Errors = errors };
            Publish();
            return;
        }
        if (name != profile.Name) _store.Rename(profile.Id, name);
        if (profile.NeedsLogin)
        {
            var kept = LoginStateOf(profile.Id);
            // A kept password belongs to its username: a new username without a new password forgets it.
            if (values.ChangePassword) KeepLogin(profile.Id, username, values.Password);
            else if (values.ForgetPassword || username != (kept.Username ?? "")) KeepLogin(profile.Id, username, null);
        }
        var reconnect = false;
        lock (_gate)
        {
            if (connects && policy is not null && policy != _settings.PolicyOf(profile.Id))
            {
                _settings.PutPolicy(profile.Id, policy);
                reconnect = true;
            }
            if (_editor?.Token == token) _editor = null;
        }
        Notify(NoticeKind.Info, reconnect ? L.T("Saved {0}. Its next connection uses these settings.", name) : L.T("Saved {0}.", name), new("profile", profile.Id));
        Publish();
    }

    // ---- Profiles dropped on the window, or opened from Explorer: onto the import page, one at a time.

    /// <summary>The files given: one profile goes to the import page, as one chosen there would; several, or anything that
    /// is not a profile, are said and left.</summary>
    public void OpenProfileFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        if (paths.Count > 1) { Notify(NoticeKind.Warning, L.T("Import one profile at a time.")); return; }
        if (!(paths[0].EndsWith(".ovpn", StringComparison.OrdinalIgnoreCase) || paths[0].EndsWith(".conf", StringComparison.OrdinalIgnoreCase)))
        {
            Notify(NoticeKind.Warning, L.T("That is not an OpenVPN profile. Choose a .ovpn file."));
            return;
        }
        bool signingIn, editing;
        lock (_gate)
        {
            signingIn = _signIn is not null;
            editing = _editor is { Kind: not "new-profile" };
        }
        // What the person was doing is not thrown away under them.
        if (signingIn) { Notify(NoticeKind.Warning, L.T("Finish signing in before importing a profile.")); return; }
        if (editing) { Notify(NoticeKind.Warning, L.T("Finish or cancel what you are editing first, then import the profile.")); return; }
        OpenProfileFile(paths[0]);
    }

    public void Connect(string id)
    {
        var profile = Profile(id);
        if (profile.Unsupported is { } why) throw new ShenoraException("PROFILE_UNSUPPORTED", "id", id, L.T("This version cannot connect {0}: {1}", profile.Name, ClientView.CannotConnect(why)));
        // Signed in first: one that asks for a username and password, with none for this connection, asks for them.
        if (profile.NeedsLogin && LoginFor(id) is null)
        {
            OpenSignIn(id, null);
            return;
        }
        _locationFailed.TryRemove(id, out _);
        _connections.Connect(id);
        // Connecting is asking again: apps whose route ended on its own for this profile are tried once more.
        _runner.Retry();
        Replan();
    }

    /// <summary>Takes the profile's channel down — its proxy, and the apps going through it, asked about first. Their
    /// switches stay on: they go through it again at the next connect.</summary>
    public async Task DisconnectAsync(string id)
    {
        if (!_connections.IsRunning(id)) return;
        if (_runner.Facts()?.LaneFor(id) is { } lane
            && lane.Apps.Keys.Select(a => _items.Find(a)?.Name).OfType<string>().ToList() is { Count: > 0 } names)
        {
            Confirm("disconnect", id, L.T("Disconnect {0}?", Profile(id).Name),
                names.Count == 1 ? L.T("{0} goes through it, and carries on over your own connection until you connect again.", names[0])
                    : L.T("{0} go through it, and carry on over your own connection until you connect again.", L.List(names)),
                L.T("Disconnect"), L.T("Keep connected"));
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
            // A password typed for this connection alone goes with it.
            _sessionLogins.TryRemove(id, out _);
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
        var inUse = _items.List().Count(i => i.UseOf(id) is not null);
        var text = L.Join(L.T("Deletes Sallyport's encrypted copy. The original file stays unchanged."),
            inUse == 0 ? null : L.Count(inUse, "1 app in its section is taken out of it; the app itself stays.", "{0} apps in its section are taken out of it; the apps themselves stay."));
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
            _looks.TryRemove(id, out _);
            _logins.TryRemove(id, out _);
            _sessionLogins.TryRemove(id, out _);
            _settings.Forget(id);
            _items.Forget(id);
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
        Replan();
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
                    // Added to use the VPN: in its profile's section with its switch on, so it goes through it whenever that is connected.
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
            var through = _store.Find(made.Sections.FirstOrDefault()?.ProfileId ?? "")?.Name;
            Notify(NoticeKind.Info, made.PacketPrograms.Count > 0 && through is not null
                ? L.T("Added {0}. Use VPN is on: it goes through {1} whenever {1} is connected.", made.Name, through)
                : made.PacketPrograms.Count == 0 ? L.T("Added {0}. Launch it from here to start it through your VPN.", made.Name)
                : L.T("Added {0}.", made.Name), new("app", made.Id));
            Replan();
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
        var path = await PickProgramAsync("add-program", L.T("Choose another program for {0}", item.Name));
        if (path is null) return;
        try { _items.AddProgram(id, path); }
        catch (ArgumentException) { throw new ShenoraException("APP_PROGRAM", "id", id, L.T("That is not a program.")); }
        Replan();
        Notify(NoticeKind.Info, L.T("Added {0} to {1}.", Path.GetFileName(path), item.Name), new("app", id));
    }

    // A new app's profile: the default the person chose, while it can connect; else the first that can.
    private StoredProfile? DefaultProfile() =>
        _settings.Defaults.ProfileId is { } chosen && _store.Find(chosen) is { Unsupported: null } profile ? profile
        : _store.List().FirstOrDefault(p => p.Unsupported is null);

    private RoutedItem WithDefaults(RoutedItem item, bool force = false)
    {
        var defaults = _settings.Defaults;
        if (!force && defaults.Loss == item.Loss && defaults.DirectAfterSeconds == item.DirectAfterSeconds) return item;
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
        RoutedItem after;
        try { after = _items.Rescan(id); }
        catch (DirectoryNotFoundException) { throw new ShenoraException("APP_FOLDER", "id", id, L.T("{0}'s folder is not there any more.", before.Name)); }
        Replan();
        var added = after.Programs.Count(e => !before.Programs.Any(b => string.Equals(b.Path, e.Path, StringComparison.OrdinalIgnoreCase)));
        var gone = before.Programs.Count(b => !after.Programs.Any(e => string.Equals(b.Path, e.Path, StringComparison.OrdinalIgnoreCase)));
        Notify(NoticeKind.Info, added + gone == 0 ? L.T("{0}'s folder holds the same programs.", after.Name) :
            L.T("{0}: {1}, {2} gone. Your choices for the rest stand.", after.Name, L.Count(added, "1 new program", "{0} new programs"), gone), new("app", id));
    }

    /// <summary>Locate…: where an app that is not here is now, asked — its folder, or one of its programs — and the app
    /// moved there (D26).</summary>
    public async Task LocateAppAsync(string id)
    {
        var item = Item(id);
        var picked = await WithDialogAsync("locate", () => _dialogs.OpenFolderAsync(new OpenFolderOptions
        {
            Title = L.T("Where is {0} now? Choose its folder, or its program", item.Name),
            AllowFileSelection = true,
            Filters = [new FileDialogFilter { Name = L.T("Programs"), Extensions = ["exe"] }],
            RememberPathKey = "add-app",
        }));
        if (picked is not { Success: true, FilePath: { } path }) return;
        RoutedItem moved;
        try { moved = _items.Relocate(id, path); }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        { throw new ShenoraException("APP_LOCATE", "id", id, L.T("{0} is not there: choose its folder, or its program.", item.Name)); }
        Replan();
        Notify(NoticeKind.Info, L.T("{0} is in {1} now: {2} of its programs are there.", moved.Name, moved.Folder ?? Path.GetDirectoryName(path)!,
            moved.Programs.Count(p => File.Exists(p.Path))), new("app", id));
    }

    // After an import: each app that is not here looked for on this computer's other drives, and moved where it is found.
    private int SeekImported(IEnumerable<string> ids)
    {
        var found = 0;
        foreach (var item in ids.Select(_items.Find).OfType<RoutedItem>().Where(i => !RoutedItemStore.IsHere(i)))
            if (RoutedItemStore.Seek(item) is { } there)
                try { _items.Relocate(item.Id, there); found++; }
                catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { }
        return found;
    }

    /// <summary>Includes or leaves out one of an app's programs, by its id: a path from the page is never trusted.</summary>
    public void SetAppProgram(string id, string programId, bool included)
    {
        var item = Item(id);
        var program = item.Programs.FirstOrDefault(p => p.Id == programId)
            ?? throw new ShenoraException("APP_PROGRAM", "id", id, L.T("That program is not one of this app's."));
        _items.Choose(id, program.Path, included);
        // While it goes through the VPN, the change does at once: the route's programs are changed, not restarted.
        Replan();
        Publish();
    }

    /// <summary>Its switch in a profile's section, Use VPN: a choice kept whether or not that profile is connected (D23).
    /// On, it goes through the profile whenever it is connected — at once if it is; off, it keeps the person's own
    /// connection there. Turned on for a profile whose section it is not in, it joins it (D24).</summary>
    public void SetAppVpn(string id, string profileId, bool on)
    {
        var item = Item(id);
        var stored = Profile(profileId);
        if (on)
        {
            if (!item.RoutesPackets) throw new ShenoraException("APP_PROXY_ONLY", "id", id, L.T("{0} uses the VPN when it is launched from here: use Launch.", item.Name));
            if (stored.Unsupported is { } why) throw new ShenoraException("PROFILE_UNSUPPORTED", "id", id, L.T("This version cannot connect {0}: {1}.", stored.Name, why));
            if (item.PacketPrograms.Count == 0) throw new ShenoraException("APP_EMPTY", "id", id, L.T("Include at least one of {0}'s programs sending all its traffic.", item.Name));
        }
        if (item.UseOf(profileId)?.On != on) _items.Put(item.WithUse(profileId, on));
        // Turning one on is asking again, as a connect is: a route that ended on its own is tried once more.
        if (on) _runner.Retry();
        Replan();
        Publish();
    }

    /// <summary>Puts an app in a profile's section, its switch on (D24).</summary>
    public void AddAppToProfile(string id, string profileId)
    {
        var item = Item(id);
        var stored = Profile(profileId);
        if (item.UseOf(profileId) is not null) return;
        _items.Put(item.WithUse(profileId, true));
        _runner.Retry();
        Replan();
        Notify(NoticeKind.Info, L.T("{0} goes through {1} whenever {1} is connected.", item.Name, stored.Name), new("app", id));
    }

    /// <summary>Takes an app out of a profile's section: it goes through its other sections, or none.</summary>
    public void RemoveAppFromProfile(string id, string profileId)
    {
        var item = Item(id);
        if (item.UseOf(profileId) is null) return;
        _items.Put(item.WithUse(profileId, null, remove: true));
        Replan();
        Publish();
    }

    /// <summary>The app's icon from another of its programs.</summary>
    public void SetAppIcon(string id, string programId)
    {
        var item = Item(id);
        if (!item.Programs.Any(p => p.Id == programId)) throw new ShenoraException("APP_PROGRAM", "id", id, L.T("That program is not one of this app's."));
        _items.Put(item with { IconProgramId = programId });
        Publish();
    }

    // The route follows the plan: the apps on whose profile the person connected.
    private void Replan() => _runner.Want(Plan());

    private RoutePlan? Plan()
    {
        var items = _items.List().Where(i => i.Rules.Count > 0).ToList();
        // Each connected profile a lane of the one run (D24), those carrying apps now first, so an app on in two keeps the
        // one it goes through; then the others in the order they were imported.
        var current = _runner.ProfilesInUse;
        var connected = _store.List().Where(p => p.Unsupported is null && _connections.IsWanted(p.Id))
            .OrderBy(p => current.Contains(p.Id) ? 0 : 1).Take(Windows.Routing.Router.Lanes).ToList();
        var placed = new HashSet<string>();
        var lanes = new List<PlannedLane>();
        foreach (var profile in connected)
        {
            var apps = items.Where(i => !placed.Contains(i.Id) && i.UseOf(profile.Id) is { On: true }).ToList();
            if (apps.Count == 0) continue;
            placed.UnionWith(apps.Select(a => a.Id));
            lanes.Add(new PlannedLane(profile.Id, [.. apps.Select(a => new PlannedApp(a.Id, a.Rules))]));
        }
        return lanes.Count == 0 ? null : new RoutePlan(lanes);
    }

    // "Turn the VPN off for it": the connection lost, such an app's switch goes off, and it keeps the person's own
    // connection until they turn it on again (the router lets it go direct meanwhile).
    private void TurnOffOnLoss(string profileId, IReadOnlyCollection<string> carried)
    {
        foreach (var item in carried.Select(_items.Find).OfType<RoutedItem>().Where(i => i.UseOf(profileId) is { On: true } && i.Loss == LossMode.Stop))
        {
            _items.Put(item.WithUse(profileId, false));
            Notify(NoticeKind.Warning, L.T("The connection was lost, so {0}'s Use VPN was turned off, as its setup says. It uses your own connection now.", item.Name),
                new("app", item.Id));
        }
        Replan();
    }

    // A route that ended on its own while its profile is still connected: said once. One the person ended by disconnecting
    // is not trouble, and a profile that gave up says so itself.
    private void SayFailed(string profileId, string why)
    {
        if (!_connections.IsWanted(profileId)) return;
        Notify(NoticeKind.Warning, L.T("Apps could not go through {0}: {1}", _store.Find(profileId)?.Name ?? L.T("its profile"), why), new("profile", profileId));
    }

    public void BeginAppEdit(string id)
    {
        var item = Item(id);
        if (_launching.ContainsKey(id)) throw new ShenoraException("LAUNCHING", "id", id, L.T("Wait for the launch to finish."));
        lock (_gate)
            _editor = new OpenEditor(NewToken(), "app", id, new AppEditValues(item.Name,
                item.Loss, item.DirectAfterSeconds?.ToString(CultureInfo.InvariantCulture) ?? "", AppViews.LaunchTarget(item)?.Id,
                AppViews.IconProgram(item)?.Id), Empty);
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
        if (!Enum.IsDefined(values.Loss)) errors["loss"] = L.T("Choose what happens when the VPN drops.");
        int? directAfter = null;
        if (values.Loss == LossMode.Hold && directAfterText.Length > 0)
        {
            if (int.TryParse(directAfterText, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds is >= 5 and <= 3600) directAfter = seconds;
            else errors["directAfterText"] = L.T("Use 5 to 3600 seconds, or leave it empty to hold until the VPN is back.");
        }
        if (values.LaunchProgramId is { } launch && !item.Programs.Any(p => p.Id == launch && p.Included))
            errors["launchProgramId"] = L.T("Choose one of its included programs.");
        if (values.IconProgramId is { } icon && !item.Programs.Any(p => p.Id == icon)) errors["form"] = L.T("That program is not one of this app's.");
        lock (_gate)
        {
            if (_editor?.Token != token) return;
            if (errors.Count > 0) { _editor = _editor with { Values = values, Errors = errors }; }
            else
            {
                _items.Put(item with { Name = name, Loss = values.Loss, DirectAfterSeconds = directAfter, LaunchProgramId = values.LaunchProgramId,
                    IconProgramId = values.IconProgramId ?? item.IconProgramId });
                _editor = null;
            }
        }
        // Another profile or loss policy takes effect at once, as any change of what goes through the VPN does.
        Replan();
        Publish();
    }

    public void BeginProgramEdit(string id, string programId)
    {
        var item = Item(id);
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
        Replan();
        Publish();
    }

    public void CancelEdit(string token)
    {
        lock (_gate)
        {
            if (_editor?.Token != token) return;
            _editor = null;
            _importing = null;
            _reading = null;
        }
        Publish();
    }

    public void RequestRemoveApp(string id)
    {
        var item = Item(id);
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
        Replan();
        Notify(NoticeKind.Info, L.T("Removed {0} from Sallyport. Its programs are unchanged.", name));
    }

    /// <summary>Starts an app as its card says: the program Launch starts, with proxy settings, its profile connected
    /// first — a launch is the person asking for the VPN. A wait that ends without the tunnel up never launches later.</summary>
    /// <param name="profileId">The section it is launched from; else the first of its sections that is connected, else its first.</param>
    public async Task LaunchAsync(string id, string? programId, string? profileId, CancellationToken ct)
    {
        var item = Item(id);
        var target = programId is null ? AppViews.LaunchTarget(item) : item.Programs.FirstOrDefault(p => p.Id == programId && p.Included);
        if (target is null) throw new ShenoraException("APP_NO_TARGET", "id", id, L.T("Choose what Launch starts for {0} (Edit).", item.Name));
        if (!File.Exists(target.Path)) throw new ShenoraException("APP_MISSING", "id", id, L.T("{0} is not where it was added from.", Path.GetFileName(target.Path)));
        profileId ??= item.LaunchProfile(_connections.IsWanted)
            ?? throw new ShenoraException("APP_NO_PROFILE", "id", id, L.T("Add {0} to a VPN first, on its page or on Overview.", item.Name));
        var profile = Profile(profileId);
        if (!_launching.TryAdd(id, AppPhase.Connecting)) return;
        Publish();
        try
        {
            try
            {
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

    // ---- a profile's connection settings, on its Edit page

    /// <summary>What of <paramref name="values"/> differs from what the profile connects with without changes of its own
    /// (Settings' choices over its directives): only that is kept, so a later change in Settings, or a later import of a
    /// profile that says otherwise, is followed where the person did not choose. Null, with <paramref name="errors"/>
    /// said, where a value cannot be kept.</summary>
    private PolicyOverride? PolicyChange(string id, PolicyValues values, Dictionary<string, string> errors)
    {
        var stored = Profile(id);
        var basis = _settings.BaseFor(OwnPolicy(id), ClientSettings.ProtocolsOf(stored.Remotes));
        var before = errors.Count;
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
        if (protocol is not null && GameViews.ProtocolChoices(stored.Remotes)?.Any(c => c.Id == protocol) != true)
            errors["protocol"] = L.T("This profile has no servers of that type.");
        var timeout = Read(values.AttemptTimeoutText, "attemptTimeoutText", 2, 300, L.T("Use 2 to 300 seconds."));
        int? pingRestart = null;
        if (!string.IsNullOrWhiteSpace(values.PingRestartText)) pingRestart = Read(values.PingRestartText, "pingRestartText", 5, 600, L.T("Use 5 to 600 seconds, or leave it empty for the server's own."));
        if (wait is { } w && waitMax is { } m && m < w) errors["retryWaitMaxText"] = L.T("The longest wait cannot be shorter than the first.");
        if (errors.Count > before) return null;

        int? Differs(int value, TimeSpan theirs) => value == (int)Math.Round(theirs.TotalSeconds) ? null : value;
        return new PolicyOverride(
            Differs(wait!.Value, basis.RetryWait), Differs(waitMax!.Value, basis.RetryWaitMax),
            rounds == (basis.RoundsMax ?? 0) ? null : rounds, values.RandomOrder == basis.RandomOrder ? null : values.RandomOrder,
            Differs(timeout!.Value, basis.AttemptTimeout), values.RetryAfterAuthFailure == basis.RetryAfterAuthFailure ? null : values.RetryAfterAuthFailure,
            pingRestart is { } r && (r != basis.PingRestart || !basis.KeepaliveOverridesServer) ? r : null,
            protocol == basis.Protocol ? null : protocol ?? PolicyOverride.Any);
    }

    // The policy the profile itself carries (its OpenVPN directives): read from the stored copy, which is opened for it.
    private ConnectionPolicy OwnPolicy(string id)
    {
        try { return _store.Load(id).Policy; }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or FormatException or FileNotFoundException)
        { throw new ShenoraException("PROFILE_UNREADABLE", "id", id, L.T("The stored profile could not be opened.")); }
    }

    // ---- moving to another computer (D26): the profiles, the apps and the connection settings, any of them

    // What the open Import read, for its token: the editor shows what is in it, the save takes what was chosen.
    private (string Token, BackupFile File, string Name)? _importing;
    private const int ShortestPassword = 8;

    /// <summary>Opens Export with every part that has anything in it chosen.</summary>
    public void BeginExport()
    {
        lock (_gate)
        {
            _importing = null;
            _editor = new OpenEditor(NewToken(), "export", "", new MoveValues(_store.List().Count > 0, _items.List().Count > 0, true, "", "", Sealed: true), Empty);
        }
        Publish();
    }

    /// <summary>Export: checked, then where to save asked, then written whole — the profiles sealed with the password.</summary>
    public async Task SaveExportAsync(string token, MoveValues values)
    {
        if (EditorFor(token, "export") is null) return;
        var errors = new Dictionary<string, string>();
        if (!values.Profiles && !values.Apps && !values.Connection) errors["form"] = L.T("Choose at least one thing to export.");
        if (values.Profiles && _store.List().Count == 0) errors["form"] = L.T("There is no VPN profile to export.");
        // A password is the person's choice (the owner: "we also can allow export/import without password"); one given
        // is long enough to mean something.
        var password = values.Password ?? "";
        if (values.Profiles && password.Length is > 0 and < ShortestPassword)
            errors["password"] = L.T("Use at least {0} characters, or leave it empty to export without a password.", ShortestPassword);
        else if (values.Profiles && (values.Confirm ?? "") != password) errors["confirm"] = L.T("The two passwords are not the same.");
        if (errors.Count > 0)
        {
            Refuse(token, values, errors);
            return;
        }
        var picked = await WithDialogAsync("export", () => _dialogs.SaveFileAsync(new SaveFileOptions
        {
            Title = L.T("Export Sallyport's settings"),
            FileName = $"Sallyport {DateTime.Now:yyyy-MM-dd}.{Backup.Extension}",
            DefaultExtension = Backup.Extension,
            Filters = [new FileDialogFilter { Name = L.T("Sallyport exports"), Extensions = [Backup.Extension] }],
            OverwritePrompt = true,
            RememberPathKey = "export",
        }));
        if (picked is not { Success: true, FilePath: { } path }) return;
        try
        {
            var profiles = values.Profiles ? Backup.Seal(_store.List().Select(p => (p, _store.Text(p.Id))), password.Length > 0 ? password : null) : null;
            var bytes = Backup.Write(profiles, values.Apps ? _items.List() : null,
                values.Connection ? new ConnectionPart(_settings.Connection, _settings.Policies, _settings.Defaults) : null);
            var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            Refuse(token, values, new Dictionary<string, string> { ["form"] = L.T("The export was not written: {0}.", e.GetType().Name) });
            return;
        }
        lock (_gate) if (_editor?.Token == token) _editor = null;
        Notify(values.Profiles && password.Length == 0 ? NoticeKind.Warning : NoticeKind.Info,
            !values.Profiles ? L.T("Exported to {0}.", Path.GetFileName(path))
            : password.Length > 0 ? L.T("Exported to {0}. Keep its password: the VPN profiles in it open only with it.", Path.GetFileName(path))
            : L.T("Exported to {0}, without a password: anyone who has the file can use your VPN profiles. Keep it somewhere only you can open.", Path.GetFileName(path)));
    }

    /// <summary>Import: a file asked for and read, then what is in it shown to choose from.</summary>
    public async Task BeginImportAsync()
    {
        var picked = await WithDialogAsync("import-file", () => _dialogs.OpenFileAsync(new OpenFileOptions
        {
            Title = L.T("Import Sallyport's settings"),
            Filters = [new FileDialogFilter { Name = L.T("Sallyport exports"), Extensions = [Backup.Extension] }],
            RememberPathKey = "export",
        }));
        if (picked is not { Success: true, FilePath: { } path }) return;
        var name = Path.GetFileName(path);
        BackupFile file;
        try
        {
            if (new FileInfo(path).Length > Backup.Largest) throw new InvalidDataException("it is far larger than any export");
            file = Backup.Read(await File.ReadAllBytesAsync(path));
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or FormatException)
        { throw new ShenoraException("IMPORT_INVALID", "file", name, L.T("{0} was not imported: {1}.", name, e is InvalidDataException ? e.Message : e.GetType().Name)); }
        lock (_gate)
        {
            _editor = new OpenEditor(NewToken(), "import", "", new MoveValues(file.Profiles is { Items.Count: > 0 }, file.Apps is { Count: > 0 }, file.Connection is not null, "", "",
                Sealed: file.Profiles is { Sealed: true }), Empty);
            _importing = (_editor.Token, file, name);
        }
        Publish();
    }

    /// <summary>Import: the profiles opened with the password and kept — one this computer holds already, the same text,
    /// is not kept twice — then the apps not here yet, their sections on the profiles kept here, then the connection
    /// settings over this computer's.</summary>
    public void SaveImport(string token, MoveValues values)
    {
        if (EditorFor(token, "import") is null) return;
        (string Token, BackupFile File, string Name)? importing;
        lock (_gate) importing = _importing is { } open && open.Token == token ? open : null;
        if (importing is not { File: var file, Name: var name }) return;
        var errors = new Dictionary<string, string>();
        var profiles = values.Profiles && file.Profiles is { } sealedProfiles ? Backup.Open(sealedProfiles, values.Password ?? "") : [];
        if (values.Profiles && profiles is null) errors["password"] = L.T("That password does not open the VPN profiles in {0}.", name);
        if (!values.Profiles && !values.Apps && !values.Connection) errors["form"] = L.T("Choose at least one thing to import.");
        if (errors.Count > 0)
        {
            Refuse(token, values, errors);
            return;
        }

        // The file's profile ids to this computer's: kept, found here already, or (when its profiles are not imported) its own.
        var mapped = _store.List().ToDictionary(p => p.Id, p => p.Id);
        int added = 0, known = 0;
        var here = _store.List().Select(p => (p.Id, Text: TryText(p.Id))).Where(p => p.Text is not null).ToList();
        foreach (var (entry, text) in profiles!)
        {
            if (here.FirstOrDefault(p => p.Text == text) is { Id: { } same })
            {
                mapped[entry.Id] = same;
                known++;
                continue;
            }
            try { mapped[entry.Id] = _store.Import(entry.Name, text).Id; added++; }
            catch (Exception e) when (e is InvalidDataException or ArgumentException) { Notify(NoticeKind.Warning, L.T("{0} was not imported: {1}.", entry.Name, e.Message)); }
        }
        int apps = 0, appsHere = 0;
        var imported = new List<string>();
        if (values.Apps)
            foreach (var app in file.Apps ?? [])
            {
                if (!RoutedItemStore.IsId(app.Id)) continue;
                if (_items.Find(app.Id) is not null) { appsHere++; continue; }
                // Sections only on profiles this computer has; an earlier client's one profile read as one.
                _items.Put(app with
                {
                    ProfileId = null, UseVpn = false,
                    Uses = [.. app.Sections.Where(u => mapped.ContainsKey(u.ProfileId)).Select(u => u with { ProfileId = mapped[u.ProfileId] })],
                });
                imported.Add(app.Id);
                apps++;
            }
        // The games may sit elsewhere on this computer (the owner: "the path might be changed"): found where they can be
        // before the route is planned, and the rest said, each with its Locate….
        var relocated = SeekImported(imported);
        var lost = imported.Select(_items.Find).OfType<RoutedItem>().Count(i => !RoutedItemStore.IsHere(i));
        if (values.Connection && file.Connection is { } connection)
        {
            _settings.Connection = connection.Connection;
            _settings.Defaults = connection.Defaults with { ProfileId = connection.Defaults.ProfileId is { } chosen && mapped.TryGetValue(chosen, out var to) ? to : null };
            foreach (var (profile, policy) in connection.Policies)
                if (mapped.TryGetValue(profile, out var target)) _settings.PutPolicy(target, policy);
        }
        lock (_gate)
        {
            if (_editor?.Token == token) _editor = null;
            _importing = null;
        }
        Replan();
        // What came in, and apart from it what this computer had already: never "0 profiles".
        var parts = new List<string>();
        if (added > 0) parts.Add(L.Count(added, "1 VPN profile", "{0} VPN profiles"));
        if (apps > 0) parts.Add(L.Count(apps, "1 app", "{0} apps"));
        if (values.Connection) parts.Add(L.T("the connection settings"));
        var already = new List<string>();
        if (known > 0) already.Add(L.Count(known, "1 VPN profile", "{0} VPN profiles"));
        if (appsHere > 0) already.Add(L.Count(appsHere, "1 app", "{0} apps"));
        Notify(NoticeKind.Info, L.Join(
            parts.Count > 0 ? L.T("Imported from {0}: {1}.", name, L.List(parts)) : L.T("Nothing new in {0}.", name),
            already.Count > 0 ? L.T("Already on this computer, so kept as they are: {0}.", L.List(already)) : null));
        if (relocated > 0) Notify(NoticeKind.Info, L.Count(relocated, "1 app was found on another drive of this computer and moved there.", "{0} apps were found on another drive of this computer and moved there."));
        if (lost > 0)
            Notify(NoticeKind.Warning, L.Count(lost, "1 app is not where it was on the other computer: open it on Apps and choose Locate….",
                "{0} apps are not where they were on the other computer: open each on Apps and choose Locate…."));
    }

    private string? TryText(string id)
    {
        try { return _store.Text(id); }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or FileNotFoundException or IOException) { return null; }
    }

    // A save refused: what was typed stands, the reason beside its field.
    private void Refuse(string token, MoveValues values, Dictionary<string, string> errors)
    {
        lock (_gate) if (_editor?.Token == token) _editor = _editor with { Values = values, Errors = errors };
        Publish();
    }

    // ---- the packet driver

    public void RequestDriverInstall() =>
        Confirm("driver-install", "", L.T("Install the packet driver?"), GameViews.DriverInstallText, L.T("Install"), L.T("Not now"));

    public void RequestDriverRemove()
    {
        if (_runner.IsRunning) throw new ShenoraException("RUN_BUSY", "driver", "", L.T("Disconnect first: apps are going through the VPN."));
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
            // A driver just put in is what a route that failed for want of it was waiting for.
            if (outcome.Succeeded && install) _runner.Retry();
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
        var live = run is { Ended: null } ? run : null;
        var profiles = stored.Select(p => ClientView.Profile(new ProfileFacts(
                p, _connections.ProxyPortOf(p.Id), _connections.StatusOf(p.Id), _connections.LocationOf(p.Id), _locating.ContainsKey(p.Id),
                _locationFailed.ContainsKey(p.Id), _connections.AttemptsSinceUp(p.Id), _disconnecting.ContainsKey(p.Id), _removing.ContainsKey(p.Id),
                live?.LaneFor(p.Id) is { } lane ? L.List(lane.Apps.Keys.Select(a => items.FirstOrDefault(g => g.Id == a)?.Name).OfType<string>()) : null,
                _connections.NextAttemptOf(p.Id), _settings.ChoicesFor(p.Id, ClientSettings.ProtocolsOf(p.Remotes)), ServersOf(p.Id),
                p.NeedsLogin ? LoginStateOf(p.Id) : null, LookOf(p.Id).Account)))
            .ToList();
        var driver = GameViews.Driver(Driver());
        var dialog = Volatile.Read(ref _dialog);
        var facts = items.ToDictionary(i => i.Id, i =>
        {
            // A proxy launch's settings name the port of the profile it would be launched through now.
            var through = stored.FirstOrDefault(p => p.Id == i.LaunchProfile(_connections.IsWanted));
            return new ItemFacts(i, stored, live, _connections.IsWanted, _runner.FailureOf, driver.Phase == DriverPhase.Ready,
                dialog is not null, _launching.TryGetValue(i.Id, out var phase) ? phase : null,
                p => through is null || p.Proxy is not { } proxy ? []
                    : AppLauncher.Describe(new RoutedApp(i.Id, i.Name, p.Path, proxy.Kind, through.Id, p.LaunchArguments), _connections.ProxyPortOf(through.Id),
                        Path.Combine(_paths.DataDir, "apps", proxy.DataKey)),
                i.Id == _watchedApp ? _programs.Of : null,
                _icons.Of(AppViews.IconProgram(i)?.Path), RoutedItemStore.IsHere(i));
        });
        var apps = items.Select(i => AppViews.Item(facts[i.Id])).ToList();
        // Each profile's section: the apps in it, and those that could join it (D24).
        var sections = stored.ToDictionary(p => p.Id, p => (
            (IReadOnlyList<AppSectionView>)[.. items.Where(i => i.UseOf(p.Id) is not null).Select(i => AppViews.InSection(facts[i.Id], p, forApp: false))],
            (IReadOnlyList<EditorOption>)[.. items.Where(i => i.UseOf(p.Id) is null).Select(i => new EditorOption(i.Id, i.Name))]));

        var guideEndpoint = GuideEndpoint(profiles);
        var guide = new ClientState.GuideView(guideEndpoint, ClientView.CurlExample(guideEndpoint), UiAction.Shown(L.T("Copy")));
        OpenEditor? editor;
        OpenConfirmation? confirmation;
        List<Notice> notices;
        NavigationView? navigation;
        (string Token, SetupPreview Preview)? setup;
        (string Token, BackupFile File, string Name)? importing;
        (string Token, string Origin, string Text, ProfilePreview Preview)? profileRead;
        (string Token, string ProfileId, string? Error)? signIn;
        bool fetching;
        lock (_gate)
        {
            signIn = _signIn;
            editor = _editor;
            importing = _importing is { } reading && reading.Token == editor?.Token ? reading : null;
            profileRead = _reading is { } looked && looked.Token == editor?.Token ? looked : null;
            fetching = _fetching is not null && _fetching == editor?.Token;
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
            WindowViews.Network(profiles, apps, live, stored, driver, stored.ToDictionary(p => p.Id, p => _connections.ProxyProblemOf(p.Id)),
                stored.ToDictionary(p => p.Id, p => _connections.LatencyOf(p.Id)), stored.ToDictionary(p => p.Id, p => _connections.TrafficOf(p.Id)), sections),
            WindowViews.Settings(driver, startsWithWindows, guide, _paths.DataDir, InstallLayout.InstalledHere(), _settings.Defaults,
                stored.FirstOrDefault(p => p.Id == _settings.Defaults.ProfileId), editor is not null, _settings.Language, _settings.Connection,
                dialog is null ? null : L.T("A file dialog is open."), editor?.Kind is "export" or "import"),
            // Where a first look is useful: with nothing to route through yet, the page that imports one; else the Overview,
            // where the switch is.
            stored.Count == 0 ? Page.Profiles : Page.Network,
            navigation,
            [.. stored.Select(p => new ClientState.ProfileChoice(p.Id, p.Name, p.Unsupported is null))],
            [.. Enum.GetValues<AppKind>().Select(k => new ClientState.KindChoice(k, ClientView.KindLabel(k), ClientView.KindHelp(k)))],
            GameViews.LossChoices,
            editor is null ? null : new EditorView(editor.Token, editor.SubjectId, fetching ? "fetching" : "editing", editor.Errors,
                // The guidance until a save is refused; then the refusal, which says it, alone. "Add an app" says what it
                // found at the place chosen, and adds nothing until it has found something. A profile this version cannot
                // connect is said before it is imported, not after.
                editor.Kind == "profile-edit" ? PolicyHint(editor.SubjectId) : editor.Kind == "new-app" ? setup?.Preview.Summary
                    : editor.Kind == "new-profile" && profileRead is { Preview.Unsupported: { } cannot }
                        ? L.T("This version cannot connect it yet: {0}. You can import it now; a later version can connect it.", cannot)
                    : editor.Kind == "import" && importing is { } read ? L.T("{0}, exported {1}.", read.Name, L.Date(read.File.Exported))
                    : editor.Kind == "export" ? L.T("Choose what goes in the file. With a password, the profiles in it open only with that password; without one, anyone who has the file can use them.") : null,
                editor.Kind switch
                {
                    "new-app" => UiAction.Shown(L.T("Add app"), setup is not null, L.T("Choose where it is first.")),
                    "export" => UiAction.Shown(L.T("Export…"), dialog is null, L.T("A file dialog is open."), busy: dialog == "export"),
                    "import" => UiAction.Shown(L.T("Import")),
                    "new-profile" => UiAction.Shown(L.T("Import"), profileRead is not null, L.T("Choose a file or get the profile from its link first.")),
                    _ => UiAction.Shown(L.T("Save")),
                },
                UiAction.Shown(L.T("Cancel")),
                // A password the person typed is never sent back to the page: the page keeps what it was given.
                editor.Kind, editor.Values is MoveValues move ? move with { Password = "", Confirm = "" }
                    : editor.Values is NewProfileValues typed ? typed with { Password = "" }
                    : editor.Values is ProfileEditValues changing ? changing with { Password = "" } : editor.Values,
                // What Launch may start: the app's included programs. The connection types the profile has servers for.
                // Only an app Sallyport starts has a choice of what it starts: one of its programs told of the proxy.
                editor.Kind == "app" && items.FirstOrDefault(i => i.Id == editor.SubjectId) is { } edited && edited.PacketPrograms.Count == 0
                    && edited.Programs.Count(p => p.Included && p.Proxy is not null) > 1
                    ? [.. edited.Programs.Where(p => p.Included && p.Proxy is not null).Select(p => new EditorOption(p.Id, Path.GetFileName(p.Path)))]
                    : editor.Kind == "profile-edit" && stored.FirstOrDefault(p => p.Id == editor.SubjectId) is { Unsupported: null } profile
                        ? GameViews.ProtocolChoices(profile.Remotes)
                    // What can go in an export, or what this file holds, each part with how much of it there is.
                    : editor.Kind == "export" ? MoveParts(stored.Count, items.Count, true)
                    : editor.Kind == "import" && importing is { File: var held }
                        ? MoveParts(held.Profiles?.Items.Count ?? 0, held.Apps?.Count ?? 0, held.Connection is not null)
                        // A folder's programs, each to turn on or off before the app is made; where it sits, if not at the top.
                        : setup is { Preview: { Kind: SetupKind.Folder, Programs.Count: > 1 } found }
                            ? [.. found.Programs.Select(p => new EditorOption(p.Path, Path.GetFileName(p.Path), p.Reason is { } reason ? L.T(reason)
                                : Path.GetRelativePath(Directory.Exists(found.Path) ? found.Path : Path.GetDirectoryName(found.Path)!, Path.GetDirectoryName(p.Path)!)
                                    is var where && where != "." ? where : null))]
                            : null,
                // The icons it may take, one per look: a game's programs often carry the same one.
                editor.Kind == "app" && items.FirstOrDefault(i => i.Id == editor.SubjectId) is { } iconed
                    ? [.. iconed.Programs.Select(p => (Program: p, Url: _icons.Of(p.Path))).Where(c => c.Url is not null).DistinctBy(c => c.Url)
                        .Select(c => new IconChoice(c.Program.Id, Path.GetFileName(c.Program.Path), c.Url!))]
                    : null,
                // What was read, as OpenVPN Connect's Imported Profile shows it: its servers, locked, whose it is, and where it
                // came from; an edited profile's, which no edit changes. One that signs in names its account in its own field.
                editor.Kind == "new-profile" && profileRead is { } got
                    ? [new Fact(L.T("Server"), string.Join(", ", got.Preview.Servers)),
                        .. !got.Preview.NeedsLogin && got.Preview.Account is { } whose ? [new Fact(L.T("Account"), whose)] : Array.Empty<Fact>(),
                        new Fact(L.T("From"), got.Origin)]
                    : editor.Kind == "profile-edit" && stored.FirstOrDefault(p => p.Id == editor.SubjectId) is { } editing
                        ? [new Fact(L.T("Server"), string.Join(", ", ServersOf(editing.Id))),
                            .. !editing.NeedsLogin && LookOf(editing.Id).Account is { } owner ? [new Fact(L.T("Account"), owner)] : Array.Empty<Fact>()]
                    : null),
            confirmation is null ? null : new ConfirmationView(confirmation.Token, confirmation.Title, confirmation.Text,
                UiAction.Shown(confirmation.ConfirmLabel), UiAction.Shown(confirmation.CancelLabel)),
            notices,
            L.Code,
            // The sign-in a connect is waiting for, over whatever page is shown; its username kept, never a password.
            signIn is { } asking && stored.FirstOrDefault(p => p.Id == asking.ProfileId) is { } signing
                ? new SignInView(asking.Token, signing.Id, L.T("Sign in to {0}", signing.Name), string.Join(", ", ServersOf(signing.Id)),
                    LoginStateOf(signing.Id).Username ?? "", LoginStateOf(signing.Id).PasswordSaved, asking.Error,
                    UiAction.Shown(L.T("Connect")), UiAction.Shown(L.T("Cancel")))
                : null,
            TextSize.Factor);
    }

    private static IReadOnlyList<EditorOption> MoveParts(int profiles, int apps, bool connection) =>
    [
        .. profiles > 0 ? [new EditorOption("profiles", L.Count(profiles, "1 VPN profile", "{0} VPN profiles"), L.T("Their keys included; the .ovpn files themselves are not needed."))] : Array.Empty<EditorOption>(),
        .. apps > 0 ? [new EditorOption("apps", L.Count(apps, "1 app", "{0} apps"), L.T("Their programs, switches and the VPNs they go through. The games themselves are not copied."))] : Array.Empty<EditorOption>(),
        .. connection ? [new EditorOption("connection", L.T("Connection settings"), L.T("VPN protocol, retries, each profile's own changes, and the defaults for new apps."))] : Array.Empty<EditorOption>(),
    ];

    private string? PolicyHint(string id)
    {
        // What "As in Settings" gives it: Settings' choices over its own directives, whatever it keeps of its own now.
        try { return GameViews.PolicyHint(_settings.BaseFor(OwnPolicy(id), ClientSettings.ProtocolsOf(Profile(id).Remotes))); }
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
