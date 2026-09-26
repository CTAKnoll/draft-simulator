using DraftSimulator.App.Services;
using DraftSimulator.Core;
using DraftSimulator.Infrastructure;
using DraftSimulator.Protocol;

namespace DraftSimulator.Integration.Tests;

public sealed class NamedPipeSessionCoordinatorTests
{
    [Fact]
    public async Task SeparateLocalTransportsRunDraftAndReconnectWithoutSteam()
    {
        var root = Path.Combine(Path.GetTempPath(), $"draftsim-local-integration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var shared = Path.Combine(root, "lobbies");
        var hostPaths = LocalAppDataPaths.FromRoot(Path.Combine(root, "host"));
        var clientPaths = LocalAppDataPaths.FromRoot(Path.Combine(root, "client"));
        var stateStore = new ClientStateStore(clientPaths.ClientStateFile);
        var sourcePath = Path.Combine(root, "Card.png");
        await File.WriteAllBytesAsync(sourcePath, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
        var sessionId = Guid.NewGuid();

        await using var host = CreateCoordinator(shared, 1001, hostPaths, sessionId);
        SessionCoordinator? client = CreateCoordinator(shared, 2002, clientPaths, stateStore: stateStore);
        try
        {
            await WaitUntilAsync(() => host.Snapshot.SteamStatus == SteamStatus.Ready && client.Snapshot.SteamStatus == SteamStatus.Ready);
            await host.HostAsync("Host");
            await WaitUntilAsync(() => host.Snapshot.Screen == ApplicationScreen.Lobby);
            var roomCode = host.Snapshot.RoomCode!;
            await client.JoinAsync("Client", roomCode);
            await WaitUntilAsync(() => host.Snapshot.Players.Count == 2 && client.Snapshot.Players.Count == 2);

            await host.ConfigureHostAsync(CreateConfiguration(sourcePath));
            await host.SetReadyAsync(true);
            await client.SetReadyAsync(true);
            await WaitUntilAsync(() => host.Snapshot.Screen == ApplicationScreen.Drafting && client.Snapshot.Screen == ApplicationScreen.Drafting);
            var playerId = client.Snapshot.LocalPlayerId;
            Assert.All(client.Snapshot.CurrentPack, card => Assert.True(File.Exists(card.AssetPath)));

            await Task.Delay(300);
            Assert.Equal(ApplicationScreen.Drafting, client.Snapshot.Screen);
            var reconnectState = stateStore.Load();
            Assert.NotNull(reconnectState);
            Assert.Equal(1001UL, reconnectState.LastHostSteamId);

            await client.DisposeAsync();
            client = null;
            await WaitUntilAsync(() => host.Snapshot.Players.Single(x => !x.IsHost).ConnectionStatus == ConnectionStatus.Disconnected);

            client = CreateCoordinator(shared, 2002, clientPaths, stateStore: stateStore);
            await WaitUntilAsync(() => client.Snapshot.SteamStatus == SteamStatus.Ready);
            await client.ReconnectAsync(reconnectState!);
            await WaitUntilAsync(() => client.Snapshot.Screen == ApplicationScreen.Drafting &&
                                       host.Snapshot.Players.Single(x => !x.IsHost).ConnectionStatus == ConnectionStatus.Connected);
            Assert.Equal(playerId, client.Snapshot.LocalPlayerId);
            Assert.Equal(host.Snapshot.PickRevision, client.Snapshot.PickRevision);
        }
        finally
        {
            if (client is not null) await client.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static SessionCoordinator CreateCoordinator(string shared, ulong peerId, LocalAppDataPaths paths,
        Guid? sessionId = null, ClientStateStore? stateStore = null)
    {
        var transport = new NamedPipeSteamTransport(new NamedPipeSteamTransportOptions(shared, new SteamPeerId(peerId))
        {
            HelloTimeout = TimeSpan.FromMilliseconds(200),
            ConnectTimeout = TimeSpan.FromSeconds(2),
        });
        return new SessionCoordinator(transport, new SessionCoordinatorOptions
        {
            CountdownDuration = TimeSpan.FromMilliseconds(20),
            Paths = paths,
            ClientStateStore = stateStore,
            RandomSourceFactory = static () => (7, new SeededRandomSource(7)),
            SessionIdFactory = sessionId is null ? Guid.NewGuid : () => sessionId.Value,
        });
    }

    private static HostDraftConfiguration CreateConfiguration(string sourcePath)
    {
        var rarities = Enum.GetValues<Rarity>().ToDictionary(x => x, _ => new RaritySettings(0, 0, 0));
        rarities[Rarity.Common] = new RaritySettings(0, 2, 1);
        return new HostDraftConfiguration(
            new DraftSettings(ReplacementMode.WithReplacement, rarities, 2, 1, 1, DirectionRule.AlwaysClockwise),
            [new CardDefinition(CardDefinitionId.New(), sourcePath, "Card", Rarity.Common)],
            HostConfiguration.Defaults);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }
}
