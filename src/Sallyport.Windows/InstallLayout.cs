namespace Sallyport.Windows;

/// <summary>
/// Where an installed Sallyport keeps what it keeps (D19, D25): all of it in the folder it was installed to, so nothing
/// of Sallyport's is where its owner did not put it. Setup, the service and the client each find their part from the
/// folder their own program runs from.
/// </summary>
public static class InstallLayout
{
    /// <summary>The machine's part — setup's record, the driver's installer, the service's logs — and the areas everyone
    /// who uses Sallyport here shares (<see cref="Areas"/>).</summary>
    public static string Data(string installFolder) => Path.Combine(installFolder, "data");

    /// <summary>What Sallyport keeps for the people who use it, one folder each in <see cref="Data"/>, shared by everyone
    /// on this computer (the owner: "One shared folder", D25): the sealed profiles, the settings, the apps, the browsers'
    /// own folders, and what can be made again. A profile stays sealed to the Windows account that imported it.</summary>
    public static readonly IReadOnlyList<string> Areas = ["profiles", "config", "library", "apps", "cache"];

    /// <summary>Of <see cref="Areas"/>, those removing Sallyport keeps unless the person says otherwise.</summary>
    public static readonly IReadOnlyList<string> Kept = ["profiles", "config", "library", "apps"];

    public static string Area(string installFolder, string name) => Path.Combine(Data(installFolder), name);

    /// <summary>Whether setup laid the shared areas out (D25); an earlier install has <see cref="Users"/> instead.</summary>
    public static bool Shared(string installFolder) => Directory.Exists(Area(installFolder, "config"));

    /// <summary>The language setup was run in ("en" or "zh-Hans"): the client's, for a person who has not chosen one in it.
    /// Written by setup, read by everyone.</summary>
    public static string Language(string installFolder) => Path.Combine(Data(installFolder), "language");

    /// <summary>An earlier install's part per Windows user (D19), a folder per SID that only that user could open: setup
    /// moves the installing person's into the shared areas; another person's stays as it was.</summary>
    public static string Users(string installFolder) => Path.Combine(Data(installFolder), "users");

    /// <summary>Where NT Kernel's installer is asked to put the driver's own files (its <c>INSTALLLOCATION</c>): beside
    /// Sallyport's folder, as Program Files would have it — not inside, which an update replaces whole. The driver
    /// itself goes where Windows keeps every driver, whatever this says.</summary>
    public static string DriverFolder(string installFolder) =>
        Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(installFolder)))!, "Windows Packet Filter");

    /// <summary>The folder this program runs from, when it is an installed Sallyport's (setup laid its data out); null
    /// for a development build.</summary>
    public static string? InstalledHere()
    {
        var folder = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        return Shared(folder) || Directory.Exists(Users(folder)) ? folder : null;
    }
}
