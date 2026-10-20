using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Principal;
using System.Text;

// Sallyport-<version>-win-x64-setup.exe [/uninstall] [/quiet] [--folder <install folder>] [--payload <folder or zip>]
//   [--no-driver] [--remove-data] [--language en|zh-Hans]
//   Installs Sallyport, or with /uninstall removes it (as Windows' installed-apps list runs it), in the folder chosen (an
//   update stays where Sallyport is; a first install defaults to Program Files). A window says each step;
//   /quiet runs without one and answers by exit code: 0 done, 1 failed, 2 bad arguments. Setup is Sallyport's one
//   program (D30) run under the release's name, or with /uninstall, or --setup: what it installs is itself. --payload
//   installs from a publish folder or zip instead (a development build, which is not one file). It asks to be
//   administrator at its start, and only there: everything it does needs it.

namespace Sallyport.Setup;

public static class SetupProgram
{
    /// <summary>Whether this start is setup's: the release's file (its name says setup), the installed program asked to
    /// remove itself, or asked by name (development).</summary>
    public static bool Asked(string[] args, string? path) =>
        args.Contains("/uninstall") || args.Contains("--setup")
        || Path.GetFileNameWithoutExtension(path ?? "").Contains("setup", StringComparison.OrdinalIgnoreCase);

    /// <summary>Setup, as asked; its exit code.</summary>
    public static int Run(string[] args)
    {
        var uninstall = args.Contains("/uninstall");
        var quiet = args.Contains("/quiet");
        // A windowed program has no console of its own: /quiet speaks to the one it was started from, if any.
        if (quiet) AttachConsole(-1);
        if (!Elevated()) return Elevate(args, quiet);
        if (Array.IndexOf(args, "--asker") is >= 0 and var by && by + 1 < args.Length && int.TryParse(args[by + 1], out var asker)) Asker = asker;
        var choices = new SetupChoices(Driver: !args.Contains("--no-driver"), RemoveData: args.Contains("--remove-data"));

        string? Value(string name) => Array.IndexOf(args, name) is >= 0 and var at && at + 1 < args.Length ? args[at + 1] : null;
        var payloadArg = Value("--payload");
        // An installed Sallyport is updated or removed where it is; the uninstaller in its folder knows its own.
        var existing = Installer.ExistingFolder()
            ?? (uninstall && File.Exists(Path.Combine(AppContext.BaseDirectory, Installer.Program)) ? AppContext.BaseDirectory : null);
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
            folder = existing ?? form.FolderLeft ?? folder;
            choices = form.Choices(choices);
            at = form.Location;
        }
    }

    private static bool Elevated() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>The start that asked for this one to be administrator, and waits for it (<c>--asker</c>): not a window
    /// to close.</summary>
    public static int Asker { get; private set; }

    // The one administrator prompt, as the setup's manifest gave it before setup became the one program (whose window
    // runs as the person): the same start again, elevated, waited for. Refused, nothing is done.
    private static int Elevate(string[] args, bool quiet)
    {
        try
        {
            using var elevated = Process.Start(new ProcessStartInfo(Environment.ProcessPath!,
                string.Join(" ", args.Append("--asker").Append(Environment.ProcessId.ToString()).Select(Quote)))
            {
                UseShellExecute = true, Verb = "runas",
            });
            if (elevated is null) return 1;
            elevated.WaitForExit();
            return elevated.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            // 1223: the person said no to Windows' prompt.
            if (quiet) Console.WriteLine(e.NativeErrorCode == 1223 ? L.T("Setup needs administrator, and was not allowed it.") : e.Message);
            return 1;
        }
    }

    // One argument as CommandLineToArgvW reads it back: quoted where it holds a space or a quote, a quote escaped, and the
    // backslashes before a quote doubled.
    internal static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0) return arg;
        var quoted = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { slashes++; continue; }
            quoted.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
            slashes = 0;
        }
        return quoted.Append('\\', slashes * 2).Append('"').ToString();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    public static string Version =>
        typeof(SetupProgram).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

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

    /// <summary>What is installed: this program itself and WebView2's loader it carries (D30), or a publish folder or zip
    /// named on the command line. Null for a development build named nothing: it is not one file, and cannot copy itself.</summary>
    public static Stream? OpenPayload(string? payloadArg)
    {
        if (payloadArg is null) return Itself();
        if (File.Exists(payloadArg)) return File.OpenRead(payloadArg);
        if (!Directory.Exists(payloadArg)) return null;
        var zip = new MemoryStream();
        ZipFile.CreateFromDirectory(payloadArg, zip, CompressionLevel.Fastest, includeBaseDirectory: false);
        zip.Position = 0;
        return zip;
    }

    // The install as a zip of what goes in the folder, as a payload was: the program under its installed name, whatever
    // the release's file is called, and the loader beside it (kept out of the single file, which would unpack it to
    // %TEMP% on the system drive at every start). Stored, not compressed: the program is compressed already.
    private static Stream? Itself()
    {
        var entry = Assembly.GetEntryAssembly();
        // A development build is its program's apphost beside its assemblies; the release is one file, with none beside it.
        if (entry is null || Environment.ProcessPath is not { } self
            || File.Exists(Path.Combine(AppContext.BaseDirectory, entry.GetName().Name + ".dll"))) return null;
        using var loader = entry.GetManifestResourceStream(Installer.Loader);
        if (loader is null) return null;
        var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var program = archive.CreateEntry(Installer.Program, CompressionLevel.NoCompression).Open())
            using (var source = File.OpenRead(self))
                source.CopyTo(program);
            using var loaded = archive.CreateEntry(Installer.Loader, CompressionLevel.NoCompression).Open();
            loader.CopyTo(loaded);
        }
        zip.Position = 0;
        return zip;
    }
}
