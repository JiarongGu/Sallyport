using System.Diagnostics;
using Sallyport.Windows;

namespace Sallyport.Client;

internal enum UpdatePhase { Unchecked, Checking, UpToDate, Available, Downloading, Starting, Failed, Unavailable }

/// <summary>Settings' About: what is known of a newer Sallyport and what can be done about it (D34). <see cref="Check"/>
/// posts CHECK_UPDATE, <see cref="Apply"/> APPLY_UPDATE; <see cref="Done"/>, how far a download is, 0 to 1.</summary>
internal sealed record UpdateView(UpdatePhase Phase, string StatusText, UiAction Check, UiAction Apply, double? Done = null);

/// <summary>
/// The update (D34): looked for on asking and once a day on its own, a newer release fetched and checked against its
/// manifest, and its setup run as a quiet upgrade, which closes this window and opens the new one. All of it as the
/// person: only setup asks to be administrator, as it always does. Only an installed copy updates.
/// </summary>
internal sealed partial class ClientCore
{
    private readonly object _updates = new();
    private UpdatePhase _updatePhase = UpdatePhase.Unchecked;
    private string? _updateProblem;
    private UpdateOffer? _offer;
    private double _downloaded;
    private long _downloadPublished;
    // The fetched setup, held unchangeable from its check until this program exits (setup closes it as it installs).
    private FileStream? _heldSetup;

    private static readonly TimeSpan CheckEvery = TimeSpan.FromDays(1);

    /// <summary>What the last start of an update came to, said once on the next start; and a look for a newer one if the
    /// last was a day ago or more, after the window has had its start.</summary>
    private void StartUpdates()
    {
        if (InstallLayout.InstalledHere() is null) return;
        if (_settings.UpdatingTo is { } wanted)
        {
            _settings.UpdatingTo = null;
            var now = UpdateVersion.Parse(WindowViews.Version);
            if (now is not null && UpdateVersion.Parse(wanted) is { } target && now >= target) Notify(NoticeKind.Info, L.T("Sallyport is updated to {0}.", wanted), stays: true);
            else Notify(NoticeKind.Warning, L.T("The update to {0} did not finish: this is still {1}. Setup's log is in Sallyport's data folder, under logs.", wanted, WindowViews.Version));
        }
        // What an earlier update fetched is not needed once it has run, or not run.
        try { foreach (var left in Directory.EnumerateFiles(UpdateFolder)) File.Delete(left); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (_settings.UpdatesCheckedAt is { } at && DateTimeOffset.Now - at < CheckEvery) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(20));
            await CheckUpdateAsync(quiet: true);
        });
    }

    private string UpdateFolder => _paths.DataArea(Path.Combine("cache", "updates"));

    private Updater? NewUpdater(out string? why)
    {
        why = null;
        if (InstallLayout.InstalledHere() is not { } folder) { why = L.T("this is not an installed Sallyport"); return null; }
        string? file = null;
        // In data's own folder, which only an administrator can change: what it names is run as administrator.
        var named = Path.Combine(InstallLayout.Data(folder), "update.json");
        try { if (File.Exists(named)) file = File.ReadAllText(named); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { why = L.T("data\\update.json could not be read"); return null; }
        var source = UpdateSource.Resolve(Environment.GetEnvironmentVariable("SALLYPORT_UPDATE_SOURCE"), file, out why);
        return source is null ? null : new Updater(source);
    }

    /// <summary>Looks for a newer release. Quiet (once a day on its own): a newer one is said as a notice, and nothing else.</summary>
    public async Task CheckUpdateAsync(bool quiet = false)
    {
        lock (_updates)
        {
            if (_updatePhase is UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.Starting) return;
            _updatePhase = UpdatePhase.Checking;
            _updateProblem = null;
        }
        Publish();
        UpdatePhase phase;
        UpdateOffer? offer = null;
        string? problem = null;
        try
        {
            var updater = NewUpdater(out var why) ?? throw new UpdateUnavailable(why!);
            var current = UpdateVersion.Parse(WindowViews.Version) ?? throw new UpdateUnavailable(L.T("this build's version {0} is not one Sallyport reads", WindowViews.Version));
            offer = await updater.CheckAsync(current);
            phase = offer is null ? UpdatePhase.UpToDate : UpdatePhase.Available;
            _settings.UpdatesCheckedAt = DateTimeOffset.Now;
        }
        catch (UpdateUnavailable e) { phase = UpdatePhase.Unavailable; problem = e.Message; }
        lock (_updates)
        {
            _updatePhase = phase;
            _offer = offer;
            _updateProblem = problem;
        }
        if (quiet && offer is not null) Notify(NoticeKind.Info, L.T("Sallyport {0} is out: Settings → About updates it.", offer.Version.ToString(3)));
        Publish();
    }

    /// <summary>Update: asked first while apps go through a VPN — the upgrade closes Sallyport, and they go back to the
    /// person's own connection until it is open again.</summary>
    public async Task RequestUpdateAsync()
    {
        UpdateOffer? offer;
        lock (_updates) offer = _updatePhase is UpdatePhase.Available or UpdatePhase.Failed ? _offer : null;
        if (offer is null) return;
        if (_runner.Facts() is { Ended: null } run && run.Lanes.Sum(l => l.Apps.Count) > 0)
        {
            Confirm("update", offer.Version.ToString(3), L.T("Update Sallyport to {0}?", offer.Version.ToString(3)),
                L.T("Sallyport closes while it updates and opens again after. Until then the apps going through your VPN use your own connection."),
                L.T("Update"), L.T("Not now"));
            return;
        }
        await ApplyUpdateAsync();
    }

    /// <summary>The offer's setup fetched, checked and held, then started as a quiet upgrade that opens Sallyport again
    /// (<c>/quiet --start-after</c>): setup asks to be administrator, closes this window, and swaps the folder (D30).</summary>
    public async Task ApplyUpdateAsync()
    {
        UpdateOffer? offer;
        lock (_updates)
        {
            // Again after a fetch that failed, as well as the first time.
            offer = _updatePhase is UpdatePhase.Available or UpdatePhase.Failed ? _offer : null;
            if (offer is null) return;
            _updatePhase = UpdatePhase.Downloading;
            _downloaded = 0;
            _updateProblem = null;
        }
        Publish();
        try
        {
            var updater = NewUpdater(out var why) ?? throw new UpdateUnavailable(why!);
            var progress = new Progress<double>(done =>
            {
                _downloaded = done;
                // A few times a second is what a person reading it needs.
                var now = Environment.TickCount64;
                if (now - Interlocked.Read(ref _downloadPublished) < 250) return;
                Interlocked.Exchange(ref _downloadPublished, now);
                Publish();
            });
            var held = await updater.DownloadAsync(offer, UpdateFolder, progress);
            lock (_updates)
            {
                _heldSetup?.Dispose();
                _heldSetup = held;
                _updatePhase = UpdatePhase.Starting;
            }
            Publish();
            _settings.UpdatingTo = offer.Version.ToString(3);
            // Through the shell, as a person starts it: setup asks to be administrator itself.
            try { Process.Start(new ProcessStartInfo(held.Name, "/quiet --start-after") { UseShellExecute = true })?.Dispose(); }
            catch (System.ComponentModel.Win32Exception e)
            {
                _settings.UpdatingTo = null;
                throw new UpdateUnavailable(e.NativeErrorCode == 1223 ? L.T("setup was not allowed to run as administrator") : e.Message);
            }
        }
        // Whatever stopped it, the update is not under way any more and says why: left as "Downloading", it held Settings'
        // buttons until Sallyport was started again.
        catch (Exception e)
        {
            lock (_updates)
            {
                _updatePhase = UpdatePhase.Failed;
                _updateProblem = e is UpdateUnavailable or InvalidDataException or IOException or UnauthorizedAccessException ? e.Message : e.GetType().Name;
                _heldSetup?.Dispose();
                _heldSetup = null;
            }
            Publish();
        }
    }

    private UpdateView? UpdateViewNow()
    {
        if (InstallLayout.InstalledHere() is null) return null;
        UpdatePhase phase;
        UpdateOffer? offer;
        string? problem;
        double done;
        lock (_updates) (phase, offer, problem, done) = (_updatePhase, _offer, _updateProblem, _downloaded);
        var checkedAt = _settings.UpdatesCheckedAt;
        var version = offer?.Version.ToString(3) ?? "";
        var text = phase switch
        {
            UpdatePhase.Checking => L.T("Looking for a newer Sallyport…"),
            UpdatePhase.UpToDate => L.T("This is the newest Sallyport."),
            UpdatePhase.Available => L.T("Sallyport {0} is out.", version),
            UpdatePhase.Downloading => L.T("Downloading Sallyport {0}… {1}%", version, (int)Math.Round(done * 100)),
            UpdatePhase.Starting => L.T("Updating: Sallyport closes, and opens again once it is done."),
            // Two different things, never one (rule 6): what failed, and what could not be checked.
            UpdatePhase.Failed => L.T("Sallyport {0} was not installed: {1}.", version, problem ?? ""),
            UpdatePhase.Unavailable => L.T("Could not look for a newer Sallyport: {0}.", problem ?? ""),
            _ => checkedAt is { } at ? L.T("Looked for a newer one on {0}.", L.Date(at)) : L.T("Not looked for a newer one yet."),
        };
        var busy = phase is UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.Starting;
        return new UpdateView(phase, text,
            UiAction.Shown(L.T("Check for updates"), !busy, busy: phase == UpdatePhase.Checking),
            offer is not null && phase is UpdatePhase.Available or UpdatePhase.Downloading or UpdatePhase.Starting or UpdatePhase.Failed
                ? UiAction.Shown(L.T("Update to {0}", version), !busy, busy: busy && phase != UpdatePhase.Checking)
                : UiAction.Hidden,
            phase == UpdatePhase.Downloading ? done : null);
    }
}
