namespace Sallyport.Windows;

/// <summary>
/// Where an installed Sallyport keeps what it keeps (D19): all of it in the folder it was installed to, so nothing of
/// Sallyport's is where its owner did not put it. Setup, the service and the client each find their part from the
/// folder their own program runs from.
/// </summary>
public static class InstallLayout
{
    /// <summary>The machine's part — setup's record, the driver's installer, the service's logs — and the people's.</summary>
    public static string Data(string installFolder) => Path.Combine(installFolder, "data");

    /// <summary>The language setup was run in ("en" or "zh-Hans"): the client's, for a person who has not chosen one in it.
    /// Written by setup, read by everyone.</summary>
    public static string Language(string installFolder) => Path.Combine(Data(installFolder), "language");

    /// <summary>Each Windows user's part, a folder per SID that only that user (and the system, and administrators) can
    /// open. Setup makes it; a folder without it is a development build, which keeps the user's part in
    /// %LOCALAPPDATA%\Sallyport as before.</summary>
    public static string Users(string installFolder) => Path.Combine(Data(installFolder), "users");

    /// <summary>Where NT Kernel's installer is asked to put the driver's own files (its <c>INSTALLLOCATION</c>): beside
    /// Sallyport's folder, as Program Files would have it — not inside, which an update replaces whole. The driver
    /// itself goes where Windows keeps every driver, whatever this says.</summary>
    public static string DriverFolder(string installFolder) =>
        Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(installFolder)))!, "Windows Packet Filter");

    /// <summary>The folder this program runs from, when it is an installed Sallyport's (its users' folder exists); null
    /// for a development build.</summary>
    public static string? InstalledHere()
    {
        var folder = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        return Directory.Exists(Users(folder)) ? folder : null;
    }
}
