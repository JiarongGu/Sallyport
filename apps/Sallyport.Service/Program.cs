using System.ComponentModel;
using System.Security.Principal;
using Sallyport.Windows.Service;

// Sallyport.Service [console | worker <run> <client pid> | install | uninstall]
//   With no argument it is the service, as the service manager starts it: the supervisor (Supervisor.cs). `console`
//   runs the supervisor in a console instead, for development (clients refuse it: it is not the service the manager
//   runs). `worker` is a run's router, started by the supervisor only. `install` and `uninstall` register this
//   executable as the service and remove it; they need administrator.
// Exit codes: 0 done; 1 failed or refused; 2 bad arguments.

const string Display = "Sallyport game routing";
const string Description = "Routes the programs of a game you run in Sallyport through your VPN. Idle until you run a game.";

switch (args.FirstOrDefault())
{
    case null:
        ServiceControl.Host(RouterProtocol.ServiceName, ct => new Supervisor(Log.ToFile()).RunAsync(ct));
        return 0;
    case "console":
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        await new Supervisor(Log.ToConsole()).RunAsync(stop.Token);
        return 0;
    }
    case "worker" when args.Length >= 3 && int.TryParse(args[2], out var client):
        return await Worker.RunAsync(args[1], client, Log.ToFile());
    case "install":
        if (!Elevated()) { Console.WriteLine("installing the service needs administrator"); return 1; }
        try
        {
            ServiceControl.Install(RouterProtocol.ServiceName, Display, Description, Environment.ProcessPath!);
            ServiceControl.Start(RouterProtocol.ServiceName);
            Console.WriteLine($"installed and started: {Environment.ProcessPath}");
            return 0;
        }
        catch (Win32Exception e) { Console.WriteLine($"not installed: {e.Message} (Win32 error {e.NativeErrorCode})"); return 1; }
    case "uninstall":
        if (!Elevated()) { Console.WriteLine("removing the service needs administrator"); return 1; }
        try
        {
            ServiceControl.Uninstall(RouterProtocol.ServiceName, TimeSpan.FromSeconds(20));
            Console.WriteLine("removed");
            return 0;
        }
        catch (Win32Exception e) { Console.WriteLine($"not removed: {e.Message} (Win32 error {e.NativeErrorCode})"); return 1; }
    default:
        Console.Error.WriteLine("usage: Sallyport.Service [console | install | uninstall]");
        return 2;
}

static bool Elevated() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
