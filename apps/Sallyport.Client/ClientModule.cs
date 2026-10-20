using Shenora;
using Shenora.Core.Events;
using Shenora.Core.Ipc;

namespace Sallyport.Client;

/// <summary>
/// The page's intents (web/src/contract.ts, <c>Commands</c>): posted, answered with nothing. What follows from one reaches
/// the page as the next <c>CLIENT.STATE</c>, and a refusal as a <c>CLIENT.NOTICE</c> — the page holds no outcome of its
/// own. Ids and tokens arriving here are checked by the core, not trusted.
/// </summary>
internal sealed class ClientModule(ClientCore core, IEventBus events) : ModuleBase(events: events)
{
    public override string ModuleName => ClientCore.Module;

    protected override async Task<object?> RouteMessageAsync(IpcRequest request, IModuleContext context, CancellationToken cancellationToken)
    {
        string Text(string key) => PayloadHelper.GetRequiredValue<string>(request.Payload, key);
        string Id() => Text("id");
        string Token() => Text("token");
        Task Done(Action action) { action(); return Task.CompletedTask; }

        Func<Task> intent = request.Type switch
        {
            "IMPORT" => () => Done(core.BeginProfileImport),
            "PROFILE_IMPORT_BROWSE" => () => core.BrowseProfileAsync(Token(), PayloadHelper.GetRequiredValue<NewProfileValues>(request.Payload, "values")),
            "PROFILE_IMPORT_LINK" => () => core.FetchProfileAsync(Token(), PayloadHelper.GetRequiredValue<NewProfileValues>(request.Payload, "values")),
            "SAVE_PROFILE_IMPORT" => () => Done(() => core.SaveProfileImport(Token(), PayloadHelper.GetRequiredValue<NewProfileValues>(request.Payload, "values"))),
            "CONNECT" => () => Done(() => core.Connect(Id())),
            "DISCONNECT" => () => core.DisconnectAsync(Id()),
            "LOCATE" => () => core.LocateAsync(Id(), cancellationToken),
            "CHECK_LATENCY" => () => core.CheckLatencyAsync(Id(), cancellationToken),
            "BEGIN_DEFAULTS_EDIT" => () => Done(core.BeginDefaultsEdit),
            "BEGIN_EXPORT" => () => Done(core.BeginExport),
            "SAVE_EXPORT" => () => core.SaveExportAsync(Token(), PayloadHelper.GetRequiredValue<MoveValues>(request.Payload, "values")),
            "BEGIN_IMPORT" => core.BeginImportAsync,
            "SAVE_IMPORT" => () => Done(() => core.SaveImport(Token(), PayloadHelper.GetRequiredValue<MoveValues>(request.Payload, "values"))),
            "SET_LANGUAGE" => () => Done(() => core.SetLanguage(Text("language"))),
            "WATCH_APP" => () => Done(() => core.WatchApp(PayloadHelper.GetOptionalValue<string>(request.Payload, "id"))),
            "SET_PROTOCOL" => () => Done(() => core.SetProtocol(Text("protocol"))),
            "SET_RETRY" => () => Done(() => core.SetRetry(PayloadHelper.GetRequiredValue<int>(request.Payload, "rounds"))),
            "SAVE_DEFAULTS" => () => Done(() => core.SaveDefaults(Token(), PayloadHelper.GetRequiredValue<DefaultsValues>(request.Payload, "values"))),
            "COPY_ENDPOINT" => () => Done(() => core.CopyEndpoint(Id())),
            "ADD_APP" => () => Done(core.BeginAddApp),
            "NEW_APP_BROWSE" => () => core.BrowseNewAppAsync(Token(), PayloadHelper.GetRequiredValue<NewAppValues>(request.Payload, "values")),
            "NEW_APP_LOCATION" => () => Done(() => core.SetNewAppLocation(Token(), PayloadHelper.GetRequiredValue<NewAppValues>(request.Payload, "values"))),
            "SAVE_NEW_APP" => () => Done(() => core.SaveNewApp(Token(), PayloadHelper.GetRequiredValue<NewAppValues>(request.Payload, "values"))),
            "ADD_APP_PROGRAM" => () => core.AddProgramToAppAsync(Id()),
            "RESCAN_APP" => () => Done(() => core.RescanApp(Id())),
            "LOCATE_APP" => () => core.LocateAppAsync(Id()),
            "SET_APP_PROGRAM" => () => Done(() => core.SetAppProgram(Id(), Text("programId"), PayloadHelper.GetRequiredValue<bool>(request.Payload, "included"))),
            "BEGIN_APP_EDIT" => () => Done(() => core.BeginAppEdit(Id())),
            "SAVE_APP" => () => Done(() => core.SaveApp(Token(), PayloadHelper.GetRequiredValue<AppEditValues>(request.Payload, "values"))),
            "BEGIN_PROGRAM_EDIT" => () => Done(() => core.BeginProgramEdit(Id(), Text("programId"))),
            "SAVE_PROGRAM" => () => Done(() => core.SaveProgram(Token(), PayloadHelper.GetRequiredValue<ProgramEditValues>(request.Payload, "values"))),
            "REQUEST_REMOVE_APP" => () => Done(() => core.RequestRemoveApp(Id())),
            "SET_APP_VPN" => () => Done(() => core.SetAppVpn(Id(), Text("profileId"), PayloadHelper.GetRequiredValue<bool>(request.Payload, "on"))),
            "ADD_APP_TO_PROFILE" => () => Done(() => core.AddAppToProfile(Id(), Text("profileId"))),
            "REMOVE_APP_FROM_PROFILE" => () => Done(() => core.RemoveAppFromProfile(Id(), Text("profileId"))),
            "SET_APP_ICON" => () => Done(() => core.SetAppIcon(Id(), Text("programId"))),
            "CANCEL_EDIT" => () => Done(() => core.CancelEdit(Token())),
            "LAUNCH_APP" => () => core.LaunchAsync(Id(), PayloadHelper.GetOptionalValue<string>(request.Payload, "programId"),
                PayloadHelper.GetOptionalValue<string>(request.Payload, "profileId"), cancellationToken),
            "REQUEST_REMOVE_PROFILE" => () => Done(() => core.RequestRemoveProfile(Id())),
            "CONFIRM" => () => core.ConfirmAsync(Token()),
            "BEGIN_POLICY_EDIT" => () => Done(() => core.BeginPolicyEdit(Id())),
            "SAVE_POLICY" => () => Done(() => core.SavePolicy(Token(), PayloadHelper.GetRequiredValue<PolicyValues>(request.Payload, "values"))),
            "RESET_POLICY" => () => Done(() => core.ResetPolicy(Token())),
            "REQUEST_DRIVER_INSTALL" => () => Done(core.RequestDriverInstall),
            "REQUEST_DRIVER_REMOVE" => () => Done(core.RequestDriverRemove),
            "CANCEL_CONFIRMATION" => () => Done(() => core.CancelConfirmation(Token())),
            "COPY_GUIDE_EXAMPLE" => () => Done(core.CopyGuideExample),
            "SET_START_WITH_WINDOWS" => () => Done(() => core.SetStartWithWindows(PayloadHelper.GetRequiredValue<bool>(request.Payload, "enabled"))),
            "DISMISS_NOTICE" => () => Done(() => core.Dismiss(Id())),
            _ => throw UnknownType(request),
        };

        try { await intent(); }
        catch (ShenoraException e) { core.Notify(NoticeKind.Error, e.Message, SubjectOf(request)); }
        // Anything else is this program's fault, not the person's: its kind is said, its message (which could carry a
        // path) is not.
        catch (Exception e) when (e is not OperationCanceledException)
        { core.Notify(NoticeKind.Error, L.T("That did not work ({0}).", e.GetType().Name), SubjectOf(request)); }
        return null;
    }

    private static NoticeSubject? SubjectOf(IpcRequest request) =>
        PayloadHelper.GetOptionalValue<string>(request.Payload, "id") is { } id
            ? new(request.Type.Contains("APP", StringComparison.Ordinal) || request.Type.Contains("PROGRAM", StringComparison.Ordinal) ? "app" : "profile", id)
            : null;
}
