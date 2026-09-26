using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DraftSimulator.App.Services;
using DraftSimulator.Core;
using DraftSimulator.Infrastructure;

namespace DraftSimulator.App.ViewModels;

public sealed partial class HostLobbyViewModel : ObservableObject
{
    private readonly SessionCoordinator _coordinator;
    private readonly LocalAppDataPaths _paths;
    private readonly IFolderPickerService _folderPicker;
    private readonly CardDirectoryScanner _scanner = new();
    private HostConfiguration? _configuration;
    private CardScanResult? _scan;
    private readonly bool _offline;
    private readonly IScryfallSetImporter _scryfallImporter;
    private CancellationTokenSource? _scryfallCancellation;

    [ObservableProperty]
    private string _playerName = string.Empty;

    [ObservableProperty]
    private string _roomCode = string.Empty;

    [ObservableProperty]
    private string? _sessionStatus;

    [ObservableProperty]
    private bool _isHost;

    [ObservableProperty]
    private bool _isPreparing;

    [ObservableProperty]
    private string _cardDirectory = string.Empty;

    [ObservableProperty]
    private string _scryfallSetName = string.Empty;

    [ObservableProperty]
    private bool _forceScryfallFetch;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private bool _isScryfallImporting;

    [ObservableProperty]
    private string? _scryfallStatus;

    [ObservableProperty]
    private double _scryfallProgress;

    [ObservableProperty]
    private int _packSize = 15;

    [ObservableProperty]
    private int _cardsPerPick = 1;

    [ObservableProperty]
    private int _packsPerPlayer = 3;

    [ObservableProperty]
    private ReplacementMode _replacementMode = ReplacementMode.WithReplacement;

    [ObservableProperty]
    private DirectionRule _directionRule = DirectionRule.StartClockwise;

    [ObservableProperty]
    private string _scanSummary = "Choose a card directory to begin.";

    [ObservableProperty]
    private string? _warningMessage;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private double _preparationProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private bool _isReady;

    public HostLobbyViewModel(
        SessionCoordinator coordinator,
        LocalAppDataPaths paths,
        IFolderPickerService folderPicker,
        bool offline,
        IScryfallSetImporter scryfallImporter)
    {
        _coordinator = coordinator;
        _paths = paths;
        _folderPicker = folderPicker;
        _offline = offline;
        _scryfallImporter = scryfallImporter;
        ApplySnapshot(coordinator.Snapshot);
    }

    public bool CanEdit => IsHost && !IsReady && !IsPreparing && !IsScryfallImporting;
    public bool CanKick => IsHost && !IsPreparing;
    public bool CanInviteSteamFriends => IsHost && !_offline;
    public ObservableCollection<RaritySettingsViewModel> Rarities { get; } = [];
    public ObservableCollection<LobbyPlayerViewModel> Players { get; } = [];
    public IReadOnlyList<ReplacementMode> ReplacementModes { get; } = Enum.GetValues<ReplacementMode>();
    public IReadOnlyList<DirectionRule> DirectionRules { get; } = Enum.GetValues<DirectionRule>();

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (!CanEdit)
            return;
        var path = await _folderPicker.PickCardDirectoryAsync();
        if (path is null)
            return;
        CardDirectory = path;
        ScanDirectory();
    }

    [RelayCommand]
    private void Refresh()
    {
        if (CanEdit)
            ScanDirectory();
    }

    [RelayCommand]
    private async Task AddFromScryfallAsync()
    {
        if (!CanEdit) return;
        if (string.IsNullOrWhiteSpace(ScryfallSetName))
        {
            ScryfallStatus = "Enter a Scryfall set name or code.";
            return;
        }

        IsScryfallImporting = true;
        ScryfallProgress = 0;
        ScryfallStatus = "Starting Scryfall import...";
        _scryfallCancellation = new CancellationTokenSource();
        try
        {
            _configuration = new HostConfigurationStore(_paths.HostConfigurationFile, _paths.ScryfallDirectory)
                .CreateDefaultIfMissingAndLoad();
            var progress = new Progress<ScryfallImportProgress>(value =>
            {
                ScryfallStatus = value.Message;
                ScryfallProgress = value.TotalCards <= 0 ? 0 : (double)value.CompletedCards / value.TotalCards * 100;
            });
            var result = await _scryfallImporter.ImportAsync(
                ScryfallSetName,
                _configuration.ScryfallDirectory,
                ForceScryfallFetch,
                _configuration.MaxCardCount,
                _configuration.MaxSourceImageBytes,
                progress,
                _scryfallCancellation.Token);

            CardDirectory = result.Directory;
            ScanDirectory();
            ScryfallProgress = 100;
            ScryfallStatus = result.ReusedExistingDirectory
                ? $"Using existing {result.SetName} ({result.SetCode}) import."
                : $"Imported {result.ImportedCards} cards from {result.SetName} ({result.SetCode}); skipped {result.SkippedCards} without usable images.";
        }
        catch (OperationCanceledException) when (_scryfallCancellation?.IsCancellationRequested == true)
        {
            ScryfallStatus = "Scryfall import cancelled. The previous set folder was left unchanged.";
        }
        catch (OperationCanceledException)
        {
            ScryfallStatus = "Scryfall request timed out. The previous set folder was left unchanged.";
        }
        catch (Exception exception) when (exception is ScryfallImportException or HostConfigurationException or IOException or UnauthorizedAccessException)
        {
            ScryfallStatus = exception.Message;
        }
        finally
        {
            _scryfallCancellation.Dispose();
            _scryfallCancellation = null;
            IsScryfallImporting = false;
        }
    }

    [RelayCommand]
    private void CancelScryfallImport() => _scryfallCancellation?.Cancel();

    [RelayCommand]
    private async Task ReadyAsync()
    {
        HostDraftConfiguration? configuration = null;
        if (IsHost && !TryBuildConfiguration(out configuration, out var message))
        {
            ErrorMessage = message;
            return;
        }

        ErrorMessage = null;
        if (IsHost)
            await _coordinator.ConfigureHostAsync(configuration!);
        await _coordinator.SetReadyAsync(true);
        if (IsHost) ScanSummary = "Ready. Settings are locked until you unready.";
    }

    [RelayCommand]
    private void Unready()
    {
        IsReady = false;
        ScanSummary = _scan is null ? "Choose a card directory to begin." : BuildSummary(_scan);
        _ = _coordinator.SetReadyAsync(false);
    }

    [RelayCommand]
    private async Task SetNameAsync() => await _coordinator.SetNameAsync(PlayerName);

    [RelayCommand]
    private async Task InviteSteamFriendsAsync() => await _coordinator.OpenSteamInviteOverlayAsync();

    [RelayCommand]
    private async Task BackAsync()
    {
        if (!IsReady)
            await _coordinator.LeaveAsync();
    }

    public void ApplySnapshot(SessionSnapshot snapshot)
    {
        IsHost = snapshot.Role == SessionRole.Host;
        RoomCode = snapshot.RoomCode ?? string.Empty;
        SessionStatus = snapshot.StatusMessage;
        ErrorMessage = snapshot.ErrorCode is null ? null : SessionCoordinator.ErrorText(snapshot.ErrorCode.Value);
        IsPreparing = snapshot.Screen == ApplicationScreen.PreparingSession;
        PreparationProgress = snapshot.BytesTotal == 0 ? 0 : (double)snapshot.BytesReady / snapshot.BytesTotal * 100;
        var local = snapshot.Players.FirstOrDefault(player => player.IsLocal);
        if (local is not null)
        {
            PlayerName = local.Name;
            IsReady = local.IsReady;
        }
        Players.Clear();
        foreach (var player in snapshot.Players)
            Players.Add(new LobbyPlayerViewModel(player, IsHost && !player.IsHost, _coordinator));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanKick));
        OnPropertyChanged(nameof(CanInviteSteamFriends));
    }

    private void ScanDirectory()
    {
        ErrorMessage = null;
        WarningMessage = null;
        try
        {
            _configuration = new HostConfigurationStore(_paths.HostConfigurationFile, _paths.ScryfallDirectory).CreateDefaultIfMissingAndLoad();
            _scan = _scanner.Scan(CardDirectory, _configuration);
            ScanSummary = BuildSummary(_scan);
            WarningMessage = _scan.Warnings.Count == 0
                ? null
                : $"{_scan.Warnings.Count} file warning(s): {_scan.Warnings[0].Message}";
            ErrorMessage = _scan.Errors.Count == 0 ? null : _scan.Errors[0].Message;

            Rarities.Clear();
            foreach (var rarity in _scan.Definitions.Select(x => x.Rarity).Distinct().OrderBy(x => x))
            {
                var row = new RaritySettingsViewModel(rarity, PackSize);
                row.PropertyChanged += RaritySettingChanged;
                Rarities.Add(row);
            }
            ValidateCurrentConfiguration();
        }
        catch (HostConfigurationException exception)
        {
            _scan = null;
            ErrorMessage = exception.Message;
            ScanSummary = "Host configuration is invalid.";
        }
    }

    private bool TryBuildConfiguration(out HostDraftConfiguration? configuration, out string message)
    {
        configuration = null;
        if (_scan is null || !_scan.IsValid || _scan.Definitions.Count == 0 || _configuration is null)
        {
            message = "Select a valid directory containing at least one usable card.";
            return false;
        }

        var visibleSettings = Rarities.ToDictionary(
            row => row.Rarity,
            row => new RaritySettings(row.Minimum, row.Maximum, row.Weight));
        var allSettings = Enum.GetValues<Rarity>().ToDictionary(
            rarity => rarity,
            rarity => visibleSettings.GetValueOrDefault(rarity, new RaritySettings(0, 0, 0)));
        var settings = new DraftSettings(
            ReplacementMode,
            allSettings,
            PackSize,
            CardsPerPick,
            PacksPerPlayer,
            DirectionRule);
        var result = DraftSettingsValidator.Validate(
            settings,
            Math.Max(1, Players.Count),
            _scan.Definitions,
            new DraftLimits(_configuration.MaxPackSize, _configuration.MaxDraftCardInstances));
        message = result.IsValid ? string.Empty : result.Issues[0].Message;
        if (result.IsValid)
            configuration = new HostDraftConfiguration(settings, _scan.Definitions.ToArray(), _configuration);
        return result.IsValid;
    }

    private void RaritySettingChanged(object? sender, PropertyChangedEventArgs e) => ValidateCurrentConfiguration();

    private void ValidateCurrentConfiguration()
    {
        if (!IsHost || IsReady || IsPreparing || _scan is null) return;
        if (TryBuildConfiguration(out var configuration, out var message))
        {
            ErrorMessage = null;
            _ = _coordinator.ConfigureHostAsync(configuration!);
        }
        else ErrorMessage = message;
    }

    partial void OnPackSizeChanged(int value) => ValidateCurrentConfiguration();
    partial void OnCardsPerPickChanged(int value) => ValidateCurrentConfiguration();
    partial void OnPacksPerPlayerChanged(int value) => ValidateCurrentConfiguration();
    partial void OnReplacementModeChanged(ReplacementMode value) => ValidateCurrentConfiguration();
    partial void OnDirectionRuleChanged(DirectionRule value) => ValidateCurrentConfiguration();

    private static string BuildSummary(CardScanResult scan) =>
        scan.IsValid
            ? $"{scan.Definitions.Count} usable cards across {scan.Definitions.Select(x => x.Rarity).Distinct().Count()} rarities."
            : "The card directory has blocking errors.";
}

public sealed partial class LobbyPlayerViewModel : ObservableObject
{
    private readonly SessionCoordinator _coordinator;

    public LobbyPlayerViewModel(SessionPlayerSnapshot player, bool canKick, SessionCoordinator coordinator)
    {
        PlayerId = player.PlayerId;
        Name = player.Name;
        Role = player.IsHost ? "HOST" : player.IsLocal ? "YOU" : $"PLAYER {player.Order + 1}";
        ReadyStatus = player.IsReady ? "READY" : "NOT READY";
        PreparationStatus = player.PreparationStatus.ToString().ToUpperInvariant();
        CanKick = canKick;
        _coordinator = coordinator;
    }

    public DraftSimulator.Protocol.PlayerId PlayerId { get; }
    public string Name { get; }
    public string Role { get; }
    public string ReadyStatus { get; }
    public string PreparationStatus { get; }
    public bool CanKick { get; }

    [RelayCommand]
    private async Task KickAsync() => await _coordinator.KickAsync(PlayerId);
}
