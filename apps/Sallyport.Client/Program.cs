using Microsoft.Extensions.DependencyInjection;
using Sallyport.Runtime;
using Sallyport.Windows.Apps;
using Shenora;
using Shenora.Core.Shell;
using Shenora.Core.WebView;
using Shenora.Windows;

namespace Sallyport.Client;

// Sallyport.Client [profile.ovpn ...] [--add-game <folder or program>] [--tray] [--exit]
//   The tray and window: import profiles, connect them (each a loopback SOCKS5 port), start applications through them,
//   and route games through them (the router service, docs/design/product.md).
//   A profile path imports it; --tray starts out of sight; --exit asks the running client to disconnect all and leave.
//   A second start hands its arguments to the one already running.
// Sallyport's one program (D30): the same file is also the router service (--service, as the service manager starts it,
//   and each run's router) and setup (the release's file, which installs itself; /uninstall, which removes it).
internal static class Program
{
    /// <summary>The page's own host inside WebView2, and the port Vite serves it on while developing: the only places the
    /// window navigates to (MainForm).</summary>
    public const string VirtualHost = "sallyport.local";
    public const int DevPort = 3920;

    [STAThread]
    private static int Main(string[] args)
    {
        // Before anything of the window's: the service and setup open no window of it, and claim no single instance.
        if (args.FirstOrDefault() == Service.ServiceProgram.Argument) return Service.ServiceProgram.Run(args[1..]);
        if (Setup.SetupProgram.Asked(args, Environment.ProcessPath)) return Setup.SetupProgram.Run(args);

        using var claim = SingleInstance.Claim(args);
        if (claim is null || args.Contains("--exit")) return 0;

        var builder = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions
        {
            Args = args,
            ApplicationName = "Sallyport",
            // In the installed folder's data, shared by everyone on this computer (D25), or %LOCALAPPDATA% for a development build.
            Paths = new ShenoraPathsOptions { DataDirectory = UserData.Folder() },
        });

        builder.Services.AddSingleton(sp => new ProfileStore(sp.GetRequiredService<ShenoraPaths>().DataArea("profiles"), new DpapiProtector()));
        builder.Services.AddSingleton(sp => new ClientSettings(Path.Combine(sp.GetRequiredService<ShenoraPaths>().DataArea("config"), "client.json")));
        builder.Services.AddSingleton<Connections>();
        builder.Services.AddSingleton(sp =>
        {
            var paths = sp.GetRequiredService<ShenoraPaths>();
            var settings = sp.GetRequiredService<ClientSettings>();
            // The earlier stores, read only if the apps have never been migrated: a game per file, and the choices and
            // proxy apps in the client's settings.
            return new RoutedItemStore(paths.DataArea("library"), () =>
            {
                var folder = Path.Combine(paths.DataDir, "games");
                if (!Directory.Exists(folder)) return ([], 0, settings.LegacyChoices, settings.Apps);
                var games = new GameProfileStore(folder).List();
                var files = Directory.EnumerateFiles(folder, "*.json").Count();
                return (games, files - games.Count, settings.LegacyChoices, settings.Apps);
            });
        });
        builder.Services.AddSingleton<GameRunner>();
        builder.Services.AddSingleton<ClientCore>();

        builder.Services.AddSingleton(sp => new WebViewEnvironmentOptions
        {
            // What can be made again goes in the cache: the window's own, made anew when absent.
            UserDataFolder = sp.GetRequiredService<ShenoraPaths>().DataArea(Path.Combine("cache", "webview2")),
            IsDevelopment = sp.GetRequiredService<ShenoraEnvironment>().IsDevelopment,
        });
        builder.Services.AddSingleton<IWebViewResourceProvider>(sp =>
        {
            var environment = sp.GetRequiredService<ShenoraEnvironment>();
            return new EmbeddedResourceProvider(new EmbeddedResourceProviderOptions
            {
                Assembly = typeof(Program).Assembly,
                ResourcePrefix = "Sallyport.Client.wwwroot",
                FileFallbackDirectory = Path.Combine(sp.GetRequiredService<ShenoraPaths>().RootDir, "wwwroot"),
                PreferFiles = environment.IsDevelopment,
            });
        });
        builder.Services.AddSingleton(sp => new WebViewHostOptions
        {
            Environment = sp.GetRequiredService<WebViewEnvironmentOptions>(),
            Pipeline = sp.GetRequiredService<WebViewPipeline>(),
            // web/vite.config.ts serves here in development.
            DevUrl = $"http://localhost:{DevPort}",
            VirtualHost = Program.VirtualHost,
            ResourceProvider = sp.GetRequiredService<IWebViewResourceProvider>(),
            ColorScheme = sp.GetService<IColorScheme>(),
            BackgroundColor = Theme.Current().Background,
        });

        builder.Services.AddIpcModule<ClientModule>();
        builder.Services.AddSingleton(new Start(args, claim));
        builder.Services.AddSingleton<MainForm>();

        builder.PrewarmWebView2(app => app.Services.GetRequiredService<WebViewEnvironmentOptions>());
        builder.OnStarting(app => app.Services.GetRequiredService<IWebViewResourceProvider>().BeginWarmup());
        builder.UseWindows(new WindowsHostOptions
        {
            MainForm = sp => sp.GetRequiredService<MainForm>(),
            WindowState = new WindowStateHostOptions
            {
                Store = sp => new JsonFileWindowStateStore(Path.Combine(sp.GetRequiredService<ShenoraPaths>().DataArea("config"), "window-state.json")),
            },
        });

        using var app = builder.Build();
        app.Run();
        return 0;
    }
}
