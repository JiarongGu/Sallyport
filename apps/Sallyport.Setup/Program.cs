using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;

// Sallyport.Setup [/uninstall] [/quiet] [--folder <install folder>] [--payload <folder or zip>] [--no-driver] [--remove-data]
//   [--language en|zh-Hans]
//   Installs Sallyport, or with /uninstall removes it (as Windows' installed-apps list runs it), in the folder chosen (an
//   update stays where Sallyport is; a first install defaults to Program Files). A window says each step;
//   /quiet runs without one and answers by exit code: 0 done, 1 failed, 2 bad arguments. --payload installs from a
//   publish folder instead of the payload carried inside (development builds have none). Started elevated (its manifest):
//   the one administrator prompt is this one.

namespace Sallyport.Setup;

static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var uninstall = args.Contains("/uninstall");
        var quiet = args.Contains("/quiet");
        var choices = new SetupChoices(Driver: !args.Contains("--no-driver"), RemoveData: args.Contains("--remove-data"));

        // A windowed program has no console of its own: /quiet speaks to the one it was started from, if any.
        if (quiet) AttachConsole(-1);

        string? Value(string name) => Array.IndexOf(args, name) is >= 0 and var at && at + 1 < args.Length ? args[at + 1] : null;
        var payloadArg = Value("--payload");
        // An installed Sallyport is updated or removed where it is; the uninstaller in its folder knows its own.
        var existing = Installer.ExistingFolder()
            ?? (uninstall && File.Exists(Path.Combine(AppContext.BaseDirectory, "Sallyport.Service.exe")) ? AppContext.BaseDirectory : null);
        L.Choose(Value("--language"), existing);
        var asked = Value("--folder");
        if (existing is not null && asked is not null && !uninstall
            && !Path.TrimEndingDirectorySeparator(Path.GetFullPath(asked)).Equals(Path.TrimEndingDirectorySeparator(existing), StringComparison.OrdinalIgnoreCase))
        {
            // Moving an installed Sallyport is removing it, then installing it elsewhere: never done behind a flag.
            if (quiet) Console.WriteLine(L.T("Sallyport is installed in {0}; remove it before installing it elsewhere.", existing));
            return 2;
        }
        var folder = existing ?? asked ?? Installer.DefaultFolder;
        if (quiet) return RunQuiet(uninstall, choices, payloadArg, folder);

        Application.EnableVisualStyles();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Point? at = null;
        while (true)
        {
            using var form = new SetupForm(uninstall, choices, () => OpenPayload(payloadArg), Version, folder, existing is not null);
            if (at is { } where)
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = where;
            }
            Application.Run(form);
            if (form.SwitchTo is not { } language) return 0;
            // Another language: the same window again in it, where it was, with what the person had set.
            L.Use(language);
            folder = existing ?? form.Folder;
            choices = form.Choices(choices);
            at = form.Location;
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    public static string Version =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    private static int RunQuiet(bool uninstall, SetupChoices choices, string? payloadArg, string folder)
    {
        var installer = new Installer(step => { if (step.Text is { } text) Console.WriteLine(text); }, folder);
        try
        {
            IReadOnlyList<string> left;
            if (uninstall) left = installer.UninstallAsync(choices, CancellationToken.None).GetAwaiter().GetResult();
            else
            {
                if (Installer.Missing() is { } why) { Console.WriteLine(why); return 1; }
                if (Installer.Unfit(folder) is { } unfit) { Console.WriteLine(unfit); return 2; }
                using var payload = OpenPayload(payloadArg) ?? throw new SetupFailed(L.T("This setup carries nothing to install."));
                left = installer.InstallAsync(payload, Version, choices, CancellationToken.None).GetAwaiter().GetResult();
            }
            // Done, but with something left (the driver, a file in use): said again at the end, where it is seen.
            foreach (var line in left) Console.WriteLine(line);
            return 0;
        }
        catch (Exception e) when (e is SetupFailed or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidDataException or HttpRequestException)
        {
            Console.WriteLine(e.Message);
            return 1;
        }
    }

    /// <summary>What is installed: the payload inside this setup, or a publish folder or zip named on the command line.</summary>
    public static Stream? OpenPayload(string? payloadArg)
    {
        if (payloadArg is null) return typeof(Program).Assembly.GetManifestResourceStream("payload.zip");
        if (File.Exists(payloadArg)) return File.OpenRead(payloadArg);
        if (!Directory.Exists(payloadArg)) return null;
        var zip = new MemoryStream();
        ZipFile.CreateFromDirectory(payloadArg, zip, CompressionLevel.Fastest, includeBaseDirectory: false);
        zip.Position = 0;
        return zip;
    }
}
