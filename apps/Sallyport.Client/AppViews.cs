using Sallyport.Runtime;
using Sallyport.Windows.Routing;

namespace Sallyport.Client;

// The Apps page's view model and its words (docs/design/window.md, step 6): one card per app — a program or a folder —
// each program with its methods, and each app in the sections of the profiles it goes through (D24). The page renders
// this and decides nothing (D15).

internal sealed record ProgramActions(UiAction Toggle, UiAction Edit, UiAction Launch, UiAction Icon);

/// <summary>What a program is doing now, in a word or two, in a tone: "Running · through jp", "Not running".</summary>
internal sealed record ActivityView(Tone Tone, string Text);

/// <summary>One program of an app, as its row shows it: what it does (<see cref="StatusText"/>: its methods, or why it is
/// left out), whether it is the one Launch starts, the flags a proxy launch gives it, and — while its app's page is
/// open — what it is doing now (<see cref="Activity"/>) with its details (<see cref="Facts"/>).</summary>
internal sealed record ProgramView(string Id, string Name, string Path, bool Included, string StatusText, bool Missing, bool LaunchTarget,
    IReadOnlyList<string> StartupSettings, ProgramActions Actions, ActivityView? Activity = null, IReadOnlyList<Fact>? Facts = null,
    string? Folder = null);

/// <summary>An app in one profile's section (D24), as the Overview's section lists its apps and the app's page lists its
/// VPNs: whether it is in it (<see cref="Member"/>) and switched on there (<see cref="On"/>), what that means now, and
/// its part of the route while that profile carries it. <see cref="Name"/> and <see cref="IconUrl"/> are the app's on the
/// Overview and the profile's on the app's page. <see cref="Vpn"/> posts SET_APP_VPN with the opposite of
/// <see cref="On"/>; <see cref="Remove"/> takes it out of the section; <see cref="Launch"/> starts it through this
/// profile's proxy.</summary>
internal sealed record AppSectionView(
    string AppId, string ProfileId, string Name, string? IconUrl, bool Member, bool On, Tone Tone, string StatusText, string? DetailText, RunView? Run,
    UiAction Vpn, UiAction Launch, UiAction Remove);

/// <summary><see cref="Locate"/>: Locate…, for an app not where it was added from (imported from another computer, its
/// game moved): posts LOCATE_APP.</summary>
internal sealed record AppItemActions(UiAction Launch, UiAction Edit, UiAction Rescan, UiAction AddProgram, UiAction Remove, UiAction? Locate = null);

/// <summary>An app's card: the VPNs it goes through (<see cref="ThroughText"/>, and on its page <see cref="Vpns"/>, one row
/// per profile with its own switch), its methods, what it is doing now (<see cref="StatusText"/>, its part of the route
/// <see cref="Run"/> while a profile carries it, <see cref="DetailText"/>: what to do for it to go through the VPN), and
/// what can be done with it. <see cref="IconUrl"/>: its program's own icon.</summary>
internal sealed record AppItemView(
    string Id, string Name, string? Folder, string ThroughText, string MethodText, string? LossText, Tone Tone, string StatusText,
    string? LaunchText, RunView? Run, string ProgramsText, IReadOnlyList<ProgramView> Programs, AppItemActions Actions, string? ProfileId = null,
    IReadOnlyList<Fact>? Setup = null, string? DetailText = null, string? IconUrl = null, IReadOnlyList<AppSectionView>? Vpns = null);

internal sealed record AppEditValues(string Name, LossMode Loss, string DirectAfterText, string? LaunchProgramId, string? IconProgramId = null);

/// <summary>An icon the app may take: one of its programs' own, by that program's id.</summary>
internal sealed record IconChoice(string Id, string Label, string Url);

internal sealed record ProgramEditValues(bool Packets, bool Proxy, AppKind Kind, string Arguments);

internal sealed record EditorOption(string Id, string Label, string? Help = null);

/// <summary>What the host knows of an app beyond the store, for <see cref="AppViews.Item"/>: every profile (its sections),
/// the route (<see cref="Run"/>), which profiles the person connected (<see cref="Connected"/>), why a profile's lane
/// ended on its own and waits for the person (<see cref="FailureOf"/>), whether it is where it was added from
/// (<see cref="Here"/>, D26).</summary>
internal sealed record ItemFacts(
    RoutedItem Item, IReadOnlyList<StoredProfile> Profiles, RunFacts? Run, Func<string, bool> Connected, Func<string, string?> FailureOf,
    bool DriverReady, bool Busy, AppPhase? Launching, Func<RoutedProgram, IReadOnlyList<string>> StartupSettings,
    Func<string, ProgramActivity?>? Activity = null, string? IconUrl = null, bool Here = true);

internal static class AppViews
{
    /// <summary>What Launch starts: the program the person chose, else the one launched with proxy settings, else the
    /// folder's program named most like the folder, else the only one.</summary>
    public static RoutedProgram? LaunchTarget(RoutedItem item)
    {
        var included = item.Programs.Where(p => p.Included).ToList();
        if (item.LaunchProgramId is { } chosen && included.FirstOrDefault(p => p.Id == chosen) is { } program) return program;
        if (item.ProxyProgram is { } proxy) return proxy;
        if (included.Count == 1) return included[0];
        if (item.Folder is null) return null;
        var folder = Simple(Path.GetFileName(Path.TrimEndingDirectorySeparator(item.Folder)));
        return included
            .Where(p => Path.GetDirectoryName(p.Path) is { } dir && string.Equals(Path.TrimEndingDirectorySeparator(dir), Path.TrimEndingDirectorySeparator(item.Folder), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => Shared(Simple(Path.GetFileNameWithoutExtension(p.Path)), folder))
            .FirstOrDefault(p => Shared(Simple(Path.GetFileNameWithoutExtension(p.Path)), folder) >= 4);

        static string Simple(string name) => new([.. name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
        static int Shared(string a, string b)
        {
            var n = 0;
            while (n < a.Length && n < b.Length && a[n] == b[n]) n++;
            return n;
        }
    }

    public static string Methods(RoutedProgram p) =>
        !p.Included ? L.T("Left out — {0}", p.Reason is { } reason ? L.T(reason) : L.T("by you"))
        : (p.Packets, p.Proxy) switch
        {
            (true, { } proxy) => L.T("All traffic · launched with proxy settings ({0})", ClientView.KindLabel(proxy.Kind)),
            (true, null) => L.T("All traffic"),
            (false, { } proxy) => L.T("Launched with proxy settings ({0})", ClientView.KindLabel(proxy.Kind)),
            _ => L.T("Included, with no method chosen"),
        };

    /// <summary>The app in one profile's section: what its switch there means now. Its route is that profile's lane; if
    /// another of its sections carries it, it says so; a profile not connected keeps it waiting.</summary>
    public static AppSectionView InSection(ItemFacts f, StoredProfile profile, bool forApp)
    {
        var item = f.Item;
        var use = item.UseOf(profile.Id);
        var member = use is not null;
        var on = use?.On == true;
        var lane = f.Run?.LaneOf(item.Id);
        var mine = lane?.ProfileId == profile.Id ? lane : null;
        var other = lane is not null && mine is null ? f.Profiles.FirstOrDefault(p => p.Id == lane.ProfileId)?.Name : null;
        var run = mine is null ? null : GameViews.Run(item, profile, f.Run!, mine);
        // On the Overview the tunnel's own facts are the section's, said once above its apps.
        if (run is not null && !forApp) run = WindowViews.InSection(run);
        var packets = item.PacketPrograms.Count > 0;
        var proxyOnly = !packets && item.ProxyProgram is not null;
        var connected = f.Connected(profile.Id);
        string? cannot =
            profile.Unsupported is not null ? L.T("This version cannot connect {0}.", profile.Name) :
            !packets && !proxyOnly ? L.T("Include at least one program sending all its traffic.") :
            null;

        var (tone, status, detail) =
            !member ? (Tone.Off, L.T("Not used"), (string?)null) :
            proxyOnly ? (Tone.Off, L.T("Launched with its proxy settings"), (string?)null) :
            !on ? (Tone.Off, L.T("Off"), null) :
            run is { } r ? (r.Tone, r.StatusText, r.DetailText) :
            other is not null ? (Tone.Info, L.T("Through {0} now", other), L.T("It goes through {0} when {1} is disconnected.", profile.Name, other)) :
            cannot is { } why ? (Tone.Trouble, L.T("Cannot use the VPN"), why) :
            !f.DriverReady ? (Tone.Trouble, L.T("Cannot use the VPN"),
                L.T("Turning on an app's VPN needs the driver and the Sallyport service. Open Settings to see what is missing.")) :
            !connected ? (Tone.Off, L.T("Waiting for {0}", profile.Name), forApp ? L.T("Connect {0} and it goes through the VPN.", profile.Name) : null) :
            f.FailureOf(profile.Id) is { } failed ? (Tone.Trouble, L.T("Not using the VPN"), L.T("{0} Turn it off and on to try again.", failed)) :
            (Tone.Busy, L.T("Connecting {0}…", profile.Name), (string?)null);

        var target = LaunchTarget(item);
        string? cannotLaunch =
            target is null ? L.T("Choose what Launch starts (Edit).") :
            !File.Exists(target.Path) ? L.T("{0} is not where it was added from.", Path.GetFileName(target.Path)) :
            profile.Unsupported is not null ? L.T("This version cannot connect {0}.", profile.Name) :
            null;
        return new AppSectionView(item.Id, profile.Id, forApp ? profile.Name : item.Name, forApp ? null : f.IconUrl, member, on, tone, status, detail, run,
            // A browser goes through a profile by being launched with its proxy: no switch, its Launch instead.
            Vpn: proxyOnly ? UiAction.Hidden : UiAction.Shown(L.T("Use VPN"), on || cannot is null, on ? null : cannot),
            Launch: proxyOnly && member ? UiAction.Shown(L.T("Launch"), cannotLaunch is null && !f.Busy && f.Launching is null, cannotLaunch, busy: f.Launching is not null) : UiAction.Hidden,
            Remove: member && !forApp ? UiAction.Shown(L.T("Remove from {0}", profile.Name)) : UiAction.Hidden);
    }

    public static AppItemView Item(ItemFacts f)
    {
        var item = f.Item;
        var packets = item.PacketPrograms;
        var proxied = item.Programs.Count(p => p.Included && p.Proxy is not null);
        var lane = f.Run?.LaneOf(item.Id);
        var carrying = lane is null ? null : f.Profiles.FirstOrDefault(p => p.Id == lane.ProfileId);
        var runView = lane is not null && carrying is not null ? GameViews.Run(item, carrying, f.Run!, lane) : null;
        var target = LaunchTarget(item);
        var launching = f.Launching is not null;
        // Sallyport starts a program only where it has to: one told of the proxy as it starts. A game or a program routed
        // by its packets is started as the person always starts it (the owner: "we dont really need start the game").
        var proxyOnly = packets.Count == 0 && proxied > 0;
        var routedNow = lane?.Phase == RoutePhase.Routing;
        var sections = f.Profiles.Where(p => item.UseOf(p.Id) is not null).ToList();
        // Its VPNs on its page: those it is in first, then every other profile it could be put in.
        var vpns = sections.Concat(f.Profiles.Where(p => p.Unsupported is null && !sections.Contains(p))).Select(p => InSection(f, p, forApp: true)).ToList();
        var waiting = vpns.FirstOrDefault(v => v is { On: true, Run: null });

        var methodText = (packets.Count, proxied) switch
        {
            (0, 0) => L.T("No program uses the VPN yet"),
            (> 0, 0) => L.Count(packets.Count, "All traffic of 1 program", "All traffic of {0} programs"),
            (0, > 0) => L.Count(proxied, "Launched with proxy settings", "{0} programs launched with proxy settings"),
            _ => L.T("{0} · {1} launched with proxy settings", L.Count(packets.Count, "All traffic of 1 program", "All traffic of {0} programs"), proxied),
        };
        string? cannotLaunch =
            target is null ? L.T("Choose what Launch starts (Edit).") :
            !File.Exists(target.Path) ? L.T("{0} is not where it was added from.", Path.GetFileName(target.Path)) :
            sections.Count == 0 ? L.T("Add it to a VPN first, on its page or on Overview.") :
            null;

        // What it is doing now, from the section that carries it, or the first whose switch is on.
        var here = f.Here;
        var (tone, status, detail) =
            !here ? (Tone.Trouble, L.T("Not found on this computer"),
                L.T("It was in {0}. Choose Locate… and show Sallyport where it is now.", item.Folder ?? Path.GetDirectoryName(item.Programs.FirstOrDefault()?.Path ?? "") ?? "")) :
            f.Launching is AppPhase.Connecting ? (Tone.Busy, L.T("Connecting before launch…"), (string?)null) :
            f.Launching is AppPhase.Starting ? (Tone.Busy, L.T("Starting…"), null) :
            runView is { } r ? (r.Tone, r.StatusText, (string?)null) :
            item.Programs.Count(p => p.Included) == 0 ? (Tone.Off, L.T("No program included"), null) :
            sections.Count == 0 ? (Tone.Off, L.T("In no VPN yet"), L.T("Turn on Use VPN for a profile below, or add it to a VPN on Overview.")) :
            proxyOnly ? (Tone.Off, L.T("Ready to launch"), null) :
            waiting is { } w ? (w.Tone, w.StatusText, w.DetailText) :
            (Tone.Off, L.T("VPN off"), null);

        // Those that go through the VPN first, as the person reads down to what matters; the store's order otherwise.
        var icon = IconProgram(item);
        var programs = item.Programs.OrderByDescending(p => p.Included).Select(p => new ProgramView(p.Id, Path.GetFileName(p.Path), p.Path, p.Included, Methods(p), !File.Exists(p.Path),
                proxyOnly && target?.Id == p.Id, p.Included && p.Proxy is not null ? f.StartupSettings(p) : [],
                new ProgramActions(
                    Toggle: UiAction.Shown(p.Included ? L.T("Leave out") : L.T("Include"), !launching),
                    // A program left out has no methods to choose until it is included again.
                    Edit: p.Included ? UiAction.Shown(L.T("Methods…"), !launching) : UiAction.Hidden,
                    // Its own Launch for a program started with proxy settings (a browser in a game's app), but the one the
                    // app's own Launch starts; a folder's helpers are started by the game, not by the person.
                    Launch: p.Included && p.Proxy is not null && !(proxyOnly && p.Id == target?.Id)
                        ? UiAction.Shown(L.T("Launch"), cannotLaunchFor(p) is null && !launching, cannotLaunchFor(p)) : UiAction.Hidden,
                    Icon: icon?.Id == p.Id || !File.Exists(p.Path) ? UiAction.Hidden : UiAction.Shown(L.T("Use its icon for the app"))),
                Activity(p), ProgramFacts(p), Under(p)))
            .ToList();

        // Where it sits under the app's folder, when it is not at the top of it.
        string? Under(RoutedProgram p) =>
            item.Folder is { } top && Path.GetDirectoryName(p.Path) is { } dir && Path.GetRelativePath(top, dir) is var where && where != "." && !where.StartsWith("..", StringComparison.Ordinal)
                ? where : null;

        // Where its traffic goes now, and whether it is running, while its app's page is open; never a guess.
        ActivityView? Activity(RoutedProgram p)
        {
            var way = !p.Included ? L.T("your own connection")
                : p.Packets && routedNow && carrying is not null ? L.T("through {0}", carrying.Name)
                : p.Packets && waiting is not null ? L.T("through the VPN once {0} is connected", f.Profiles.First(x => x.Id == waiting.ProfileId).Name)
                : p.Packets ? L.T("through the VPN when Use VPN is on")
                : L.T("through the VPN when launched from here");
            if (f.Activity?.Invoke(p.Path) is not { } seen) return null;
            if (seen.Processes == 0) return new(Tone.Off, L.T("Not running"));
            var running = seen.Processes == 1 ? L.T("Running") : L.T("Running · {0} processes", seen.Processes);
            return new(p.Included && p.Packets && routedNow ? Tone.Up : Tone.Info, L.T("{0} · {1}", running, way));
        }

        IReadOnlyList<Fact> ProgramFacts(RoutedProgram p)
        {
            var facts = new List<Fact> { new(L.T("How"), Methods(p)) };
            if (f.Activity?.Invoke(p.Path) is { Processes: > 0 } seen)
                facts.Add(new(L.T("Connections now"), L.T("{0} TCP · {1} UDP", seen.Tcp, seen.Udp)));
            // The router's own count, by the executable it found each process running: what went through the VPN.
            if (lane is not null && f.Run?.Route?.Programs?.FirstOrDefault(r => string.Equals(r.Path, p.Path, StringComparison.OrdinalIgnoreCase)) is { } routed)
                facts.Add(new(L.T("Through the VPN"), L.T("{0} · {1}", L.Count(routed.Flows, "1 connection", "{0} connections"),
                    L.Count(routed.Packets, "1 packet sent", "{0} packets sent"))));
            else if (p.Included && p.Packets && routedNow)
                facts.Add(new(L.T("Through the VPN"), L.T("Nothing yet")));
            return facts;
        }

        string? cannotLaunchFor(RoutedProgram p) =>
            !File.Exists(p.Path) ? L.T("{0} is not where it was added from.", Path.GetFileName(p.Path)) :
            sections.Count == 0 ? L.T("Add it to a VPN first, on its page or on Overview.") : null;

        var included = item.Programs.Count(p => p.Included);
        var programsText = item.Folder is null && item.Programs.Count == 1 ? "" :
            L.T("Using the VPN: {0} of {1}.", L.Count(included, "1 program", "{0} programs"), item.Programs.Count);
        var through = sections.Count == 0 ? L.T("In no VPN yet") : L.T("Through {0}", L.List(sections.Select(p => p.Name)));
        return new AppItemView(item.Id, item.Name, item.Folder, through, methodText,
            item.RoutesPackets ? GameViews.LossText(item.Choice) : null, tone, status,
            proxyOnly && target is not null ? L.T("Launch starts {0}", Path.GetFileName(target.Path)) : null, runView, programsText, programs,
            new AppItemActions(
                Launch: proxyOnly ? UiAction.Shown(L.T("Launch"), cannotLaunch is null && !f.Busy, cannotLaunch, busy: launching) : UiAction.Hidden,
                Edit: UiAction.Shown(L.T("Edit"), !launching, L.T("Wait for the launch to finish.")),
                Rescan: item.Folder is null ? UiAction.Hidden : UiAction.Shown(L.T("Scan folder again"), !launching, L.T("Wait for the launch to finish.")),
                AddProgram: UiAction.Shown(L.T("Add a program…"), !launching && !f.Busy, L.T("A file dialog is open.")),
                Remove: UiAction.Shown(L.T("Remove app"), !launching, L.T("Wait for the launch to finish.")),
                Locate: here ? UiAction.Hidden : UiAction.Shown(L.T("Locate…"), !f.Busy, L.T("A file dialog is open."))),
            carrying?.Id ?? sections.FirstOrDefault()?.Id,
            // Its page's setup, label by label, where the card has one line of it; its VPNs are a section of their own.
            [
                new(L.T("Uses the VPN"), methodText),
                .. item.RoutesPackets ? [new Fact(L.T("If the VPN drops"), GameViews.LossShort(item.Choice))] : Array.Empty<Fact>(),
                .. proxyOnly && target is not null ? [new Fact(L.T("Launch starts"), Path.GetFileName(target.Path))] : Array.Empty<Fact>(),
            ],
            detail, f.IconUrl, vpns);
    }

    /// <summary>The program whose icon is the app's: the one the person chose, else what Launch starts, else the first of
    /// those it includes.</summary>
    public static RoutedProgram? IconProgram(RoutedItem item) =>
        (item.IconProgramId is { } chosen ? item.Programs.FirstOrDefault(p => p.Id == chosen) : null)
        ?? LaunchTarget(item) ?? item.Programs.FirstOrDefault(p => p.Included) ?? item.Programs.FirstOrDefault();

    public static EmptyView? Empty(int profiles, int apps) =>
        apps > 0 ? null :
        profiles == 0 ? new(L.T("No apps yet"), L.T("Import a VPN profile first, then add your game or program."), "IMPORT") :
        new(L.T("No apps yet"), L.T("Add a game or a program: choose its folder or its .exe, see what is in it, then turn it on. Only what you add goes through your VPN."), "ADD_APP");
}
