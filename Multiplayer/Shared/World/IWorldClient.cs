using Multiplayer.Shared.Session;

namespace Multiplayer.Shared.World;

/// <summary>
///     The world requests a player at this keyboard sends, as Shared code makes them without
///     referencing the client (docs/design/code-layout.md). <c>NetworkClient</c> implements it, in
///     <c>Client/Session/NetworkClient.World.cs</c>. On the host the requests it handles in-process
///     go straight to the server (docs/design/host-local-session.md).
/// </summary>
public interface IWorldClient
{
    void SendJunctionSwitched(ushort netId, byte selectedBranch, Junction.SwitchMode mode);

    void SendTurntableRotation(byte netId, float rotation);

    void SendGenericSwitchState(uint netId, bool isOn);
}

public static class WorldClient
{
    /// <summary>This game's player session, or null when no client is running.</summary>
    public static IWorldClient Current => NetworkLifecycle.Instance?.LocalSession as IWorldClient;
}
