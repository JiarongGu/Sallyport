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

/// <summary>Apps (docs/design/window.md): added from a program or a folder, each program with its methods, put in profiles'
/// sections and switched there (D23, D24), edited, launched and removed — and the route that follows them.</summary>
internal sealed partial class ClientCore
{
    // ---- apps (docs/design/window.md): a program or a folder, each program with its methods

    /// <summary>A start's <c>--add-game</c>, a folder or a program: onto "Add an app" with it looked at, as a profile
    /// opened from Explorer goes onto the import page — the person sees what was found before anything is kept.</summary>
    public void OpenAddApp(string path)
    {
        bool editing;
        lock (_gate) editing = _editor is { Kind: not "new-app" };
        if (editing) { Notify(NoticeKind.Warning, L.T("Finish or cancel what you are editing first, then add the app.")); return; }
        BeginAddApp();
        OpenEditor? open;
        lock (_gate) open = _editor;
        if (open is { Kind: "new-app" }) SetNewAppLocation(open.Token, (NewAppValues)open.Values with { Path = path });
        Navigate(Page.Apps);
    }

    /// <summary>Asks for the app's program and makes an app of it — one way in for a game and a program alike: a browser
    /// is launched with proxy settings, its own browser profile kept apart; anything else is an app of its folder, every
    /// program there sending all its traffic through the packet driver, the utilities left out, the one picked what
    /// Launch starts — unless its folder is one many programs share (Windows', Program Files itself, Downloads), where it
    /// is the program alone.</summary>
    // ---- "Add an app": a page of its own, the place first, then what is found there

    // What the open "Add an app" found, for its token: the page shows it, the save makes the app from it.
    private (string Token, SetupPreview Preview)? _setup;

    /// <summary>Opens "Add an app" with nothing chosen yet: the person's defaults for a new app, going through
    /// <paramref name="profileId"/> when it is opened from that profile's section on the Overview, else the default
    /// profile, if any.</summary>
    public void BeginAddApp(string? profileId = null)
    {
        IReadOnlyList<string> through = profileId is not null && _store.Find(profileId) is not null ? [profileId]
            : DefaultProfile() is { } first ? [first.Id] : [];
        lock (_gate)
        {
            _setup = null;
            // Held while the VPN reconnects, the safe choice: what else is wanted is chosen on the page, for this app (the
            // owner: "新应用的默认设置 this can be just internal because the app add just one off").
            _editor = new OpenEditor(NewToken(), "new-app", "", new NewAppValues("", "", through, LossMode.Hold, "", []), Empty);
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
                    IconPath = AppSetup.IconPathOf(preview),
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
        var through = (values.ProfileIds ?? []).Distinct().ToList();
        // Only a profile this version can connect, as the page offers them: the host checks what it is sent.
        if (through.Any(id => _store.Find(id) is not { Unsupported: null })) errors["profileId"] = L.T("Choose a VPN profile.");
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
                    var first = through.FirstOrDefault();
                    made = preview!.Kind switch
                    {
                        SetupKind.Browser => _items.CreateFromProgram(name, preview.Path, preview.ProgramKind, first, packets: false, proxy: true),
                        SetupKind.Program => _items.CreateFromProgram(name, preview.Path, preview.ProgramKind, first, packets: true, proxy: false),
                        _ when Directory.Exists(preview.Path) => _items.CreateFromFolder(name, preview.Path, first),
                        _ => _items.CreateFromProgramFolder(name, preview.Path, first),
                    };
                    // Each other profile ticked: its section too, its switch on.
                    foreach (var other in through.Skip(1)) made = made.WithUse(other, true);
                    // The person's choice of programs over the scan's, each marked as theirs where it differs.
                    foreach (var program in made.Programs.Where(p => p.Included != included.Contains(p.Path)).ToList())
                        if (preview.Kind == SetupKind.Folder) made = _items.Choose(made.Id, program.Path, included.Contains(program.Path));
                    // Added to use the VPN: in its profile's section with its switch on, so it goes through it whenever that is connected.
                    // Its icon, the program's the person chose; one not among what the place has now is let go, and the
                    // app takes its own.
                    var icon = made.Programs.FirstOrDefault(p => string.Equals(p.Path, values.IconPath, StringComparison.OrdinalIgnoreCase));
                    made = made with { Loss = values.Loss, DirectAfterSeconds = directAfter, IconProgramId = icon?.Id };
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
            var names = made.Sections.Select(u => _store.Find(u.ProfileId)?.Name).OfType<string>().ToList();
            Notify(NoticeKind.Info, made.PacketPrograms.Count == 0 ? L.T("Added {0}. Launch it from here to start it through your VPN.", made.Name)
                : names.Count == 1 ? L.T("Added {0}. Use VPN is on: it goes through {1} whenever {1} is connected.", made.Name, names[0])
                : names.Count > 1 ? L.T("Added {0}. Use VPN is on in {1}: it goes through the first of them that is connected.", made.Name, L.List(names))
                : L.T("Added {0}. Put it in a VPN on the Overview, and it goes through it.", made.Name), new("app", made.Id));
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

    // A new app's profile, when it is not added from a profile's section: the first that can connect.
    private StoredProfile? DefaultProfile() => _store.List().FirstOrDefault(p => p.Unsupported is null);

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
            if (stored.Unsupported is { } why) throw new ShenoraException("PROFILE_UNSUPPORTED", "id", id, L.T("This version cannot connect {0}: {1}", stored.Name, ClientView.CannotConnect(why)));
            if (item.PacketPrograms.Count == 0) throw new ShenoraException("APP_EMPTY", "id", id, L.T("Include at least one of {0}'s programs sending all its traffic.", item.Name));
        }
        _items.Update(id, now => now.UseOf(profileId)?.On != on ? now.WithUse(profileId, on) : now);
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
        _items.Update(id, now => now.UseOf(profileId) is null ? now.WithUse(profileId, true) : now);
        _runner.Retry();
        Replan();
        Notify(NoticeKind.Info, L.T("{0} goes through {1} whenever {1} is connected.", item.Name, stored.Name), new("app", id));
    }

    /// <summary>Takes an app out of a profile's section: it goes through its other sections, or none.</summary>
    public void RemoveAppFromProfile(string id, string profileId)
    {
        var item = Item(id);
        if (item.UseOf(profileId) is null) return;
        _items.Update(id, now => now.UseOf(profileId) is null ? now : now.WithUse(profileId, null, remove: true));
        Replan();
        Publish();
    }

    /// <summary>The app's icon from another of its programs.</summary>
    public void SetAppIcon(string id, string programId)
    {
        var item = Item(id);
        if (!item.Programs.Any(p => p.Id == programId)) throw new ShenoraException("APP_PROGRAM", "id", id, L.T("That program is not one of this app's."));
        _items.Update(id, now => now with { IconProgramId = programId });
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
            _items.Update(item.Id, now => now.UseOf(profileId) is { On: true } ? now.WithUse(profileId, false) : now);
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
                // What the page edits, onto the app as it is now: its sections (Use VPN) may have changed since it was read.
                _items.Update(item.Id, now => now with { Name = name, Loss = values.Loss, DirectAfterSeconds = directAfter, LaunchProgramId = values.LaunchProgramId,
                    IconProgramId = values.IconProgramId ?? now.IconProgramId });
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
                _items.Update(item.Id, now => now with
                {
                    Programs = [.. now.Programs.Select(p => p.Id != programId ? p : p with
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
        // What the page offers to start, and through: a program of its started with proxy settings (one routed by its
        // packets starts as the person starts it, D22), through a profile the app is in — the host checks what it is sent.
        var target = programId is null ? AppViews.LaunchTarget(item) : item.Programs.FirstOrDefault(p => p.Id == programId && p.Included && p.Proxy is not null);
        if (target is null) throw new ShenoraException("APP_NO_TARGET", "id", id, L.T("Choose what Launch starts for {0} (Edit).", item.Name));
        if (!File.Exists(target.Path)) throw new ShenoraException("APP_MISSING", "id", id, L.T("{0} is not where it was added from.", Path.GetFileName(target.Path)));
        if (profileId is not null && item.UseOf(profileId) is null) profileId = null;
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
}
