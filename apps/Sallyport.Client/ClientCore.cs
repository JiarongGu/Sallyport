using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Sallyport.Runtime;
using Shenora;
using Shenora.Core.Events;
using Shenora.Core.Ipc;
using Shenora.Modules.FileDialog;

namespace Sallyport.Client;

/// <summary>
/// What the window and the tray both drive, and the only author of what the window shows: the page is sent
/// <c>CLIENT.STATE</c> (<see cref="ClientState"/>) whenever anything changes and <c>CLIENT.NOTICE</c> for each outcome,
/// and sends back intents that answer nothing. An edit and a confirmation are the host's too, so the page never decides
/// what may be saved or removed. It never sees a profile's text (D14).
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
    private long _sequence;
    private long _noticeIds;

    private sealed record OpenEditor(string Token, string Kind, string SubjectId, object Values, IReadOnlyDictionary<string, string> Errors);

    private sealed record OpenConfirmation(string Token, string ProfileId, string Title, string Text);

    /// <summary>Raised after every published state, on whichever thread published it.</summary>
    public event Action? Changed;

    /// <summary>Raised with every notice, so the tray can say it while the window is hidden.</summary>
    public event Action<Notice>? Noticed;

    public ClientCore(ProfileStore store, ClientSettings settings, Connections connections, ShenoraPaths paths, IEventBus events, IFileDialogs dialogs)
    {
        _store = store;
        _settings = settings;
        _connections = connections;
        _paths = paths;
        _events = events;
        _dialogs = dialogs;
        _connections.Changed += Publish;
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

    public async Task DisconnectAsync(string id)
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
        var shortcuts = _settings.Apps.Count(a => a.ProfileId == id);
        var text = "Deletes Sallyport's encrypted copy. The original file stays unchanged." +
            (shortcuts == 0 ? "" : shortcuts == 1 ? " 1 app shortcut will need another profile." : $" {shortcuts} app shortcuts will need another profile.");
        lock (_gate) _confirmation = new OpenConfirmation(NewToken(), id, $"Remove {profile.Name}?", text);
        Publish();
    }

    public async Task ConfirmRemoveProfileAsync(string token)
    {
        OpenConfirmation? confirmation;
        lock (_gate)
        {
            confirmation = _confirmation?.Token == token ? _confirmation : null;
            _confirmation = null;
        }
        if (confirmation is null) return;
        var id = confirmation.ProfileId;
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

    // ---- apps

    public async Task AddAppFromDialogAsync()
    {
        var picked = await WithDialogAsync("add-app", () => _dialogs.OpenFileAsync(new OpenFileOptions
        {
            Title = "Choose the app to launch through Sallyport",
            Filters = [new FileDialogFilter { Name = "Programs", Extensions = ["exe"] }],
            RememberPathKey = "add-app",
        }));
        if (picked is not { Success: true, FilePath: { } path }) return;

        var info = FileVersionInfo.GetVersionInfo(path);
        var name = new[] { info.FileDescription, info.ProductName, Path.GetFileNameWithoutExtension(path) }
            .First(n => !string.IsNullOrWhiteSpace(n))!.Trim();
        if (name.Length > LongestName) name = name[..LongestName];
        var app = new RoutedApp(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)), name, path, AppLauncher.Guess(path),
            _store.List().FirstOrDefault(p => p.Unsupported is null)?.Id ?? "", "");
        _settings.PutApp(app);
        Notify(NoticeKind.Info, $"Added {app.Name} as a {ClientView.KindLabel(app.Kind).ToLowerInvariant()}.", new("app", app.Id));
    }

    public void BeginAppEdit(string id)
    {
        var app = App(id);
        if (_launching.ContainsKey(id)) throw new ShenoraException("LAUNCHING", "id", id, "Wait for the launch to finish.");
        lock (_gate)
            _editor = new OpenEditor(NewToken(), "app", id,
                new AppValues(app.Name, _store.Find(app.ProfileId) is null ? null : app.ProfileId, app.Kind, app.Arguments), Empty);
        Publish();
    }

    public void UpdateApp(string token, AppValues values)
    {
        if (EditorFor(token, "app") is not { } editor) return;
        var app = App(editor.SubjectId);
        var name = values.Name?.Trim() ?? "";
        var arguments = values.Arguments?.Trim() ?? "";
        var errors = new Dictionary<string, string>();
        if (name.Length is 0 or > LongestName) errors["name"] = $"Use 1 to {LongestName} characters.";
        if (values.ProfileId is null || _store.Find(values.ProfileId) is null) errors["profileId"] = "Choose a profile.";
        if (!Enum.IsDefined(values.Kind)) errors["kind"] = "Choose how the app is told about the proxy.";
        if (arguments.Length > LongestArguments) errors["arguments"] = $"Use at most {LongestArguments:N0} characters.";
        if (_launching.ContainsKey(app.Id)) errors["form"] = "Wait for the launch to finish.";

        lock (_gate)
        {
            if (_editor?.Token != token) return;
            if (errors.Count > 0) { _editor = _editor with { Values = values, Errors = errors }; }
            else
            {
                _settings.PutApp(app with { Name = name, ProfileId = values.ProfileId!, Kind = values.Kind, Arguments = arguments });
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

    public void RemoveApp(string id)
    {
        var app = App(id);
        if (_launching.ContainsKey(id)) throw new ShenoraException("LAUNCHING", "id", id, "Wait for the launch to finish.");
        if (!_settings.RemoveApp(id)) return;
        lock (_gate)
        {
            if (_editor?.SubjectId == id) _editor = null;
        }
        Notify(NoticeKind.Info, $"Removed the shortcut to {app.Name}. A running copy keeps running.");
    }

    /// <summary>Connects the app's profile if it is not, waits for it to be up, then starts the app through it. A wait
    /// that ends without the tunnel up never launches later.</summary>
    public async Task LaunchAsync(string id, CancellationToken ct)
    {
        var app = App(id);
        var profile = _store.Find(app.ProfileId) ?? throw new ShenoraException("APP_NO_PROFILE", "id", id, $"Choose a profile for {app.Name} before launching.");
        if (!_launching.TryAdd(id, AppPhase.Connecting)) return;
        Publish();
        try
        {
            try
            {
                Connect(app.ProfileId);
                await _connections.WaitUpAsync(app.ProfileId, TimeSpan.FromSeconds(45), ct);
            }
            catch (ShenoraException e) { throw new ShenoraException(e.Code, "id", id, $"{app.Name} was not started. {Capital(e.Message)}"); }
            _launching[id] = AppPhase.Starting;
            Publish();
            try { AppLauncher.Launch(app, _settings.PortOf(app.ProfileId), OwnProfile(app)); }
            catch (FileNotFoundException) { throw new ShenoraException("APP_MISSING", "id", id, $"{app.Name} was not found where it was added from."); }
            catch (System.ComponentModel.Win32Exception e) { throw new ShenoraException("APP_FAILED", "id", id, $"Windows did not start {app.Name}: {e.Message}"); }
        }
        finally
        {
            _launching.TryRemove(id, out _);
            Publish();
        }
        Notify(NoticeKind.Info, $"{app.Name} started with proxy settings for {profile.Name}.", new("app", id));
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
        var profiles = stored.Select(p => ClientView.Profile(new ProfileFacts(
                p, _settings.PortOf(p.Id), _connections.StatusOf(p.Id), _connections.LocationOf(p.Id), _locating.ContainsKey(p.Id),
                _locationFailed.ContainsKey(p.Id), _connections.AttemptsSinceUp(p.Id), _disconnecting.ContainsKey(p.Id), _removing.ContainsKey(p.Id))))
            .ToList();
        var apps = _settings.Apps.Select(a =>
            {
                var profile = stored.FirstOrDefault(p => p.Id == a.ProfileId);
                var settings = profile is null ? [] : AppLauncher.Describe(a, _settings.PortOf(profile.Id), OwnProfile(a));
                return ClientView.App(a, profile, File.Exists(a.Path), settings, _launching.TryGetValue(a.Id, out var phase) ? phase : null);
            })
            .ToList();

        var dialog = Volatile.Read(ref _dialog);
        var guideEndpoint = GuideEndpoint(profiles);
        OpenEditor? editor;
        OpenConfirmation? confirmation;
        List<Notice> notices;
        lock (_gate)
        {
            editor = _editor;
            confirmation = _confirmation;
            notices = [.. _notices];
        }

        return new ClientState(
            ++_sequence,
            ClientView.Summary(profiles),
            new ClientState.TopActions(
                UiAction.Shown("Import profile…", dialog is null, "A file dialog is open.", busy: dialog == "import"),
                UiAction.Shown("Add app…", dialog is null && stored.Count > 0, stored.Count == 0 ? "Import a profile first." : "A file dialog is open.",
                    busy: dialog == "add-app")),
            profiles,
            apps,
            ClientView.ProfilesEmpty(stored.Count),
            ClientView.AppsEmpty(stored.Count, apps.Count),
            ClientView.AppsNote,
            [.. stored.Select(p => new ClientState.ProfileChoice(p.Id, $"{p.Name} (port {_settings.PortOf(p.Id)})", p.Unsupported is null))],
            [.. Enum.GetValues<AppKind>().Select(k => new ClientState.KindChoice(k, ClientView.KindLabel(k), ClientView.KindHelp(k)))],
            editor is null ? null : new EditorView(editor.Token, editor.SubjectId, "editing", editor.Errors,
                // The guidance until a save is refused; then the refusal, which says it, alone.
                editor.Kind == "port" && editor.Errors.Count == 0 ? "Use a port from 1024 to 65535." : null,
                UiAction.Shown("Save"), UiAction.Shown("Cancel"), editor.Kind, editor.Values),
            confirmation is null ? null : new ConfirmationView(confirmation.Token, confirmation.Title, confirmation.Text,
                UiAction.Shown("Remove profile"), UiAction.Shown("Keep profile")),
            new ClientState.GuideView(guideEndpoint, ClientView.CurlExample(guideEndpoint), UiAction.Shown("Copy")),
            notices);
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

    private RoutedApp App(string id) =>
        _settings.FindApp(id) ?? throw new ShenoraException("APP_GONE", "id", id, "That app is no longer listed.");

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

    /// <summary>Where a browser started through Sallyport keeps its own profile, apart from the person's usual one.</summary>
    private string OwnProfile(RoutedApp app) => Path.Combine(_paths.DataDir, "apps", app.Id);
}
