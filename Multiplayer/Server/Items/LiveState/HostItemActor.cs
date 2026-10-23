using Multiplayer.Core.Items.LiveState;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Session;

namespace Multiplayer.Server.Items.LiveState;

/// <summary>
///     Who the host's own world's item changes are made by: the host's player on a player host
///     (what its own hands did and what its game did around it, as always), or, on a host with no
///     player of its own (a dedicated host), the server itself (host-local-session stage 3): the
///     server commits its world's changes, simulates its own releases and witnesses its own impacts
///     as <see cref="ItemTransition.ServerActorId"/>.
/// </summary>
internal static class HostItemActor
{
    /// <summary>The host's own player, or null (none yet, or none at all: <see cref="IsServer"/>).</summary>
    public static ServerPlayer Player => NetworkServer.Instance?.LocalPlayer;

    /// <summary>
    ///     This host has no player of its own: a dedicated host. (A player host's player is admitted as
    ///     its server starts.)
    /// </summary>
    public static bool IsServer => NetworkServer.Instance != null &&
                                   NetworkServer.Instance.LocalPlayer == null;

    /// <summary>Whether the host's world has someone to commit its changes: its player, or the server itself.</summary>
    public static bool Present => Player != null || IsServer;

    /// <summary>The simulator id of the server itself, with no player of its own.</summary>
    public const byte ServerSimulatorId = 0;

    /// <summary>The simulator id of the host's own simulation in item leases and samples.</summary>
    public static byte SimulatorId => Player?.PlayerId ?? ServerSimulatorId;
}
