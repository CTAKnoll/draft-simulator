using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DraftSimulator.App.Services;
using DraftSimulator.Protocol;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace DraftSimulator.App.ViewModels;

public sealed partial class DraftViewModel : ObservableObject, IDisposable
{
    private readonly SessionCoordinator _coordinator;
    private readonly TransformedBitmapCache _images;
    private DraftCardViewModel? _previewCard;

    [ObservableProperty] private Bitmap? _previewImage;
    [ObservableProperty] private bool _isZoomOpen;
    [ObservableProperty] private bool _isZoomEnabled;
    [ObservableProperty] private bool _canLock;
    [ObservableProperty] private bool _canUnlock;
    [ObservableProperty] private bool _canForceReady;
    [ObservableProperty] private string _roundText = string.Empty;

    public DraftViewModel(SessionCoordinator coordinator, TransformedBitmapCache images)
    {
        _coordinator = coordinator; _images = images;
        ApplySnapshot(coordinator.Snapshot);
    }

    public ObservableCollection<DraftCardViewModel> CurrentPack { get; } = [];
    public ObservableCollection<DraftCardViewModel> Collection { get; } = [];
    public ObservableCollection<DraftPlayerStatusViewModel> Players { get; } = [];

    public void ApplySnapshot(SessionSnapshot snapshot)
    {
        var selected = snapshot.SelectedInstanceIds.ToHashSet();
        var previewInstanceId = _previewCard?.InstanceId;
        Preview(null);
        DisposeCards(CurrentPack); CurrentPack.Clear();
        foreach (var card in snapshot.CurrentPack)
            CurrentPack.Add(new(card, selected.Contains(card.InstanceId), ToggleCardAsync, _images));
        if (previewInstanceId is { } instanceId)
            Preview(CurrentPack.FirstOrDefault(card => card.InstanceId == instanceId));
        DisposeCards(Collection); Collection.Clear();
        foreach (var card in snapshot.Collection)
            Collection.Add(new(card, false, null, _images));
        Players.Clear();
        foreach (var player in snapshot.Players.OrderBy(x => x.Order))
            Players.Add(new(player.Name, player.ConnectionStatus.ToString(), player.IsDraftLocked ? "LOCKED" : "PICKING", player.CurrentPackCardCount));
        CanLock = snapshot.CanLock;
        CanUnlock = snapshot.CanUnlock;
        CanForceReady = snapshot.CanForceReady;
        RoundText = $"PACK {snapshot.PackRound}/{snapshot.TotalPackRounds}  {snapshot.Direction.ToString().ToUpperInvariant()}";
    }

    public void Preview(DraftCardViewModel? card)
    {
        if (_previewCard is not null)
            _previewCard.PropertyChanged -= PreviewCardPropertyChanged;
        _previewCard = card;
        if (_previewCard is not null)
            _previewCard.PropertyChanged += PreviewCardPropertyChanged;
        PreviewImage = card?.Image;
        IsZoomOpen = IsZoomEnabled && card is not null;
    }

    partial void OnIsZoomEnabledChanged(bool value) => IsZoomOpen = value && _previewCard is not null;

    private void PreviewCardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DraftCardViewModel.Image) && ReferenceEquals(sender, _previewCard))
            PreviewImage = _previewCard?.Image;
    }

    private Task ToggleCardAsync(DraftCardViewModel card)
    {
        card.IsSelected = !card.IsSelected;
        return _coordinator.ToggleSelectionAsync(card.InstanceId);
    }

    [RelayCommand] private Task LockAsync() => _coordinator.SetPickLockedAsync(true);
    [RelayCommand] private Task UnlockAsync() => _coordinator.SetPickLockedAsync(false);
    [RelayCommand] private Task ForceReadyAsync() => _coordinator.ForceReadyAsync();

    public void Dispose() { Preview(null); DisposeCards(CurrentPack); DisposeCards(Collection); }
    private static void DisposeCards(IEnumerable<DraftCardViewModel> cards) { foreach (var card in cards) card.Dispose(); }
}

public sealed partial class DraftCardViewModel : ObservableObject, IDisposable
{
    private readonly Func<DraftCardViewModel, Task>? _toggle;
    private BitmapLease? _lease;
    private bool _disposed;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private Bitmap? _image;

    public DraftCardViewModel(SessionCardSnapshot card, bool selected, Func<DraftCardViewModel, Task>? toggle, TransformedBitmapCache images)
    {
        InstanceId = card.InstanceId; AssetPath = card.AssetPath; ExportName = card.ExportName; _isSelected = selected; _toggle = toggle;
        _ = LoadAsync(images);
    }

    public CardInstanceId InstanceId { get; }
    public string AssetPath { get; }
    public string? ExportName { get; }
    public bool CanSelect => _toggle is not null;

    [RelayCommand]
    private async Task ToggleAsync()
    {
        if (_toggle is not null) await _toggle(this);
    }

    private async Task LoadAsync(TransformedBitmapCache images)
    {
        try
        {
            var lease = await images.AcquireAsync(AssetPath);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed) { lease.Dispose(); return; }
                _lease = lease; Image = lease.Bitmap;
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    public void Dispose() { _disposed = true; Image = null; _lease?.Dispose(); _lease = null; }
}

public sealed record DraftPlayerStatusViewModel(string Name, string Connection, string PickStatus, int CardCount);
