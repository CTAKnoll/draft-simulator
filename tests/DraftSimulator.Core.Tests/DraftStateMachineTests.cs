using DraftSimulator.Core;

namespace DraftSimulator.Core.Tests;

public sealed class DraftStateMachineTests
{
    [Fact]
    public void RejectsInvalidSelectionsAndRequiresExactCountToLock()
    {
        var machine = TestData.Machine();
        var player = machine.Players[0];

        Assert.Equal(DraftActionError.InvalidSelection,
            machine.SetSelection(player.Id, machine.PickRevision, [CardInstanceId.New()]).Error);
        Assert.Equal(DraftActionError.SelectionCount,
            machine.SetPickLocked(player.Id, machine.PickRevision, true).Error);
    }

    [Fact]
    public void LockedPlayerMustUnlockBeforeChangingSelection()
    {
        var machine = TestData.Machine();
        var player = machine.Players[0];
        var revision = machine.PickRevision;
        TestData.SelectAndLock(machine, player);

        Assert.Equal(DraftActionError.PlayerLocked,
            machine.SetSelection(player.Id, revision, [machine.Packs[player.CurrentPackId!.Value].Cards[1]]).Error);
        Assert.True(machine.SetPickLocked(player.Id, revision, false).Succeeded);
        Assert.False(player.IsDraftLocked);
    }

    [Fact]
    public void FinalLockResolvesAtomicallyAndMakesOldRevisionStale()
    {
        var machine = TestData.Machine();
        var revision = machine.PickRevision;
        TestData.SelectAndLock(machine, machine.Players[0]);
        TestData.SelectAndLock(machine, machine.Players[1]);

        Assert.Equal(revision + 1, machine.PickRevision);
        Assert.All(machine.Players, x => Assert.Single(x.Collection));
        Assert.Equal(DraftActionError.StaleRevision,
            machine.SetPickLocked(machine.Players[0].Id, revision, false).Error);
    }

    [Theory]
    [InlineData(DirectionRule.AlwaysClockwise, 0, 1)]
    [InlineData(DirectionRule.AlwaysCounterclockwise, 0, -1)]
    [InlineData(DirectionRule.StartClockwise, 0, 1)]
    [InlineData(DirectionRule.StartClockwise, 1, -1)]
    [InlineData(DirectionRule.StartCounterclockwise, 0, -1)]
    [InlineData(DirectionRule.StartCounterclockwise, 1, 1)]
    public void DirectionRulesMatchRound(DirectionRule rule, int round, int expected)
    {
        Assert.Equal(expected, TestData.Settings(direction: rule).DirectionForRound(round));
    }

    [Fact]
    public void PacksPassClockwiseToTheNextPlayer()
    {
        var machine = TestData.Machine(playerCount: 3, packSize: 2);
        var initial = machine.Players.Select(x => x.CurrentPackId).ToArray();
        foreach (var player in machine.Players.ToArray())
            TestData.SelectAndLock(machine, player);

        Assert.Equal(initial[0], machine.Players[1].CurrentPackId);
        Assert.Equal(initial[1], machine.Players[2].CurrentPackId);
        Assert.Equal(initial[2], machine.Players[0].CurrentPackId);
    }

    [Fact]
    public void PacksPassCounterclockwiseToThePreviousPlayer()
    {
        var machine = TestData.Machine(playerCount: 3, packSize: 2, direction: DirectionRule.AlwaysCounterclockwise);
        var initial = machine.Players.Select(x => x.CurrentPackId).ToArray();
        foreach (var player in machine.Players.ToArray())
            TestData.SelectAndLock(machine, player);

        Assert.Equal(initial[0], machine.Players[2].CurrentPackId);
    }

    [Fact]
    public void FewerThanPickSizeIsCollectedAutomatically()
    {
        var machine = TestData.Machine(playerCount: 2, packSize: 5, pickSize: 2);
        for (var turn = 0; turn < 2; turn++)
        {
            foreach (var player in machine.Players.ToArray())
            {
                var pack = machine.Packs[player.CurrentPackId!.Value];
                var selection = pack.Cards.Take(2).ToArray();
                Assert.True(machine.SetSelection(player.Id, machine.PickRevision, selection).Succeeded);
                Assert.True(machine.SetPickLocked(player.Id, machine.PickRevision, true).Succeeded);
            }
        }

        Assert.Equal(DraftPhase.Complete, machine.Phase);
        Assert.All(machine.Players, x => Assert.Equal(5, x.Collection.Count));
    }

    [Fact]
    public void ExactlyPickSizeStillRequiresExplicitLock()
    {
        var machine = TestData.Machine(playerCount: 1, packSize: 2, pickSize: 2);
        Assert.Equal(DraftPhase.Drafting, machine.Phase);
        Assert.False(machine.Players[0].IsDraftLocked);
    }

    [Fact]
    public void ForceReadyPreservesSelectionsAndIncludesDisconnectedPlayers()
    {
        var machine = TestData.Machine(playerCount: 3, packSize: 6, pickSize: 2, randomValues: [1]);
        var host = machine.Players.Single(x => x.SteamId == 1);
        var other = machine.Players.First(x => x.Id != host.Id);
        var disconnected = machine.Players.Last(x => x.Id != host.Id && x.Id != other.Id);
        var hostCards = machine.Packs[host.CurrentPackId!.Value].Cards.Take(2).ToArray();
        var preserved = machine.Packs[other.CurrentPackId!.Value].Cards[0];
        Assert.True(machine.SetSelection(host.Id, machine.PickRevision, hostCards).Succeeded);
        Assert.True(machine.SetPickLocked(host.Id, machine.PickRevision, true).Succeeded);
        Assert.True(machine.SetSelection(other.Id, machine.PickRevision, [preserved]).Succeeded);
        Assert.True(machine.Disconnect(disconnected.Id).Succeeded);

        Assert.True(machine.ForceReady(host.Id).Succeeded);

        Assert.Contains(preserved, other.Collection);
        Assert.Equal(2, other.Collection.Count);
        Assert.Equal(2, disconnected.Collection.Count);
        Assert.False(disconnected.IsConnected);
    }

    [Fact]
    public void ReconnectRequiresMatchingRetainedSteamIdentity()
    {
        var machine = TestData.Machine();
        var player = machine.Players[1];
        machine.Disconnect(player.Id);

        Assert.Equal(DraftActionError.UnknownPlayer, machine.Reconnect(player.Id, 999).Error);
        Assert.True(machine.Reconnect(player.Id, player.SteamId).Succeeded);
        Assert.True(player.IsConnected);
    }

    [Fact]
    public void OnePlayerPassesPackBackToSelfAndCompletes()
    {
        var machine = TestData.Machine(playerCount: 1, packSize: 2);
        var player = machine.Players[0];
        var pack = player.CurrentPackId;
        TestData.SelectAndLock(machine, player);

        Assert.Equal(pack, player.CurrentPackId);
        TestData.SelectAndLock(machine, player);
        Assert.Equal(DraftPhase.Complete, machine.Phase);
        Assert.Equal(2, player.Collection.Count);
    }

    [Fact]
    public void AlternatingRoundsChangeDirectionOnlyAfterExhaustion()
    {
        var machine = TestData.Machine(playerCount: 2, packSize: 1, rounds: 2, direction: DirectionRule.StartClockwise);
        Assert.Equal(1, machine.ActiveDirection);
        foreach (var player in machine.Players.ToArray())
            TestData.SelectAndLock(machine, player);

        Assert.Equal(2, machine.PackRound);
        Assert.Equal(-1, machine.ActiveDirection);
    }

    [Fact]
    public void FullyAutomaticRoundsCompleteIteratively()
    {
        var machine = TestData.Machine(playerCount: 1, packSize: 1, pickSize: 2, rounds: 100);

        Assert.Equal(DraftPhase.Complete, machine.Phase);
        Assert.Equal(100, machine.PackRound);
        Assert.Equal(100, machine.Players[0].Collection.Count);
    }
}
