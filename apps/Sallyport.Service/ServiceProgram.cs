using System.ComponentModel;
using System.Security.Principal;
using Sallyport.Windows.Service;

namespace Sallyport.Service;

/// <summary>
/// The router service, hosted by Sallyport's one program (D30): <c>Sallyport.Client.exe --service [console | worker
/// &lt;run&gt; &lt;client pid&gt; | install | uninstall]</c>.
/// With nothing after <see cref="Argument"/> it is the service, as the service manager starts it: the supervisor
/// (Supervisor.cs). <c>console</c> runs the supervisor in a console instead, for development (clients refuse it: it is
/// not the service the manager runs). <c>worker</c> is a run's router, started by the supervisor only. <c>install</c> and
/// <c>uninstall</c> register this executable as the service and remove it; they need administrator.
/// Exit codes: 0 done; 1 failed or refused; 2 bad arguments.
/// </summary>
public static class ServiceProgram
{
    /// <summary>The first argument that makes the program the service rather than the window.</summary>
    public const string Argument = RouterProtocol.ServiceArgument;

    public const string Display = "Sallyport game routing";
    public const string Description = "Routes the programs you run in Sallyport through your VPN. Idle until you run one.";

    /// <summary>Runs what <paramref name="args"/> (what follows <see cref="Argument"/>) asks.</summary>
    public static int Run(string[] args) => RunAsync(args).GetAwaiter().GetResult();

    private static async Task<int> RunAsync(string[] args)
    {
        switch (args.FirstOrDefault())
        {
            case null:
                ServiceControl.Host(RouterProtocol.ServiceName, ct => new Supervisor(Log.ToFile()).RunAsync(ct));
                return 0;
            case "console":
            {
                // A windowed program has no console of its own: it speaks to the one it was started from.
                AttachConsole(-1);
                using var stop = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
                await new Supervisor(Log.ToConsole()).RunAsync(stop.Token);
                return 0;
            }
            case "worker" when args.Length >= 3 && int.TryParse(args[2], out var client):
                return await Worker.RunAsync(args[1], client, Log.ToFile());
            case "install":
                AttachConsole(-1);
                if (!Elevated()) { Console.WriteLine("installing the service needs administrator"); return 1; }
                try
                {
                    ServiceControl.Install(RouterProtocol.ServiceName, Display, Description, Environment.ProcessPath!, Argument);
                    ServiceControl.Start(RouterProtocol.ServiceName);
                    Console.WriteLine($"installed and started: {Environment.ProcessPath} {Argument}");
                    return 0;
                }
                catch (Win32Exception e) { Console.WriteLine($"not installed: {e.Message} (Win32 error {e.NativeErrorCode})"); return 1; }
            case "uninstall":
                AttachConsole(-1);
                if (!Elevated()) { Console.WriteLine("removing the service needs administrator"); return 1; }
                try
                {
                    ServiceControl.Uninstall(RouterProtocol.ServiceName, TimeSpan.FromSeconds(20));
                    Console.WriteLine("removed");
                    return 0;
                }
                catch (Win32Exception e) { Console.WriteLine($"not removed: {e.Message} (Win32 error {e.NativeErrorCode})"); return 1; }
            default:
                AttachConsole(-1);
                Console.Error.WriteLine($"usage: Sallyport.Client {Argument} [console | install | uninstall]");
                return 2;
        }
    }

    private static bool Elevated() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
}
