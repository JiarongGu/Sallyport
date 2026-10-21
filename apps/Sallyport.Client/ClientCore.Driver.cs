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

/// <summary>The packet driver: installed and removed from Settings, and what it and the service are, read at most every
/// few seconds.</summary>
internal sealed partial class ClientCore
{
    // ---- the packet driver

    public void RequestDriverInstall() =>
        Confirm("driver-install", "", L.T("Install the packet driver?"), GameViews.DriverInstallText, L.T("Install"), L.T("Not now"));

    public void RequestDriverRemove()
    {
        if (_runner.IsRunning) throw new ShenoraException("RUN_BUSY", "driver", "", L.T("Disconnect first: apps are going through the VPN."));
        Confirm("driver-remove", "", L.T("Remove the packet driver?"), GameViews.DriverRemoveText, L.T("Remove"), L.T("Keep it"));
    }

    private async Task ManageDriverAsync(bool install)
    {
        if (Interlocked.CompareExchange(ref _driverWork, install ? L.T("Installing the packet driver…") : L.T("Removing the packet driver…"), null) is not null) return;
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
            // A driver just put in is what a route that failed for want of it was waiting for.
            if (outcome.Succeeded && install) _runner.Retry();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidDataException)
        {
            Notify(NoticeKind.Error, L.T("The driver's installer could not be fetched from NT Kernel ({0}). Check the connection and try again.", e.GetType().Name));
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
        // Read on whichever thread publishes, a timer's among them, where a throw would end Sallyport: a service manager
        // that would not say is a service whose state is not known (rule 6), as the driver's own already is.
        Windows.PacketFilter.ServiceState service;
        try { (service, _) = ServiceControl.Status(RouterProtocol.ServiceName); }
        catch (System.ComponentModel.Win32Exception) { service = Windows.PacketFilter.ServiceState.Unknown; }
        var facts = new DriverFacts(service != Windows.PacketFilter.ServiceState.NotInstalled, service == Windows.PacketFilter.ServiceState.Running,
            driver.Service is not (Windows.PacketFilter.ServiceState.NotInstalled or Windows.PacketFilter.ServiceState.Unknown),
            driver.Service == Windows.PacketFilter.ServiceState.Running, driver.VersionText, _driverWork is not null, _driverWork);
        _driverFacts = (facts, now);
        return facts;
    }

    public void CopyGuideExample()
    {
        if (Clipboard(ClientView.CurlExample(GuideEndpoint(Snapshot().Profiles)))) Notify(NoticeKind.Info, L.T("Copied the example."));
    }
}
