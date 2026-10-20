using Sallyport.Runtime;
using Sallyport.Windows.Routing;

namespace Sallyport.Client;

// The Apps page's view model and its words (docs/design/window.md, step 6): one card per app — a program or a folder —
// each program with its methods. The page renders this and decides nothing (D15).

internal sealed record ProgramActions(UiAction Toggle, UiAction Edit, UiAction Launch);

/// <summary>What a program is doing now, in a word or two, in a tone: "Running · through jp", "Not running".</summary>
internal sealed record ActivityView(Tone Tone, string Text);

/// <summary>One program of an app, as its row shows it: what it does (<see cref="StatusText"/>: its methods, or why it is
/// left out), whether it is the one Launch starts, the flags a proxy launch gives it, and — while its app's page is
/// open — what it is doing now (<see cref="Activity"/>) with its details (<see cref="Facts"/>).</summary>
internal sealed record ProgramView(string Id, string Name, string Path, bool Included, string StatusText, bool Missing, bool LaunchTarget,
    IReadOnlyList<string> StartupSettings, ProgramActions Actions, ActivityView? Activity = null, IReadOnlyList<Fact>? Facts = null,
    string? Folder = null);

internal sealed record AppItemActions(UiAction Route, UiAction Stop, UiAction Launch, UiAction Edit, UiAction Rescan, UiAction AddProgram, UiAction Remove);

/// <summary>An app's card: its profile and methods, its routing now (<see cref="Run"/>), and what can be done with it.</summary>
internal sealed record AppItemView(
    string Id, string Name, string? Folder, string ThroughText, string MethodText, string? LossText, Tone Tone, string StatusText,
    string? LaunchText, RunView? Run, string ProgramsText, IReadOnlyList<ProgramView> Programs, AppItemActions Actions, string? ProfileId = null,
    IReadOnlyList<Fact>? Setup = null);

internal sealed record AppEditValues(string Name, string? ProfileId, LossMode Loss, string DirectAfterText, string? LaunchProgramId);

internal sealed record ProgramEditValues(bool Packets, bool Proxy, AppKind Kind, string Arguments);

internal sealed record EditorOption(string Id, string Label, string? Help = null);

/// <summary>What the host knows of an app beyond the store, for <see cref="AppViews.Item"/>.</summary>
internal sealed record ItemFacts(
    RoutedItem Item, StoredProfile? Profile, RunFacts? Run, bool AnotherRunning, bool DriverReady, bool Busy, AppPhase? Launching,
    Func<RoutedProgram, IReadOnlyList<string>> StartupSettings, Func<string, ProgramActivity?>? Activity = null);

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

    public static AppItemView Item(ItemFacts f)
    {
        var item = f.Item;
        var profile = f.Profile;
        var packets = item.PacketPrograms;
        var proxied = item.Programs.Count(p => p.Included && p.Proxy is not null);
        var mine = f.Run?.GameId == item.Id ? f.Run : null;
        var live = mine is { Ended: null };
        var runView = mine is null ? null : GameViews.Run(item.AsGame(), profile, mine);
        var target = LaunchTarget(item);
        var launching = f.Launching is not null;
        // Sallyport starts a program only where it has to: one told of the proxy as it starts. A game or a program routed
        // by its packets is started as the person always starts it (the owner: "we dont really need start the game").
        var proxyOnly = packets.Count == 0 && proxied > 0;
        var routedNow = live && mine?.Route is not null;

        var methodText = (packets.Count, proxied) switch
        {
            (0, 0) => L.T("No program uses the VPN yet"),
            (> 0, 0) => L.Count(packets.Count, "All traffic of 1 program", "All traffic of {0} programs"),
            (0, > 0) => L.Count(proxied, "Launched with proxy settings", "{0} programs launched with proxy settings"),
            _ => L.T("{0} · {1} launched with proxy settings", L.Count(packets.Count, "All traffic of 1 program", "All traffic of {0} programs"), proxied),
        };

        string? cannotRoute =
            f.AnotherRunning ? L.T("Another app is using the VPN. Turn its VPN off first.") :
            profile is null ? L.T("Choose a VPN profile for it first (Edit).") :
            profile.Unsupported is not null ? L.T("This version cannot connect its VPN profile.") :
            packets.Count == 0 ? L.T("Include at least one program sending all its traffic.") :
            !f.DriverReady ? L.T("Turning on an app's VPN needs the driver and the Sallyport service. Open Settings to see what is missing.") :
            null;
        string? cannotLaunch =
            target is null ? L.T("Choose what Launch starts (Edit).") :
            !File.Exists(target.Path) ? L.T("{0} is not where it was added from.", Path.GetFileName(target.Path)) :
            profile is null ? L.T("Choose a VPN profile for it first (Edit).") :
            profile.Unsupported is not null ? L.T("This version cannot connect its VPN profile.") :
            packets.Count > 0 && !live ? cannotRoute :
            null;

        var (tone, status) =
            f.Launching is AppPhase.Connecting ? (Tone.Busy, packets.Count > 0 && !live ? L.T("Turning on its VPN…") : L.T("Connecting {0} before launch…", profile?.Name)) :
            f.Launching is AppPhase.Starting ? (Tone.Busy, L.T("Starting…")) :
            runView is { } r ? (r.Tone, r.StatusText) :
            profile is null ? (Tone.Off, L.T("Choose a VPN profile")) :
            item.Programs.Count(p => p.Included) == 0 ? (Tone.Off, L.T("No program included")) :
            packets.Count > 0 ? (Tone.Off, L.T("VPN off")) :
            (Tone.Off, L.T("Ready to launch"));

        // Those that go through the VPN first, as the person reads down to what matters; the store's order otherwise.
        var programs = item.Programs.OrderByDescending(p => p.Included).Select(p => new ProgramView(p.Id, Path.GetFileName(p.Path), p.Path, p.Included, Methods(p), !File.Exists(p.Path),
                proxyOnly && target?.Id == p.Id, p.Included && p.Proxy is not null ? f.StartupSettings(p) : [],
                new ProgramActions(
                    Toggle: UiAction.Shown(p.Included ? L.T("Leave out") : L.T("Include"), !live && !launching, L.T("Turn its VPN off before changing its programs.")),
                    // A program left out has no methods to choose until it is included again.
                    Edit: p.Included ? UiAction.Shown(L.T("Methods…"), !live && !launching, L.T("Turn its VPN off before changing its methods.")) : UiAction.Hidden,
                    // Its own Launch for a program started with proxy settings (a browser in a game's app), but the one the
                    // app's own Launch starts; a folder's helpers are started by the game, not by the person.
                    Launch: p.Included && p.Proxy is not null && !(proxyOnly && p.Id == target?.Id)
                        ? UiAction.Shown(L.T("Launch"), cannotLaunchFor(p) is null && !launching, cannotLaunchFor(p)) : UiAction.Hidden),
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
                : p.Packets && routedNow && profile is not null ? L.T("through {0}", profile.Name)
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
            if (mine?.Route?.Programs?.FirstOrDefault(r => string.Equals(r.Path, p.Path, StringComparison.OrdinalIgnoreCase)) is { } routed)
                facts.Add(new(L.T("Through the VPN"), L.T("{0} · {1}", L.Count(routed.Flows, "1 connection", "{0} connections"),
                    L.Count(routed.Packets, "1 packet sent", "{0} packets sent"))));
            else if (p.Included && p.Packets && routedNow)
                facts.Add(new(L.T("Through the VPN"), L.T("Nothing yet")));
            return facts;
        }

        string? cannotLaunchFor(RoutedProgram p) =>
            !File.Exists(p.Path) ? L.T("{0} is not where it was added from.", Path.GetFileName(p.Path)) :
            profile is null ? L.T("Choose a VPN profile for it first (Edit).") :
            packets.Count > 0 && !live ? cannotRoute : null;

        var included = item.Programs.Count(p => p.Included);
        var programsText = item.Folder is null && item.Programs.Count == 1 ? "" :
            L.T(live ? "Using the VPN: {0} of {1}. To change them, turn its VPN off first." : "Using the VPN: {0} of {1}.",
                L.Count(included, "1 program", "{0} programs"), item.Programs.Count);
        return new AppItemView(item.Id, item.Name, item.Folder, profile is null ? L.T("No VPN profile chosen") : L.T("Through {0}", profile.Name), methodText,
            item.RoutesPackets ? GameViews.LossText(item.Choice) : null, tone, status,
            proxyOnly && target is not null ? L.T("Launch starts {0}", Path.GetFileName(target.Path)) : null, runView, programsText, programs,
            new AppItemActions(
                Route: !item.RoutesPackets || live ? UiAction.Hidden
                    : UiAction.Shown(mine is { Ended: not null } ? L.T("Turn on VPN again") : L.T("Turn on VPN"), cannotRoute is null && !f.Busy && !launching, cannotRoute),
                Stop: live ? UiAction.Shown(L.T("Turn off VPN")) : UiAction.Hidden,
                Launch: proxyOnly ? UiAction.Shown(L.T("Launch"), cannotLaunch is null && !f.Busy, cannotLaunch, busy: launching) : UiAction.Hidden,
                Edit: UiAction.Shown(L.T("Edit"), !live && !launching, L.T("Turn its VPN off before changing it.")),
                Rescan: item.Folder is null ? UiAction.Hidden : UiAction.Shown(L.T("Scan folder again"), !live && !launching, L.T("Turn its VPN off before scanning again.")),
                AddProgram: UiAction.Shown(L.T("Add a program…"), !live && !launching && !f.Busy,
                    live ? L.T("Turn its VPN off before changing its programs.") : L.T("A file dialog is open.")),
                Remove: UiAction.Shown(L.T("Remove app"), !live && !launching, L.T("Turn its VPN off before removing it."))),
            profile?.Id,
            // Its page's setup, label by label, where the card has one line of it.
            [
                new(L.T("VPN profile"), profile?.Name ?? L.T("None chosen")),
                new(L.T("Uses the VPN"), methodText),
                .. item.RoutesPackets ? [new Fact(L.T("If the VPN drops"), GameViews.LossShort(item.Choice))] : Array.Empty<Fact>(),
                .. proxyOnly && target is not null ? [new Fact(L.T("Launch starts"), Path.GetFileName(target.Path))] : Array.Empty<Fact>(),
            ]);
    }

    public static EmptyView? Empty(int profiles, int apps) =>
        apps > 0 ? null :
        profiles == 0 ? new(L.T("No apps yet"), L.T("Import a VPN profile first, then add your game or program."), "IMPORT") :
        new(L.T("No apps yet"), L.T("Add a game or a program: choose its folder or its .exe, see what is in it, then turn it on. Only what you add goes through your VPN."), "ADD_APP");
}
