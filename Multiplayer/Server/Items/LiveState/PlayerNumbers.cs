using System;
using System.Linq;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Protocol.Data.Items;

namespace Multiplayer.Server.Items.LiveState;

/// <summary>
/// Where an item's owner and holder, which the server keeps as identities, become session player
/// numbers: only at the wire and the session edge (docs/design/item-refactor.md, "Identity only").
/// An offline player is an identity with no session, so nothing is bound or re-bound when a
/// player comes or goes.
/// </summary>
public static class PlayerNumbers
{
    /// <summary>The connected player with this identity, or null.</summary>
    public static ServerPlayer Session(Guid identity)
    {
        if (identity == Guid.Empty || NetworkServer.Instance == null)
            return null;
        return NetworkServer.Instance.ServerPlayers.FirstOrDefault(player =>
            player.PlayerId != 0 && player.Guid == identity);
    }

    public static bool TryGetSession(Guid identity, out ServerPlayer player)
    {
        player = Session(identity);
        return player != null;
    }

    public static bool IsConnected(Guid identity) => Session(identity) != null;

    /// <summary>The session number of a holder or a claim's player, or 0 when nobody or not connected.</summary>
    public static byte Of(Guid identity) => Session(identity)?.PlayerId ?? 0;

    /// <summary>
    /// The owner number to send: the owner's session number while they're connected,
    /// <see cref="ItemUpdateData.OfflineOwnerNumber"/> while they aren't, 0 for no owner.
    /// </summary>
    public static byte OwnerOf(Guid owner) =>
        owner == Guid.Empty ? (byte)0 : Session(owner)?.PlayerId ?? ItemUpdateData.OfflineOwnerNumber;

    /// <summary>The identity of a connected session number, or empty.</summary>
    public static Guid Identity(byte playerId) =>
        playerId != 0 && NetworkServer.Instance != null &&
        NetworkServer.Instance.TryGetServerPlayer(playerId, out ServerPlayer player)
            ? player.Guid
            : Guid.Empty;
}
