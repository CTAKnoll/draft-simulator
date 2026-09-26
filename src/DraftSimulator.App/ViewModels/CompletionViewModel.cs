using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DraftSimulator.App.Services;

namespace DraftSimulator.App.ViewModels;

public sealed partial class CompletionViewModel : ObservableObject, IDisposable
{
    private readonly SessionCoordinator _coordinator;
    private readonly ICompletionActions _actions;
    private string _playerName = string.Empty;
    private readonly TransformedBitmapCache _images;

    [ObservableProperty] private bool _isHost;
    [ObservableProperty] private bool _lobbyAvailable;
    [ObservableProperty] private string? _statusMessage;

    public CompletionViewModel(SessionCoordinator coordinator, ICompletionActions actions, TransformedBitmapCache images)
    {
        _coordinator = coordinator; _actions = actions; _images = images; ApplySnapshot(coordinator.Snapshot);
    }

    public ObservableCollection<DraftCardViewModel> Collection { get; } = [];

    public void ApplySnapshot(SessionSnapshot snapshot)
    {
        IsHost = snapshot.Role == SessionRole.Host;
        LobbyAvailable = snapshot.LobbyAvailable;
        _playerName = snapshot.Players.FirstOrDefault(x => x.IsLocal)?.Name ?? _playerName;
        foreach (var card in Collection) card.Dispose();
        Collection.Clear();
        foreach (var card in snapshot.Collection) Collection.Add(new(card, false, null, _images));
    }

    [RelayCommand]
    private async Task CopyAsync()
    {
        await _actions.CopyTextAsync(string.Join(Environment.NewLine, Collection.Select(x => $"# {x.ExportName}")));
        StatusMessage = "Card list copied.";
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        await using var output = await _actions.OpenExportFileAsync();
        if (output is null) return;
        _coordinator.ExportCardSet(output);
        await output.FlushAsync();
        StatusMessage = "Cockatrice XML exported.";
    }

    [RelayCommand] private Task ReopenLobbyAsync() => _coordinator.ReopenLobbyAsync();
    [RelayCommand] private Task JoinLobbyAsync() => _coordinator.JoinReopenedLobbyAsync(_playerName);
    [RelayCommand] private Task ExitAsync() => _coordinator.LeaveAsync();
    public void Dispose() { foreach (var card in Collection) card.Dispose(); }
}
