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

/// <summary>Profiles: imported (a file, a drop or a link, D29), signed in with, edited — their name, their sign-in and
/// how they connect — connected and disconnected, located, and removed.</summary>
internal sealed partial class ClientCore
{
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
        bool signIn;
        lock (_gate)
        {
            _fetching = token;
            // Only to the address that asked, for the sign-in it asked for: a link changed since is another server, which
            // gets no password until it asks too — whatever the page still holds.
            signIn = values.SignIn && _askedToSignIn is { } asked && asked.Token == token && asked.Address == address;
        }
        Publish();
        try { got = await ProfileDownload.GetAsync(Downloads, address, signIn ? values.Username : null, signIn ? values.Password : null, CancellationToken.None); }
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
                var asking = NewToken();
                _askedToSignIn = (asking, address);
                _editor = new OpenEditor(asking, "new-profile", "", values with { Source = "link", SignIn = true, Password = "" },
                    new Dictionary<string, string>
                    {
                        [got.Why is null ? "form" : "password"] = got.Why ?? L.T("{0} asks who you are: type the username and password your VPN provider gave you.", address.Host),
                    });
            }
            Publish();
            return;
        }
        Read(token, values with { Source = "link", Password = "" }, (got.From ?? address).Host, ProfileDownload.NameFor(address), got.Text, got.Why);
    }

    // The import page's token while a link is being fetched: its button says so.
    private string? _fetching;

    // The sign-in a link's server asked for: the token of the page that asks it, and the address that asked.
    private (string Token, Uri Address)? _askedToSignIn;

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


    public void CopyEndpoint(string id)
    {
        var profile = Profile(id);
        var endpoint = ClientView.Endpoint(_connections.ProxyPortOf(id)
            ?? throw new ShenoraException("NOT_CONNECTED", "id", id, L.T("Connect {0} first: its proxy takes a free port when it connects.", profile.Name)));
        if (Clipboard(endpoint)) Notify(NoticeKind.Info, L.T("Copied {0}.", endpoint), new("profile", id));
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
}
