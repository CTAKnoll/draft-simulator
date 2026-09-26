namespace DraftSimulator.Protocol;

public static class MessagePhaseValidator
{
    public static void ValidateClient(ClientMessageCode code, ProtocolPhase phase)
    {
        var valid = code switch
        {
            ClientMessageCode.Hello => phase == ProtocolPhase.Handshake,
            ClientMessageCode.SetName or ClientMessageCode.SetLobbyReady => phase is ProtocolPhase.LobbyOpen or ProtocolPhase.StartingCountdown,
            ClientMessageCode.SetSelection or ClientMessageCode.SetPickLocked => phase == ProtocolPhase.Drafting,
            ClientMessageCode.AssetNeed or ClientMessageCode.PreparationReady or ClientMessageCode.PreparationFailed => phase is ProtocolPhase.PreparingAssets or ProtocolPhase.Drafting or ProtocolPhase.Complete,
            ClientMessageCode.ReopenLobbyJoin => phase is ProtocolPhase.Complete or ProtocolPhase.LobbyOpen,
            _ => false,
        };
        if (!valid) throw new ProtocolException($"Client message {code} is invalid during phase {phase}.");
    }

    public static void ValidateHost(HostMessageCode code, ProtocolPhase phase)
    {
        var valid = code switch
        {
            HostMessageCode.Welcome => phase == ProtocolPhase.Handshake,
            HostMessageCode.Error or HostMessageCode.HostClosed => true,
            HostMessageCode.LobbySnapshot => phase is ProtocolPhase.LobbyOpen or ProtocolPhase.StartingCountdown,
            HostMessageCode.CountdownStarted => phase == ProtocolPhase.StartingCountdown,
            HostMessageCode.CountdownCancelled => phase == ProtocolPhase.LobbyOpen,
            HostMessageCode.AssetManifest => phase is ProtocolPhase.PreparingAssets or ProtocolPhase.Drafting,
            HostMessageCode.PreparationSnapshot => phase == ProtocolPhase.PreparingAssets,
            HostMessageCode.DraftSnapshot => phase == ProtocolPhase.Drafting,
            HostMessageCode.DraftCompleted or HostMessageCode.LobbyAvailability => phase == ProtocolPhase.Complete,
            HostMessageCode.Kicked => phase is ProtocolPhase.LobbyOpen or ProtocolPhase.StartingCountdown,
            _ => false,
        };
        if (!valid) throw new ProtocolException($"Host message {code} is invalid during phase {phase}.");
    }
}
