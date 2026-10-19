using Windows.ApplicationModel;
using Windows.ApplicationModel.Core;
using Windows.Storage;
using Windows.UI.Core;

namespace Sallyport.VpnSpike.PluginHost;

/// <summary>The vpnClient task's custom host: the app model starts this process like a UWP app and activates the task's
/// class (the plug-in component, served in-process by C#/WinRT's host) inside it. It only has to run CoreApplication.</summary>
public static class Program
{
    [MTAThread]
    public static void Main()
    {
        Write($"host started: {Environment.CommandLine}");
        Write($"probe: activation factory for the task's class -> {ProbeActivation("Sallyport.VpnSpike.Plugin.VpnBackgroundTask")}");
        CoreApplication.Run(new ViewSource());
    }

    /// <summary>What the app model will do for the task's EntryPoint, done here first so its HRESULT can be read: resolve
    /// the class through the package's in-process server registration.</summary>
    private static string ProbeActivation(string classId)
    {
        var iid = new Guid("00000035-0000-0000-C000-000000000046");   // IActivationFactory
        var hr = WindowsCreateString(classId, classId.Length, out var name);
        if (hr < 0) return $"WindowsCreateString 0x{hr:X8}";
        try
        {
            hr = RoGetActivationFactory(name, ref iid, out var factory);
            if (factory != IntPtr.Zero) System.Runtime.InteropServices.Marshal.Release(factory);
            return hr < 0 ? $"failed 0x{hr:X8}" : "ok";
        }
        finally { WindowsDeleteString(name); }
    }

    [System.Runtime.InteropServices.DllImport("combase.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);
    [System.Runtime.InteropServices.DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);
    [System.Runtime.InteropServices.DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);

    internal static void Write(string line)
    {
        try
        {
            File.AppendAllText(Path.Combine(ApplicationData.Current.LocalFolder.Path, "plugin.log"),
                $"{DateTime.Now:HH:mm:ss.fff} [{Environment.ProcessId}] {line}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Logging must never be what stops the host.
        }
    }
}

internal sealed partial class ViewSource : IFrameworkViewSource
{
    public IFrameworkView CreateView() => new View();
}

internal sealed partial class View : IFrameworkView
{
    public void Initialize(CoreApplicationView applicationView) =>
        applicationView.Activated += (_, e) => Program.Write($"host activated: {e.Kind}");
    public void SetWindow(CoreWindow window) { }
    public void Load(string entryPoint) { }
    public void Run() => CoreWindow.GetForCurrentThread()?.Dispatcher.ProcessEvents(CoreProcessEventsOption.ProcessUntilQuit);
    public void Uninitialize() { }
}
