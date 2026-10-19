using Windows.ApplicationModel.Background;
using Windows.Networking.Vpn;

namespace Sallyport.Vpn.Plugin;

/// <summary>What the VPN platform activates for every plug-in event: connect, encapsulate, decapsulate, disconnect.</summary>
public sealed partial class VpnBackgroundTask : IBackgroundTask
{
    // One plug-in for the life of the host process: the platform activates this task again for each event, and the
    // channel's state (its socket, what it has been told) must outlive any one of them.
    private static readonly RelayPlugin Plugin = new();

    public void Run(IBackgroundTaskInstance taskInstance)
    {
        var deferral = taskInstance.GetDeferral();
        try
        {
            VpnChannel.ProcessEventAsync(Plugin, taskInstance.TriggerDetails);
        }
        catch (Exception e)
        {
            Log.Write($"task run failed: {e}");
        }
        finally
        {
            deferral.Complete();
        }
    }
}
