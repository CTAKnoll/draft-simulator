namespace DraftSimulator.Core;

public enum DraftActionError
{
    None,
    InvalidPhase,
    UnknownPlayer,
    StaleRevision,
    InvalidSelection,
    PlayerLocked,
    SelectionCount,
    CannotUnlock,
    HostMustBeLocked,
}

public readonly record struct DraftActionResult(bool Succeeded, DraftActionError Error)
{
    public static DraftActionResult Success => new(true, DraftActionError.None);
    public static DraftActionResult Failure(DraftActionError error) => new(false, error);
}

public sealed class DraftStateMachine
{
    private readonly DraftSettings _settings;
    private readonly IRandomSource _random;
    private readonly PlayerId _hostId;
    private readonly IReadOnlyList<IReadOnlyList<Pack>> _rounds;
    private readonly Dictionary<PackId, Pack> _packs;
    private readonly List<PlayerState> _players;
    private readonly Dictionary<PlayerId, PlayerState> _playersById;
    private bool _resolving;
    private bool _processingAutomaticPicks;
    private int _roundIndex;

    public DraftStateMachine(DraftSettings settings, GeneratedDraft generatedDraft, PlayerId hostId, IRandomSource random)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        ArgumentNullException.ThrowIfNull(generatedDraft);
        _random = random ?? throw new ArgumentNullException(nameof(random));
        if (generatedDraft.PlayerOrder.All(x => x.Id != hostId))
            throw new ArgumentException("The host must be part of the generated draft.", nameof(hostId));

        _hostId = hostId;
        _rounds = generatedDraft.PackRounds;
        _packs = _rounds.SelectMany(x => x).ToDictionary(x => x.Id);
        _players = generatedDraft.PlayerOrder.Select((x, order) => new PlayerState(x.Id, x.SteamId, x.Name, order, x.IsConnected)).ToList();
        _playersById = _players.ToDictionary(x => x.Id);
        Instances = generatedDraft.Instances;
        Phase = DraftPhase.Drafting;
        DraftRevision = 1;
        PickRevision = 1;
        PickNumber = 1;
        AssignRound();
        ProcessAutomaticPicks();
    }

    public DraftPhase Phase { get; private set; }
    public long DraftRevision { get; private set; }
    public long PickRevision { get; private set; }
    public int PackRound => Math.Min(_roundIndex + 1, _rounds.Count);
    public int TotalPackRounds => _rounds.Count;
    public int PickNumber { get; private set; }
    public int ActiveDirection => _settings.DirectionForRound(_roundIndex);
    public IReadOnlyList<PlayerState> Players => _players;
    public IReadOnlyDictionary<PackId, Pack> Packs => _packs;
    public IReadOnlyDictionary<CardInstanceId, CardInstance> Instances { get; }
    public bool CanForceReady => Phase == DraftPhase.Drafting && _playersById[_hostId].IsDraftLocked;

    public DraftActionResult SetSelection(PlayerId playerId, long pickRevision, IEnumerable<CardInstanceId> selection)
    {
        if (!TryGetPlayer(playerId, pickRevision, out var player, out var failure))
            return failure;
        ArgumentNullException.ThrowIfNull(selection);
        if (player.IsDraftLocked)
            return DraftActionResult.Failure(DraftActionError.PlayerLocked);

        var submitted = selection.ToList();
        var requested = submitted.ToHashSet();
        if (requested.Count > _settings.CardsPerPick)
            return DraftActionResult.Failure(DraftActionError.SelectionCount);
        if (requested.Count != submitted.Count)
            return DraftActionResult.Failure(DraftActionError.InvalidSelection);
        var pack = CurrentPack(player);
        if (!requested.IsSubsetOf(pack.Cards))
            return DraftActionResult.Failure(DraftActionError.InvalidSelection);
        if (player.MutableSelection.SetEquals(requested))
            return DraftActionResult.Success;

        player.MutableSelection.Clear();
        player.MutableSelection.UnionWith(requested);
        DraftRevision++;
        return DraftActionResult.Success;
    }

    public DraftActionResult SetPickLocked(PlayerId playerId, long pickRevision, bool locked)
    {
        if (!TryGetPlayer(playerId, pickRevision, out var player, out var failure))
            return failure;
        if (player.IsDraftLocked == locked)
            return DraftActionResult.Success;

        if (locked)
        {
            if (player.Selection.Count != _settings.CardsPerPick)
                return DraftActionResult.Failure(DraftActionError.SelectionCount);
            player.IsDraftLocked = true;
            DraftRevision++;
            if (_players.All(x => x.IsDraftLocked))
                ResolveLockedPicks();
            return DraftActionResult.Success;
        }

        if (_resolving || _players.All(x => x.IsDraftLocked))
            return DraftActionResult.Failure(DraftActionError.CannotUnlock);
        player.IsDraftLocked = false;
        DraftRevision++;
        return DraftActionResult.Success;
    }

    public DraftActionResult ForceReady(PlayerId requestingPlayer)
    {
        if (Phase != DraftPhase.Drafting)
            return DraftActionResult.Failure(DraftActionError.InvalidPhase);
        if (requestingPlayer != _hostId || !_playersById[_hostId].IsDraftLocked)
            return DraftActionResult.Failure(DraftActionError.HostMustBeLocked);

        foreach (var player in _players.Where(x => !x.IsDraftLocked))
        {
            var available = CurrentPack(player).Cards.Where(x => !player.MutableSelection.Contains(x)).ToList();
            while (player.MutableSelection.Count < _settings.CardsPerPick)
            {
                var index = _random.NextInt(available.Count);
                player.MutableSelection.Add(available[index]);
                available.RemoveAt(index);
            }

            player.IsDraftLocked = true;
        }

        DraftRevision++;
        ResolveLockedPicks();
        return DraftActionResult.Success;
    }

    public DraftActionResult Disconnect(PlayerId playerId)
    {
        if (!_playersById.TryGetValue(playerId, out var player))
            return DraftActionResult.Failure(DraftActionError.UnknownPlayer);
        if (!player.IsConnected)
            return DraftActionResult.Success;
        player.IsConnected = false;
        DraftRevision++;
        return DraftActionResult.Success;
    }

    public DraftActionResult Reconnect(PlayerId playerId, ulong steamId)
    {
        if (!_playersById.TryGetValue(playerId, out var player) || player.SteamId != steamId)
            return DraftActionResult.Failure(DraftActionError.UnknownPlayer);
        if (player.IsConnected)
            return DraftActionResult.Success;
        player.IsConnected = true;
        DraftRevision++;
        return DraftActionResult.Success;
    }

    public void Close()
    {
        Phase = DraftPhase.Closed;
        DraftRevision++;
    }

    private bool TryGetPlayer(PlayerId id, long revision, out PlayerState player, out DraftActionResult failure)
    {
        player = null!;
        if (Phase != DraftPhase.Drafting)
        {
            failure = DraftActionResult.Failure(DraftActionError.InvalidPhase);
            return false;
        }
        if (revision != PickRevision)
        {
            failure = DraftActionResult.Failure(DraftActionError.StaleRevision);
            return false;
        }
        if (!_playersById.TryGetValue(id, out player!))
        {
            failure = DraftActionResult.Failure(DraftActionError.UnknownPlayer);
            return false;
        }

        failure = DraftActionResult.Success;
        return true;
    }

    private Pack CurrentPack(PlayerState player) => _packs[player.CurrentPackId ?? throw new InvalidOperationException("Player has no current pack.")];

    private void ResolveLockedPicks()
    {
        _resolving = true;
        foreach (var player in _players)
        {
            var pack = CurrentPack(player);
            pack.Remove(player.Selection);
            player.MutableCollection.AddRange(player.Selection);
            player.MutableSelection.Clear();
            player.IsDraftLocked = false;
        }

        if (_rounds[_roundIndex].All(x => x.Cards.Count == 0))
        {
            _roundIndex++;
            if (_roundIndex == _rounds.Count)
            {
                foreach (var player in _players)
                    player.CurrentPackId = null;
                Phase = DraftPhase.Complete;
                DraftRevision++;
                PickRevision++;
                _resolving = false;
                return;
            }

            PickNumber = 1;
            AssignRound();
        }
        else
        {
            PassPacks();
            PickNumber++;
        }

        PickRevision++;
        DraftRevision++;
        _resolving = false;
        ProcessAutomaticPicks();
    }

    private void AssignRound()
    {
        var packs = _rounds[_roundIndex];
        for (var index = 0; index < _players.Count; index++)
        {
            packs[index].CurrentHolder = _players[index].Id;
            _players[index].CurrentPackId = packs[index].Id;
            _players[index].MutableSelection.Clear();
            _players[index].IsDraftLocked = false;
        }
    }

    private void PassPacks()
    {
        var current = _players.Select(CurrentPack).ToArray();
        for (var source = 0; source < _players.Count; source++)
        {
            var target = Mod(source + ActiveDirection, _players.Count);
            current[source].CurrentHolder = _players[target].Id;
            _players[target].CurrentPackId = current[source].Id;
        }
    }

    private void ProcessAutomaticPicks()
    {
        if (_processingAutomaticPicks)
            return;

        _processingAutomaticPicks = true;
        try
        {
            while (Phase == DraftPhase.Drafting)
            {
                var changed = false;
                foreach (var player in _players.Where(x => !x.IsDraftLocked))
                {
                    var pack = CurrentPack(player);
                    if (pack.Cards.Count >= _settings.CardsPerPick)
                        continue;
                    player.MutableSelection.UnionWith(pack.Cards);
                    player.IsDraftLocked = true;
                    changed = true;
                }

                if (!changed || !_players.All(x => x.IsDraftLocked))
                    return;
                ResolveLockedPicks();
            }
        }
        finally
        {
            _processingAutomaticPicks = false;
        }
    }

    private static int Mod(int value, int modulus) => (value % modulus + modulus) % modulus;
}
