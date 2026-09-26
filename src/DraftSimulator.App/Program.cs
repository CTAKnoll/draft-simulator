using Avalonia;

namespace DraftSimulator.App;

internal static class Program
{
    public static StartupOptions StartupOptions { get; private set; } = new(false, null);

    [STAThread]
    public static void Main(string[] args)
    {
#if OFFLINE_DEBUG_ARTIFACT
        StartupOptions = DraftSimulator.App.StartupOptions.Parse(args, offlineArtifact: true);
#else
        StartupOptions = DraftSimulator.App.StartupOptions.Parse(args);
#endif
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
