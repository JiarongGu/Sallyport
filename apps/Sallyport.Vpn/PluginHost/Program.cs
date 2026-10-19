using Windows.ApplicationModel.Core;
using Windows.UI.Core;

namespace Sallyport.Vpn.PluginHost;

/// <summary>The vpnClient task's custom host: the app model starts this process like a UWP app and activates the task's
/// class (the plug-in component, served in-process by C#/WinRT's host) inside it. It only has to run CoreApplication.</summary>
public static class Program
{
    [MTAThread]
    public static void Main() => CoreApplication.Run(new ViewSource());
}

internal sealed partial class ViewSource : IFrameworkViewSource
{
    public IFrameworkView CreateView() => new View();
}

internal sealed partial class View : IFrameworkView
{
    public void Initialize(CoreApplicationView applicationView) { }
    public void SetWindow(CoreWindow window) { }
    public void Load(string entryPoint) { }
    public void Run() => CoreWindow.GetForCurrentThread()?.Dispatcher.ProcessEvents(CoreProcessEventsOption.ProcessUntilQuit);
    public void Uninitialize() { }
}
