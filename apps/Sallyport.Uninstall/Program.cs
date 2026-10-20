using System.Runtime.InteropServices;
using Sallyport.Setup;

namespace Sallyport.Uninstall;

// uninstall.exe [/quiet] [--remove-data]
//   Removes the Sallyport installed in its own folder, as setup's removal does (Installer.UninstallAsync): its window
//   asks first, in the language the person chose in Sallyport; /quiet asks nothing and answers by exit code (0 done,
//   1 not), the profiles and settings kept unless --remove-data. Windows asks for administrator as it starts (its
//   manifest). Another folder's Sallyport is not its to remove.
internal static partial class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var quiet = args.Contains("/quiet");
        if (quiet) AttachConsole(-1);
        var folder = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        L.Choose(null, folder);
        if (!File.Exists(Path.Combine(folder, Installer.Program)))
        {
            var why = L.T("Sallyport is not installed in {0}.", folder);
            if (quiet) Console.WriteLine(why);
            else RemovalDialog.Tell(L.T("Sallyport was not removed"), why);
            return 1;
        }
        return quiet ? Quiet(folder, args.Contains("--remove-data")) : RemovalDialog.Run(folder);
    }

    private static int Quiet(string folder, bool removeData)
    {
        var installer = new Installer(step => { if (step.Text is { } text) Console.WriteLine(text); }, folder);
        try
        {
            foreach (var line in installer.UninstallAsync(new SetupChoices(Driver: false, RemoveData: removeData), CancellationToken.None).GetAwaiter().GetResult())
                Console.WriteLine(line);
            return 0;
        }
        catch (Exception e) when (e is SetupFailed or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Console.WriteLine(e.Message);
            return 1;
        }
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);
}
