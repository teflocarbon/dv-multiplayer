using Multiplayer.Shared.Session;

namespace Multiplayer.Shared.World;

/// <summary>
///     The server's side of the world, as Shared code reaches it without referencing the server's
///     types (docs/design/code-layout.md). <c>NetworkServer</c> implements it, in
///     <c>Server/Session/NetworkServer.World.cs</c>.
/// </summary>
public interface IWorldServer
{
    /// <summary>A generic switch changed in the server's world: tell every player.</summary>
    void BroadcastGenericSwitchState(uint netId, bool isOn);

    /// <summary>
    ///     A junction switched in the server's world, by anyone or anything but another player's
    ///     request (that one is relayed as it's applied): tell every player.
    /// </summary>
    void BroadcastJunctionSwitched(ushort netId, byte selectedBranch, Junction.SwitchMode mode);

    /// <summary>A turntable's target rotation changed in the server's world: tell every player.</summary>
    void BroadcastTurntableRotation(byte netId, float rotation);
}

public static class WorldServer
{
    /// <summary>This game's server, or null when it isn't one.</summary>
    public static IWorldServer Current => NetworkLifecycle.Instance?.LocalServer as IWorldServer;
}
