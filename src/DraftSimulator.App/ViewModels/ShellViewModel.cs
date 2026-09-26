using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Threading;
using DraftSimulator.App.Services;
using DraftSimulator.Infrastructure;

namespace DraftSimulator.App.ViewModels;

public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly LocalAppDataPaths _paths;
    private readonly ClientStateStore _stateStore;
    private readonly IFolderPickerService _folderPicker;
    private readonly SessionCoordinator _coordinator;
    private readonly ICompletionActions _completionActions;
    private readonly TransformedBitmapCache _images;
    private readonly bool _offline;
    private readonly IScryfallSetImporter _scryfallImporter;

    [ObservableProperty]
    private object _currentPage;

    public ShellViewModel(
        LocalAppDataPaths paths,
        ClientStateStore stateStore,
        ClientState? state,
        HostConfiguration configuration,
        bool offline,
        IScryfallSetImporter scryfallImporter,
        IFolderPickerService folderPicker,
        ICompletionActions completionActions,
        SessionCoordinator coordinator)
    {
        _paths = paths;
        _stateStore = stateStore;
        _folderPicker = folderPicker;
        _completionActions = completionActions;
        _images = new TransformedBitmapCache(configuration.MaxMemoryCacheBytes);
        _offline = offline;
        _scryfallImporter = scryfallImporter;
        _coordinator = coordinator;
        _currentPage = CreateStartPage(state);
        coordinator.SnapshotChanged += snapshot => Dispatcher.UIThread.Post(() => ApplySnapshot(snapshot));
    }

    private void ApplySnapshot(SessionSnapshot snapshot)
    {
        if (snapshot.Screen is ApplicationScreen.Lobby or ApplicationScreen.StartingCountdown or ApplicationScreen.PreparingSession)
        {
            if (CurrentPage is not HostLobbyViewModel lobby)
            {
                DisposeCurrentPage();
                CurrentPage = lobby = new HostLobbyViewModel(_coordinator, _paths, _folderPicker, _offline, _scryfallImporter);
            }
            lobby.ApplySnapshot(snapshot);
            var localName = snapshot.Players.FirstOrDefault(player => player.IsLocal)?.Name;
            if (localName is not null) PersistPlayerName(localName);
        }
        else if (snapshot.Screen == ApplicationScreen.Drafting)
        {
            if (CurrentPage is not DraftViewModel draft) { DisposeCurrentPage(); CurrentPage = draft = new DraftViewModel(_coordinator, _images); }
            draft.ApplySnapshot(snapshot);
        }
        else if (snapshot.Screen == ApplicationScreen.Complete)
        {
            if (CurrentPage is not CompletionViewModel completion) { DisposeCurrentPage(); CurrentPage = completion = new CompletionViewModel(_coordinator, _completionActions, _images); }
            completion.ApplySnapshot(snapshot);
        }
        else if (CurrentPage is StartPageViewModel start)
            start.ApplySnapshot(snapshot);
        else
        {
            DisposeCurrentPage();
            CurrentPage = CreateStartPage(TryLoadState());
        }
    }

    private void DisposeCurrentPage()
    {
        if (CurrentPage is IDisposable disposable) disposable.Dispose();
    }

    private StartPageViewModel CreateStartPage(ClientState? state) => new(
        state?.LastAcceptedPlayerName ?? string.Empty,
        state?.LastNormalizedRoomCode ?? string.Empty,
        state,
        _offline,
        _coordinator);

    private void PersistPlayerName(string playerName)
    {
        var current = TryLoadState();
        _stateStore.Save(new ClientState(
            current?.LastHostSteamId,
            current?.LastSteamLobbyId,
            current?.LastApplicationSessionId,
            current?.LastNormalizedRoomCode,
            playerName,
            current?.PreviousSessionEndedNormally ?? true));
    }

    private ClientState? TryLoadState()
    {
        try
        {
            return _stateStore.Load();
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        DisposeCurrentPage();
        _images.Dispose();
    }
}
