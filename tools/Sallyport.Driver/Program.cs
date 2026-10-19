using Sallyport.Windows.PacketFilter;

// Sallyport.Driver status | fetch | install | uninstall
//   The packet driver (Windows Packet Filter, D18) as Sallyport manages it: NT Kernel's pinned release, fetched into
//   %LOCALAPPDATA%\Sallyport\drivers and checked, installed or removed with one administrator prompt.
// Exit codes: 0 done; 1 failed or refused; 2 bad arguments.

var setup = new PacketFilterSetup(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sallyport", "drivers"));

switch (args.FirstOrDefault())
{
    case "status":
        Print(setup.Status());
        return 0;
    case "fetch":
        Console.WriteLine($"fetched and checked: {await FetchAsync()}");
        return 0;
    case "install":
    {
        if (!PacketFilterRelease.FitsThisMachine) { Console.WriteLine("this machine is not x64: the pinned installer does not fit it"); return 1; }
        await FetchAsync();
        var outcome = await setup.InstallAsync();
        Report(outcome);
        Print(setup.Status());
        return outcome.Succeeded ? 0 : 1;
    }
    case "uninstall":
    {
        await FetchAsync();
        var outcome = await setup.UninstallAsync();
        Report(outcome);
        Print(setup.Status());
        return outcome.Succeeded ? 0 : 1;
    }
    default:
        Console.Error.WriteLine("usage: Sallyport.Driver status | fetch | install | uninstall");
        return 2;
}

async Task<string> FetchAsync()
{
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
    return await setup.FetchAsync(http);
}

static void Report(SetupOutcome outcome) =>
    Console.WriteLine($"{outcome.Text} (exit {outcome.ExitCode}){(outcome.LogPath is { } log ? $"; log {log}" : "")}");

static void Print(PacketFilterStatus status)
{
    Console.WriteLine($"service: {status.Service}");
    Console.WriteLine(status.DriverVersion is { } v
        ? $"driver: answers, version word {status.VersionText}"
        : $"driver: not reached (Win32 error {status.DeviceError}{(status.DeviceError == 5 ? ": access denied — opening it needs administrator" : status.DeviceError == 2 ? ": no such device" : "")})");
    Console.WriteLine($"installer kept: {status.InstallerPath ?? "no"}");
}
