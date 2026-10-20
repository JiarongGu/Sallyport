using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Sallyport.Client;

/// <summary>Each app's icon as its program carries it (the owner: "set app icon from app exe"): drawn once for the file as
/// it is now, and handed to the page as a PNG data URL, so the page is given no path to read.</summary>
internal sealed class AppIcons
{
    // Shown 32 pixels square on the page: twice that, for a screen at 200%.
    private const int Size = 64;
    private readonly ConcurrentDictionary<string, (DateTime Written, string? Url)> _drawn = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The program's own icon, or null when it has none (the page then shows the app's initial).</summary>
    public string? Of(string? path)
    {
        if (path is null) return null;
        var written = File.GetLastWriteTimeUtc(path);
        // A file that is not there reads as written in 1601.
        if (written.Year == 1601) return null;
        if (_drawn.TryGetValue(path, out var known) && known.Written == written) return known.Url;
        var url = Draw(path);
        _drawn[path] = (written, url);
        return url;
    }

    private static string? Draw(string path)
    {
        try
        {
            // Only the program's own: Windows' stand-in for a program without one says less than the initial does.
            using var icon = Icon.ExtractIcon(path, 0, Size);
            if (icon is null) return null;
            using var bitmap = icon.ToBitmap();
            using var png = new MemoryStream();
            bitmap.Save(png, ImageFormat.Png);
            return "data:image/png;base64," + Convert.ToBase64String(png.ToArray());
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or ExternalException) { return null; }
    }
}
