using System.Collections.Generic;
using Multiplayer.Shared.Players;

namespace Multiplayer.Shared.Session;

/// <summary>
///     The player at this keyboard, as Shared code reaches it without referencing the client:
///     its player id and the other players' avatars. Today the game's <c>NetworkClient</c> (on a
///     host, its loopback client); in host-local-session stage 3, the host's local player session.
///     Reached as <see cref="NetworkLifecycle.LocalSession"/>. Areas that need more declare their
///     own interface (docs/design/host-local-session.md, "What's built for it").
/// </summary>
public interface ILocalPlayerSession
{
    /// <summary>This player's id; 0 until the server has accepted the login.</summary>
    byte PlayerId { get; }

    /// <summary>The other players' avatars in this game's world.</summary>
    IReadOnlyCollection<NetworkedPlayer> RemotePlayers { get; }

    bool TryGetRemotePlayer(byte playerId, out NetworkedPlayer player);
}
