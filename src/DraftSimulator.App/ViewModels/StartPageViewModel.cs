using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DraftSimulator.App.Services;
using DraftSimulator.Core;
using DraftSimulator.Infrastructure;

namespace DraftSimulator.App.ViewModels;

public sealed partial class StartPageViewModel : ObservableObject
{
    private readonly SessionCoordinator _coordinator;
    private readonly ClientState? _reconnectState;

    [ObservableProperty]
    private string _playerName;

    [ObservableProperty]
    private string _roomCode;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _steamInviteAvailable;

    public StartPageViewModel(string playerName, string roomCode, ClientState? reconnectState, bool offline, SessionCoordinator coordinator)
    {
        _playerName = playerName;
        _roomCode = roomCode;
        _reconnectState = reconnectState;
        TransportDescription = offline ? "Offline transport: local machine only" : "Steam transport target: AppID 480";
        CanReconnect = reconnectState is
        {
            PreviousSessionEndedNormally: false,
            LastHostSteamId: not null,
            LastApplicationSessionId: not null,
            LastAcceptedPlayerName: not null,
        };
        _coordinator = coordinator;
        ApplySnapshot(coordinator.Snapshot);
    }

    public bool CanReconnect { get; }
    public string TransportDescription { get; }

    [RelayCommand]
    private async Task HostAsync()
    {
        if (!PlayerNameValidator.TryNormalize(PlayerName, [], out var normalized))
        {
            StatusMessage = "Use 1-16 printable ASCII characters for your name.";
            return;
        }

        StatusMessage = null;
        await _coordinator.HostAsync(normalized);
    }

    [RelayCommand]
    private async Task JoinAsync()
    {
        if (!PlayerNameValidator.TryNormalize(PlayerName, [], out var normalized))
        {
            StatusMessage = SessionCoordinator.ErrorText(DraftSimulator.Protocol.ErrorCode.NameInvalid);
            return;
        }
        if (!SteamRoomCode.TryNormalize(RoomCode, out var roomCode))
        {
            StatusMessage = SessionCoordinator.ErrorText(DraftSimulator.Protocol.ErrorCode.RoomCodeInvalid);
            return;
        }
        await _coordinator.JoinAsync(normalized, roomCode);
    }

    [RelayCommand]
    private async Task ReconnectAsync()
    {
        if (_reconnectState is null) return;
        StatusMessage = null;
        await _coordinator.ReconnectAsync(_reconnectState);
    }

    [RelayCommand]
    private async Task JoinSteamInviteAsync() => await _coordinator.JoinSteamInviteAsync(PlayerName);

    public void ApplySnapshot(SessionSnapshot snapshot)
    {
        StatusMessage = snapshot.StatusMessage;
        SteamInviteAvailable = snapshot.SteamInviteAvailable;
    }
}
