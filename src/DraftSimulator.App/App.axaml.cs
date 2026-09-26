using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DraftSimulator.App.Services;
using DraftSimulator.App.ViewModels;
using DraftSimulator.App.Views;
using DraftSimulator.Infrastructure;

namespace DraftSimulator.App;

public sealed partial class App : Application
{
#if DEBUG
    private OfflineDebugProfile? _offlineProfile;
#endif

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            var startup = Program.StartupOptions;
            var paths = CreatePaths(startup);
            var stateStore = new ClientStateStore(paths.ClientStateFile);
            var logger = new RollingLocalFileLogger(paths.LogsDirectory);
            var state = TryLoadState(stateStore);
            var configuration = new HostConfigurationStore(paths.HostConfigurationFile).CreateDefaultIfMissingAndLoad();
            var scryfallImporter = new ScryfallSetImporter();
            ISteamTransport transport = CreateTransport(startup, logger);
            var coordinator = new SessionCoordinator(transport, new SessionCoordinatorOptions
            {
                Paths = paths,
                Logger = logger,
                ClientStateStore = stateStore,
                ClientConfiguration = configuration,
            });
            var shell = new ShellViewModel(
                paths,
                stateStore,
                state,
                configuration,
                startup.Offline,
                scryfallImporter,
                new FolderPickerService(window),
                new CompletionActions(window),
                coordinator);
            window.DataContext = shell;
            desktop.Exit += async (_, _) =>
            {
                if (coordinator.Snapshot.Role == SessionRole.Host)
                    await coordinator.LeaveAsync();
                shell.Dispose();
                await coordinator.DisposeAsync();
                scryfallImporter.Dispose();
#if DEBUG
                _offlineProfile?.Dispose();
#endif
            };
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private LocalAppDataPaths CreatePaths(StartupOptions startup)
    {
        if (!startup.Offline) return new LocalAppDataPaths();
#if DEBUG
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _offlineProfile = OfflineDebugProfile.Open(localAppData, startup.Profile!);
        return _offlineProfile.Paths;
#else
        throw new InvalidOperationException("Offline mode is available only in Debug builds.");
#endif
    }

    private ISteamTransport CreateTransport(StartupOptions startup, ILocalLogger logger)
    {
        if (!startup.Offline)
            return new SteamTransport(new SteamworksNetAdapter(), SteamTransportOptions.FromBuildConfiguration(), logger);
#if DEBUG
        return new NamedPipeSteamTransport(new NamedPipeSteamTransportOptions(
            _offlineProfile!.SharedLobbyDirectory, _offlineProfile.PeerId));
#else
        throw new InvalidOperationException("Offline mode is available only in Debug builds.");
#endif
    }

    private static ClientState? TryLoadState(ClientStateStore store)
    {
        try
        {
            return store.Load();
        }
        catch
        {
            return null;
        }
    }
}
