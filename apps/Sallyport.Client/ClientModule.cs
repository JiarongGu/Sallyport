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
            "IMPORT" => core.ImportFromDialogAsync,
            "CONNECT" => () => Done(() => core.Connect(Id())),
            "DISCONNECT" => () => core.DisconnectAsync(Id()),
            "LOCATE" => () => core.LocateAsync(Id(), cancellationToken),
            "COPY_ENDPOINT" => () => Done(() => core.CopyEndpoint(Id())),
            "BEGIN_PORT_EDIT" => () => Done(() => core.BeginPortEdit(Id())),
            "SET_PORT" => () => Done(() => core.SetPort(Token(), Text("portText"))),
            "ADD_APP" => core.AddAppFromDialogAsync,
            "BEGIN_APP_EDIT" => () => Done(() => core.BeginAppEdit(Id())),
            "UPDATE_APP" => () => Done(() => core.UpdateApp(Token(), PayloadHelper.GetRequiredValue<AppValues>(request.Payload, "values"))),
            "CANCEL_EDIT" => () => Done(() => core.CancelEdit(Token())),
            "LAUNCH_APP" => () => core.LaunchAsync(Id(), cancellationToken),
            "REMOVE_APP" => () => Done(() => core.RemoveApp(Id())),
            "REQUEST_REMOVE_PROFILE" => () => Done(() => core.RequestRemoveProfile(Id())),
            "CONFIRM" => () => core.ConfirmAsync(Token()),
            "ADD_GAME" => core.AddGameFromDialogAsync,
            "RESCAN_GAME" => () => Done(() => core.RescanGame(Id())),
            "SET_GAME_PROGRAM" => () => Done(() => core.SetGameProgram(Id(), Text("path"), PayloadHelper.GetRequiredValue<bool>(request.Payload, "included"))),
            "BEGIN_GAME_EDIT" => () => Done(() => core.BeginGameEdit(Id())),
            "SAVE_GAME" => () => Done(() => core.SaveGame(Token(), PayloadHelper.GetRequiredValue<GameValues>(request.Payload, "values"))),
            "REQUEST_REMOVE_GAME" => () => Done(() => core.RequestRemoveGame(Id())),
            "RUN_GAME" => () => Done(() => core.RunGame(Id())),
            "STOP_RUN" => core.StopRunAsync,
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
        { core.Notify(NoticeKind.Error, $"That did not work ({e.GetType().Name}).", SubjectOf(request)); }
        return null;
    }

    private static NoticeSubject? SubjectOf(IpcRequest request) =>
        PayloadHelper.GetOptionalValue<string>(request.Payload, "id") is { } id
            ? new(request.Type.Contains("APP", StringComparison.Ordinal) ? "app" : request.Type.Contains("GAME", StringComparison.Ordinal) ? "game" : "profile", id)
            : null;
}
