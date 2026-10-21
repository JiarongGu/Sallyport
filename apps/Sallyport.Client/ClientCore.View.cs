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

/// <summary>The view: the whole <see cref="ClientState"/> the window is sent, taken from every store at once.</summary>
internal sealed partial class ClientCore
{
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
            WindowViews.Settings(driver, startsWithWindows, guide, _paths.DataDir, InstallLayout.InstalledHere(),
                editor is not null, _settings.Language, _settings.Connection,
                dialog is null ? null : L.T("A file dialog is open."), editor?.Kind is "export" or "import", UpdateViewNow()),
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
                        ? L.T("This version cannot connect it: {0} You can import it now.", ClientView.CannotConnect(cannot))
                    // What the file is; what each part holds and what its password does are Settings' help (the "?").
                    : editor.Kind == "import" && importing is { } read ? L.T("{0}, exported {1}.", read.Name, L.Date(read.File.Exported)) : null,
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
                // The icons it may take: an app's programs by id; one being added, the place's by path.
                editor.Kind == "app" && items.FirstOrDefault(i => i.Id == editor.SubjectId) is { } iconed
                    ? IconChoices(iconed.Programs.Select(p => (p.Id, p.Path)), (editor.Values as AppEditValues)?.IconProgramId)
                    : editor.Kind == "new-app" && setup is { } adding
                        ? IconChoices(AppSetup.ProgramsOf(adding.Preview).Select(p => (p.Path, p.Path)), (editor.Values as NewAppValues)?.IconPath)
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

    /// <summary>One choice per look — a game's programs often carry the same icon — each standing for the program that has
    /// it, <paramref name="current"/> where that is one of them, so the one chosen shows as chosen.</summary>
    private IReadOnlyList<IconChoice> IconChoices(IEnumerable<(string Id, string Path)> programs, string? current) =>
    [
        .. programs.Select(p => (p.Id, p.Path, Url: _icons.Of(p.Path))).Where(c => c.Url is not null).GroupBy(c => c.Url)
            .Select(look => look.FirstOrDefault(c => c.Id == current) is { Url: not null } chosen ? chosen : look.First())
            .Select(c => new IconChoice(c.Id, Path.GetFileName(c.Path), c.Url!)),
    ];

    /// <summary>The parts of an export, each with how much of it there is; what each holds is said in Settings' help.</summary>
    private static IReadOnlyList<EditorOption> MoveParts(int profiles, int apps, bool connection) =>
    [
        .. profiles > 0 ? [new EditorOption("profiles", L.Count(profiles, "1 VPN profile", "{0} VPN profiles"))] : Array.Empty<EditorOption>(),
        .. apps > 0 ? [new EditorOption("apps", L.Count(apps, "1 app", "{0} apps"))] : Array.Empty<EditorOption>(),
        .. connection ? [new EditorOption("connection", L.T("Connection settings"))] : Array.Empty<EditorOption>(),
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
}
