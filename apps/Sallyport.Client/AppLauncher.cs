using System.Diagnostics;
using System.Text;

namespace Sallyport.Client;

/// <summary>
/// Starts an application through a profile's SOCKS port, each family told the way it listens. Proxy mode only: what
/// ignores proxies — most games — is not reached this way (D12).
/// </summary>
internal static class AppLauncher
{
    public static AppKind Guess(string executable)
    {
        var name = Path.GetFileNameWithoutExtension(executable).ToLowerInvariant();
        if (name is "chrome" or "msedge" or "brave" or "vivaldi" or "opera" or "chromium" or "thorium") return AppKind.Chromium;
        if (name is "firefox" or "librewolf" or "waterfox" or "floorp") return AppKind.Firefox;
        var resources = Path.Combine(Path.GetDirectoryName(executable) ?? "", "resources");
        if (File.Exists(Path.Combine(resources, "app.asar")) || Directory.Exists(Path.Combine(resources, "app"))) return AppKind.Electron;
        return AppKind.Environment;
    }

    /// <summary>The proxy settings <paramref name="app"/> starts with, as the window shows them.</summary>
    public static IReadOnlyList<string> Describe(RoutedApp app, int port, string ownProfile) => app.Kind switch
    {
        AppKind.Chromium => [.. ChromiumFlags(port, ownProfile)],
        AppKind.Electron => [.. ElectronFlags(port)],
        AppKind.Firefox => [$"-profile \"{ownProfile}\" -no-remote", .. FirefoxPrefs(port)],
        _ => [.. ProxyVariables(port).Select(v => $"{v.Key}={v.Value}")],
    };

    public static Process Launch(RoutedApp app, int port, string ownProfile)
    {
        if (!File.Exists(app.Path)) throw new FileNotFoundException("the application is no longer where it was added from");
        var start = new ProcessStartInfo(app.Path)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(app.Path) ?? "",
        };

        IEnumerable<string> ours = [];
        switch (app.Kind)
        {
            case AppKind.Chromium:
                Directory.CreateDirectory(ownProfile);
                ours = ChromiumFlags(port, ownProfile);
                break;
            case AppKind.Electron:
                ours = ElectronFlags(port);
                break;
            case AppKind.Firefox:
                Directory.CreateDirectory(ownProfile);
                // user.js is read at every start and wins over what the profile saved, so a changed port takes effect.
                File.WriteAllLines(Path.Combine(ownProfile, "user.js"), FirefoxPrefs(port));
                ours = ["-profile", ownProfile, "-no-remote"];
                break;
            default:
                foreach (var (name, value) in ProxyVariables(port)) start.Environment[name] = value;
                break;
        }
        // The person's own arguments go last and as written: a URL, a switch the application has.
        var line = string.Join(' ', ours.Select(Quote));
        start.Arguments = string.IsNullOrWhiteSpace(app.Arguments) ? line : $"{line} {app.Arguments.Trim()}".Trim();
        return Process.Start(start) ?? throw new InvalidOperationException("the application did not start");
    }

    private static IEnumerable<string> ChromiumFlags(int port, string ownProfile) =>
    [
        // A profile of its own: a start that lands in an already-running profile is handed to that window and these
        // flags are dropped with it.
        $"--user-data-dir={ownProfile}",
        .. ElectronFlags(port),
        "--no-first-run",
        "--no-default-browser-check",
    ];

    private static IEnumerable<string> ElectronFlags(int port) =>
    [
        $"--proxy-server=socks5://127.0.0.1:{port}",
        // Names go to the proxy whole; this stops the browser's own prefetching from resolving them on this machine.
        "--host-resolver-rules=MAP * ~NOTFOUND , EXCLUDE 127.0.0.1",
        // WebRTC would otherwise send UDP directly, and with it this machine's own address.
        "--force-webrtc-ip-handling-policy=disable_non_proxied_udp",
    ];

    private static IEnumerable<string> FirefoxPrefs(int port) =>
    [
        "user_pref(\"network.proxy.type\", 1);",
        "user_pref(\"network.proxy.socks\", \"127.0.0.1\");",
        $"user_pref(\"network.proxy.socks_port\", {port});",
        "user_pref(\"network.proxy.socks_version\", 5);",
        "user_pref(\"network.proxy.socks_remote_dns\", true);",
        "user_pref(\"media.peerconnection.ice.proxy_only\", true);",
    ];

    /// <summary>socks5h: the name is resolved by the proxy, so nothing is looked up on this machine. Read by curl, git,
    /// .NET's and Go's HTTP clients, and most tools that honour a proxy at all.</summary>
    private static IEnumerable<KeyValuePair<string, string>> ProxyVariables(int port)
    {
        var proxy = $"socks5h://127.0.0.1:{port}";
        return
        [
            new("ALL_PROXY", proxy),
            new("HTTPS_PROXY", proxy),
            new("HTTP_PROXY", proxy),
            new("NO_PROXY", "localhost,127.0.0.1,::1"),
        ];
    }

    /// <summary>One argument as the Windows command-line convention reads it back (CommandLineToArgvW).</summary>
    internal static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0) return argument;
        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\') { backslashes++; continue; }
            // Backslashes count only before a quote: doubled there, and the quote itself escaped.
            quoted.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes).Append(c);
            backslashes = 0;
        }
        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
