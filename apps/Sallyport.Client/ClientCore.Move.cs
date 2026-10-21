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

/// <summary>Moving to another computer (D26): what an export holds and an import takes.</summary>
internal sealed partial class ClientCore
{
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
                values.Connection ? new ConnectionPart(_settings.Connection, _settings.Policies) : null);
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
        IReadOnlyList<(SealedProfile Entry, string Text)>? profiles;
        // Read, the file was whole; opened, a profile in it may still be cut short or sealed in a way this version
        // does not know: said beside the form, as the file's other faults are.
        try { profiles = values.Profiles && file.Profiles is { } sealedProfiles ? Backup.Open(sealedProfiles, values.Password ?? "") : []; }
        catch (InvalidDataException e)
        {
            Refuse(token, values, new Dictionary<string, string> { ["form"] = L.T("{0} was not imported: {1}.", name, e.Message) });
            return;
        }
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
}
