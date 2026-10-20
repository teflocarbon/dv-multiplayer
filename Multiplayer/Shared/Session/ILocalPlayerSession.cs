using System;
using System.Collections.Generic;
using Multiplayer.Shared.Players;

namespace Multiplayer.Shared.Session;

/// <summary>
///     The player at this keyboard, as Shared and role code reach it without naming the client:
///     who it is, its ping, the other players' avatars and its chat. Today the game's
///     <c>NetworkClient</c> (on a host, its loopback client); in host-local-session stage 3, the
///     host's local player session, so code that runs for the host's player too (presentation,
///     the player list, chat, the map) reads this, never <c>NetworkClient</c>. Reached as
///     <see cref="NetworkLifecycle.LocalSession"/>; null when nobody plays here. Area requests
///     are on the areas' own interfaces (<c>IItemClient</c>, <c>IStationsClient</c>, ...; see
///     docs/design/host-local-session.md, "What's built for it").
/// </summary>
public interface ILocalPlayerSession
{
    /// <summary>
    ///     This player's id; 0 until the server has accepted the login. The one "local player id":
    ///     <see cref="NetworkRole.LocalPlayerId"/> reads it.
    /// </summary>
    byte PlayerId { get; }

    /// <summary>The name other players see: the username, with the crew name in brackets if there is one.</summary>
    string DisplayName { get; }

    /// <summary>One-way latency to the server in milliseconds.</summary>
    int Ping { get; }

    /// <summary>The other players' avatars in this game's world.</summary>
    IReadOnlyCollection<NetworkedPlayer> RemotePlayers { get; }

    bool TryGetRemotePlayer(byte playerId, out NetworkedPlayer player);

    /// <summary>Another player's avatar was added to this game's world.</summary>
    event Action<NetworkedPlayer> RemotePlayerConnected;

    /// <summary>Another player left; their avatar is about to be removed.</summary>
    event Action<NetworkedPlayer> RemotePlayerDisconnected;

    /// <summary>Another player's preferences (character model) changed.</summary>
    event Action<NetworkedPlayer> RemotePlayerPrefsUpdated;

    /// <summary>Sends a chat line or command, as typed, to the server.</summary>
    void SendChat(string message);
}
