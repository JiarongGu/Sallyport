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
        _connections = connections;
        _items = items;
        _runner = runner;
        _paths = paths;
        _events = events;
        _dialogs = dialogs;
        _driverSetup = new PacketFilterSetup(paths.DataArea("drivers"));
        _connections.Changed += Publish;
        _runner.Changed += Publish;
        // Said once, where the person will see it: the window shows warnings until they are dismissed.
        if (items.Unreadable is { } why)
            Notify(NoticeKind.Warning, $"Your apps could not be read: {why}. Sallyport starts without them and keeps the file as it is until you change an app.");
        else if (items.Migrated is { Unreadable: > 0 and var lost })
            Notify(NoticeKind.Warning, $"Some apps could not be migrated: {lost} earlier game file{(lost == 1 ? "" : "s")} could not be read. They are kept as they were.");
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
            UiAction.Shown("Dismiss"));
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
    public void Navigate(Page page)
    {
        lock (_gate) _navigation = new NavigationView(page, Interlocked.Increment(ref _navigations));
        Publish();
    }

    public void SetStartWithWindows(bool on)
    {
        try { StartupRegistration.Set(on); }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        { throw new ShenoraException("STARTUP", "startup", "", $"Windows did not let Sallyport change how it starts ({e.GetType().Name})."); }
        Notify(NoticeKind.Info, on ? "Sallyport starts in the notification area when you sign in." : "Sallyport no longer starts when you sign in.");
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
            Title = "Import an OpenVPN profile",
            Filters = [new FileDialogFilter { Name = "OpenVPN profiles", Extensions = ["ovpn", "conf"] }],
            RememberPathKey = "import-profile",
        }));
        if (picked is { Success: true, FilePath: { } path }) Import(path);
    }

    public StoredProfile Import(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new ShenoraException("FILE_GONE", "file", file.Name, $"{file.Name} is no longer there.");
        if (file.Length > LargestProfile) throw new ShenoraException("PROFILE_INVALID", "file", file.Name, $"{file.Name} is far larger than any profile.");
        var name = Path.GetFileNameWithoutExtension(file.Name).Trim();
        if (name.Length == 0) name = "profile";
        if (name.Length > LongestName) name = name[..LongestName];

        StoredProfile stored;
        // The store's own reasons never quote the text, so they may cross to the page.
        try { stored = _store.Import(name, File.ReadAllText(file.FullName)); }
        catch (InvalidDataException e) { throw new ShenoraException("PROFILE_INVALID", "file", file.Name, $"{file.Name} was not imported: {e.Message}."); }
        _settings.PortOf(stored.Id);
        Notify(NoticeKind.Info, $"Imported {stored.Name}. Its copy is encrypted for your Windows account.", new("profile", stored.Id));
        return stored;
    }

    public void Connect(string id)
    {
        var profile = Profile(id);
        if (profile.Unsupported is { } why) throw new ShenoraException("PROFILE_UNSUPPORTED", "id", id, $"This version cannot connect {profile.Name}: {why}.");
        _locationFailed.TryRemove(id, out _);
        _connections.Connect(id);
    }

    /// <summary>Takes the profile's channel down — its proxy, and what is routed through it, asked about first.</summary>
    public async Task DisconnectAsync(string id)
    {
        if (!_connections.IsRunning(id)) return;
        if (_runner.ProfileInUse == id && _runner.Facts() is { Ended: null } run && _items.Find(run.GameId) is { } routed)
        {
            Confirm("disconnect", id, $"Disconnect {Profile(id).Name}?",
                $"{routed.Name} is routed through it. Its routing stops too, and it carries on over your own connection.", "Disconnect", "Keep connected");
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

    public void CopyEndpoint(string id)
    {
        var endpoint = ClientView.Endpoint(_settings.PortOf(Profile(id).Id));
        Clipboard(endpoint);
        Notify(NoticeKind.Info, $"Copied {endpoint}.", new("profile", id));
    }

    public void BeginPortEdit(string id)
    {
        Profile(id);
        if (_connections.IsRunning(id)) throw new ShenoraException("CONNECTED", "id", id, "Disconnect before changing the port.");
        lock (_gate) _editor = new OpenEditor(NewToken(), "port", id, new PortValues(_settings.PortOf(id).ToString(CultureInfo.InvariantCulture)), Empty);
        Publish();
    }

    public void SetPort(string token, string portText)
    {
        if (EditorFor(token, "port") is not { } editor) return;
        var id = editor.SubjectId;
        var current = _settings.PortOf(id);
        var errors = new Dictionary<string, string>();
        if (!int.TryParse(portText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1024 or > 65535)
            errors["portText"] = "Use a port from 1024 to 65535.";
        else if (_connections.IsRunning(id))
            errors["form"] = "Disconnect before changing the port.";
        else if (_settings.IsTakenByAnother(id, port))
            errors["portText"] = "Another profile uses this port.";
        else if (port != current && !ClientSettings.IsFree(port))
            errors["portText"] = "Something else on this computer is using this port.";

        lock (_gate)
        {
            if (_editor?.Token != token) return;
            if (errors.Count > 0) { _editor = _editor with { Values = new PortValues(portText), Errors = errors }; }
            else
            {
                _settings.SetPort(id, port);
                _editor = null;
            }
        }
        Publish();
    }

    public void RequestRemoveProfile(string id)
    {
        var profile = Profile(id);
        if (_connections.IsRunning(id)) throw new ShenoraException("CONNECTED", "id", id, $"Disconnect {profile.Name} before removing it.");
        if (_runner.ProfileInUse == id) throw new ShenoraException("PROFILE_IN_USE", "id", id, "Stop routing the app that uses it first.");
        var inUse = _items.List().Count(i => i.ProfileId == id || i.Programs.Any(p => p.ProfileId == id));
        var text = "Deletes Sallyport's encrypted copy. The original file stays unchanged." +
            (inUse == 0 ? "" : inUse == 1 ? " 1 app will need another profile." : $" {inUse} apps will need another profile.");
        Confirm("remove-profile", id, $"Remove {profile.Name}?", text, "Remove profile", "Keep profile");
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
        Notify(NoticeKind.Info, $"Removed {name}. The original file is unchanged.");
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

    /// <summary>Asks for a game's or program's folder and makes an app of it: every program found, routed by its
    /// packets, the utilities left out.</summary>
    public async Task AddFolderFromDialogAsync()
    {
        var picked = await WithDialogAsync("add-folder", () => _dialogs.OpenFolderAsync(new OpenFolderOptions
        {
            Title = "Choose the folder of the game or program",
            RememberPathKey = "add-game",
        }));
        if (picked is { Success: true, FilePath: { } folder }) AddFolder(folder);
    }

    /// <summary>An app from a folder (the dialog's choice, or a start's <c>--add-game</c>).</summary>
    public void AddFolder(string folder)
    {
        if (!Directory.Exists(folder)) throw new ShenoraException("APP_FOLDER", "folder", folder, "That folder is not there.");
        RoutedItem item;
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        if (name.Length == 0) name = "app";
        if (name.Length > LongestName) name = name[..LongestName];
        var profile = _store.List().FirstOrDefault(p => p.Unsupported is null);
        try { item = _items.CreateFromFolder(name, folder, profile?.Id); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new ShenoraException("APP_FOLDER", "folder", name, $"{name} could not be read: {e.GetType().Name}."); }
        var included = item.Programs.Count(p => p.Included);
        Notify(NoticeKind.Info, item.Programs.Count == 0
            ? $"Added {item.Name}, but its folder holds no program. Scan it again once it is installed."
            : $"Added {item.Name}: {included} of its {item.Programs.Count} programs will send all their traffic through the VPN.", new("app", item.Id));
    }

    /// <summary>Asks for one program and makes an app of it: a browser is launched with proxy settings, its own browser
    /// profile kept apart; anything else sends all its traffic through the packet driver.</summary>
    public async Task AddProgramFromDialogAsync()
    {
        var path = await PickProgramAsync("add-program", "Choose the program to route");
        if (path is null) return;
        var info = FileVersionInfo.GetVersionInfo(path);
        var name = new[] { info.FileDescription, info.ProductName, Path.GetFileNameWithoutExtension(path) }
            .First(n => !string.IsNullOrWhiteSpace(n))!.Trim();
        if (name.Length > LongestName) name = name[..LongestName];
        var kind = AppLauncher.Guess(path);
        var browser = kind is AppKind.Chromium or AppKind.Firefox;
        var item = _items.CreateFromProgram(name, path, kind, _store.List().FirstOrDefault(p => p.Unsupported is null)?.Id, packets: !browser, proxy: browser);
        Notify(NoticeKind.Info, browser
            ? $"Added {item.Name}: it is launched with proxy settings, in a browser profile of its own."
            : $"Added {item.Name}: all its traffic goes through the VPN while it is routed.", new("app", item.Id));
    }

    /// <summary>Adds one more program to an app — a launcher elsewhere, say — sending all its traffic.</summary>
    public async Task AddProgramToAppAsync(string id)
    {
        var item = Item(id);
        if (IsLive(id)) throw new ShenoraException("APP_ROUTING", "id", id, "Stop routing before changing its programs.");
        var path = await PickProgramAsync("add-program", $"Choose another program for {item.Name}");
        if (path is null) return;
        try { _items.AddProgram(id, path); }
        catch (ArgumentException) { throw new ShenoraException("APP_PROGRAM", "id", id, "That is not a program."); }
        Notify(NoticeKind.Info, $"Added {Path.GetFileName(path)} to {item.Name}.", new("app", id));
    }

    private async Task<string?> PickProgramAsync(string which, string title)
    {
        var picked = await WithDialogAsync(which, () => _dialogs.OpenFileAsync(new OpenFileOptions
        {
            Title = title,
            Filters = [new FileDialogFilter { Name = "Programs", Extensions = ["exe"] }],
            RememberPathKey = "add-app",
        }));
        return picked is { Success: true, FilePath: { } path } ? path : null;
    }

    public void RescanApp(string id)
    {
        var before = Item(id);
        if (IsLive(id)) throw new ShenoraException("APP_ROUTING", "id", id, "Stop routing before scanning again.");
        RoutedItem after;
        try { after = _items.Rescan(id); }
        catch (DirectoryNotFoundException) { throw new ShenoraException("APP_FOLDER", "id", id, $"{before.Name}'s folder is not there any more."); }
        var added = after.Programs.Count(e => !before.Programs.Any(b => string.Equals(b.Path, e.Path, StringComparison.OrdinalIgnoreCase)));
        var gone = before.Programs.Count(b => !after.Programs.Any(e => string.Equals(b.Path, e.Path, StringComparison.OrdinalIgnoreCase)));
        Notify(NoticeKind.Info, added + gone == 0 ? $"{after.Name}'s folder holds the same programs." :
            $"{after.Name}: {added} new program{(added == 1 ? "" : "s")}, {gone} gone. Your choices for the rest stand.", new("app", id));
    }

    /// <summary>Includes or leaves out one of an app's programs, by its id: a path from the page is never trusted.</summary>
    public void SetAppProgram(string id, string programId, bool included)
    {
        var item = Item(id);
        if (IsLive(id)) throw new ShenoraException("APP_ROUTING", "id", id, "Stop routing before changing its programs.");
        var program = item.Programs.FirstOrDefault(p => p.Id == programId)
            ?? throw new ShenoraException("APP_PROGRAM", "id", id, "That program is not one of this app's.");
        _items.Choose(id, program.Path, included);
        Publish();
    }

    public void BeginAppEdit(string id)
    {
        var item = Item(id);
        if (IsLive(id)) throw new ShenoraException("APP_ROUTING", "id", id, "Stop routing before changing it.");
        if (_launching.ContainsKey(id)) throw new ShenoraException("LAUNCHING", "id", id, "Wait for the launch to finish.");
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
        if (name.Length is 0 or > LongestName) errors["name"] = $"Use 1 to {LongestName} characters.";
        if (values.ProfileId is null || _store.Find(values.ProfileId) is null) errors["profileId"] = "Choose a VPN profile.";
        if (!Enum.IsDefined(values.Loss)) errors["loss"] = "Choose what happens when the VPN drops.";
        int? directAfter = null;
        if (values.Loss == LossMode.Hold && directAfterText.Length > 0)
        {
            if (int.TryParse(directAfterText, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds is >= 5 and <= 3600) directAfter = seconds;
            else errors["directAfterText"] = "Use 5 to 3600 seconds, or leave it empty to hold until the VPN is back.";
        }
        if (values.LaunchProgramId is { } launch && !item.Programs.Any(p => p.Id == launch && p.Included))
            errors["launchProgramId"] = "Choose one of its included programs.";
        if (IsLive(item.Id)) errors["form"] = "Stop routing before changing it.";

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
        if (IsLive(id)) throw new ShenoraException("APP_ROUTING", "id", id, "Stop routing before changing its methods.");
        var program = item.Programs.FirstOrDefault(p => p.Id == programId)
            ?? throw new ShenoraException("APP_PROGRAM", "id", id, "That program is not one of this app's.");
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
        if (!values.Packets && !values.Proxy) errors["form"] = "Choose at least one way for its traffic to reach the VPN, or leave the program out instead.";
        if (values.Proxy && !Enum.IsDefined(values.Kind)) errors["kind"] = "Choose how the program is told about the proxy.";
        if (arguments.Length > LongestArguments) errors["arguments"] = $"Use at most {LongestArguments:N0} characters.";
        if (IsLive(id)) errors["form"] = "Stop routing before changing its methods.";

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
        if (IsLive(id)) throw new ShenoraException("APP_ROUTING", "id", id, "Stop routing before removing it.");
        if (_launching.ContainsKey(id)) throw new ShenoraException("LAUNCHING", "id", id, "Wait for the launch to finish.");
        Confirm("remove-app", id, $"Remove {item.Name}?", "Removes it from Sallyport. Its programs and files are not touched; a running copy keeps running.",
            "Remove app", "Keep app");
    }

    private void RemoveApp(string id)
    {
        var name = _items.Find(id)?.Name;
        if (!_items.Remove(id)) return;
        lock (_gate)
        {
            if (_editor?.SubjectId.StartsWith(id, StringComparison.Ordinal) == true) _editor = null;
        }
        Notify(NoticeKind.Info, $"Removed {name} from Sallyport. Its programs are unchanged.");
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
            throw new ShenoraException("APP_NO_PROFILE", "id", id, $"Choose a VPN profile for {item.Name} first (Edit).");
        if (stored.Unsupported is { } why) throw new ShenoraException("PROFILE_UNSUPPORTED", "id", id, $"This version cannot connect {stored.Name}: {why}.");
        if (_runner.IsRunning) throw new ShenoraException("RUN_BUSY", "id", id, "Another app is being routed. Stop it first.");
        if (item.PacketPrograms.Count == 0) throw new ShenoraException("APP_EMPTY", "id", id, $"Include at least one of {item.Name}'s programs sending all its traffic.");

        // Through the profile's channel: the same login as its proxy, if that is connected (docs/design/window.md).
        _runner.Start(game, stored.Id, choice.Policy);
        Notify(NoticeKind.Info, $"Routing {item.Name} through {stored.Name}. If it is running already, it may need to reconnect.", new("app", id));
    }

    public Task StopRunAsync() => _runner.StopAsync();

    /// <summary>Starts an app as its card says: its packet programs routed first (and ready before it starts, so its
    /// first packet goes through the VPN), then the program Launch starts — with proxy settings, its profile connected,
    /// if that is its method. A wait that ends without the route or the tunnel up never launches later.</summary>
    public async Task LaunchAsync(string id, string? programId, CancellationToken ct)
    {
        var item = Item(id);
        var target = programId is null ? AppViews.LaunchTarget(item) : item.Programs.FirstOrDefault(p => p.Id == programId && p.Included);
        if (target is null) throw new ShenoraException("APP_NO_TARGET", "id", id, $"Choose what Launch starts for {item.Name} (Edit).");
        if (!File.Exists(target.Path)) throw new ShenoraException("APP_MISSING", "id", id, $"{Path.GetFileName(target.Path)} is not where it was added from.");
        var profileId = item.ProfileId ?? throw new ShenoraException("APP_NO_PROFILE", "id", id, $"Choose a VPN profile for {item.Name} first (Edit).");
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
            catch (ShenoraException e) { throw new ShenoraException(e.Code, "id", id, $"{item.Name} was not started. {Capital(e.Message)}"); }
            _launching[id] = AppPhase.Starting;
            Publish();
            try
            {
                if (target.Proxy is { } proxy)
                    AppLauncher.Launch(new RoutedApp(item.Id, item.Name, target.Path, proxy.Kind, profileId, target.LaunchArguments), _settings.PortOf(profileId),
                        Path.Combine(_paths.DataDir, "apps", proxy.DataKey));
                else
                    // Started as the person, from its own folder, as Explorer would: its packets are routed by its path.
                    Process.Start(new ProcessStartInfo(target.Path, target.LaunchArguments) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(target.Path)! })?.Dispose();
            }
            catch (FileNotFoundException) { throw new ShenoraException("APP_MISSING", "id", id, $"{Path.GetFileName(target.Path)} was not found where it was added from."); }
            catch (System.ComponentModel.Win32Exception e) { throw new ShenoraException("APP_FAILED", "id", id, $"Windows did not start {Path.GetFileName(target.Path)}: {e.Message}"); }
        }
        finally
        {
            _launching.TryRemove(id, out _);
            Publish();
        }
        Notify(NoticeKind.Info, target.Proxy is not null
            ? $"{Path.GetFileName(target.Path)} started with proxy settings for {profile.Name}."
            : $"{Path.GetFileName(target.Path)} started; its traffic goes through {profile.Name}.", new("app", id));
    }

    // Until the service routes the app's programs through a generation that is up: what a program then sends is the
    // VPN's from its first packet.
    private async Task WaitRoutedAsync(string id, TimeSpan within, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + within;
        while (true)
        {
            var run = _runner.Facts();
            if (run is null || run.GameId != id) throw new ShenoraException("NOT_ROUTED", "id", id, "routing did not start");
            if (run.Ended is { } why) throw new ShenoraException("NOT_ROUTED", "id", id, $"routing stopped: {why}");
            if (run.Route?.Phase == RoutePhase.Routing) return;
            if (DateTime.UtcNow > deadline) throw new ShenoraException("ROUTE_TIMEOUT", "id", id, $"its routing was not ready within {within.TotalSeconds:0} s");
            await Task.Delay(200, ct);
        }
    }

    private bool IsLive(string id) => _runner.Facts() is { Ended: null } run && run.GameId == id;

    // ---- a profile's connection settings

    public void BeginPolicyEdit(string id)
    {
        var stored = Profile(id);
        if (stored.Unsupported is not null) throw new ShenoraException("PROFILE_UNSUPPORTED", "id", id, "This version cannot connect this profile.");
        var effective = _settings.PolicyOf(id).Over(OwnPolicy(id));
        lock (_gate) _editor = new OpenEditor(NewToken(), "policy", id, GameViews.PolicyValuesOf(effective), Empty);
        Publish();
    }

    public void SavePolicy(string token, PolicyValues values)
    {
        if (EditorFor(token, "policy") is not { } editor) return;
        var own = OwnPolicy(editor.SubjectId);
        var errors = new Dictionary<string, string>();
        int? Read(string? text, string field, int least, int most, string message)
        {
            var trimmed = text?.Trim() ?? "";
            if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= least && n <= most) return n;
            errors[field] = message;
            return null;
        }
        var wait = Read(values.RetryWaitText, "retryWaitText", 1, 300, "Use 1 to 300 seconds.");
        var waitMax = Read(values.RetryWaitMaxText, "retryWaitMaxText", 1, 3600, "Use 1 to 3600 seconds.");
        var rounds = Read(values.RoundsMaxText, "roundsMaxText", 0, 1000, "Use 0 (never give up) to 1000.");
        var timeout = Read(values.AttemptTimeoutText, "attemptTimeoutText", 2, 300, "Use 2 to 300 seconds.");
        int? pingRestart = null;
        if (!string.IsNullOrWhiteSpace(values.PingRestartText)) pingRestart = Read(values.PingRestartText, "pingRestartText", 5, 600, "Use 5 to 600 seconds, or leave it empty for the server's own.");
        if (wait is { } w && waitMax is { } m && m < w) errors["retryWaitMaxText"] = "The longest wait cannot be shorter than the first.";

        lock (_gate)
        {
            if (_editor?.Token != token) return;
            if (errors.Count > 0) { _editor = _editor with { Values = values, Errors = errors }; Publish(); return; }
            // Only what differs from the profile's own is kept as a change: a later import of a profile that says otherwise
            // is then followed where the person did not choose.
            int? Differs(int value, TimeSpan theirs) => value == (int)Math.Round(theirs.TotalSeconds) ? null : value;
            _settings.PutPolicy(editor.SubjectId, new PolicyOverride(
                Differs(wait!.Value, own.RetryWait), Differs(waitMax!.Value, own.RetryWaitMax),
                rounds == (own.RoundsMax ?? 0) ? null : rounds, values.RandomOrder == own.RandomOrder ? null : values.RandomOrder,
                Differs(timeout!.Value, own.AttemptTimeout), values.RetryAfterAuthFailure == own.RetryAfterAuthFailure ? null : values.RetryAfterAuthFailure,
                pingRestart is { } r && (r != own.PingRestart || !own.KeepaliveOverridesServer) ? r : null));
            _editor = null;
        }
        Publish();
        Notify(NoticeKind.Info, "Saved. The next connection uses these settings.", new("profile", editor.SubjectId));
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
        Notify(NoticeKind.Info, "The profile's own connection settings apply again from the next connection.", new("profile", editor.SubjectId));
    }

    // The policy the profile itself carries (its OpenVPN directives): read from the stored copy, which is opened for it.
    private ConnectionPolicy OwnPolicy(string id)
    {
        try { return _store.Load(id).Policy; }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or FormatException or FileNotFoundException)
        { throw new ShenoraException("PROFILE_UNREADABLE", "id", id, "The stored profile could not be opened."); }
    }

    // ---- the packet driver

    public void RequestDriverInstall() =>
        Confirm("driver-install", "", "Install the packet driver?", GameViews.DriverInstallText, "Install", "Not now");

    public void RequestDriverRemove()
    {
        if (_runner.IsRunning) throw new ShenoraException("RUN_BUSY", "driver", "", "Stop routing the game first.");
        Confirm("driver-remove", "", "Remove the packet driver?", GameViews.DriverRemoveText, "Remove", "Keep it");
    }

    private async Task ManageDriverAsync(bool install)
    {
        if (Interlocked.CompareExchange(ref _driverWork, install ? "Installing the packet driver…" : "Removing the packet driver…", null) is not null) return;
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
            Notify(NoticeKind.Error, $"The driver's installer could not be fetched from NT Kernel ({e.GetType().Name}). Check the connection and try again.");
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
        Notify(NoticeKind.Info, "Copied the example.");
    }

    // ---- the view

    private ClientState Take()
    {
        var stored = _store.List();
        var items = _items.List();
        var run = _runner.Facts();
        var routedThrough = run is { Ended: null } ? run.ProfileId : null;
        var routedGame = routedThrough is null ? null : items.FirstOrDefault(g => g.Id == run!.GameId)?.Name ?? "an app";
        var profiles = stored.Select(p => ClientView.Profile(new ProfileFacts(
                p, _settings.PortOf(p.Id), _connections.StatusOf(p.Id), _connections.LocationOf(p.Id), _locating.ContainsKey(p.Id),
                _locationFailed.ContainsKey(p.Id), _connections.AttemptsSinceUp(p.Id), _disconnecting.ContainsKey(p.Id), _removing.ContainsKey(p.Id),
                routedThrough == p.Id ? routedGame : null, !_settings.PolicyOf(p.Id).IsEmpty)))
            .ToList();
        var driver = GameViews.Driver(Driver());
        var dialog = Volatile.Read(ref _dialog);
        var apps = items.Select(i =>
            {
                var profile = stored.FirstOrDefault(p => p.Id == i.ProfileId);
                return AppViews.Item(new ItemFacts(i, profile, run, run is { Ended: null } && run.GameId != i.Id, driver.Phase == DriverPhase.Ready,
                    dialog is not null, _launching.TryGetValue(i.Id, out var phase) ? phase : null,
                    p => profile is null || p.Proxy is not { } proxy ? []
                        : AppLauncher.Describe(new RoutedApp(i.Id, i.Name, p.Path, proxy.Kind, profile.Id, p.LaunchArguments), _settings.PortOf(profile.Id),
                            Path.Combine(_paths.DataDir, "apps", proxy.DataKey))));
            })
            .ToList();

        var guideEndpoint = GuideEndpoint(profiles);
        var guide = new ClientState.GuideView(guideEndpoint, ClientView.CurlExample(guideEndpoint), UiAction.Shown("Copy"));
        OpenEditor? editor;
        OpenConfirmation? confirmation;
        List<Notice> notices;
        NavigationView? navigation;
        lock (_gate)
        {
            editor = _editor;
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
                UiAction.Shown("Import profile…", dialog is null, "A file dialog is open.", busy: dialog == "import"),
                UiAction.Shown("Add a program…", dialog is null, "A file dialog is open.", busy: dialog == "add-program"),
                UiAction.Shown("Add a folder…", dialog is null, "A file dialog is open.", busy: dialog == "add-folder")),
            profiles,
            apps,
            ClientView.ProfilesEmpty(stored.Count),
            AppViews.Empty(stored.Count, apps.Count),
            WindowViews.Network(profiles, apps, run, stored, driver, stored.ToDictionary(p => p.Id, p => _connections.ProxyProblemOf(p.Id))),
            WindowViews.Settings(driver, startsWithWindows, guide, _paths.DataDir, InstallLayout.InstalledHere()),
            // Where a first look is useful: with nothing to route through yet, the page that imports one.
            stored.Count == 0 ? Page.Profiles : Page.Apps,
            navigation,
            [.. stored.Select(p => new ClientState.ProfileChoice(p.Id, $"{p.Name} (port {_settings.PortOf(p.Id)})", p.Unsupported is null))],
            [.. Enum.GetValues<AppKind>().Select(k => new ClientState.KindChoice(k, ClientView.KindLabel(k), ClientView.KindHelp(k)))],
            GameViews.LossChoices,
            editor is null ? null : new EditorView(editor.Token, editor.SubjectId, "editing", editor.Errors,
                // The guidance until a save is refused; then the refusal, which says it, alone.
                editor.Kind == "port" && editor.Errors.Count == 0 ? "Use a port from 1024 to 65535." :
                editor.Kind == "policy" ? PolicyHint(editor.SubjectId) : null,
                UiAction.Shown("Save"), UiAction.Shown("Cancel"),
                editor.Kind == "policy" && !_settings.PolicyOf(editor.SubjectId).IsEmpty ? UiAction.Shown("Use the profile's own") : UiAction.Hidden,
                editor.Kind, editor.Values,
                // What Launch may start: the app's included programs.
                editor.Kind == "app" && items.FirstOrDefault(i => i.Id == editor.SubjectId) is { } edited
                    ? [.. edited.Programs.Where(p => p.Included).Select(p => new EditorOption(p.Id, Path.GetFileName(p.Path)))]
                    : null),
            confirmation is null ? null : new ConfirmationView(confirmation.Token, confirmation.Title, confirmation.Text,
                UiAction.Shown(confirmation.ConfirmLabel), UiAction.Shown(confirmation.CancelLabel)),
            notices);
    }

    private string? PolicyHint(string id)
    {
        try { return GameViews.PolicyHint(OwnPolicy(id), !_settings.PolicyOf(id).IsEmpty); }
        catch (ShenoraException) { return null; }
    }

    /// <summary>The examples use a connected profile's port when there is one, so they can be copied as they are.</summary>
    private static string GuideEndpoint(IReadOnlyList<ProfileView> profiles) =>
        (profiles.FirstOrDefault(p => p.Phase == ProfilePhase.Up) ?? profiles.FirstOrDefault())?.EndpointText
        ?? ClientView.Endpoint(ClientSettings.FirstPort);

    // ---- helpers

    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

    private static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private OpenEditor? EditorFor(string token, string kind)
    {
        lock (_gate) return _editor is { } e && e.Token == token && e.Kind == kind ? e : null;
    }

    private StoredProfile Profile(string id) =>
        _store.Find(id) ?? throw new ShenoraException("PROFILE_GONE", "id", id, "That profile is no longer stored.");

    private RoutedItem Item(string id) =>
        _items.Find(id) ?? throw new ShenoraException("APP_GONE", "id", id, "That app is no longer listed.");

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
