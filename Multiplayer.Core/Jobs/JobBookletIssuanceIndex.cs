using System;
using System.Collections.Generic;

namespace Multiplayer.Core.Jobs;

/// <summary>
/// Tracks which authoritative physical booklet copy is issued to each player for one job, by the
/// player's identity, not their number, so it outlives their session (OI-LF-17). The job may own many
/// copies, but a player has at most one currently issued copy.
/// </summary>
public sealed class JobBookletIssuanceIndex
{
    private readonly Dictionary<Guid, ushort> playerToItem = new();
    private readonly Dictionary<ushort, Guid> itemToPlayer = new();

    public int Count => playerToItem.Count;

    public bool Assign(Guid playerId, ushort itemNetId)
    {
        if (playerId == Guid.Empty || itemNetId == 0)
            return false;

        if (playerToItem.TryGetValue(playerId, out ushort previousItem))
            itemToPlayer.Remove(previousItem);
        if (itemToPlayer.TryGetValue(itemNetId, out Guid previousPlayer))
            playerToItem.Remove(previousPlayer);

        playerToItem[playerId] = itemNetId;
        itemToPlayer[itemNetId] = playerId;
        return true;
    }

    public bool TryGetItem(Guid playerId, out ushort itemNetId)
    {
        itemNetId = 0;
        return playerId != Guid.Empty && playerToItem.TryGetValue(playerId, out itemNetId);
    }

    public bool TryGetPlayer(ushort itemNetId, out Guid playerId)
    {
        playerId = Guid.Empty;
        return itemNetId != 0 && itemToPlayer.TryGetValue(itemNetId, out playerId);
    }

    public bool RemoveItem(ushort itemNetId)
    {
        if (!itemToPlayer.TryGetValue(itemNetId, out Guid playerId))
            return false;
        itemToPlayer.Remove(itemNetId);
        playerToItem.Remove(playerId);
        return true;
    }

    public void Clear()
    {
        playerToItem.Clear();
        itemToPlayer.Clear();
    }
}
